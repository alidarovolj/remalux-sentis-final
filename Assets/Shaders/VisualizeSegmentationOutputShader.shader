Shader "Custom/VisualizeSegmentationOutput"
{
    Properties
    {
        _MainTex ("Segmentation Output Texture", 2D) = "white" {}
        _Threshold ("Wall Confidence Threshold", Range(0.0, 1.0)) = 0.15
        _WallClassChannel ("Wall Class Channel (0=R, 1=G, 2=B, 3=A)", Range(0,3)) = 0 
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
            float _Threshold;
            float _WallClassChannel; // Используем float, чтобы легко сравнивать

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 texColor = tex2D(_MainTex, i.uv);
                float channelValue;

                // Выбираем нужный канал на основе _WallClassChannel
                // 0 = Red, 1 = Green, 2 = Blue, 3 = Alpha
                if (_WallClassChannel < 0.5) // Приблизительно 0
                {
                    channelValue = texColor.r;
                }
                else if (_WallClassChannel < 1.5) // Приблизительно 1
                {
                    channelValue = texColor.g;
                }
                else if (_WallClassChannel < 2.5) // Приблизительно 2
                {
                    channelValue = texColor.b;
                }
                else // Приблизительно 3
                {
                    channelValue = texColor.a;
                }

                // Если значение в канале выше порога, считаем это стеной (белый цвет)
                // Иначе - не стена (черный цвет)
                if (channelValue > _Threshold)
                {
                    return fixed4(1.0, 1.0, 1.0, 1.0); // Белый (стена)
                }
                else
                {
                    return fixed4(0.0, 0.0, 0.0, 1.0); // Черный (не стена)
                }
            }
            ENDCG
        }
    }
} 