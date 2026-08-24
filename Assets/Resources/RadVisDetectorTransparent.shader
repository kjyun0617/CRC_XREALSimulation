Shader "RadVis/DetectorTransparent"
{
    Properties
    {
        _Color ("Detector Line Color", Color) = (0.65, 0.65, 0.65, 0.9)
        _LongitudeLines ("Longitude Lines", Range(4, 24)) = 12
        _LatitudeLines ("Latitude Lines", Range(2, 12)) = 6
        _GridLineWidth ("Grid Line Half Width", Range(0.01, 0.12)) = 0.04
        _RimWidth ("Silhouette Rim Width", Range(0.05, 0.35)) = 0.18
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
        // XREAL optical displays can make bright RGB look opaque even with normal
        // alpha blending. Only the globe lines survive clip(), so every space
        // between them remains a real see-through hole.
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
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                float3 worldPosition : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            fixed4 _Color;
            float _LongitudeLines;
            float _LatitudeLines;
            float _GridLineWidth;
            float _RimWidth;

            Varyings Vert(AppData input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_OUTPUT(Varyings, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.position = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                output.worldNormal = UnityObjectToWorldNormal(input.normal);
                output.worldPosition = mul(unity_ObjectToWorld, input.vertex).xyz;
                return output;
            }

            fixed4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // The built-in Unity sphere has latitude/longitude UVs. Repeating
                // narrow bands in both axes creates a clean globe instead of a
                // noisy filled surface.
                float2 gridCoordinate = input.uv * float2(_LongitudeLines, _LatitudeLines);
                float2 distanceToGridLine = abs(frac(gridCoordinate + 0.5) - 0.5);
                float2 antiAliasWidth = max(
                    fwidth(gridCoordinate),
                    float2(0.0001, 0.0001));
                float2 lineWidth = float2(_GridLineWidth, _GridLineWidth);
                float2 gridCoverage = 1.0 - smoothstep(
                    lineWidth,
                    lineWidth + antiAliasWidth,
                    distanceToGridLine);
                float globeGrid = max(gridCoverage.x, gridCoverage.y);

                // Add a camera-facing silhouette ring so the sphere's diameter is
                // always legible even between sparse grid lines.
                float3 normal = normalize(input.worldNormal);
                float3 viewDirection = normalize(UnityWorldSpaceViewDir(input.worldPosition));
                float normalFacing = abs(dot(normal, viewDirection));
                float rimAntiAlias = max(fwidth(normalFacing), 0.0001);
                float silhouette = 1.0 - smoothstep(
                    _RimWidth,
                    _RimWidth + rimAntiAlias,
                    normalFacing);

                float coverage = saturate(max(globeGrid, silhouette));
                clip(coverage - 0.02);

                // Blend One/Zero is intentional for XREAL. Brightness-based edge
                // smoothing avoids the former random screen-door texture.
                float brightness = saturate(_Color.a) * coverage;
                return fixed4(_Color.rgb * brightness, 1.0);
            }
            ENDCG
        }
    }

    FallBack Off
}
