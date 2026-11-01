using PathDrawer.Geometry;
using PathDrawer.Input;
using PathDrawer.Physics;
using PathDrawer.Rendering;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace PathDrawer.Runtime
{
    /// <summary>Connects pointer capture, trajectory generation, batched validation, and both renderers.</summary>
    [DisallowMultipleComponent]
    public sealed class PathDrawerDemoController : MonoBehaviour
    {
        public enum TrajectoryMode : byte
        {
            SplineTerrain,
            BallisticArc
        }

        [Header("Scene references")]
        [SerializeField] private Camera drawCamera;
        [SerializeField] private LayerMask groundMask = 1;
        [SerializeField] private LayerMask obstacleMask;
        [SerializeField] private LineRenderer reachableLine;
        [SerializeField] private LineRenderer blockedLine;
        [SerializeField] private MeshFilter ribbonMeshFilter;

        [Header("Input and geometry")]
        [SerializeField, Min(16)] private int maxInputPoints = 1024;
        [SerializeField, Min(2)] private int maxOutputPoints = 4096;
        [SerializeField, Min(1)] private int lookupSamplesPerSegment = 16;
        [SerializeField, Min(0.01f)] private float outputSpacing = 0.2f;
        [SerializeField, Min(0f)] private float simplifyTolerance = 0.025f;
        [SerializeField, Min(0f)] private float minimumPointerDistance = 0.035f;
        [SerializeField] private TrajectoryMode mode = TrajectoryMode.SplineTerrain;

        [Header("Ballistic arc")]
        [SerializeField, Min(0.1f)] private float ballisticDuration = 2.5f;
        [SerializeField, Min(0.1f)] private float ballisticSpeed = 8f;
        [SerializeField] private float ballisticLift = 5f;
        [SerializeField] private Vector3 ballisticGravity = new(0f, -9.81f, 0f);
        [SerializeField, Min(0f)] private float ballisticDrag;

        [Header("Rendering and queries")]
        [SerializeField, Min(0.01f)] private float ribbonWidth = 0.22f;
        [SerializeField, Min(0.01f)] private float ribbonUvMetersPerTile = 1f;
        [SerializeField, Min(0f)] private float blockerRadius = 0.18f;
        [SerializeField, Min(0f)] private float surfaceOffset = 0.025f;
        [SerializeField, Min(0f)] private float pointerRayDistance = 200f;

        private PathInputDecimator _input;
        private NativeArray<float3> _simplifiedControls;
        private TrajectoryGeometryBuffer _geometry;
        private TrajectoryPhysicsValidator _physics;
        private PathRenderAdapter _lineAdapter;
        private BurstRibbonMeshBuilder _ribbonBuilder;
        private Mesh _ownedRibbonMesh;
        private JobHandle _geometryHandle;
        private bool _geometryPending;
        private bool _physicsPending;
        private bool _needsRebuild;
        private bool _isDragging;
        private TrajectoryMode _scheduledMode;

        public TrajectoryMode CurrentMode => mode;
        public float ReachableDistance => _physics != null ? _physics.ReachableDistance : 0f;

        private void Awake()
        {
            if (drawCamera == null) drawCamera = Camera.main;
            if (drawCamera == null || reachableLine == null || ribbonMeshFilter == null)
            {
                Debug.LogError("PathDrawerDemoController requires a camera, reachable LineRenderer, and ribbon MeshFilter.", this);
                enabled = false;
                return;
            }

            int lookupCapacity = checked((maxInputPoints - 1) * lookupSamplesPerSegment + 1);
            _input = new PathInputDecimator(maxInputPoints, minimumPointerDistance);
            _simplifiedControls = new NativeArray<float3>(maxInputPoints, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _geometry = new TrajectoryGeometryBuffer(lookupCapacity, maxOutputPoints, Allocator.Persistent);
            _physics = new TrajectoryPhysicsValidator(
                maxOutputPoints,
                groundMask.value,
                obstacleMask.value,
                projectionHeight: 100f,
                maximumDrop: 200f,
                surfaceOffset: surfaceOffset,
                blockerRadius: blockerRadius,
                minimumCommandsPerJob: 16,
                allocator: Allocator.Persistent);
            _lineAdapter = new PathRenderAdapter(reachableLine, blockedLine, maxOutputPoints, Allocator.Persistent);
            _ribbonBuilder = new BurstRibbonMeshBuilder(Allocator.Persistent);

            if (ribbonMeshFilter.sharedMesh == null)
            {
                _ownedRibbonMesh = new Mesh { name = "PathDrawer Runtime Ribbon" };
                _ownedRibbonMesh.MarkDynamic();
                ribbonMeshFilter.sharedMesh = _ownedRibbonMesh;
            }
        }

        private void Update()
        {
            ReadModeHotkeys();
            CapturePointer();
            AdvancePipeline();
        }

        /// <summary>UI entry point for the terrain-projected spline mode.</summary>
        public void UseSplineTerrainMode()
        {
            SetMode(TrajectoryMode.SplineTerrain);
        }

        /// <summary>UI entry point for the free-flight ballistic mode.</summary>
        public void UseBallisticArcMode()
        {
            SetMode(TrajectoryMode.BallisticArc);
        }

        /// <summary>UI entry point for a mode toggle button.</summary>
        public void ToggleTrajectoryMode()
        {
            SetMode(mode == TrajectoryMode.SplineTerrain ? TrajectoryMode.BallisticArc : TrajectoryMode.SplineTerrain);
        }

        private void SetMode(TrajectoryMode nextMode)
        {
            if (mode == nextMode) return;
            mode = nextMode;
            _needsRebuild = _input != null && _input.Count >= 2;
        }

        private void ReadModeHotkeys()
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.sKey.wasPressedThisFrame) UseSplineTerrainMode();
                if (keyboard.bKey.wasPressedThisFrame) UseBallisticArcMode();
            }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.S)) UseSplineTerrainMode();
            if (Input.GetKeyDown(KeyCode.B)) UseBallisticArcMode();
#endif
        }

        private void CapturePointer()
        {
            if (!TryReadPointer(out Vector2 screenPosition, out bool pressed, out bool held, out bool released)) return;

            if (pressed)
            {
                if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
                _input.Clear();
                _isDragging = true;
                _needsRebuild = false;
            }

            if (_isDragging && (held || released) && TryRaycastGround(screenPosition, out float3 worldPosition))
            {
                bool force = released;
                if (_input.TryAddSample(worldPosition, force)) _needsRebuild = true;
            }

            if (released && _isDragging)
            {
                _isDragging = false;
                _needsRebuild = true;
            }
        }

        private static bool TryReadPointer(out Vector2 screenPosition, out bool pressed, out bool held, out bool released)
        {
#if ENABLE_INPUT_SYSTEM
            Pointer pointer = Pointer.current;
            if (pointer != null)
            {
                screenPosition = pointer.position.ReadValue();
                pressed = pointer.press.wasPressedThisFrame;
                held = pointer.press.isPressed;
                released = pointer.press.wasReleasedThisFrame;
                return true;
            }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            screenPosition = Input.mousePosition;
            pressed = Input.GetMouseButtonDown(0);
            held = Input.GetMouseButton(0);
            released = Input.GetMouseButtonUp(0);
            return true;
#else
            screenPosition = default;
            pressed = held = released = false;
            return false;
#endif
        }

        private bool TryRaycastGround(Vector2 screenPosition, out float3 worldPosition)
        {
            Ray ray = drawCamera.ScreenPointToRay(screenPosition);
            if (UnityEngine.Physics.Raycast(ray, out RaycastHit hit, pointerRayDistance, groundMask, QueryTriggerInteraction.Ignore))
            {
                worldPosition = hit.point;
                return true;
            }

            worldPosition = default;
            return false;
        }

        private void AdvancePipeline()
        {
            if (_geometryPending && _geometryHandle.IsCompleted)
            {
                _geometryHandle.Complete();
                _geometryPending = false;
                int outputCount = _geometry.Count;
                if (outputCount >= 2)
                {
                    _physics.ScheduleValidation(_geometry, outputCount, _scheduledMode == TrajectoryMode.SplineTerrain);
                    _physicsPending = true;
                }
            }

            if (_physicsPending && _physics.TryCompleteValidation())
            {
                _physicsPending = false;
                int outputCount = _physics.PointCount;
                _lineAdapter.Apply(_geometry.Positions, outputCount, _physics.FirstBlockedSegment, _physics.ImpactCenter);
                if (ribbonMeshFilter.sharedMesh != null)
                {
                    _ribbonBuilder.Build(
                        ribbonMeshFilter.sharedMesh,
                        _geometry.Positions,
                        _physics.PathDistances,
                        outputCount,
                        ribbonWidth,
                        ribbonUvMetersPerTile,
                        RibbonJoinMode.Miter,
                        surfaceUp: new float3(0f, 1f, 0f));
                }
            }

            if (!_geometryPending && !_physicsPending && _needsRebuild)
                ScheduleCurrentPath();
        }

        private void ScheduleCurrentPath()
        {
            _needsRebuild = false;
            if (_input.Count < 2) return;

            _scheduledMode = mode;
            int controlCount = _input.Simplify(simplifyTolerance, _simplifiedControls);
            if (controlCount < 2) return;

            if (mode == TrajectoryMode.SplineTerrain)
            {
                _geometryHandle = _geometry.RebuildSpline(
                    _simplifiedControls,
                    controlCount,
                    lookupSamplesPerSegment,
                    outputSpacing);
            }
            else
            {
                float3 origin = _simplifiedControls[0];
                float3 destination = _simplifiedControls[controlCount - 1];
                float3 horizontal = new float3(destination.x - origin.x, 0f, destination.z - origin.z);
                float3 direction = math.normalizesafe(horizontal, new float3(0f, 0f, 1f));
                float3 initialVelocity = direction * ballisticSpeed + new float3(0f, ballisticLift, 0f);
                int lookupSegments = math.max(2, _geometry.LookupCapacity - 1);
                var settings = new BallisticTrajectorySettings(
                    origin,
                    initialVelocity,
                    ballisticGravity,
                    ballisticDrag,
                    ballisticDuration,
                    lookupSegments);
                _geometryHandle = _geometry.RebuildBallistic(settings, outputSpacing);
            }

            _geometryPending = true;
        }

        private void OnDestroy()
        {
            _ribbonBuilder?.Dispose();
            _physics?.Dispose();
            _geometry?.Dispose();
            _lineAdapter?.Dispose();
            if (_simplifiedControls.IsCreated) _simplifiedControls.Dispose();
            _input?.Dispose();

            if (_ownedRibbonMesh != null)
            {
                if (ribbonMeshFilter != null && ribbonMeshFilter.sharedMesh == _ownedRibbonMesh)
                    ribbonMeshFilter.sharedMesh = null;
                Destroy(_ownedRibbonMesh);
            }
        }
    }
}
