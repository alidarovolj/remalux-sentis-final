Shader "Custom/ThresholdMask"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Threshold ("Threshold", Range(0.01, 0.99)) = 0.5
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "DisableBatching"="True" }
        LOD 100

        Pass
        {
            ZTest Always Cull Off ZWrite Off

            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            fixed _Threshold;

            fixed4 frag(v2f_img i) : SV_Target
            {
                fixed maskValue = tex2D(_MainTex, i.uv).r; // Assuming R channel contains the mask
                fixed binaryValue = step(_Threshold, maskValue);
                return fixed4(binaryValue, binaryValue, binaryValue, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
} 