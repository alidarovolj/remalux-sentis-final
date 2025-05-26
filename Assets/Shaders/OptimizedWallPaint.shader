Shader "Custom/OptimizedWallPaint"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _GridScale ("Grid Scale", Float) = 10.0
        _GridLineWidth ("Grid Line Width", Range(0.001, 0.05)) = 0.01
        _GridColor ("Grid Color", Color) = (0.2, 0.2, 0.2, 1)
        _DebugMode ("Debug Mode", Float) = 0.0
    }
    
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        LOD 100
        
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Back
        
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
                float3 normal : NORMAL;
            };
            
            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                float3 worldPos : TEXCOORD1;
                float3 normal : NORMAL;
            };
            
            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _Color;
            float _GridScale;
            float _GridLineWidth;
            float4 _GridColor;
            float _DebugMode;
            
            // Матрицы для преобразования координат
            float4x4 _WorldToCameraMatrix;
            float4x4 _CameraInverseProjection;
            
            // Параметры плоскости
            float3 _PlanePosition;
            float3 _PlaneNormal;
            
            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.normal = UnityObjectToWorldNormal(v.normal);
                return o;
            }
            
            // Функция для создания сетки в мировом пространстве
            float DrawWorldGrid(float3 worldPos, float scale, float lineWidth)
            {
                // Проецируем мировую позицию на оси X и Z для создания сетки
                float2 grid = worldPos.xz * scale;
                
                // Вычисляем расстояние до ближайшей линии сетки
                float2 gridDist = abs(frac(grid + 0.5) - 0.5);
                float minDist = min(gridDist.x, gridDist.y);
                
                // Рисуем линии сетки с заданной шириной
                return 1.0 - saturate((minDist - lineWidth) / fwidth(minDist));
            }
            
            fixed4 frag (v2f i) : SV_Target
            {
                // Основной цвет с текстурой
                fixed4 col = tex2D(_MainTex, i.uv) * _Color;
                
                if (_DebugMode > 0.5)
                {
                    // В режиме отладки рисуем сетку в мировом пространстве
                    float grid = DrawWorldGrid(i.worldPos, _GridScale, _GridLineWidth);
                    col = lerp(col, _GridColor, grid * _GridColor.a);
                    
                    // Добавляем визуализацию нормали в режиме отладки
                    col.rgb = lerp(col.rgb, abs(i.normal), 0.3);
                }
                
                return col;
            }
            ENDCG
        }
    }
    
    FallBack "Diffuse"
} 