Shader "RadVis/DetectorTransparent"
{
    Properties
    {
        _Color ("Detector Volume Color", Color) = (0.65, 0.65, 0.65, 0.18)
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull Mode", Float) = 2
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
        Cull [_Cull]
        ZWrite Off
        ZTest LEqual
        // Match the XREAL SDK's transparent-plane convention. On the black
        // optical render target, lower alpha becomes lower emitted brightness.
        Blend SrcAlpha OneMinusSrcAlpha

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
                float normalFacing = saturate(abs(dot(normal, viewDirection)));

                // A single soft volume with no contour, grid, or silhouette line.
                // The center is slightly denser than the edge so its size remains
                // readable while large falloff shells stay out of the user's way.
                float volumeShape = lerp(0.38, 1.0, smoothstep(0.0, 1.0, normalFacing));
                float opacity = saturate(_Color.a) * volumeShape;
                return fixed4(_Color.rgb, opacity);
            }
            ENDCG
        }
    }

    FallBack Off
}
