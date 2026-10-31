using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using OmniTwin.PathDrawer.Geometry;
using OmniTwin.PathDrawer.Input;
using OmniTwin.PathDrawer.Rendering;
using UnityEngine;

namespace OmniTwin.PathDrawer.Tests
{
    /// <summary>Geometry and steady-state allocation regression coverage for the PathDrawer runtime assembly.</summary>
    [TestFixture]
    public sealed class PathDrawerGeometryTests
    {
        [Test]
        public void RdpReducesAndPreservesEndpoints()
        {
            const int pointCount = 101;
            using var source = new NativeArray<float3>(pointCount, Allocator.Temp);
            using var output = new NativeArray<float3>(pointCount, Allocator.Temp);
            using var stack = new NativeArray<int>(pointCount * 2, Allocator.Temp);
            using var mask = new NativeArray<byte>(pointCount, Allocator.Temp);
            NativeArray<float3> writableSource = source;
            for (int i = 0; i < pointCount; i++)
                writableSource[i] = new float3(i * 0.1f, (i % 2 == 0 ? 0.003f : -0.003f), 0f);

            int outputCount = PathInputDecimator.Simplify(source, pointCount, output, stack, mask, 0.01f);
            Require(outputCount == 2, "RDP should collapse near-collinear micro-jitter to its endpoints.");
            Require(math.all(output[0] == source[0]), "RDP must preserve the first point.");
            Require(math.all(output[outputCount - 1] == source[pointCount - 1]), "RDP must preserve the last point.");
        }

        [Test]
        public void CentripetalSplineInterpolatesControlsWithoutCusps()
        {
            Span<float3> controls = stackalloc float3[5];
            controls[0] = new float3(0f, 0f, 0f);
            controls[1] = new float3(1f, 0f, 0f);
            controls[2] = new float3(1.02f, 0f, 0.8f);
            controls[3] = new float3(0.4f, 0f, 0.95f);
            controls[4] = new float3(-0.2f, 0f, 0.7f);

            for (int segment = 0; segment < controls.Length - 1; segment++)
            {
                Require(math.all(TrajectoryGeometryBuffer.Evaluate(controls, segment, 0f) == controls[segment]), "Spline span must interpolate its first control.");
                Require(math.all(TrajectoryGeometryBuffer.Evaluate(controls, segment, 1f) == controls[segment + 1]), "Spline span must interpolate its second control.");
            }

            float3 previous = TrajectoryGeometryBuffer.Evaluate(controls, 1, 0f);
            float3 previousDirection = float3.zero;
            for (int i = 1; i <= 500; i++)
            {
                float3 current = TrajectoryGeometryBuffer.Evaluate(controls, 1, i / 500f);
                float3 delta = current - previous;
                Require(math.lengthsq(delta) > 1e-10f, "Acute-turn spline samples must not collapse to a cusp.");
                float3 direction = math.normalize(delta);
                if (i > 1)
                    Require(math.dot(previousDirection, direction) > -0.95f, "Centripetal interpolation must not reverse tangent direction into a cusp.");
                previousDirection = direction;
                previous = current;
            }
        }

        [Test]
        public void ArcLengthResamplingIsUniformOnUnevenControls()
        {
            using var controls = CreateUnevenControls();
            using var geometry = new TrajectoryGeometryBuffer((controls.Length - 1) * 96 + 1, 2048, Allocator.TempJob);
            geometry.RebuildSpline(controls, controls.Length, 96, 0.2f).Complete();
            int count = geometry.Count;
            Require(count > 5, "Resampler must produce a usable path.");
            for (int i = 1; i < count; i++)
            {
                float spacing = math.distance(geometry.Positions[i - 1], geometry.Positions[i]);
                Require(math.abs(spacing - 0.2f) < 1e-3f, "Consecutive output vertices must be spaced 0.20 m apart.");
            }
        }

        [Test]
        public void RebuildsAllocateNoManagedMemoryAfterWarmup()
        {
            using var controls = CreateUnevenControls();
            using var geometry = new TrajectoryGeometryBuffer((controls.Length - 1) * 32 + 1, 1024, Allocator.Persistent);
            geometry.RebuildSpline(controls, controls.Length, 32, 0.2f).Complete();
            geometry.RebuildSpline(controls, controls.Length, 32, 0.2f).Complete();

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                geometry.RebuildSpline(controls, controls.Length, 32, 0.2f).Complete();
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Require(allocated == 0, $"Steady-state path rebuilds allocated {allocated} managed bytes.");
        }

        [Test]
        public void FullPipelineFrameLoop_AllocatesZeroManagedBytesAfterWarmup()
        {
            const int inputCapacity = 128;
            const int lookupSamplesPerSegment = 8;
            using var input = new PathInputDecimator(inputCapacity, minimumDistance: 0.025f);
            using var controls = new NativeArray<float3>(inputCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            using var geometry = new TrajectoryGeometryBuffer(
                (inputCapacity - 1) * lookupSamplesPerSegment + 1,
                2048,
                Allocator.Persistent);
            using var ribbon = new BurstRibbonMeshBuilder(Allocator.Persistent);
            var mesh = new Mesh { name = "PathDrawer Full Pipeline Allocation Test" };
            mesh.MarkDynamic();
            long allocated;
            try
            {
                for (int warmup = 0; warmup < 6; warmup++)
                    RebuildInputGeometryAndRibbon(input, controls, geometry, ribbon, mesh, inputCapacity, lookupSamplesPerSegment);

                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int frame = 0; frame < 500; frame++)
                    RebuildInputGeometryAndRibbon(input, controls, geometry, ribbon, mesh, inputCapacity, lookupSamplesPerSegment);
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }

            Require(allocated == 0, $"The warmed input + geometry + ribbon frame loop allocated {allocated} managed bytes across 500 iterations.");
        }

        private static void RebuildInputGeometryAndRibbon(
            PathInputDecimator input,
            NativeArray<float3> controls,
            TrajectoryGeometryBuffer geometry,
            BurstRibbonMeshBuilder ribbon,
            Mesh mesh,
            int inputCapacity,
            int lookupSamplesPerSegment)
        {
            input.Clear();
            for (int i = 0; i < 64 && i < inputCapacity; i++)
            {
                float x = i * 0.18f;
                float z = math.sin(i * 0.22f) * 0.9f;
                input.TryAddSample(new float3(x, 0f, z), force: i == 63);
            }

            int controlCount = input.Simplify(0.005f, controls);
            if (controlCount < 2) throw new InvalidOperationException("The allocation probe did not produce enough control points.");

            geometry.RebuildSpline(controls, controlCount, lookupSamplesPerSegment, 0.2f).Complete();
            int pointCount = geometry.Count;
            if (pointCount < 2) throw new InvalidOperationException("The allocation probe did not produce enough output points.");
            ribbon.Build(mesh, geometry.Positions, geometry.SampleDistances, pointCount, 0.2f);
        }

        [Test]
        public void BallisticLookupCountOverflowIsRejectedBeforeScheduling()
        {
            using var geometry = new TrajectoryGeometryBuffer(128, 32, Allocator.TempJob);
            var settings = new BallisticTrajectorySettings(
                float3.zero,
                new float3(0f, 0f, 10f),
                new float3(0f, -9.81f, 0f),
                drag: 0f,
                duration: 2f,
                lookupSegments: int.MaxValue);

            bool rejected = false;
            try
            {
                geometry.RebuildBallistic(settings, 0.2f);
            }
            catch (ArgumentException)
            {
                rejected = true;
            }

            Require(rejected, "An overflowing ballistic lookup count must be rejected before a job is scheduled.");
        }

        [Test]
        public void DistanceEvaluationRejectsCountsBeyondAnyNativeBuffer()
        {
            using var shortPositions = new NativeArray<float3>(2, Allocator.Temp);
            using var fullPositions = new NativeArray<float3>(3, Allocator.Temp);
            using var shortDistances = new NativeArray<float>(2, Allocator.Temp);
            using var fullDistances = new NativeArray<float>(3, Allocator.Temp);
            using var shortTangents = new NativeArray<float3>(2, Allocator.Temp);
            using var fullTangents = new NativeArray<float3>(3, Allocator.Temp);

            Require(!TrajectoryGeometryBuffer.EvaluateAtDistance(
                    shortPositions, fullDistances, fullTangents, 3, 0.5f, out _, out _),
                "Evaluation must reject a count larger than the positions buffer.");
            Require(!TrajectoryGeometryBuffer.EvaluateAtDistance(
                    fullPositions, shortDistances, fullTangents, 3, 0.5f, out _, out _),
                "Evaluation must reject a count larger than the distances buffer.");
            Require(!TrajectoryGeometryBuffer.EvaluateAtDistance(
                    fullPositions, fullDistances, shortTangents, 3, 0.5f, out _, out _),
                "Evaluation must reject a count larger than the tangents buffer.");
        }

        private static NativeArray<float3> CreateUnevenControls()
        {
            var points = new NativeArray<float3>(7, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            points[0] = new float3(0f, 0f, 0f);
            points[1] = new float3(0.03f, 0f, 0.1f);
            points[2] = new float3(0.4f, 0f, 0.12f);
            points[3] = new float3(0.43f, 0f, 1.6f);
            points[4] = new float3(2.7f, 0f, 1.8f);
            points[5] = new float3(2.75f, 0f, 4.9f);
            points[6] = new float3(6.4f, 0f, 5.0f);
            return points;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
