using System;
using System.IO;
using System.Globalization;
using System.Text;
using Stopwatch = System.Diagnostics.Stopwatch;
using PathDrawer.Geometry;
using PathDrawer.Input;
using PathDrawer.Rendering;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace PathDrawer.Editor
{
    /// <summary>Measures the full CPU path rebuild and exports median/p95 timings as Markdown.</summary>
    public static class PathDrawerBenchmarkRunner
    {
        private const int Iterations = 30;
        private const int WarmupIterations = 3;
        private const float OutputSpacing = 0.2f;

        [MenuItem("PathDrawer/Benchmarks/Run 100, 500, 2,000 points")]
        public static void Run()
        {
            RunAndExport();
        }

        public static string RunAndExport()
        {
            var results = new BenchmarkResult[3];
            results[0] = MeasureSize(100);
            results[1] = MeasureSize(500);
            results[2] = MeasureSize(2000);

            var markdown = new StringBuilder(1024);
            markdown.AppendLine("# PathDrawer Benchmark Results");
            markdown.AppendLine();
            markdown.AppendLine($"Unity {Application.unityVersion} | {SystemInfo.processorType} | {Iterations} measured iterations after {WarmupIterations} warmups.");
            markdown.AppendLine();
            markdown.AppendLine("| Input points | RDP points | Output points | RDP median (µs) | Spline + LUT median (µs) | Ribbon median (µs) | Total median (µs) | Total P95 (µs) |");
            markdown.AppendLine("|---:|---:|---:|---:|---:|---:|---:|---:|");
            for (int i = 0; i < results.Length; i++)
            {
                BenchmarkResult result = results[i];
                markdown.AppendLine($"| {result.InputCount} | {result.SimplifiedCount} | {result.ResampledCount} | {FormatMicroseconds(result.RdpMedianMicroseconds)} | {FormatMicroseconds(result.GeometryMedianMicroseconds)} | {FormatMicroseconds(result.RibbonMedianMicroseconds)} | {FormatMicroseconds(result.TotalMedianMicroseconds)} | {FormatMicroseconds(result.TotalP95Microseconds)} |");
            }
            markdown.AppendLine();
            markdown.AppendLine("Timed scope: iterative RDP, Burst centripetal spline LUT, uniform-distance resampling, writable MeshData allocation, Burst ribbon generation, and mesh application. Scene rendering and GPU time are excluded.");

            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "PathDrawer-Benchmark-Results.md"));
            File.WriteAllText(path, markdown.ToString());
            Debug.Log($"PathDrawer benchmark written to {path}\n{markdown}");
            if (!Application.isBatchMode) EditorUtility.RevealInFinder(path);
            return markdown.ToString();
        }

        private static BenchmarkResult MeasureSize(int pointCount)
        {
            int lookupCapacity = checked((pointCount - 1) * 16 + 1);
            int outputCapacity = checked(pointCount * 2 + 16);
            using var source = new NativeArray<float3>(pointCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            using var simplified = new NativeArray<float3>(pointCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            using var stack = new NativeArray<int>(pointCount * 2, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            using var mask = new NativeArray<byte>(pointCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            using var geometry = new TrajectoryGeometryBuffer(lookupCapacity, outputCapacity, Allocator.Persistent);
            using var ribbonBuilder = new BurstRibbonMeshBuilder(Allocator.Persistent);
            var mesh = new Mesh { name = $"PathDrawer Benchmark Ribbon {pointCount}" };
            var timings = new long[Iterations];
            var rdpTimings = new long[Iterations];
            var geometryTimings = new long[Iterations];
            var ribbonTimings = new long[Iterations];
            NativeArray<float3> writableSource = source;

            for (int i = 0; i < pointCount; i++)
            {
                float t = i * 0.045f;
                writableSource[i] = new float3(i * 0.22f, math.sin(t) * 0.5f, math.cos(t * 0.63f) * 0.8f);
            }

            int simplifiedCount = 0;
            int resampledCount = 0;
            for (int iteration = -WarmupIterations; iteration < Iterations; iteration++)
            {
                long totalStart = Stopwatch.GetTimestamp();
                long stageStart = totalStart;
                simplifiedCount = PathInputDecimator.Simplify(source, pointCount, simplified, stack, mask, 0.025f);
                long afterRdp = Stopwatch.GetTimestamp();
                geometry.RebuildSpline(simplified, simplifiedCount, 16, OutputSpacing).Complete();
                resampledCount = geometry.Count;
                long afterGeometry = Stopwatch.GetTimestamp();
                if (resampledCount >= 2)
                    ribbonBuilder.Build(mesh, geometry.Positions, geometry.SampleDistances, resampledCount, 0.18f, 1f, RibbonJoinMode.Miter, 3f);
                long end = Stopwatch.GetTimestamp();
                if (iteration >= 0)
                {
                    rdpTimings[iteration] = afterRdp - stageStart;
                    geometryTimings[iteration] = afterGeometry - afterRdp;
                    ribbonTimings[iteration] = end - afterGeometry;
                    timings[iteration] = end - totalStart;
                }
            }

            Array.Sort(timings);
            Array.Sort(rdpTimings);
            Array.Sort(geometryTimings);
            Array.Sort(ribbonTimings);
            double medianTicks = (timings[(Iterations - 1) / 2] + timings[Iterations / 2]) * 0.5;
            double rdpMedianTicks = (rdpTimings[(Iterations - 1) / 2] + rdpTimings[Iterations / 2]) * 0.5;
            double geometryMedianTicks = (geometryTimings[(Iterations - 1) / 2] + geometryTimings[Iterations / 2]) * 0.5;
            double ribbonMedianTicks = (ribbonTimings[(Iterations - 1) / 2] + ribbonTimings[Iterations / 2]) * 0.5;
            int p95Index = (int)Math.Ceiling(Iterations * 0.95) - 1;
            var result = new BenchmarkResult
            {
                InputCount = pointCount,
                SimplifiedCount = simplifiedCount,
                ResampledCount = resampledCount,
                RdpMedianMicroseconds = rdpMedianTicks * 1_000_000.0 / Stopwatch.Frequency,
                GeometryMedianMicroseconds = geometryMedianTicks * 1_000_000.0 / Stopwatch.Frequency,
                RibbonMedianMicroseconds = ribbonMedianTicks * 1_000_000.0 / Stopwatch.Frequency,
                TotalMedianMicroseconds = medianTicks * 1_000_000.0 / Stopwatch.Frequency,
                TotalP95Microseconds = timings[p95Index] * 1_000_000.0 / Stopwatch.Frequency
            };
            UnityEngine.Object.DestroyImmediate(mesh);
            return result;
        }

        private static string FormatMicroseconds(double value) => value.ToString("F1", CultureInfo.InvariantCulture);

        private struct BenchmarkResult
        {
            public int InputCount;
            public int SimplifiedCount;
            public int ResampledCount;
            public double RdpMedianMicroseconds;
            public double GeometryMedianMicroseconds;
            public double RibbonMedianMicroseconds;
            public double TotalMedianMicroseconds;
            public double TotalP95Microseconds;
        }
    }
}
