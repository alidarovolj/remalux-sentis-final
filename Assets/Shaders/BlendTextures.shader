Shader "Hidden/BlendTextures"
{
    Properties
    {
        _MainTex ("Current Texture", 2D) = "white" {}
        _BlendTex ("Previous Texture", 2D) = "white" {}
        _BlendFactor ("Blend Factor", Range(0, 1)) = 0.5
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
            sampler2D _BlendTex;
            float _BlendFactor;
            
            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }
            
            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 col1 = tex2D(_MainTex, i.uv);
                fixed4 col2 = tex2D(_BlendTex, i.uv);
                
                // Linear interpolation between current and previous textures
                return lerp(col2, col1, _BlendFactor);
            }
            ENDCG
        }
    }
} 