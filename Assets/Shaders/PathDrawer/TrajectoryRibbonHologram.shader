Shader "PathDrawer/TrajectoryRibbonHologram"
{
    Properties
    {
        [HDR] _HDRColor ("Hologram Emission", Color) = (0.02, 2.2, 2.3, 1)
        _ChevronsPerMeter ("Chevron Repeat per Meter", Float) = 0.72
        _ScrollSpeed ("Chevron Scroll Speed", Float) = 0.32
        _EdgeGlow ("Edge Glow", Range(0, 2)) = 0.85
        _Opacity ("Opacity", Range(0, 1)) = 0.9
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent" }
        Pass
        {
            Name "AdditiveHologram"
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha One
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _HDRColor;
                float _ChevronsPerMeter;
                float _ScrollSpeed;
                float _EdgeGlow;
                float _Opacity;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
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
                float2 uv = input.uv;
                float2 pattern = float2(frac(uv.x * max(_ChevronsPerMeter, 0.01) - _Time.y * _ScrollSpeed), saturate(uv.y));
                float d1 = DistanceToSegment(pattern, float2(0.15, 0.16), float2(0.83, 0.50));
                float d2 = DistanceToSegment(pattern, float2(0.83, 0.50), float2(0.15, 0.84));
                float chevron = 1.0 - smoothstep(0.025, 0.055, min(d1, d2));

                float meterPhase = frac(uv.x);
                float tickDistance = min(meterPhase, 1.0 - meterPhase);
                float tick = (1.0 - smoothstep(0.012, 0.04, tickDistance)) * smoothstep(0.03, 0.18, uv.y) * (1.0 - smoothstep(0.82, 0.97, uv.y));
                float edge = 1.0 - smoothstep(0.37, 0.5, abs(uv.y - 0.5));
                float fresnel = pow(1.0 - saturate(abs(normalize(input.normalWS).y)), 2.0);
                float scan = 0.84 + 0.16 * sin((uv.x + _Time.y * 0.12) * 6.28318);
                float intensity = (0.22 + edge * _EdgeGlow + chevron * 1.2 + tick * 0.9 + fresnel * 0.3) * scan;
                return half4(_HDRColor.rgb * intensity, _Opacity * (0.55 + edge * 0.35 + chevron * 0.1));
            }
            ENDHLSL
        }
    }
    FallBack Off
}
