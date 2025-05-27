Shader "Custom/GaussianBlur3x3"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        // _TexelSize is automatically provided as _MainTex_TexelSize by Unity
        // float4 _TexelSize ("Texel Size", Vector) = (1.0,1.0,0,0); // x = 1/width, y = 1/height (No longer needed here)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "DisableBatching"="True" } // DisableBatching важно для vert_img и _MainTex_TexelSize
        LOD 100

        Pass
        {
            ZTest Always Cull Off ZWrite Off

            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc" // Contains v2f_img and vert_img

            sampler2D _MainTex;
            float4 _MainTex_TexelSize; // Automatically populated by Unity: (1/width, 1/height, width, height)

            float4 frag (v2f_img i) : SV_Target
            {
                float4 color = float4(0,0,0,0);
                // Gaussian kernel 3x3 (normalized)
                float kernel[9] = {
                    1.0/16.0, 2.0/16.0, 1.0/16.0,
                    2.0/16.0, 4.0/16.0, 2.0/16.0,
                    1.0/16.0, 2.0/16.0, 1.0/16.0
                };

                int k = 0;
                for (int y = -1; y <= 1; y++)
                {
                    for (int x = -1; x <= 1; x++)
                    {
                        // Use _MainTex_TexelSize.xy for 1/width and 1/height
                        color += tex2D(_MainTex, i.uv + float2(x * _MainTex_TexelSize.x, y * _MainTex_TexelSize.y)) * kernel[k++];
                    }
                }
                return color;
            }
            ENDHLSL
        }
    }
    Fallback Off
} 