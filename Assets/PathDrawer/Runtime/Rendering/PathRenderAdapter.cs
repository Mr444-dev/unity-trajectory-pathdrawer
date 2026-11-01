using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace PathDrawer.Rendering
{
    /// <summary>Pushes native path samples into zero-copy LineRenderer NativeArray slices.</summary>
    public sealed class PathRenderAdapter : IDisposable
    {
        private readonly LineRenderer _reachableLine;
        private readonly LineRenderer _blockedLine;
        private NativeArray<float3> _displayPositions;

        public PathRenderAdapter(LineRenderer reachableLine, LineRenderer blockedLine, int maxPoints, Allocator allocator = Allocator.Persistent)
        {
            if (reachableLine == null) throw new ArgumentNullException(nameof(reachableLine));
            if (maxPoints < 2) throw new ArgumentOutOfRangeException(nameof(maxPoints));
            _reachableLine = reachableLine;
            _blockedLine = blockedLine;
            _displayPositions = new NativeArray<float3>(checked(maxPoints + 1), allocator, NativeArrayOptions.UninitializedMemory);
        }

        /// <summary>Updates both path colors and inserts an exact obstacle contact into the reusable display buffer.</summary>
        public void Apply(NativeArray<float3> positions, int count, int firstBlockedSegment = -1, float3 impactCenter = default)
        {
            if (count < 0 || count > positions.Length || count >= _displayPositions.Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            for (int i = 0; i < count; i++) _displayPositions[i] = positions[i];
            int displayCount = count;
            int reachableCount = count;
            int blockedStart = -1;

            if (firstBlockedSegment >= 0 && firstBlockedSegment < count - 1 && count < _displayPositions.Length)
            {
                int insertIndex = firstBlockedSegment + 1;
                for (int i = count; i > insertIndex; i--) _displayPositions[i] = _displayPositions[i - 1];
                _displayPositions[insertIndex] = impactCenter;
                displayCount++;
                reachableCount = insertIndex + 1;
                blockedStart = insertIndex;
            }

            NativeArray<Vector3> vectorPositions = _displayPositions.Reinterpret<Vector3>();
            _reachableLine.positionCount = reachableCount;
            if (reachableCount > 0)
                _reachableLine.SetPositions(vectorPositions);

            if (_blockedLine != null)
            {
                int blockedCount = blockedStart >= 0 ? displayCount - blockedStart : 0;
                _blockedLine.positionCount = blockedCount;
                if (blockedCount > 0)
                    _blockedLine.SetPositions(new NativeSlice<Vector3>(vectorPositions, blockedStart, blockedCount));
            }
        }

        public void Dispose()
        {
            if (_displayPositions.IsCreated) _displayPositions.Dispose();
        }
    }
}
