using System;
using Unity.Collections;
using Unity.Mathematics;

namespace OmniTwin.PathDrawer.Input
{
    /// <summary>Captures pointer samples and reduces jittery paths with iterative RDP.</summary>
    public sealed class PathInputDecimator : IDisposable
    {
        private readonly NativeList<float3> _samples;
        private readonly NativeArray<int> _stack;
        private readonly NativeArray<byte> _keepMask;
        private readonly float _minimumDistance;
        private readonly float _turnAngleRadians;
        private readonly float _turnHysteresisRadians;
        private readonly float _turnDistanceFraction;
        private bool _turnLatched;

        public PathInputDecimator(
            int capacity,
            float minimumDistance = 0.05f,
            float turnAngleRadians = 0.55f,
            float turnHysteresisRadians = 0.08f,
            float turnDistanceFraction = 0.5f,
            Allocator allocator = Allocator.Persistent)
        {
            if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (minimumDistance < 0f) throw new ArgumentOutOfRangeException(nameof(minimumDistance));
            if (turnAngleRadians < 0f || turnAngleRadians > math.PI) throw new ArgumentOutOfRangeException(nameof(turnAngleRadians));
            if (turnHysteresisRadians < 0f || turnHysteresisRadians > turnAngleRadians) throw new ArgumentOutOfRangeException(nameof(turnHysteresisRadians));
            if (turnDistanceFraction < 0f || turnDistanceFraction > 1f) throw new ArgumentOutOfRangeException(nameof(turnDistanceFraction));

            _minimumDistance = minimumDistance;
            _turnAngleRadians = turnAngleRadians;
            _turnHysteresisRadians = turnHysteresisRadians;
            _turnDistanceFraction = turnDistanceFraction;
            _samples = new NativeList<float3>(capacity, allocator);
            _stack = new NativeArray<int>(capacity * 2, allocator, NativeArrayOptions.UninitializedMemory);
            _keepMask = new NativeArray<byte>(capacity, allocator, NativeArrayOptions.UninitializedMemory);
        }

        /// <summary>Provides the captured samples without copying.</summary>
        public NativeArray<float3> Samples => _samples.AsArray();
        public int Count => _samples.Length;
        public int Capacity => _samples.Capacity;

        /// <summary>Clears the current gesture while retaining its native buffers.</summary>
        public void Clear()
        {
            _samples.Clear();
            _turnLatched = false;
        }

        /// <summary>Adds a world-space sample when distance or turn hysteresis warrants it.</summary>
        public bool TryAddSample(float3 worldPosition, bool force = false)
        {
            if (_samples.Length == 0)
            {
                if (_samples.Capacity == 0) return false;
                _samples.AddNoResize(worldPosition);
                return true;
            }

            int lastIndex = _samples.Length - 1;
            float3 offset = worldPosition - _samples[lastIndex];
            float distance = math.length(offset);
            if (distance <= 1e-6f) return false;
            if (force) return AddWithoutResize(worldPosition);

            bool turnActive = false;
            if (lastIndex > 0)
            {
                float3 previousDirection = math.normalizesafe(_samples[lastIndex] - _samples[lastIndex - 1]);
                float3 currentDirection = offset / distance;
                float angle = math.acos(math.clamp(math.dot(previousDirection, currentDirection), -1f, 1f));
                if (_turnLatched && angle < _turnAngleRadians - _turnHysteresisRadians)
                    _turnLatched = false;
                else if (!_turnLatched && angle >= _turnAngleRadians)
                    _turnLatched = true;
                turnActive = _turnLatched;
            }

            bool distancePassed = distance >= _minimumDistance;
            bool turnPassed = turnActive && distance >= _minimumDistance * _turnDistanceFraction;
            if (!distancePassed && !turnPassed) return false;
            return AddWithoutResize(worldPosition);
        }

        /// <summary>RDP-reduces the captured path into a caller-owned destination buffer.</summary>
        public int Simplify(float tolerance, NativeArray<float3> destination)
        {
            return Simplify(_samples.AsArray(), _samples.Length, destination, _stack, _keepMask, tolerance);
        }

        /// <summary>RDP-reduces a native source using caller-owned stack and mask buffers.</summary>
        public static int Simplify(
            NativeArray<float3> source,
            int sourceCount,
            NativeArray<float3> destination,
            NativeArray<int> stack,
            NativeArray<byte> keepMask,
            float tolerance)
        {
            if (sourceCount < 0 || sourceCount > source.Length) throw new ArgumentOutOfRangeException(nameof(sourceCount));
            if (tolerance < 0f) throw new ArgumentOutOfRangeException(nameof(tolerance));
            if (destination.Length < sourceCount || keepMask.Length < sourceCount || stack.Length < sourceCount * 2)
                throw new ArgumentException("RDP scratch and destination buffers must be sized for the source count.");
            if (sourceCount == 0) return 0;
            if (sourceCount == 1)
            {
                destination[0] = source[0];
                return 1;
            }

            for (int i = 0; i < sourceCount; i++) keepMask[i] = 0;
            int stackTop = 0;
            keepMask[0] = 1;
            keepMask[sourceCount - 1] = 1;
            stack[stackTop++] = 0;
            stack[stackTop++] = sourceCount - 1;
            float toleranceSq = tolerance * tolerance;

            while (stackTop > 0)
            {
                int end = stack[--stackTop];
                int start = stack[--stackTop];
                float maxDistanceSq = toleranceSq;
                int farthestIndex = -1;
                float3 a = source[start];
                float3 b = source[end];

                for (int i = start + 1; i < end; i++)
                {
                    float distanceSq = DistanceToSegmentSq(source[i], a, b);
                    if (distanceSq > maxDistanceSq)
                    {
                        maxDistanceSq = distanceSq;
                        farthestIndex = i;
                    }
                }

                if (farthestIndex < 0) continue;
                keepMask[farthestIndex] = 1;
                stack[stackTop++] = start;
                stack[stackTop++] = farthestIndex;
                stack[stackTop++] = farthestIndex;
                stack[stackTop++] = end;
            }

            int outputCount = 0;
            for (int i = 0; i < sourceCount; i++)
                if (keepMask[i] != 0) destination[outputCount++] = source[i];
            return outputCount;
        }

        /// <summary>Span-based RDP entry point for caller-owned managed or stack memory.</summary>
        public static int Simplify(
            ReadOnlySpan<float3> source,
            Span<float3> destination,
            Span<int> stack,
            Span<byte> keepMask,
            float tolerance)
        {
            if (tolerance < 0f) throw new ArgumentOutOfRangeException(nameof(tolerance));
            if (destination.Length < source.Length || keepMask.Length < source.Length || stack.Length < source.Length * 2)
                throw new ArgumentException("RDP scratch and destination spans must be sized for the source length.");
            if (source.Length == 0) return 0;
            if (source.Length == 1)
            {
                destination[0] = source[0];
                return 1;
            }

            keepMask[..source.Length].Clear();
            int stackTop = 0;
            keepMask[0] = 1;
            keepMask[source.Length - 1] = 1;
            stack[stackTop++] = 0;
            stack[stackTop++] = source.Length - 1;
            float toleranceSq = tolerance * tolerance;

            while (stackTop > 0)
            {
                int end = stack[--stackTop];
                int start = stack[--stackTop];
                float maxDistanceSq = toleranceSq;
                int farthestIndex = -1;
                float3 a = source[start];
                float3 b = source[end];

                for (int i = start + 1; i < end; i++)
                {
                    float distanceSq = DistanceToSegmentSq(source[i], a, b);
                    if (distanceSq > maxDistanceSq)
                    {
                        maxDistanceSq = distanceSq;
                        farthestIndex = i;
                    }
                }

                if (farthestIndex < 0) continue;
                keepMask[farthestIndex] = 1;
                stack[stackTop++] = start;
                stack[stackTop++] = farthestIndex;
                stack[stackTop++] = farthestIndex;
                stack[stackTop++] = end;
            }

            int outputCount = 0;
            for (int i = 0; i < source.Length; i++)
                if (keepMask[i] != 0) destination[outputCount++] = source[i];
            return outputCount;
        }

        public void Dispose()
        {
            if (_samples.IsCreated) _samples.Dispose();
            if (_stack.IsCreated) _stack.Dispose();
            if (_keepMask.IsCreated) _keepMask.Dispose();
        }

        private bool AddWithoutResize(float3 point)
        {
            if (_samples.Length == _samples.Capacity) return false;
            _samples.AddNoResize(point);
            return true;
        }

        private static float DistanceToSegmentSq(float3 point, float3 start, float3 end)
        {
            float3 segment = end - start;
            float lengthSq = math.lengthsq(segment);
            if (lengthSq <= 1e-12f) return math.lengthsq(point - start);
            float t = math.saturate(math.dot(point - start, segment) / lengthSq);
            return math.lengthsq(point - (start + t * segment));
        }
    }
}
