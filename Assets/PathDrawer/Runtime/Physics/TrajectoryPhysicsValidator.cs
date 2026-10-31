using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace OmniTwin.PathDrawer.Physics
{
    /// <summary>Runs batched terrain projection followed by batched swept-volume blocker queries.</summary>
    public sealed class TrajectoryPhysicsValidator : IDisposable
    {
        private readonly NativeArray<RaycastCommand> _projectionCommands;
        private readonly NativeArray<RaycastHit> _projectionHits;
        private NativeArray<SpherecastCommand> _blockerCommands;
        private readonly NativeArray<RaycastHit> _blockerHits;
        private NativeArray<float> _pathDistances;
        private NativeArray<float3> _pathTangents;
        private readonly QueryParameters _projectionQuery;
        private readonly QueryParameters _blockerQuery;
        private readonly float3 _projectionUp;
        private readonly float _projectionHeight;
        private readonly float _maximumDrop;
        private readonly float _surfaceOffset;
        private readonly float _blockerRadius;
        private readonly int _minimumCommandsPerJob;
        private JobHandle _projectionHandle;
        private JobHandle _blockerHandle;
        private NativeArray<float3> _pathPositions;
        private int _pointCount;
        private int _firstBlockedSegment = -1;
        private float _totalLength;
        private float _reachableDistance;
        private float3 _impactCenter;
        private float3 _surfaceImpactPoint;
        private Collider _firstBlocker;
        private bool _projectionPending;
        private bool _blockerPending;

        public TrajectoryPhysicsValidator(
            int maxPoints,
            int projectionLayerMask,
            int blockerLayerMask,
            float projectionHeight = 5f,
            float maximumDrop = 20f,
            float surfaceOffset = 0.02f,
            float blockerRadius = 0.15f,
            float3 projectionUp = default,
            int minimumCommandsPerJob = 16,
            Allocator allocator = Allocator.Persistent)
        {
            if (maxPoints < 2) throw new ArgumentOutOfRangeException(nameof(maxPoints));
            if (projectionHeight < 0f) throw new ArgumentOutOfRangeException(nameof(projectionHeight));
            if (maximumDrop <= 0f) throw new ArgumentOutOfRangeException(nameof(maximumDrop));
            if (surfaceOffset < 0f) throw new ArgumentOutOfRangeException(nameof(surfaceOffset));
            if (blockerRadius < 0f) throw new ArgumentOutOfRangeException(nameof(blockerRadius));
            if (minimumCommandsPerJob < 1) throw new ArgumentOutOfRangeException(nameof(minimumCommandsPerJob));

            _projectionHeight = projectionHeight;
            _maximumDrop = maximumDrop;
            _surfaceOffset = surfaceOffset;
            _blockerRadius = blockerRadius;
            _minimumCommandsPerJob = minimumCommandsPerJob;
            _projectionUp = math.normalizesafe(projectionUp, new float3(0f, 1f, 0f));
            _projectionQuery = new QueryParameters(projectionLayerMask, false, QueryTriggerInteraction.Ignore, false);
            _blockerQuery = new QueryParameters(blockerLayerMask, false, QueryTriggerInteraction.Ignore, false);
            _projectionCommands = new NativeArray<RaycastCommand>(maxPoints, allocator, NativeArrayOptions.UninitializedMemory);
            _projectionHits = new NativeArray<RaycastHit>(maxPoints, allocator, NativeArrayOptions.UninitializedMemory);
            _blockerCommands = new NativeArray<SpherecastCommand>(maxPoints - 1, allocator, NativeArrayOptions.UninitializedMemory);
            _blockerHits = new NativeArray<RaycastHit>(maxPoints - 1, allocator, NativeArrayOptions.UninitializedMemory);
            _pathDistances = new NativeArray<float>(maxPoints, allocator, NativeArrayOptions.UninitializedMemory);
            _pathTangents = new NativeArray<float3>(maxPoints, allocator, NativeArrayOptions.UninitializedMemory);
        }

        public int PointCount => _pointCount;
        public int FirstBlockedSegment => _firstBlockedSegment;
        public bool IsBlocked => _firstBlockedSegment >= 0;
        public float TotalLength => _totalLength;
        public float ReachableDistance => _reachableDistance;
        public float3 ImpactCenter => _impactCenter;
        public float3 SurfaceImpactPoint => _surfaceImpactPoint;
        public Collider FirstBlocker => _firstBlocker;
        public NativeArray<float> PathDistances => _pathDistances;
        public NativeArray<float3> PathTangents => _pathTangents;

        /// <summary>Schedules vertical terrain raycasts after the geometry job has finished.</summary>
        public JobHandle ScheduleValidation(TrajectoryGeometryBuffer geometry, int pointCount, JobHandle dependency = default)
        {
            CompletePending();
            if (geometry == null) throw new ArgumentNullException(nameof(geometry));
            if (pointCount < 2 || pointCount > _projectionCommands.Length || pointCount > geometry.Positions.Length)
                throw new ArgumentOutOfRangeException(nameof(pointCount));

            _pathPositions = geometry.Positions;
            _pointCount = pointCount;
            _firstBlockedSegment = -1;
            _firstBlocker = null;
            _totalLength = 0f;
            _reachableDistance = 0f;
            JobHandle buildCommands = new BuildProjectionCommandsJob
            {
                Positions = _pathPositions,
                Commands = _projectionCommands,
                PointCount = pointCount,
                Up = _projectionUp,
                CastHeight = _projectionHeight,
                CastDistance = _projectionHeight + _maximumDrop,
                Query = _projectionQuery
            }.Schedule(JobHandle.CombineDependencies(geometry.PendingHandle, dependency));
            _projectionHandle = RaycastCommand.ScheduleBatch(
                _projectionCommands.GetSubArray(0, pointCount),
                _projectionHits.GetSubArray(0, pointCount),
                _minimumCommandsPerJob,
                1,
                buildCommands);
            _projectionPending = true;
            return _projectionHandle;
        }

        /// <summary>Completes terrain projection, then schedules swept sphere casts along each projected segment.</summary>
        public JobHandle ScheduleBlockerCasts()
        {
            if (!_projectionPending) throw new InvalidOperationException("Call ScheduleValidation before scheduling blocker casts.");
            _projectionHandle.Complete();
            ApplyProjectionAndMeasurePath();

            for (int i = 0; i < _pointCount - 1; i++)
            {
                float3 delta = _pathPositions[i + 1] - _pathPositions[i];
                float length = math.length(delta);
                float3 direction = length > 1e-5f ? delta / length : _projectionUp;
                _blockerCommands[i] = new SpherecastCommand(
                    (Vector3)_pathPositions[i],
                    _blockerRadius,
                    (Vector3)direction,
                    _blockerQuery,
                    math.max(length, 1e-4f));
            }

            _blockerHandle = SpherecastCommand.ScheduleBatch(
                _blockerCommands.GetSubArray(0, _pointCount - 1),
                _blockerHits.GetSubArray(0, _pointCount - 1),
                _minimumCommandsPerJob,
                1,
                default);
            _projectionPending = false;
            _blockerPending = true;
            return _blockerHandle;
        }

        /// <summary>Completes blocker casts and records the earliest contact along the path.</summary>
        public void CompleteValidation()
        {
            if (_projectionPending) ScheduleBlockerCasts();
            if (!_blockerPending) return;
            _blockerHandle.Complete();

            _firstBlockedSegment = -1;
            _firstBlocker = null;
            _reachableDistance = _totalLength;
            for (int i = 0; i < _pointCount - 1; i++)
            {
                RaycastHit hit = _blockerHits[i];
                if (hit.collider == null) continue;

                float3 delta = _pathPositions[i + 1] - _pathPositions[i];
                float segmentLength = math.length(delta);
                float hitDistance = math.clamp(hit.distance, 0f, segmentLength);
                float3 direction = math.normalizesafe(delta, _projectionUp);
                _firstBlockedSegment = i;
                _firstBlocker = hit.collider;
                _impactCenter = _pathPositions[i] + direction * hitDistance;
                _surfaceImpactPoint = hit.point;
                _reachableDistance = _pathDistances[i] + hitDistance;
                break;
            }
            _blockerPending = false;
        }

        /// <summary>Evaluates distance along the reachable portion, clamping at an obstacle contact.</summary>
        public bool EvaluateAtDistance(float distance, out float3 position, out float3 tangent)
        {
            float clampedDistance = math.clamp(distance, 0f, _reachableDistance);
            return TrajectoryGeometryBuffer.EvaluateAtDistance(_pathPositions, _pathDistances, _pathTangents, _pointCount, clampedDistance, out position, out tangent);
        }

        /// <summary>Evaluates normalized travel over the reachable path, clamping the normalized value to 0–1.</summary>
        public bool EvaluateAtNormalizedDistance(float normalizedDistance, out float3 position, out float3 tangent)
        {
            return EvaluateAtDistance(math.saturate(normalizedDistance) * _reachableDistance, out position, out tangent);
        }

        /// <summary>Completes all outstanding physics work before buffers are reused or disposed.</summary>
        public void CompletePending()
        {
            if (_projectionPending) ScheduleBlockerCasts();
            if (_blockerPending) CompleteValidation();
        }

        public void Dispose()
        {
            CompletePending();
            if (_projectionCommands.IsCreated) _projectionCommands.Dispose();
            if (_projectionHits.IsCreated) _projectionHits.Dispose();
            if (_blockerCommands.IsCreated) _blockerCommands.Dispose();
            if (_blockerHits.IsCreated) _blockerHits.Dispose();
            if (_pathDistances.IsCreated) _pathDistances.Dispose();
            if (_pathTangents.IsCreated) _pathTangents.Dispose();
        }

        private void ApplyProjectionAndMeasurePath()
        {
            for (int i = 0; i < _pointCount; i++)
            {
                RaycastHit hit = _projectionHits[i];
                if (hit.collider != null)
                    _pathPositions[i] = (float3)hit.point + (float3)hit.normal * _surfaceOffset;
            }

            _pathDistances[0] = 0f;
            for (int i = 1; i < _pointCount; i++)
            {
                float3 delta = _pathPositions[i] - _pathPositions[i - 1];
                _pathDistances[i] = _pathDistances[i - 1] + math.length(delta);
            }
            _totalLength = _pathDistances[_pointCount - 1];

            for (int i = 0; i < _pointCount; i++)
            {
                float3 before = _pathPositions[math.max(0, i - 1)];
                float3 after = _pathPositions[math.min(_pointCount - 1, i + 1)];
                _pathTangents[i] = math.normalizesafe(after - before, new float3(0f, 0f, 1f));
            }
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict)]
    internal struct BuildProjectionCommandsJob : IJob
    {
        [ReadOnly] public NativeArray<float3> Positions;
        [WriteOnly] public NativeArray<RaycastCommand> Commands;
        public int PointCount;
        public float3 Up;
        public float CastHeight;
        public float CastDistance;
        public QueryParameters Query;

        public void Execute()
        {
            for (int i = 0; i < PointCount; i++)
            {
                Vector3 origin = (Vector3)(Positions[i] + Up * CastHeight);
                Commands[i] = new RaycastCommand(origin, (Vector3)-Up, Query, CastDistance);
            }
        }
    }
}
