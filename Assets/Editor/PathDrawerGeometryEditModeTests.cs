using NUnit.Framework;
using OmniTwin.PathDrawer.Tests;

namespace OmniTwin.PathDrawer.EditorTests
{
    /// <summary>NUnit EditMode entry points; this file lives in Unity's built-in Editor test assembly.</summary>
    [TestFixture]
    public sealed class PathDrawerGeometryEditModeTests
    {
        [Test]
        public void RdpReducesAndPreservesEndpoints() => PathDrawerGeometryTestCases.RdpReducesAndPreservesEndpoints();

        [Test]
        public void CentripetalSplineInterpolatesControlsWithoutCusps() => PathDrawerGeometryTestCases.CentripetalSplineInterpolatesControlsWithoutCusps();

        [Test]
        public void ArcLengthResamplingIsUniformOnUnevenControls() => PathDrawerGeometryTestCases.ArcLengthResamplingIsUniformOnUnevenControls();

        [Test]
        public void RebuildsAllocateNoManagedMemoryAfterWarmup() => PathDrawerGeometryTestCases.RebuildsAllocateNoManagedMemoryAfterWarmup();

        [Test]
        public void BallisticLookupCountOverflowIsRejectedBeforeScheduling() => PathDrawerGeometryTestCases.BallisticLookupCountOverflowIsRejectedBeforeScheduling();

        [Test]
        public void DistanceEvaluationRejectsCountsBeyondAnyNativeBuffer() => PathDrawerGeometryTestCases.DistanceEvaluationRejectsCountsBeyondAnyNativeBuffer();
    }
}
