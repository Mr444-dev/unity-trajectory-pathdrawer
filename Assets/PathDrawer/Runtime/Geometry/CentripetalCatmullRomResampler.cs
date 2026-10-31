using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace OmniTwin.PathDrawer.Geometry
{
    /// <summary>Controls a drag-free ballistic trajectory sampled into the same arc-length pipeline as splines.</summary>
    public readonly struct BallisticTrajectorySettings
    {
        public readonly float3 Origin;
        public readonly float3 InitialVelocity;
        public readonly float3 Gravity;
        public readonly float Drag;
        public readonly float Duration;
        public readonly int LookupSegments;

        public BallisticTrajectorySettings(float3 origin, float3 initialVelocity, float3 gravity, float drag, float duration, int lookupSegments)
        {
            if (drag < 0f) throw new ArgumentOutOfRangeException(nameof(drag));
            if (duration <= 0f) throw new ArgumentOutOfRangeException(nameof(duration));
            if (lookupSegments < 2) throw new ArgumentOutOfRangeException(nameof(lookupSegments));
            Origin = origin;
            InitialVelocity = initialVelocity;
            Gravity = gravity;
            Drag = drag;
            Duration = duration;
            LookupSegments = lookupSegments;
        }
    }

    /// <summary>Owns preallocated spline lookup, uniform-distance output, tangent, and length buffers.</summary>
    public sealed class TrajectoryGeometryBuffer : IDisposable
    {
        private readonly NativeArray<float3> _lookupPositions;
        private readonly NativeArray<float> _cumulativeLengths;
        private readonly NativeArray<float3> _positions;
        private readonly NativeArray<float> _sampleDistances;
        private readonly NativeArray<float3> _tangents;
        private readonly NativeArray<int> _resultCount;
        private readonly NativeArray<float> _totalLength;
        private JobHandle _pending;

        public TrajectoryGeometryBuffer(int maxLookupSamples, int maxOutputPoints, Allocator allocator = Allocator.Persistent)
        {
            if (maxLookupSamples < 2) throw new ArgumentOutOfRangeException(nameof(maxLookupSamples));
            if (maxOutputPoints < 2) throw new ArgumentOutOfRangeException(nameof(maxOutputPoints));
            _lookupPositions = new NativeArray<float3>(maxLookupSamples, allocator, NativeArrayOptions.UninitializedMemory);
            _cumulativeLengths = new NativeArray<float>(maxLookupSamples, allocator, NativeArrayOptions.UninitializedMemory);
            _positions = new NativeArray<float3>(maxOutputPoints, allocator, NativeArrayOptions.UninitializedMemory);
            _sampleDistances = new NativeArray<float>(maxOutputPoints, allocator, NativeArrayOptions.UninitializedMemory);
            _tangents = new NativeArray<float3>(maxOutputPoints, allocator, NativeArrayOptions.UninitializedMemory);
            _resultCount = new NativeArray<int>(1, allocator, NativeArrayOptions.UninitializedMemory);
            _totalLength = new NativeArray<float>(1, allocator, NativeArrayOptions.UninitializedMemory);
        }

        /// <summary>Resampled world positions. Complete the returned handle before reading.</summary>
        public NativeArray<float3> Positions => _positions;
        /// <summary>Accumulated source arc length at each output position.</summary>
        public NativeArray<float> SampleDistances => _sampleDistances;
        /// <summary>Normalized path tangents at each output position.</summary>
        public NativeArray<float3> Tangents => _tangents;
        /// <summary>Handle for the latest lookup and resampling chain.</summary>
        public JobHandle PendingHandle => _pending;
        /// <summary>Number of valid output points after the scheduled work completes.</summary>
        public int Count => _resultCount[0];
        /// <summary>Total measured length of the source curve.</summary>
        public float TotalLength => _totalLength[0];
        public int PositionCapacity => _positions.Length;
        public int LookupCapacity => _lookupPositions.Length;

        /// <summary>Schedules a Burst centripetal spline lookup followed by uniform chord-distance sampling.</summary>
        public JobHandle RebuildSpline(
            NativeArray<float3> controlPoints,
            int controlPointCount,
            int lookupSamplesPerSegment,
            float outputSpacing,
            JobHandle dependency = default)
        {
            Complete();
            if (controlPointCount < 2 || controlPointCount > controlPoints.Length) throw new ArgumentOutOfRangeException(nameof(controlPointCount));
            if (lookupSamplesPerSegment < 1) throw new ArgumentOutOfRangeException(nameof(lookupSamplesPerSegment));
            if (outputSpacing <= 0f) throw new ArgumentOutOfRangeException(nameof(outputSpacing));
            long requiredLookupCount = (long)(controlPointCount - 1) * lookupSamplesPerSegment + 1;
            if (requiredLookupCount > _lookupPositions.Length) throw new ArgumentException("The lookup buffer is too small for this control path and sampling rate.");

            int lookupCount = (int)requiredLookupCount;
            JobHandle lookup = new CentripetalSplineLookupJob
            {
                ControlPoints = controlPoints,
                ControlPointCount = controlPointCount,
                SamplesPerSegment = lookupSamplesPerSegment,
                LookupCount = lookupCount,
                LookupPositions = _lookupPositions,
                CumulativeLengths = _cumulativeLengths
            }.Schedule(dependency);
            _pending = ScheduleUniformOutput(lookup, lookupCount, outputSpacing);
            return _pending;
        }

        /// <summary>Schedules a ballistic lookup and the same uniform chord-distance output resampler.</summary>
        public JobHandle RebuildBallistic(BallisticTrajectorySettings settings, float outputSpacing, JobHandle dependency = default)
        {
            Complete();
            if (outputSpacing <= 0f) throw new ArgumentOutOfRangeException(nameof(outputSpacing));
            int lookupCount = settings.LookupSegments + 1;
            if (lookupCount > _lookupPositions.Length) throw new ArgumentException("The lookup buffer is too small for this ballistic sampling rate.");

            JobHandle lookup = new BallisticLookupJob
            {
                Settings = settings,
                LookupCount = lookupCount,
                LookupPositions = _lookupPositions,
                CumulativeLengths = _cumulativeLengths
            }.Schedule(dependency);
            _pending = ScheduleUniformOutput(lookup, lookupCount, outputSpacing);
            return _pending;
        }

        /// <summary>Completes pending geometry work before its buffers are read or reused.</summary>
        public void Complete() => _pending.Complete();

        /// <summary>Interpolates the resampled path by travelled world distance.</summary>
        public bool EvaluateAtDistance(float distance, out float3 position, out float3 tangent)
        {
            return EvaluateAtDistance(_positions, _sampleDistances, _tangents, Count, distance, out position, out tangent);
        }

        /// <summary>Interpolates by normalized arc length in the inclusive range 0 to 1.</summary>
        public bool EvaluateAtNormalizedDistance(float normalizedDistance, out float3 position, out float3 tangent)
        {
            float distance = math.saturate(normalizedDistance) * TotalLength;
            return EvaluateAtDistance(distance, out position, out tangent);
        }

        /// <summary>Evaluates caller-owned resampled buffers without allocating.</summary>
        public static bool EvaluateAtDistance(
            NativeArray<float3> positions,
            NativeArray<float> sampleDistances,
            NativeArray<float3> tangents,
            int count,
            float distance,
            out float3 position,
            out float3 tangent)
        {
            if (count <= 0)
            {
                position = float3.zero;
                tangent = new float3(0f, 0f, 1f);
                return false;
            }

            if (count == 1 || distance <= sampleDistances[0])
            {
                position = positions[0];
                tangent = tangents[0];
                return true;
            }

            if (distance >= sampleDistances[count - 1])
            {
                position = positions[count - 1];
                tangent = tangents[count - 1];
                return true;
            }

            int low = 0;
            int high = count - 1;
            while (low + 1 < high)
            {
                int middle = (low + high) >> 1;
                if (sampleDistances[middle] < distance) low = middle;
                else high = middle;
            }

            float span = sampleDistances[high] - sampleDistances[low];
            float t = span > 1e-6f ? (distance - sampleDistances[low]) / span : 0f;
            position = math.lerp(positions[low], positions[high], t);
            tangent = math.normalizesafe(math.lerp(tangents[low], tangents[high], t), tangents[low]);
            return true;
        }

        /// <summary>Evaluates a single centripetal Catmull-Rom span; endpoints are interpolated exactly.</summary>
        public static float3 Evaluate(ReadOnlySpan<float3> controlPoints, int segmentIndex, float t)
        {
            if (controlPoints.Length == 0) return float3.zero;
            if (controlPoints.Length == 1) return controlPoints[0];
            if (segmentIndex < 0 || segmentIndex >= controlPoints.Length - 1) throw new ArgumentOutOfRangeException(nameof(segmentIndex));
            int i1 = segmentIndex;
            int i2 = segmentIndex + 1;
            if (t <= 0f) return controlPoints[i1];
            if (t >= 1f) return controlPoints[i2];

            float3 p0 = controlPoints[math.max(i1 - 1, 0)];
            float3 p1 = controlPoints[i1];
            float3 p2 = controlPoints[i2];
            float3 p3 = controlPoints[math.min(i2 + 1, controlPoints.Length - 1)];
            return EvaluateCentripetalSpan(p0, p1, p2, p3, t);
        }

        public void Dispose()
        {
            Complete();
            if (_lookupPositions.IsCreated) _lookupPositions.Dispose();
            if (_cumulativeLengths.IsCreated) _cumulativeLengths.Dispose();
            if (_positions.IsCreated) _positions.Dispose();
            if (_sampleDistances.IsCreated) _sampleDistances.Dispose();
            if (_tangents.IsCreated) _tangents.Dispose();
            if (_resultCount.IsCreated) _resultCount.Dispose();
            if (_totalLength.IsCreated) _totalLength.Dispose();
        }

        private JobHandle ScheduleUniformOutput(JobHandle dependency, int lookupCount, float outputSpacing)
        {
            return new UniformDistanceResampleJob
            {
                LookupCount = lookupCount,
                OutputSpacing = outputSpacing,
                LookupPositions = _lookupPositions,
                CumulativeLengths = _cumulativeLengths,
                OutputPositions = _positions,
                OutputDistances = _sampleDistances,
                OutputTangents = _tangents,
                OutputCount = _resultCount,
                TotalLength = _totalLength
            }.Schedule(dependency);
        }

        internal static float3 EvaluateCentripetalSpan(float3 p0, float3 p1, float3 p2, float3 p3, float t)
        {
            float t0 = 0f;
            float t1 = t0 + KnotInterval(p0, p1);
            float t2 = t1 + KnotInterval(p1, p2);
            float t3 = t2 + KnotInterval(p2, p3);
            float u = math.lerp(t1, t2, math.saturate(t));

            float3 a1 = InterpolateAt(p0, p1, t0, t1, u);
            float3 a2 = InterpolateAt(p1, p2, t1, t2, u);
            float3 a3 = InterpolateAt(p2, p3, t2, t3, u);
            float3 b1 = InterpolateAt(a1, a2, t0, t2, u);
            float3 b2 = InterpolateAt(a2, a3, t1, t3, u);
            return InterpolateAt(b1, b2, t1, t2, u);
        }

        private static float KnotInterval(float3 a, float3 b)
        {
            return math.max(math.sqrt(math.max(math.distance(a, b), 1e-6f)), 1e-3f);
        }

        private static float3 InterpolateAt(float3 a, float3 b, float ta, float tb, float u)
        {
            float inverseSpan = 1f / math.max(tb - ta, 1e-6f);
            return a * ((tb - u) * inverseSpan) + b * ((u - ta) * inverseSpan);
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict)]
    internal struct CentripetalSplineLookupJob : IJob
    {
        [ReadOnly] public NativeArray<float3> ControlPoints;
        public int ControlPointCount;
        public int SamplesPerSegment;
        public int LookupCount;
        [WriteOnly] public NativeArray<float3> LookupPositions;
        [WriteOnly] public NativeArray<float> CumulativeLengths;

        public void Execute()
        {
            LookupPositions[0] = ControlPoints[0];
            CumulativeLengths[0] = 0f;
            for (int sample = 1; sample < LookupCount; sample++)
            {
                int segment = (sample - 1) / SamplesPerSegment;
                int localStep = (sample - 1) % SamplesPerSegment + 1;
                float t = (float)localStep / SamplesPerSegment;
                float3 p0 = ControlPoints[math.max(segment - 1, 0)];
                float3 p1 = ControlPoints[segment];
                float3 p2 = ControlPoints[segment + 1];
                float3 p3 = ControlPoints[math.min(segment + 2, ControlPointCount - 1)];
                float3 current = TrajectoryGeometryBuffer.EvaluateCentripetalSpan(p0, p1, p2, p3, t);
                float3 previous = LookupPositions[sample - 1];
                LookupPositions[sample] = current;
                CumulativeLengths[sample] = CumulativeLengths[sample - 1] + math.distance(previous, current);
            }
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict)]
    internal struct BallisticLookupJob : IJob
    {
        public BallisticTrajectorySettings Settings;
        public int LookupCount;
        [WriteOnly] public NativeArray<float3> LookupPositions;
        [WriteOnly] public NativeArray<float> CumulativeLengths;

        public void Execute()
        {
            LookupPositions[0] = BallisticPosition(0f);
            CumulativeLengths[0] = 0f;
            for (int i = 1; i < LookupCount; i++)
            {
                float time = Settings.Duration * i / (LookupCount - 1);
                float3 point = BallisticPosition(time);
                LookupPositions[i] = point;
                CumulativeLengths[i] = CumulativeLengths[i - 1] + math.distance(LookupPositions[i - 1], point);
            }
        }

        private float3 BallisticPosition(float time)
        {
            float drag = Settings.Drag;
            if (drag <= 1e-5f)
                return Settings.Origin + Settings.InitialVelocity * time + 0.5f * Settings.Gravity * time * time;

            float decay = math.exp(-drag * time);
            float velocityIntegral = (1f - decay) / drag;
            float gravityIntegral = time / drag - velocityIntegral / drag;
            return Settings.Origin + Settings.InitialVelocity * velocityIntegral + Settings.Gravity * gravityIntegral;
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict)]
    internal struct UniformDistanceResampleJob : IJob
    {
        public int LookupCount;
        public float OutputSpacing;
        [ReadOnly] public NativeArray<float3> LookupPositions;
        [ReadOnly] public NativeArray<float> CumulativeLengths;
        [WriteOnly] public NativeArray<float3> OutputPositions;
        [WriteOnly] public NativeArray<float> OutputDistances;
        [WriteOnly] public NativeArray<float3> OutputTangents;
        [WriteOnly] public NativeArray<int> OutputCount;
        [WriteOnly] public NativeArray<float> TotalLength;

        public void Execute()
        {
            OutputCount[0] = 0;
            float pathLength = CumulativeLengths[LookupCount - 1];
            TotalLength[0] = pathLength;
            if (LookupCount <= 0) return;

            int count = 1;
            OutputPositions[0] = LookupPositions[0];
            OutputDistances[0] = 0f;
            OutputTangents[0] = TangentAtArc(0f, pathLength);
            if (LookupCount == 1 || pathLength <= 1e-6f)
            {
                OutputCount[0] = count;
                return;
            }

            float previousArc = 0f;
            float3 previousPosition = LookupPositions[0];
            while (count < OutputPositions.Length)
            {
                int upperIndex = FindFirstLookupIndexAfter(previousArc);
                float spacingSq = OutputSpacing * OutputSpacing;
                while (upperIndex < LookupCount && math.distancesq(previousPosition, LookupPositions[upperIndex]) < spacingSq)
                    upperIndex++;
                if (upperIndex >= LookupCount) break;

                // Find the first LUT edge that reaches the requested chord length, then refine within it.
                float low = math.max(previousArc, CumulativeLengths[upperIndex - 1]);
                float high = CumulativeLengths[upperIndex];
                for (int iteration = 0; iteration < 24; iteration++)
                {
                    float middle = (low + high) * 0.5f;
                    float3 middlePoint = PositionAtArc(middle);
                    if (math.distancesq(previousPosition, middlePoint) < spacingSq) low = middle;
                    else high = middle;
                }

                float arc = (low + high) * 0.5f;
                float3 point = PositionAtArc(arc);
                OutputPositions[count] = point;
                OutputDistances[count] = arc;
                OutputTangents[count] = TangentAtArc(arc, pathLength);
                previousArc = arc;
                previousPosition = point;
                count++;
            }
            OutputCount[0] = count;
        }

        private int FindFirstLookupIndexAfter(float arc)
        {
            int low = 0;
            int high = LookupCount - 1;
            while (low < high)
            {
                int middle = (low + high) >> 1;
                if (CumulativeLengths[middle] <= arc) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        private float3 PositionAtArc(float arc)
        {
            int low = 0;
            int high = LookupCount - 1;
            while (low + 1 < high)
            {
                int middle = (low + high) >> 1;
                if (CumulativeLengths[middle] < arc) low = middle;
                else high = middle;
            }
            float segmentLength = CumulativeLengths[high] - CumulativeLengths[low];
            float t = segmentLength > 1e-6f ? (arc - CumulativeLengths[low]) / segmentLength : 0f;
            return math.lerp(LookupPositions[low], LookupPositions[high], math.saturate(t));
        }

        private float3 TangentAtArc(float arc, float pathLength)
        {
            float delta = math.max(OutputSpacing * 0.25f, 1e-3f);
            float3 before = PositionAtArc(math.max(0f, arc - delta));
            float3 after = PositionAtArc(math.min(pathLength, arc + delta));
            return math.normalizesafe(after - before, new float3(0f, 0f, 1f));
        }
    }
}
