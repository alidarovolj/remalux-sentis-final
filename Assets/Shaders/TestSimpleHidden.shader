Shader "Hidden/MyTestSimpleHiddenShader"
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
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            // Используем стандартную структуру appdata_img или appdata_base, если appdata_full не нужна
            // или определяем свою четко
            struct appdata_custom // Переименовали, чтобы избежать конфликтов
            {
                float4 vertex : POSITION;
                float2 texcoord : TEXCOORD0; // Стандартное имя для УФ в appdata структурах UnityCG
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;

            v2f vert (appdata_custom v) // Используем appdata_custom
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord; // Используем texcoord, как определено в appdata_custom
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv);
                return col;
            }
            ENDCG
        }
    }
    Fallback Off // Добавим Fallback Off для чистоты
}