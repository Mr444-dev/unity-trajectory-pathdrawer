Shader "PathDrawer/TrajectoryRibbonHologram"
{
    Properties
    {
        _BaseColor ("Ribbon Teal", Color) = (0.051, 0.58, 0.54, 0.82)
        _ChevronColor ("Directional Chevrons", Color) = (0.20, 0.77, 0.69, 1)
        _EdgeColor ("Thin Edge Borders", Color) = (0.025, 0.18, 0.17, 1)
        _Opacity ("Opacity", Range(0, 1)) = 0.82
        _ChevronsPerMeter ("Chevron Repeat per Meter", Float) = 0.72
        _ScrollSpeed ("Chevron Scroll Speed", Float) = 0.22
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent" }
        Pass
        {
            Name "TealDirectionalRibbon"
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _ChevronColor;
                float4 _EdgeColor;
                float _Opacity;
                float _ChevronsPerMeter;
                float _ScrollSpeed;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            float DistanceToSegment(float2 p, float2 a, float2 b)
            {
                float2 ab = b - a;
                float t = saturate(dot(p - a, ab) / max(dot(ab, ab), 1e-5));
                return length(p - (a + ab * t));
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 pattern = float2(frac(input.uv.x * max(_ChevronsPerMeter, 0.01) - _Time.y * _ScrollSpeed), input.uv.y);
                float d1 = DistanceToSegment(pattern, float2(0.18, 0.18), float2(0.74, 0.5));
                float d2 = DistanceToSegment(pattern, float2(0.74, 0.5), float2(0.18, 0.82));
                float chevron = 1.0 - smoothstep(0.025, 0.052, min(d1, d2));
                float edgeDistance = min(input.uv.y, 1.0 - input.uv.y);
                float edge = 1.0 - smoothstep(0.012, 0.04, edgeDistance);

                float3 color = lerp(_BaseColor.rgb, _ChevronColor.rgb, chevron * 0.82);
                color = lerp(color, _EdgeColor.rgb, edge * 0.9);
                float alpha = min(_Opacity, _BaseColor.a) * (0.86 + chevron * 0.12 + edge * 0.05);
                return half4(color, saturate(alpha));
            }
            ENDHLSL
        }
    }
    FallBack Off
}
