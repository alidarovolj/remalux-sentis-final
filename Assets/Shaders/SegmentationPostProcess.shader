Shader "Hidden/SegmentationPostProcess"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _BlurSize ("Blur Size", Float) = 3.0
        _Contrast ("Contrast", Float) = 1.5
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        // 0: Pass-through
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
            float4 _MainTex_ST;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv);
                return col;
            }
            ENDCG
        }

        // 1: Gaussian Blur
        Pass
        {
            Name "GAUSSIAN_BLUR"
            
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
                float2 uv01 : TEXCOORD1;
                float2 uv02 : TEXCOORD2;
                float2 uv03 : TEXCOORD3;
                float2 uv04 : TEXCOORD4;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _MainTex_TexelSize;
            float _BlurSize;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                
                // Calculate texture coordinates for samples
                float2 texelSize = _MainTex_TexelSize.xy * _BlurSize;
                o.uv01 = o.uv + float2(-texelSize.x, -texelSize.y);
                o.uv02 = o.uv + float2(texelSize.x, -texelSize.y);
                o.uv03 = o.uv + float2(-texelSize.x, texelSize.y);
                o.uv04 = o.uv + float2(texelSize.x, texelSize.y);
                
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Sample multiple times with weights
                fixed4 col = tex2D(_MainTex, i.uv) * 0.4;
                col += tex2D(_MainTex, i.uv01) * 0.15;
                col += tex2D(_MainTex, i.uv02) * 0.15;
                col += tex2D(_MainTex, i.uv03) * 0.15;
                col += tex2D(_MainTex, i.uv04) * 0.15;
                
                return col;
            }
            ENDCG
        }

        // 2: Sharpen
        Pass
        {
            Name "SHARPEN"
            
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
                float2 uv01 : TEXCOORD1;
                float2 uv02 : TEXCOORD2;
                float2 uv03 : TEXCOORD3;
                float2 uv04 : TEXCOORD4;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _MainTex_TexelSize;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                
                // Calculate texture coordinates for neighboring pixels
                float2 texelSize = _MainTex_TexelSize.xy;
                o.uv01 = o.uv + float2(-texelSize.x, 0);
                o.uv02 = o.uv + float2(texelSize.x, 0);
                o.uv03 = o.uv + float2(0, -texelSize.y);
                o.uv04 = o.uv + float2(0, texelSize.y);
                
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Apply a simple sharpening kernel
                fixed4 center = tex2D(_MainTex, i.uv) * 5.0;
                fixed4 neighbors = tex2D(_MainTex, i.uv01) +
                                  tex2D(_MainTex, i.uv02) +
                                  tex2D(_MainTex, i.uv03) +
                                  tex2D(_MainTex, i.uv04);
                
                fixed4 col = center - neighbors;
                return saturate(col); // Clamp to [0, 1]
            }
            ENDCG
        }

        // 3: Contrast
        Pass
        {
            Name "CONTRAST"
            
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
            float4 _MainTex_ST;
            float _Contrast;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv);
                
                // Apply contrast
                col.rgb = (col.rgb - 0.5f) * _Contrast + 0.5f;
                col.rgb = saturate(col.rgb); // Clamp to [0, 1]
                
                return col;
            }
            ENDCG
        }
    }
    FallBack "Diffuse"
} 