Shader "Hidden/SegMaskDisplay"
{
    Properties
    {
        _MainTex("Mask Texture (Source)", 2D) = "white" {}
        _Threshold("Threshold", Range(0.01, 0.99)) = 0.5
        _EdgeSharpness("Edge Sharpness", Range(1.0, 20.0)) = 6.0 // Increased range for more effect
        _TintColor("Tint Color", Color) = (1, 0, 0, 0.5) // Red, 50% alpha
        _OutputOpacity("Overall Opacity", Range(0,1)) = 0.5
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Overlay+10" } // Ensure it renders over most UI

        Pass
        {
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            fixed _Threshold;
            float _EdgeSharpness;
            fixed4 _TintColor;
            fixed _OutputOpacity;

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

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed maskValue = tex2D(_MainTex, i.uv).r; // Assuming mask is in Red channel
                
                // Apply threshold and contrast/sharpness
                // This creates a sharper transition around the threshold
                fixed sharpenedMask = saturate((maskValue - _Threshold) * _EdgeSharpness + 0.5);
                
                // If you want a very hard edge (binary after sharpen), uncomment next line
                // sharpenedMask = step(_Threshold, sharpenedMask); 

                fixed4 finalColor = _TintColor;
                finalColor.a = sharpenedMask * _TintColor.a * _OutputOpacity;
                
                return finalColor;
            }
            ENDHLSL
        }
    }
    Fallback "Transparent/VertexLit"
} 