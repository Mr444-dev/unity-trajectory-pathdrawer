Shader "PathDrawer/TacticalArenaGrid"
{
    Properties
    {
        _BaseColor ("Slate Base", Color) = (0.12, 0.15, 0.19, 1)
        _PrimaryGrid ("1m Grid", Color) = (0.27, 0.31, 0.36, 1)
        _SubGrid ("Subtle Quarter Grid", Color) = (0.16, 0.19, 0.23, 1)
        _ContourColor ("Height Contours", Color) = (0.31, 0.38, 0.43, 1)
        _Metallic ("Metallic", Range(0,1)) = 0.05
        _Smoothness ("Smoothness", Range(0,1)) = 0.22
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Name "SlateGridForward"
            Tags { "LightMode"="UniversalForward" }
            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _PrimaryGrid;
                float4 _SubGrid;
                float4 _ContourColor;
                float _Metallic;
                float _Smoothness;
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
                float fogFactor : TEXCOORD2;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.fogFactor = ComputeFogFactor(position.positionCS.z);
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
                float primary = max(GridLine(worldXZ.x, 0.009), GridLine(worldXZ.y, 0.009));
                float2 subCoordinate = worldXZ * 4.0;
                float secondary = max(GridLine(subCoordinate.x, 0.008), GridLine(subCoordinate.y, 0.008));
                secondary *= 1.0 - primary;

                float contourPhase = frac(input.positionWS.y * 4.0);
                float contourDistance = min(contourPhase, 1.0 - contourPhase);
                float contourWidth = 0.012 + fwidth(input.positionWS.y * 4.0) * 1.4;
                float contour = 1.0 - smoothstep(contourWidth, contourWidth * 2.3, contourDistance);
                contour *= saturate((1.0 - abs(normalWS.y)) * 2.5);

                float3 albedo = _BaseColor.rgb;
                albedo = lerp(albedo, _SubGrid.rgb, secondary * 0.28);
                albedo = lerp(albedo, _PrimaryGrid.rgb, primary * 0.42);
                albedo = lerp(albedo, _ContourColor.rgb, contour * 0.42);

                InputData lightingInput = (InputData)0;
                lightingInput.positionWS = input.positionWS;
                lightingInput.normalWS = normalWS;
                lightingInput.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                lightingInput.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                lightingInput.fogCoord = input.fogFactor;
                lightingInput.vertexLighting = VertexLighting(input.positionWS, normalWS);
                lightingInput.bakedGI = SampleSH(normalWS);
                lightingInput.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);

                SurfaceData surface = (SurfaceData)0;
                surface.albedo = albedo;
                surface.metallic = _Metallic;
                surface.specular = half3(0.04, 0.04, 0.04);
                surface.smoothness = _Smoothness;
                surface.normalTS = half3(0, 0, 1);
                surface.occlusion = 1.0;
                surface.alpha = 1.0;
                half4 color = UniversalFragmentPBR(lightingInput, surface);
                color.rgb = MixFog(color.rgb, input.fogFactor);
                return color;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
