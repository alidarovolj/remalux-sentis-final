Shader "Custom/ARWallPaint"
{
    Properties
    {
        _PaintColor ("Paint Color", Color) = (1,1,1,1)
        _SegmentationMask ("Segmentation Mask (R is Wall)", 2D) = "white" {}
        _BaseTex ("Base Texture (Fallback)", 2D) = "white" {} // Optional: for areas not painted
        _Transparency ("Overall Transparency", Range(0,1)) = 1.0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        LOD 100

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off // Important for transparent objects

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0

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

            sampler2D _SegmentationMask;
            float4 _SegmentationMask_ST;
            sampler2D _BaseTex;
            float4 _BaseTex_ST;
            fixed4 _PaintColor;
            float _Transparency;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _SegmentationMask); // Assuming mask and base UVs are the same
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed maskValue = tex2D(_SegmentationMask, i.uv).r; // Use Red channel for wall mask
                fixed4 baseColor = tex2D(_BaseTex, i.uv);
                
                // If maskValue is high (it's a wall), use PaintColor. Otherwise, use base texture (e.g. transparent or camera feed)
                // For simplicity, we'll make non-wall parts transparent if no good base texture provided
                
                fixed4 colorToShow = _PaintColor;
                
                // The final alpha is determined by the mask and overall transparency
                // If maskValue is 0 (not a wall), alpha becomes 0 (fully transparent)
                // If maskValue is 1 (is a wall), alpha becomes _PaintColor.a * _Transparency
                fixed finalAlpha = maskValue * _PaintColor.a * _Transparency;

                return fixed4(colorToShow.rgb, finalAlpha);
            }
            ENDCG
        }
    }
    FallBack "Mobile/VertexLit" // A fallback shader for older devices
} 