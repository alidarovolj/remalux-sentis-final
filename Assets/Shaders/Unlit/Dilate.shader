Shader "Unlit/Dilate"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Step ("Step Size", Float) = 1.0 // Сколько пикселей отступать для выборки соседей
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            float4 _MainTex_TexelSize; // x = 1/width, y = 1/height
            float _Step;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float maxVal = 0.0;
                float2 stepUV = _MainTex_TexelSize.xy * _Step;

                // Sample 3x3 neighborhood
                maxVal = max(maxVal, tex2D(_MainTex, i.uv + float2(-stepUV.x, -stepUV.y)).r);
                maxVal = max(maxVal, tex2D(_MainTex, i.uv + float2(0, -stepUV.y)).r);
                maxVal = max(maxVal, tex2D(_MainTex, i.uv + float2(stepUV.x, -stepUV.y)).r);

                maxVal = max(maxVal, tex2D(_MainTex, i.uv + float2(-stepUV.x, 0)).r);
                maxVal = max(maxVal, tex2D(_MainTex, i.uv).r); // center
                maxVal = max(maxVal, tex2D(_MainTex, i.uv + float2(stepUV.x, 0)).r);

                maxVal = max(maxVal, tex2D(_MainTex, i.uv + float2(-stepUV.x, stepUV.y)).r);
                maxVal = max(maxVal, tex2D(_MainTex, i.uv + float2(0, stepUV.y)).r);
                maxVal = max(maxVal, tex2D(_MainTex, i.uv + float2(stepUV.x, stepUV.y)).r);

                return fixed4(maxVal, maxVal, maxVal, 1.0);
            }
            ENDCG
        }
    }
} 