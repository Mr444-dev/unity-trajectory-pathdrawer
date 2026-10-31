using System;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace OmniTwin.PathDrawer.Rendering
{
    public enum RibbonJoinMode : byte
    {
        Miter,
        Bevel
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RibbonVertex
    {
        public float3 Position;
        public float3 Normal;
        public float4 Tangent;
        public float2 Uv;
    }

    /// <summary>Builds a terrain-following ribbon into writable MeshData using a Burst job.</summary>
    public sealed class BurstRibbonMeshBuilder : IDisposable
    {
        private readonly NativeArray<VertexAttributeDescriptor> _vertexLayout;
        private readonly NativeArray<float3> _boundsMinMax;
        private Mesh.MeshDataArray _meshDataArray;
        private JobHandle _pending;
        private Mesh _targetMesh;
        private bool _hasPendingBuild;

        public BurstRibbonMeshBuilder(Allocator allocator = Allocator.Persistent)
        {
            _vertexLayout = new NativeArray<VertexAttributeDescriptor>(4, allocator, NativeArrayOptions.UninitializedMemory);
            _vertexLayout[0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0);
            _vertexLayout[1] = new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, 0);
            _vertexLayout[2] = new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4, 0);
            _vertexLayout[3] = new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, 0);
            _boundsMinMax = new NativeArray<float3>(2, allocator, NativeArrayOptions.UninitializedMemory);
        }

        /// <summary>Schedules buffer creation and Burst mesh generation; apply after useful work has run.</summary>
        public JobHandle ScheduleBuild(
            Mesh targetMesh,
            NativeArray<float3> path,
            NativeArray<float> cumulativeDistances,
            int pointCount,
            float width,
            float uvMetersPerTile = 1f,
            RibbonJoinMode joinMode = RibbonJoinMode.Miter,
            float miterLimit = 3f,
            float3 surfaceUp = default,
            JobHandle dependency = default)
        {
            if (_hasPendingBuild) throw new InvalidOperationException("Complete the previous ribbon build before scheduling another.");
            if (targetMesh == null) throw new ArgumentNullException(nameof(targetMesh));
            if (pointCount < 2 || pointCount > path.Length || pointCount > cumulativeDistances.Length)
                throw new ArgumentOutOfRangeException(nameof(pointCount));
            if (width <= 0f) throw new ArgumentOutOfRangeException(nameof(width));
            if (uvMetersPerTile <= 0f) throw new ArgumentOutOfRangeException(nameof(uvMetersPerTile));
            if (miterLimit < 1f) throw new ArgumentOutOfRangeException(nameof(miterLimit));

            if (math.lengthsq(surfaceUp) < 1e-8f) surfaceUp = new float3(0f, 1f, 0f);
            surfaceUp = math.normalizesafe(surfaceUp, new float3(0f, 1f, 0f));
            int vertexCount = checked(pointCount * 5);
            int indexCount = checked((pointCount - 1) * 6 + math.max(0, pointCount - 2) * 6);

            _targetMesh = targetMesh;
            _meshDataArray = Mesh.AllocateWritableMeshData(1);
            Mesh.MeshData meshData = _meshDataArray[0];
            meshData.SetVertexBufferParams(vertexCount, _vertexLayout);
            meshData.SetIndexBufferParams(indexCount, IndexFormat.UInt32);

            NativeArray<RibbonVertex> vertices = meshData.GetVertexData<RibbonVertex>(0);
            NativeArray<uint> indices = meshData.GetIndexData<uint>();
            _pending = new BuildRibbonJob
            {
                Path = path,
                CumulativeDistances = cumulativeDistances,
                PointCount = pointCount,
                HalfWidth = width * 0.5f,
                MiterLimit = miterLimit,
                UvMetersPerTile = uvMetersPerTile,
                JoinMode = joinMode,
                SurfaceUp = surfaceUp,
                Vertices = vertices,
                Indices = indices,
                BoundsMinMax = _boundsMinMax
            }.Schedule(dependency);
            _hasPendingBuild = true;
            return _pending;
        }

        /// <summary>Completes the Burst job and applies the writable mesh buffers to the target mesh.</summary>
        public void CompleteAndApply()
        {
            if (!_hasPendingBuild) return;
            _pending.Complete();
            Mesh.MeshData meshData = _meshDataArray[0];
            float3 minimum = _boundsMinMax[0];
            float3 maximum = _boundsMinMax[1];
            var bounds = new Bounds((Vector3)((minimum + maximum) * 0.5f), (Vector3)(maximum - minimum));
            meshData.subMeshCount = 1;
            var descriptor = new SubMeshDescriptor(0, meshData.GetIndexData<uint>().Length, MeshTopology.Triangles)
            {
                vertexCount = meshData.vertexCount,
                bounds = bounds
            };
            meshData.SetSubMesh(0, descriptor, MeshUpdateFlags.DontRecalculateBounds);
            _targetMesh.bounds = bounds;
            Mesh.ApplyAndDisposeWritableMeshData(_meshDataArray, _targetMesh, MeshUpdateFlags.DontRecalculateBounds);
            _hasPendingBuild = false;
            _targetMesh = null;
        }

        /// <summary>Builds and applies immediately when the caller has no work to overlap.</summary>
        public void Build(
            Mesh targetMesh,
            NativeArray<float3> path,
            NativeArray<float> cumulativeDistances,
            int pointCount,
            float width,
            float uvMetersPerTile = 1f,
            RibbonJoinMode joinMode = RibbonJoinMode.Miter,
            float miterLimit = 3f,
            float3 surfaceUp = default,
            JobHandle dependency = default)
        {
            ScheduleBuild(targetMesh, path, cumulativeDistances, pointCount, width, uvMetersPerTile, joinMode, miterLimit, surfaceUp, dependency);
            CompleteAndApply();
        }

        public void Dispose()
        {
            if (_hasPendingBuild)
            {
                _pending.Complete();
                _meshDataArray.Dispose();
                _hasPendingBuild = false;
            }
            if (_vertexLayout.IsCreated) _vertexLayout.Dispose();
            if (_boundsMinMax.IsCreated) _boundsMinMax.Dispose();
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict)]
    internal struct BuildRibbonJob : IJob
    {
        [ReadOnly] public NativeArray<float3> Path;
        [ReadOnly] public NativeArray<float> CumulativeDistances;
        public int PointCount;
        public float HalfWidth;
        public float MiterLimit;
        public float UvMetersPerTile;
        public RibbonJoinMode JoinMode;
        public float3 SurfaceUp;
        [WriteOnly] public NativeArray<RibbonVertex> Vertices;
        [WriteOnly] public NativeArray<uint> Indices;
        [WriteOnly] public NativeArray<float3> BoundsMinMax;

        public void Execute()
        {
            float3 minimum = new float3(float.MaxValue);
            float3 maximum = new float3(float.MinValue);

            for (int i = 0; i < PointCount; i++)
            {
                float3 center = Path[i];
                float3 previousDirection = i > 0
                    ? math.normalizesafe(Path[i] - Path[i - 1], new float3(0f, 0f, 1f))
                    : math.normalizesafe(Path[1] - Path[0], new float3(0f, 0f, 1f));
                float3 nextDirection = i < PointCount - 1
                    ? math.normalizesafe(Path[i + 1] - Path[i], previousDirection)
                    : previousDirection;
                float3 previousSide = math.normalizesafe(math.cross(SurfaceUp, previousDirection), new float3(1f, 0f, 0f));
                float3 nextSide = math.normalizesafe(math.cross(SurfaceUp, nextDirection), previousSide);
                float3 incomingOffset = previousSide * HalfWidth;
                float3 outgoingOffset = nextSide * HalfWidth;

                if (i > 0 && i < PointCount - 1 && JoinMode == RibbonJoinMode.Miter)
                {
                    float3 miter = math.normalizesafe(previousSide + nextSide, nextSide);
                    float denominator = math.abs(math.dot(miter, nextSide));
                    float miterLength = HalfWidth / math.max(denominator, 1e-5f);
                    if (miterLength <= HalfWidth * MiterLimit)
                        incomingOffset = outgoingOffset = miter * miterLength;
                }

                float3 leftIn = center - incomingOffset;
                float3 rightIn = center + incomingOffset;
                float3 leftOut = center - outgoingOffset;
                float3 rightOut = center + outgoingOffset;
                float3 tangent = math.normalizesafe(previousDirection + nextDirection, nextDirection);
                float u = CumulativeDistances[i] / UvMetersPerTile;
                int baseIndex = i * 5;
                Vertices[baseIndex] = MakeVertex(leftIn, SurfaceUp, tangent, new float2(u, 0f));
                Vertices[baseIndex + 1] = MakeVertex(rightIn, SurfaceUp, tangent, new float2(u, 1f));
                Vertices[baseIndex + 2] = MakeVertex(leftOut, SurfaceUp, tangent, new float2(u, 0f));
                Vertices[baseIndex + 3] = MakeVertex(rightOut, SurfaceUp, tangent, new float2(u, 1f));
                Vertices[baseIndex + 4] = MakeVertex(center, SurfaceUp, tangent, new float2(u, 0.5f));

                minimum = math.min(minimum, math.min(math.min(leftIn, rightIn), math.min(leftOut, rightOut)));
                maximum = math.max(maximum, math.max(math.max(leftIn, rightIn), math.max(leftOut, rightOut)));
            }

            int cursor = 0;
            for (int segment = 0; segment < PointCount - 1; segment++)
            {
                uint start = (uint)(segment * 5);
                uint end = (uint)((segment + 1) * 5);
                // Two triangles join this segment's outgoing edge to the next point's incoming edge.
                Indices[cursor++] = start + 2;
                Indices[cursor++] = end;
                Indices[cursor++] = start + 3;
                Indices[cursor++] = start + 3;
                Indices[cursor++] = end;
                Indices[cursor++] = end + 1;
            }

            for (int point = 1; point < PointCount - 1; point++)
            {
                uint baseIndex = (uint)(point * 5);
                Indices[cursor++] = baseIndex + 4;
                Indices[cursor++] = baseIndex;
                Indices[cursor++] = baseIndex + 2;
                Indices[cursor++] = baseIndex + 4;
                Indices[cursor++] = baseIndex + 1;
                Indices[cursor++] = baseIndex + 3;
            }

            BoundsMinMax[0] = minimum;
            BoundsMinMax[1] = maximum;
        }

        private static RibbonVertex MakeVertex(float3 position, float3 normal, float3 tangent, float2 uv)
        {
            return new RibbonVertex
            {
                Position = position,
                Normal = normal,
                Tangent = new float4(tangent, 1f),
                Uv = uv
            };
        }
    }
}
