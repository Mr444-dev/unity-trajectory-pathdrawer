Shader "PathDrawer/TacticalArenaGrid"
{
    Properties
    {
        _BaseColor ("Obsidian Base", Color) = (0.035, 0.055, 0.09, 1)
        _PrimaryGrid ("1m Grid", Color) = (0.025, 0.09, 0.17, 1)
        _SubGrid ("0.25m Grid", Color) = (0.012, 0.035, 0.065, 1)
        _ContourColor ("Elevation Contour", Color) = (0, 0.38, 0.52, 1)
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Name "TacticalGrid"
            Tags { "LightMode"="UniversalForward" }
            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _PrimaryGrid;
                float4 _SubGrid;
                float4 _ContourColor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            float GridLine(float coordinate, float width)
            {
                float cell = frac(coordinate);
                float distanceToLine = min(cell, 1.0 - cell);
                return 1.0 - smoothstep(width, width + fwidth(coordinate) * 1.4, distanceToLine);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 normalWS = normalize(input.normalWS);
                float2 worldXZ = input.positionWS.xz;
                float primary = max(GridLine(worldXZ.x, 0.012), GridLine(worldXZ.y, 0.012));
                float2 subCoordinate = worldXZ * 4.0;
                float secondary = max(GridLine(subCoordinate.x, 0.009), GridLine(subCoordinate.y, 0.009));
                secondary *= 1.0 - primary;

                float contourPhase = frac(input.positionWS.y * 3.0);
                float contourDistance = min(contourPhase, 1.0 - contourPhase);
                float contourWidth = 0.018 + fwidth(input.positionWS.y * 3.0) * 1.5;
                float contour = (1.0 - smoothstep(contourWidth, contourWidth * 2.2, contourDistance));
                float slope = saturate((1.0 - abs(normalWS.y)) * 2.4);
                contour *= slope;

                float3 base = _BaseColor.rgb * lerp(0.62, 1.05, saturate(normalWS.y * 0.5 + 0.5));
                float3 color = base;
                color = lerp(color, _SubGrid.rgb, secondary * 0.55);
                color = lerp(color, _PrimaryGrid.rgb, primary * 0.94);
                color += _ContourColor.rgb * contour * 1.35;
                float rimLight = pow(1.0 - saturate(dot(normalWS, normalize(float3(-0.35, 0.82, -0.45)))), 2.2);
                color += float3(0.01, 0.045, 0.075) * rimLight * 0.55;
                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
