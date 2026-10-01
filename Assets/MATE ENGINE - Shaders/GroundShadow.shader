Shader "Matee/Ground Shadow"
{
    Properties
    {
        _MainTex ("Shadow mask", 2D) = "white" {}
        _Color ("Shadow tint", Color) = (0, 0, 0, 0.37254903)
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" }
        Cull Off
        ZWrite Off
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;

            struct v2f
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata_base input)
            {
                v2f output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.uv = TRANSFORM_TEX(input.texcoord, _MainTex);
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                fixed alpha = tex2D(_MainTex, input.uv).a * _Color.a;
                // Premultiplied blending preserves the mask's transparency
                // in the RGBA frames passed to the native presenter.
                return fixed4(_Color.rgb * alpha, alpha);
            }
            ENDCG
        }
    }
    Fallback Off
}
