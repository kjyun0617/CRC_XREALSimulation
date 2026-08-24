Shader "RadVis/DetectorTransparent"
{
    Properties
    {
        _Color ("Detector Contour Color", Color) = (0.65, 0.65, 0.65, 0.9)
        _SurfaceBrightness ("Surface Fill Brightness", Range(0.02, 0.35)) = 0.12
        _ContourBands ("Iso-response Contour Bands", Range(2, 6)) = 3
        _ContourLineWidth ("Contour Line Half Width", Range(0.003, 0.05)) = 0.012
        _RimWidth ("Silhouette Line Width", Range(0.01, 0.15)) = 0.045
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        LOD 100
        Cull Back
        ZWrite Off
        ZTest LEqual
        // XREAL optical displays express transparency most reliably through
        // emitted brightness. Keep explicit replacement blending, then draw the
        // shell dimly and the measurement contours brightly in the detector color.
        Blend One Zero

        Pass
        {
            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"

            struct AppData
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 position : SV_POSITION;
                float3 worldNormal : TEXCOORD0;
                float3 worldPosition : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            fixed4 _Color;
            float _SurfaceBrightness;
            float _ContourBands;
            float _ContourLineWidth;
            float _RimWidth;

            float PeriodicLineCoverage(float coordinate, float halfWidth)
            {
                float distanceToLine = abs(frac(coordinate + 0.5) - 0.5);
                float antiAliasWidth = max(fwidth(coordinate) * 0.75, 0.0001);
                return 1.0 - smoothstep(
                    max(halfWidth - antiAliasWidth, 0.0),
                    halfWidth + antiAliasWidth,
                    distanceToLine);
            }

            Varyings Vert(AppData input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_OUTPUT(Varyings, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.position = UnityObjectToClipPos(input.vertex);
                output.worldNormal = UnityObjectToWorldNormal(input.normal);
                output.worldPosition = mul(unity_ObjectToWorld, input.vertex).xyz;
                return output;
            }

            fixed4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 normal = normalize(input.worldNormal);
                float3 viewDirection = normalize(UnityWorldSpaceViewDir(input.worldPosition));

                // The response coordinate depends only on N dot V. It is the
                // projected radial distance from the visible sphere center, so
                // no object, anchor, plane or world axis can rotate this pattern.
                float normalFacing = saturate(abs(dot(normal, viewDirection)));
                float projectedRadius = sqrt(saturate(1.0 - normalFacing * normalFacing));

                // Draw only the interior integer contours. For N bands their
                // radii are 1/(N+1) ... N/(N+1); the center and silhouette are
                // deliberately excluded to avoid a crosshair/target appearance.
                float contourBands = max(round(_ContourBands), 1.0);
                float contourCoordinate = projectedRadius * (contourBands + 1.0);
                float isoResponseContours = PeriodicLineCoverage(
                    contourCoordinate,
                    _ContourLineWidth);
                float contourInteriorMask =
                    smoothstep(0.55, 0.8, contourCoordinate) *
                    (1.0 - smoothstep(
                        contourBands + 0.2,
                        contourBands + 0.45,
                        contourCoordinate));
                isoResponseContours *= contourInteriorMask;

                // A separate thin silhouette keeps the measured volume legible.
                float rimAntiAlias = max(fwidth(normalFacing), 0.0001);
                float silhouette = 1.0 - smoothstep(
                    max(_RimWidth - rimAntiAlias, 0.0),
                    _RimWidth + rimAntiAlias,
                    normalFacing);

                float lineCoverage = saturate(max(isoResponseContours, silhouette));

                // The subdued shell provides volume without adding a directional
                // cue. Contours and silhouette remain precise and brighter.
                float shellShape = lerp(0.68, 1.0, normalFacing);
                float surfaceBrightness = saturate(_SurfaceBrightness) * shellShape;
                float lineBrightness = max(surfaceBrightness, saturate(_Color.a));
                float brightness = lerp(surfaceBrightness, lineBrightness, lineCoverage);
                return fixed4(_Color.rgb * brightness, 1.0);
            }
            ENDCG
        }
    }

    FallBack Off
}
