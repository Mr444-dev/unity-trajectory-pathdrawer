using UnityEngine;

namespace PathDrawer.Runtime
{
    /// <summary>Shared height function for the demo mesh, showcase route and obstacle placement.</summary>
    public static class PathDrawerTerrainProfile
    {
        public const float HalfExtent = 12f;
        public const float MaximumHeight = 1.65f;

        public static float EvaluateHeight(float x, float z)
        {
            const float inverseSqrtTwo = 0.70710678118f;
            const float ridgeWidth = 3.2f;
            const float ridgeLength = 9f;
            float along = (x + z) * inverseSqrtTwo;
            float across = (x - z) * inverseSqrtTwo;
            float ridge = Mathf.Exp(-0.5f * across * across / (ridgeWidth * ridgeWidth));
            float lengthEnvelope = Mathf.Exp(-0.5f * along * along / (ridgeLength * ridgeLength));
            float edgeX = SmoothEdge((HalfExtent - Mathf.Abs(x)) / 4f);
            float edgeZ = SmoothEdge((HalfExtent - Mathf.Abs(z)) / 4f);
            return MaximumHeight * ridge * lengthEnvelope * edgeX * edgeZ;
        }

        private static float SmoothEdge(float value)
        {
            float t = Mathf.Clamp01(value);
            return t * t * (3f - 2f * t);
        }
    }
}
