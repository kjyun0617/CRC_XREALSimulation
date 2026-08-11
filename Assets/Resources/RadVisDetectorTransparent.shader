Shader "RadVis/DetectorTransparent"
{
    Properties
    {
        _Color ("Detector Color", Color) = (0.65, 0.65, 0.65, 0.35)
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
        // XREAL optical displays can make bright RGB look opaque even when normal
        // alpha blending is configured. Kept pixels are opaque and the fragment
        // shader physically removes the rest, creating reliable screen-door transparency.
        Blend One Zero

        Pass
        {
            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"

            struct AppData
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 position : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            fixed4 _Color;

            Varyings Vert(AppData input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.position = UnityObjectToClipPos(input.vertex);
                return output;
            }

            fixed4 Frag(Varyings input) : SV_Target
            {
                // Stable screen-space interleaved noise. _Color.a is the fraction
                // of pixels that remain visible (0.35 = 35% visible, 65% real holes).
                float2 pixel = floor(input.position.xy);
                float dither = frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
                clip(_Color.a - dither);
                return fixed4(_Color.rgb, 1.0);
            }
            ENDCG
        }
    }

    FallBack Off
}
