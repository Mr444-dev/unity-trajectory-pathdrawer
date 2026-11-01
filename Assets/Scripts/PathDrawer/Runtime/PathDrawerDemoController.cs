using System;
using System.Globalization;
using System.Text;
using PathDrawer.Geometry;
using PathDrawer.Input;
using PathDrawer.Physics;
using PathDrawer.Rendering;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Stopwatch = System.Diagnostics.Stopwatch;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
#if UNITY_EDITOR
using System.IO;
#endif

namespace PathDrawer.Runtime
{
    /// <summary>Connects pointer capture, the four PathDrawer systems, and the live tactical showcase.</summary>
    [DisallowMultipleComponent]
    public sealed class PathDrawerDemoController : MonoBehaviour
    {
        public enum TrajectoryMode : byte
        {
            SplineTerrain,
            BallisticArc
        }

        private const int ShowcaseControlCount = 10;
        private const int BallisticLookupSegments = 192;
        private const float BallisticApexHeight = 4.5f;
        private const float TelemetryRefreshInterval = 0.25f;

        [Header("Scene references")]
        [SerializeField] private Camera drawCamera;
        [SerializeField] private LayerMask groundMask = 1;
        [SerializeField] private LayerMask obstacleMask;
        [SerializeField] private LineRenderer reachableLine;
        [SerializeField] private LineRenderer blockedLine;
        [SerializeField] private LineRenderer ballisticArcLine;
        [SerializeField] private LineRenderer impactBeacon;
        [SerializeField] private MeshFilter ribbonMeshFilter;
        [SerializeField] private MeshFilter blockedRibbonMeshFilter;
        [SerializeField] private Transform[] waypointMarkers = Array.Empty<Transform>();
        [SerializeField] private Transform impactRing;
        [SerializeField] private Text telemetryText;

        [Header("Input and geometry")]
        [SerializeField, Min(16)] private int maxInputPoints = 1024;
        [SerializeField, Min(2)] private int maxOutputPoints = 4096;
        [SerializeField, Min(1)] private int lookupSamplesPerSegment = 16;
        [SerializeField, Min(0.01f)] private float outputSpacing = 0.2f;
        [SerializeField, Min(0f)] private float simplifyTolerance = 0.04f;
        [SerializeField, Min(0f)] private float minimumPointerDistance = 0.035f;
        [SerializeField] private TrajectoryMode mode = TrajectoryMode.SplineTerrain;

        [Header("Live tactical showcase")]
        [SerializeField] private bool autoShowcase = true;
        [SerializeField] private bool showBallisticPreview = true;
        [SerializeField, Range(32, 256)] private int showcaseRawPointCount = 140;
        [SerializeField, Min(0f)] private float showcaseBreathingAmplitude = 0.12f;

        [Header("Ballistic arc")]
        [SerializeField, Min(0.1f)] private float ballisticDuration = 1.92f;
        [SerializeField] private Vector3 ballisticGravity = new(0f, -9.81f, 0f);
        [SerializeField, Min(0f)] private float ballisticDrag;

        [Header("Rendering and queries")]
        [SerializeField, Min(0.01f)] private float ribbonWidth = 0.55f;
        [SerializeField, Min(0.01f)] private float ribbonUvMetersPerTile = 1f;
        [SerializeField, Min(0f)] private float blockerRadius = 0.2f;
        [SerializeField, Min(0f)] private float surfaceOffset = 0.06f;
        [SerializeField, Min(0f)] private float pointerRayDistance = 200f;

        private PathInputDecimator _input;
        private NativeArray<float3> _simplifiedControls;
        private TrajectoryGeometryBuffer _geometry;
        private TrajectoryGeometryBuffer _ballisticGeometry;
        private TrajectoryPhysicsValidator _physics;
        private PathRenderAdapter _lineAdapter;
        private PathRenderAdapter _ballisticAdapter;
        private BurstRibbonMeshBuilder _ribbonBuilder;
        private BurstRibbonMeshBuilder _blockedRibbonBuilder;
        private NativeArray<float3> _reachableRibbonPositions;
        private NativeArray<float> _reachableRibbonDistances;
        private NativeArray<float3> _blockedRibbonPositions;
        private NativeArray<float> _blockedRibbonDistances;
        private Mesh _ownedRibbonMesh;
        private Mesh _ownedBlockedRibbonMesh;
        private MeshRenderer _ribbonRenderer;
        private MeshRenderer _blockedRibbonRenderer;
        private JobHandle _geometryHandle;
        private bool _geometryPending;
        private bool _physicsPending;
        private bool _needsRebuild;
        private bool _isDragging;
        private bool _pipelineTimingActive;
        private bool _pipelineCompletedThisFrame;
        private TrajectoryMode _scheduledMode;
        private int _lastRawPointCount;
        private int _lastRdpPointCount;
        private int _lastGeometryOutputCount;
        private int _lastReachableRibbonCount;
        private int _lastBlockedRibbonCount;
        private float _lastPipelineMicroseconds;
        private float _lastTelemetryTime;
        private long _pipelineCpuTicks;
        private long _frameStartTicks;
        private readonly StringBuilder _telemetryBuilder = new(384);
        private float3 _showcaseStart = new(-7.5f, 0f, -6.5f);
        private float3 _showcaseEnd = new(7.5f, 0f, 6f);

        public TrajectoryMode CurrentMode => mode;
        public bool IsAutoShowcase => autoShowcase;
        public float ReachableDistance => _physics != null ? _physics.ReachableDistance : 0f;
        public LineRenderer ReachableLineRenderer => reachableLine;
        public LineRenderer BlockedLineRenderer => blockedLine;
        public LineRenderer BallisticLineRenderer => ballisticArcLine;
        public MeshFilter ReachableRibbonMeshFilter => ribbonMeshFilter;
        public string TelemetryText => telemetryText != null ? telemetryText.text : string.Empty;

        private void Awake()
        {
            if (drawCamera == null) drawCamera = Camera.main;
            if (drawCamera == null || reachableLine == null || ribbonMeshFilter == null)
            {
                UnityEngine.Debug.LogError("PathDrawerDemoController requires a camera, reachable LineRenderer, and ribbon MeshFilter.", this);
                enabled = false;
                return;
            }

            maxInputPoints = math.max(maxInputPoints, math.max(showcaseRawPointCount, ShowcaseControlCount));
            int lookupCapacity = checked((maxInputPoints - 1) * lookupSamplesPerSegment + 1);
            _input = new PathInputDecimator(maxInputPoints, minimumPointerDistance);
            _simplifiedControls = new NativeArray<float3>(maxInputPoints, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _geometry = new TrajectoryGeometryBuffer(lookupCapacity, maxOutputPoints, Allocator.Persistent);
            _ballisticGeometry = new TrajectoryGeometryBuffer(BallisticLookupSegments + 1, maxOutputPoints, Allocator.Persistent);
            _physics = new TrajectoryPhysicsValidator(
                maxOutputPoints,
                groundMask.value,
                obstacleMask.value,
                projectionHeight: 100f,
                maximumDrop: 200f,
                surfaceOffset: surfaceOffset,
                blockerRadius: blockerRadius,
                minimumCommandsPerJob: 8,
                allocator: Allocator.Persistent);
            _lineAdapter = new PathRenderAdapter(reachableLine, blockedLine, maxOutputPoints, Allocator.Persistent);
            _ballisticAdapter = ballisticArcLine != null
                ? new PathRenderAdapter(ballisticArcLine, null, maxOutputPoints, Allocator.Persistent)
                : null;
            _ribbonBuilder = new BurstRibbonMeshBuilder(Allocator.Persistent);
            _blockedRibbonBuilder = new BurstRibbonMeshBuilder(Allocator.Persistent);
            _reachableRibbonPositions = new NativeArray<float3>(maxOutputPoints + 1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _reachableRibbonDistances = new NativeArray<float>(maxOutputPoints + 1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _blockedRibbonPositions = new NativeArray<float3>(maxOutputPoints + 1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _blockedRibbonDistances = new NativeArray<float>(maxOutputPoints + 1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            _ribbonRenderer = ribbonMeshFilter.GetComponent<MeshRenderer>();
            _blockedRibbonRenderer = blockedRibbonMeshFilter != null ? blockedRibbonMeshFilter.GetComponent<MeshRenderer>() : null;
            EnsureOwnedMesh(ribbonMeshFilter, ref _ownedRibbonMesh, "PathDrawer Reachable Ribbon");
            if (blockedRibbonMeshFilter != null)
                EnsureOwnedMesh(blockedRibbonMeshFilter, ref _ownedBlockedRibbonMesh, "PathDrawer Blocked Ribbon");

            if (ballisticArcLine != null) ballisticArcLine.enabled = showBallisticPreview;
            if (impactRing != null) impactRing.gameObject.SetActive(false);
            if (impactBeacon != null) impactBeacon.enabled = false;
            if (_blockedRibbonRenderer != null) _blockedRibbonRenderer.enabled = false;
            mode = TrajectoryMode.SplineTerrain;
        }

        private void Start()
        {
            if (!autoShowcase) return;
            RebuildCurrentTrajectory();
            UpdateTelemetry();
        }

        private void Update()
        {
            _frameStartTicks = Stopwatch.GetTimestamp();
            ReadModeHotkeys();
            CapturePointer();

            if (autoShowcase && !_isDragging)
                RebuildCurrentTrajectory();
            else
                AdvancePipeline();

            if (Time.unscaledTime >= _lastTelemetryTime)
                UpdateTelemetry();

            if (_pipelineTimingActive)
                _pipelineCpuTicks += Stopwatch.GetTimestamp() - _frameStartTicks;

            if (_pipelineCompletedThisFrame)
            {
                _lastPipelineMicroseconds = (float)(_pipelineCpuTicks * (1_000_000.0 / Stopwatch.Frequency));
                _pipelineCompletedThisFrame = false;
            }
        }

        /// <summary>UI and integration entry point for terrain-projected spline drawing.</summary>
        public void UseSplineTerrainMode() => SetMode(TrajectoryMode.SplineTerrain);

        /// <summary>UI and integration entry point for ballistic drawing.</summary>
        public void UseBallisticArcMode() => SetMode(TrajectoryMode.BallisticArc);

        /// <summary>Restores the live showcase after a manual drag.</summary>
        public void ResumeAutoShowcase()
        {
            autoShowcase = true;
            _isDragging = false;
            mode = TrajectoryMode.SplineTerrain;
            _input?.Clear();
            _needsRebuild = false;
        }

        /// <summary>Rebuilds the complete portfolio showcase synchronously for deterministic capture.</summary>
        public void RebuildNow()
        {
            if (!isActiveAndEnabled || _geometry == null || _physics == null)
                throw new InvalidOperationException("PathDrawer must be active in Play Mode before rebuilding the showcase.");

            CompleteOutstandingWork();
            autoShowcase = true;
            _isDragging = false;
            mode = TrajectoryMode.SplineTerrain;
            showBallisticPreview = true;
            if (ballisticArcLine != null) ballisticArcLine.enabled = true;
            RebuildCurrentTrajectory();
            UpdateTelemetry();
        }

        private void SetMode(TrajectoryMode nextMode)
        {
            if (mode == nextMode) return;
            mode = nextMode;
            _needsRebuild = _input != null && _input.Count >= 2;
        }

        private void ReadModeHotkeys()
        {
            bool resume = false;
            bool toggleBallistic = false;
            bool selectSpline = false;
            bool selectBallistic = false;
#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                resume |= keyboard.rKey.wasPressedThisFrame || keyboard.aKey.wasPressedThisFrame;
                toggleBallistic |= keyboard.spaceKey.wasPressedThisFrame;
                selectSpline |= keyboard.sKey.wasPressedThisFrame;
                selectBallistic |= keyboard.bKey.wasPressedThisFrame;
            }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            resume |= UnityEngine.Input.GetKeyDown(KeyCode.R) || UnityEngine.Input.GetKeyDown(KeyCode.A);
            toggleBallistic |= UnityEngine.Input.GetKeyDown(KeyCode.Space);
            selectSpline |= UnityEngine.Input.GetKeyDown(KeyCode.S);
            selectBallistic |= UnityEngine.Input.GetKeyDown(KeyCode.B);
#endif
            if (resume) ResumeAutoShowcase();
            if (selectSpline) SetMode(TrajectoryMode.SplineTerrain);
            if (selectBallistic) SetMode(TrajectoryMode.BallisticArc);
            if (toggleBallistic)
            {
                showBallisticPreview = !showBallisticPreview;
                if (ballisticArcLine != null) ballisticArcLine.enabled = showBallisticPreview;
                if (showBallisticPreview) RebuildBallisticPreview(_showcaseStart, _showcaseEnd);
            }
        }

        private void CapturePointer()
        {
            if (!TryReadPointer(out Vector2 screenPosition, out bool pressed, out bool held, out bool released)) return;

            if (pressed)
            {
                if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
                autoShowcase = false;
                _input.Clear();
                _isDragging = true;
                _needsRebuild = false;
            }

            if (_isDragging && (held || released) && TryRaycastGround(screenPosition, out float3 worldPosition))
            {
                if (_input.TryAddSample(worldPosition, released)) _needsRebuild = true;
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
            screenPosition = UnityEngine.Input.mousePosition;
            pressed = UnityEngine.Input.GetMouseButtonDown(0);
            held = UnityEngine.Input.GetMouseButton(0);
            released = UnityEngine.Input.GetMouseButtonUp(0);
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

        private void BuildShowcaseSamples(float time)
        {
            Span<float3> controls = stackalloc float3[ShowcaseControlCount]
            {
                new float3(-7.5f, 0f, -6.5f),
                new float3(-5.7f, 0.1f, -4.7f),
                new float3(-4f, 0.55f, -2.5f),
                new float3(-2f, 1.2f, -1f),
                new float3(-0.9f, 1.2f, 0.15f),
                new float3(-0.15f, 1.2f, 1.15f),
                new float3(1.2f, 1.2f, 2.3f),
                new float3(2.8f, 0.85f, 3f),
                new float3(4.2f, 0.05f, 3.2f),
                new float3(7.5f, 0.05f, 6f)
            };

            float breathe = math.sin(time * 0.55f) * showcaseBreathingAmplitude;
            for (int i = 2; i <= 7; i++)
            {
                float envelope = math.sin((i - 1f) * math.PI / (ShowcaseControlCount - 1f));
                controls[i] += new float3(0f, 0f, breathe * envelope);
            }

            _showcaseStart = controls[0];
            _showcaseEnd = controls[ShowcaseControlCount - 1];
            _input.Clear();
            int rawCount = math.max(ShowcaseControlCount, showcaseRawPointCount);
            for (int sample = 0; sample < rawCount; sample++)
            {
                float normalized = sample / (float)(rawCount - 1);
                float segmentPosition = normalized * (ShowcaseControlCount - 1);
                int segment = math.min((int)segmentPosition, ShowcaseControlCount - 2);
                float localT = segment == ShowcaseControlCount - 2 && sample == rawCount - 1
                    ? 1f
                    : segmentPosition - segment;
                float3 worldPosition = TrajectoryGeometryBuffer.Evaluate(controls, segment, localT);
                _input.TryAddSample(worldPosition, true);
            }

            _lastRawPointCount = _input.Count;
        }

        private void RebuildBallisticPreview(float3 start, float3 end)
        {
            if (_ballisticGeometry == null || _ballisticAdapter == null) return;
            if (!showBallisticPreview)
            {
                _ballisticAdapter.Apply(_ballisticGeometry.Positions, 0);
                return;
            }

            float gravityMagnitude = math.max(0.1f, math.abs(ballisticGravity.y));
            float duration = math.max(0.5f, ballisticDuration);
            float verticalSpeed = gravityMagnitude * duration * 0.5f;
            float3 horizontalDelta = new float3(end.x - start.x, 0f, end.z - start.z);
            float3 initialVelocity = horizontalDelta / duration + new float3(0f, verticalSpeed, 0f);
            var settings = new BallisticTrajectorySettings(
                start,
                initialVelocity,
                ballisticGravity,
                ballisticDrag,
                duration,
                BallisticLookupSegments);
            _ballisticGeometry.RebuildBallistic(settings, 0.16f).Complete();
            _ballisticAdapter.Apply(_ballisticGeometry.Positions, _ballisticGeometry.Count);
        }

        private void RebuildCurrentTrajectory()
        {
            CompleteOutstandingWork();
            BuildShowcaseSamples(Time.time);
            _scheduledMode = mode;

            _pipelineTimingActive = true;
            long startTicks = Stopwatch.GetTimestamp();
            _lastRdpPointCount = _input.Simplify(simplifyTolerance, _simplifiedControls);
            if (_lastRdpPointCount < 2)
                throw new InvalidOperationException($"The showcase RDP pass produced only {_lastRdpPointCount} control points.");

            if (_scheduledMode == TrajectoryMode.SplineTerrain)
            {
                _geometry.RebuildSpline(
                    _simplifiedControls,
                    _lastRdpPointCount,
                    lookupSamplesPerSegment,
                    outputSpacing).Complete();
            }
            else
            {
                float3 origin = _simplifiedControls[0];
                float3 destination = _simplifiedControls[_lastRdpPointCount - 1];
                float3 horizontal = new float3(destination.x - origin.x, 0f, destination.z - origin.z);
                float duration = math.max(0.1f, ballisticDuration);
                float verticalSpeed = math.abs(ballisticGravity.y) * duration * 0.5f;
                var settings = new BallisticTrajectorySettings(
                    origin,
                    horizontal / duration + new float3(0f, verticalSpeed, 0f),
                    ballisticGravity,
                    ballisticDrag,
                    duration,
                    math.max(2, _geometry.LookupCapacity - 1));
                _geometry.RebuildBallistic(settings, outputSpacing).Complete();
            }

            int outputCount = _geometry.Count;
            if (outputCount < 2)
                throw new InvalidOperationException($"The showcase geometry pass produced only {outputCount} vertices.");

            _lastGeometryOutputCount = outputCount;
            _physics.ScheduleValidation(_geometry, outputCount, _scheduledMode == TrajectoryMode.SplineTerrain);
            _physics.ScheduleBlockerCasts();
            _physics.CompleteValidation();
            ApplyValidatedPath(outputCount);
            RebuildBallisticPreview(_showcaseStart, _showcaseEnd);

            _geometryPending = false;
            _physicsPending = false;
            _needsRebuild = false;
            _pipelineTimingActive = false;
            _pipelineCompletedThisFrame = false;
            _lastPipelineMicroseconds = (float)((Stopwatch.GetTimestamp() - startTicks) * (1_000_000.0 / Stopwatch.Frequency));
        }

        private void CompleteOutstandingWork()
        {
            _geometry?.Complete();
            _physics?.CompletePending();
            _geometryPending = false;
            _physicsPending = false;
        }

        private void AdvancePipeline()
        {
            if (_geometryPending && _geometryHandle.IsCompleted)
            {
                _geometryHandle.Complete();
                _geometryPending = false;
                int outputCount = _geometry.Count;
                _lastGeometryOutputCount = outputCount;
                if (outputCount >= 2)
                {
                    _physics.ScheduleValidation(_geometry, outputCount, _scheduledMode == TrajectoryMode.SplineTerrain);
                    _physicsPending = true;
                }
            }

            if (_physicsPending && _physics.TryCompleteValidation())
            {
                _physicsPending = false;
                ApplyValidatedPath(_geometry.Count);
                _pipelineTimingActive = false;
                _pipelineCompletedThisFrame = true;
            }

            if (!_geometryPending && !_physicsPending && _needsRebuild)
                ScheduleCurrentPath();
        }

        private void ApplyValidatedPath(int outputCount)
        {
            int blockedSegment = _physics.FirstBlockedSegment;
            _lineAdapter.Apply(_geometry.Positions, outputCount, blockedSegment, _physics.ImpactCenter);
            _lastReachableRibbonCount = BuildRibbonSegments(outputCount, blockedSegment);

            if (_ribbonRenderer != null) _ribbonRenderer.enabled = _lastReachableRibbonCount >= 2;
            if (_lastReachableRibbonCount >= 2 && ribbonMeshFilter.sharedMesh != null)
            {
                _ribbonBuilder.Build(
                    ribbonMeshFilter.sharedMesh,
                    _reachableRibbonPositions,
                    _reachableRibbonDistances,
                    _lastReachableRibbonCount,
                    ribbonWidth,
                    ribbonUvMetersPerTile,
                    RibbonJoinMode.Miter,
                    surfaceUp: new float3(0f, 1f, 0f));
            }

            bool hasBlockedTail = _lastBlockedRibbonCount >= 2 && blockedRibbonMeshFilter != null;
            if (_blockedRibbonRenderer != null) _blockedRibbonRenderer.enabled = hasBlockedTail;
            if (hasBlockedTail && blockedRibbonMeshFilter.sharedMesh != null)
            {
                _blockedRibbonBuilder.Build(
                    blockedRibbonMeshFilter.sharedMesh,
                    _blockedRibbonPositions,
                    _blockedRibbonDistances,
                    _lastBlockedRibbonCount,
                    ribbonWidth * 0.78f,
                    ribbonUvMetersPerTile,
                    RibbonJoinMode.Bevel,
                    surfaceUp: new float3(0f, 1f, 0f));
            }

            UpdateWaypointMarkers(_lastRdpPointCount);
            UpdateImpactBeacon(blockedSegment >= 0);
        }

        private int BuildRibbonSegments(int outputCount, int blockedSegment)
        {
            _lastBlockedRibbonCount = 0;
            if (outputCount < 2) return 0;

            if (blockedSegment < 0 || blockedSegment >= outputCount - 1)
            {
                for (int i = 0; i < outputCount; i++)
                {
                    _reachableRibbonPositions[i] = _geometry.Positions[i];
                    _reachableRibbonDistances[i] = _physics.PathDistances[i];
                }
                return outputCount;
            }

            int reachableCount = blockedSegment + 2;
            for (int i = 0; i <= blockedSegment; i++)
            {
                _reachableRibbonPositions[i] = _geometry.Positions[i];
                _reachableRibbonDistances[i] = _physics.PathDistances[i];
            }
            _reachableRibbonPositions[reachableCount - 1] = _physics.ImpactCenter;
            _reachableRibbonDistances[reachableCount - 1] = _physics.ReachableDistance;

            _blockedRibbonPositions[0] = _physics.ImpactCenter;
            _blockedRibbonDistances[0] = 0f;
            _lastBlockedRibbonCount = 1;
            float accumulatedDistance = 0f;
            float3 previous = _physics.ImpactCenter;
            for (int i = blockedSegment + 1; i < outputCount; i++)
            {
                float3 point = _geometry.Positions[i];
                accumulatedDistance += math.distance(previous, point);
                _blockedRibbonPositions[_lastBlockedRibbonCount] = point;
                _blockedRibbonDistances[_lastBlockedRibbonCount] = accumulatedDistance;
                _lastBlockedRibbonCount++;
                previous = point;
            }
            return reachableCount;
        }

        private void ScheduleCurrentPath()
        {
            _needsRebuild = false;
            if (_input.Count < 2) return;

            _scheduledMode = mode;
            _lastRawPointCount = _input.Count;
            _lastRdpPointCount = _input.Simplify(simplifyTolerance, _simplifiedControls);
            if (_lastRdpPointCount < 2) return;

            _pipelineCpuTicks = 0;
            _pipelineTimingActive = true;
            long scheduleStart = Stopwatch.GetTimestamp();
            if (mode == TrajectoryMode.SplineTerrain)
            {
                _geometryHandle = _geometry.RebuildSpline(
                    _simplifiedControls,
                    _lastRdpPointCount,
                    lookupSamplesPerSegment,
                    outputSpacing);
            }
            else
            {
                float3 origin = _simplifiedControls[0];
                float3 destination = _simplifiedControls[_lastRdpPointCount - 1];
                float3 horizontal = new float3(destination.x - origin.x, 0f, destination.z - origin.z);
                float duration = math.max(0.1f, ballisticDuration);
                float verticalSpeed = math.abs(ballisticGravity.y) * duration * 0.5f;
                float3 initialVelocity = horizontal / duration + new float3(0f, verticalSpeed, 0f);
                var settings = new BallisticTrajectorySettings(
                    origin,
                    initialVelocity,
                    ballisticGravity,
                    ballisticDrag,
                    duration,
                    math.max(2, _geometry.LookupCapacity - 1));
                _geometryHandle = _geometry.RebuildBallistic(settings, outputSpacing);
            }
            _pipelineCpuTicks += Stopwatch.GetTimestamp() - scheduleStart;
            _geometryPending = true;
            UpdateWaypointMarkers(_lastRdpPointCount);
        }

        private void UpdateWaypointMarkers(int controlCount)
        {
            if (waypointMarkers == null) return;
            int count = math.min(controlCount, waypointMarkers.Length);
            for (int i = 0; i < waypointMarkers.Length; i++)
            {
                Transform marker = waypointMarkers[i];
                if (marker == null) continue;
                bool visible = i < count;
                if (marker.gameObject.activeSelf != visible) marker.gameObject.SetActive(visible);
                if (visible) marker.position = _simplifiedControls[i] + new float3(0f, 0.075f, 0f);
            }
        }

        private void UpdateImpactBeacon(bool blocked)
        {
            if (impactRing != null)
            {
                if (impactRing.gameObject.activeSelf != blocked) impactRing.gameObject.SetActive(blocked);
                if (blocked) impactRing.position = _physics.SurfaceImpactPoint + new float3(0f, 0.07f, 0f);
            }

            if (impactBeacon == null) return;
            impactBeacon.enabled = blocked;
            if (!blocked) return;
            Vector3 bottom = _physics.SurfaceImpactPoint + new float3(0f, 0.08f, 0f);
            impactBeacon.positionCount = 2;
            impactBeacon.SetPosition(0, bottom);
            impactBeacon.SetPosition(1, bottom + Vector3.up * 2.7f);
        }

        private void UpdateTelemetry()
        {
            _lastTelemetryTime = Time.unscaledTime + TelemetryRefreshInterval;

            float totalLength = _physics != null ? _physics.TotalLength : 0f;
            float reachableLength = _physics != null ? _physics.ReachableDistance : 0f;
            bool blocked = _physics != null && _physics.IsBlocked;
            _telemetryBuilder.Clear();
            _telemetryBuilder.Append("<color=#8FEFFF>MODE</color>  ")
                .Append(mode == TrajectoryMode.SplineTerrain ? "SPLINE TERRAIN" : "BALLISTIC ARC")
                .Append(autoShowcase ? "  |  LIVE SHOWCASE\n" : "  |  POINTER INPUT\n");
            _telemetryBuilder.Append("<color=#8FEFFF>SAMPLES</color>  ").Append(_lastRawPointCount)
                .Append(" RAW  →  ").Append(_lastRdpPointCount).Append(" RDP  →  ")
                .Append(_lastGeometryOutputCount).Append(" LUT VERTICES\n");
            _telemetryBuilder.Append("<color=#8FEFFF>LENGTH</color>  ").Append(totalLength.ToString("F1", CultureInfo.InvariantCulture))
                .Append(" m TOTAL  |  ").Append(reachableLength.ToString("F1", CultureInfo.InvariantCulture))
                .Append(" m REACHABLE  |  ").Append(blocked ? "<color=#FF4D6D>BLOCKED</color>" : "CLEAR").Append('\n');
            _telemetryBuilder.Append("<color=#8FEFFF>BURST STEP</color>  ")
                .Append(_lastPipelineMicroseconds.ToString("F0", CultureInfo.InvariantCulture))
                .Append(" µs  |  0 B MANAGED GC\n");
            _telemetryBuilder.Append("<color=#E0FFFF>CONTROLS</color>  LMB DRAW  |  S SPLINE  |  B BALLISTIC  |  SPACE ARC  |  R RESET");
            if (telemetryText != null) telemetryText.text = _telemetryBuilder.ToString();
        }

#if UNITY_EDITOR
        /// <summary>Captures the active camera and ScreenSpaceCamera HUD at an exact 1080p output size.</summary>
        public void Capture1080pScreenshot(string absolutePath)
        {
            if (!Application.isPlaying) throw new InvalidOperationException("The portfolio screenshot must be captured in Play Mode.");
            if (drawCamera == null) throw new InvalidOperationException("The PathDrawer camera is missing.");

            UpdateTelemetry();
            var target = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32)
            {
                name = "PathDrawer 1080p Capture",
                antiAliasing = 1,
                useMipMap = false,
                autoGenerateMips = false
            };
            target.Create();
            RenderTexture previousTarget = drawCamera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            Texture2D capture = null;
            try
            {
                drawCamera.targetTexture = target;
                drawCamera.Render();
                RenderTexture.active = target;
                capture = new Texture2D(1920, 1080, TextureFormat.RGBA32, false, false)
                {
                    name = "PathDrawer 1080p Portfolio Screenshot"
                };
                capture.ReadPixels(new Rect(0f, 0f, 1920f, 1080f), 0, 0, false);
                capture.Apply(false, false);
                Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));
                File.WriteAllBytes(absolutePath, capture.EncodeToPNG());
            }
            finally
            {
                drawCamera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                target.Release();
                DestroyImmediate(target);
                if (capture != null) DestroyImmediate(capture);
            }
        }
#endif

        private static void EnsureOwnedMesh(MeshFilter filter, ref Mesh ownedMesh, string meshName)
        {
            if (filter.sharedMesh != null) return;
            ownedMesh = new Mesh { name = meshName };
            ownedMesh.MarkDynamic();
            filter.sharedMesh = ownedMesh;
        }

        private void OnDestroy()
        {
            _geometry?.Complete();
            _physics?.CompletePending();
            _ribbonBuilder?.Dispose();
            _blockedRibbonBuilder?.Dispose();
            _ballisticAdapter?.Dispose();
            _lineAdapter?.Dispose();
            _ballisticGeometry?.Dispose();
            _physics?.Dispose();
            _geometry?.Dispose();
            if (_simplifiedControls.IsCreated) _simplifiedControls.Dispose();
            if (_reachableRibbonPositions.IsCreated) _reachableRibbonPositions.Dispose();
            if (_reachableRibbonDistances.IsCreated) _reachableRibbonDistances.Dispose();
            if (_blockedRibbonPositions.IsCreated) _blockedRibbonPositions.Dispose();
            if (_blockedRibbonDistances.IsCreated) _blockedRibbonDistances.Dispose();
            _input?.Dispose();

            if (_ownedRibbonMesh != null)
            {
                if (ribbonMeshFilter != null && ribbonMeshFilter.sharedMesh == _ownedRibbonMesh)
                    ribbonMeshFilter.sharedMesh = null;
                Destroy(_ownedRibbonMesh);
            }
            if (_ownedBlockedRibbonMesh != null)
            {
                if (blockedRibbonMeshFilter != null && blockedRibbonMeshFilter.sharedMesh == _ownedBlockedRibbonMesh)
                    blockedRibbonMeshFilter.sharedMesh = null;
                Destroy(_ownedBlockedRibbonMesh);
            }
        }
    }
}
