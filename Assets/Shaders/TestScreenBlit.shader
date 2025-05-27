Shader "Hidden/TestScreenBlit"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            ZTest Always Cull Off ZWrite Off

            HLSLPROGRAM
            #pragma vertex vert_img // Используем встроенный вершинный шейдер для полноэкранных эффектов
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;

            fixed4 frag (v2f_img i) : SV_Target // v2f_img из UnityCG.cginc
            {
                return tex2D(_MainTex, i.uv);
            }
            ENDHLSL
        }
    }
    Fallback Off
} 