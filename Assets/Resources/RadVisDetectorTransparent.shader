Shader "RadVis/DetectorTransparent"
{
    Properties
    {
        _Color ("Detector Line Color", Color) = (0.65, 0.65, 0.65, 0.9)
        _SurfaceBrightness ("Surface Fill Brightness", Range(0.02, 0.45)) = 0.16
        _LongitudeLines ("Longitude Lines", Range(4, 24)) = 12
        _LatitudeLines ("Latitude Lines", Range(2, 12)) = 6
        _GridLineWidth ("Grid Line Half Width", Range(0.005, 0.08)) = 0.018
        _RimWidth ("Silhouette Rim Width", Range(0.02, 0.25)) = 0.08
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
        // shell dimly and the guide lines brightly in the same detector color.
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
            float _LongitudeLines;
            float _LatitudeLines;
            float _GridLineWidth;
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

                // Generate the globe in camera space instead of mesh UV space.
                // A plane anchor may rotate the marker object, but a sphere's
                // camera-space normal field stays identical to the wearer. This
                // prevents horizontal/vertical placement from rotating the grid.
                float3 viewNormal = normalize(mul((float3x3)UNITY_MATRIX_V, normal));
                float longitudeAngle = atan2(viewNormal.x, viewNormal.z);
                float latitudeAngle = asin(clamp(viewNormal.y, -1.0, 1.0));
                float longitudeCoordinate =
                    longitudeAngle * (_LongitudeLines / (2.0 * UNITY_PI));
                float latitudeCoordinate =
                    (latitudeAngle / UNITY_PI + 0.5) * _LatitudeLines;

                float longitudeGrid = PeriodicLineCoverage(
                    longitudeCoordinate,
                    _GridLineWidth);
                float latitudeGrid = PeriodicLineCoverage(
                    latitudeCoordinate,
                    _GridLineWidth);
                float globeGrid = max(longitudeGrid, latitudeGrid);

                // Add a camera-facing silhouette ring so the sphere's diameter is
                // always legible even between sparse grid lines.
                float normalFacing = abs(dot(normal, viewDirection));
                float rimAntiAlias = max(fwidth(normalFacing), 0.0001);
                float silhouette = 1.0 - smoothstep(
                    max(_RimWidth - rimAntiAlias, 0.0),
                    _RimWidth + rimAntiAlias,
                    normalFacing);

                float lineCoverage = saturate(max(globeGrid, silhouette));

                // The whole visible shell uses a subtle fill. A small radial
                // brightness change gives it volume without introducing any
                // object- or world-axis cue, while lines stay clearly brighter.
                float shellShape = lerp(0.72, 1.0, saturate(normalFacing));
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
