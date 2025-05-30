Shader "Custom/TemporalBlendShader"
{
    Properties
    {
        _PrevTex ("Previous Mask", 2D) = "white" {} // Текстура с предыдущего (уже сглаженного) кадра
        _CurrTex ("Current Mask", 2D) = "white" {}  // Текстура с текущего (нового) кадра
        _BlendFactor ("Blend Factor", Range(0.0, 1.0)) = 0.5 // Коэффициент смешивания
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
            // Включаем для доступа к стандартным переменным Unity
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

            sampler2D _PrevTex;
            sampler2D _CurrTex;
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
                // Сэмплируем обе текстуры
                fixed4 prevColor = tex2D(_PrevTex, i.uv);
                fixed4 currColor = tex2D(_CurrTex, i.uv);

                // Линейно интерполируем (смешиваем) цвета
                // _BlendFactor = 0 -> полностью текущий кадр
                // _BlendFactor = 1 -> полностью предыдущий кадр
                // Обычно в скрипте вы будете передавать (1.0 - maskInterpolationSpeed) как _BlendFactor,
                // чтобы maskInterpolationSpeed = 1 означало мгновенное обновление (т.е. _BlendFactor = 0)
                // или наоборот, в зависимости от того, как вы интерпретируете _BlendFactor.
                // Данный шейдер предполагает, что _BlendFactor - это вес ПРЕДЫДУЩЕГО кадра.
                // Если ваш WallSegmentation.maskInterpolationSpeed (0.1-1.0, где 1.0 - мгновенно)
                // должен напрямую управлять весом НОВОГО кадра, то используйте:
                // fixed4 blendedColor = lerp(prevColor, currColor, _BlendFactor); // где _BlendFactor это maskInterpolationSpeed
                // Если же maskInterpolationSpeed (в скрипте) это СКОРОСТЬ обновления, то
                // lerp(предыдущий, текущий, СКОРОСТЬ)
                // В вашем WallSegmentation.cs есть maskInterpolationSpeed, где 1.0 - мгновенное, 0.1 - плавное.
                // Значит, вес нового кадра должен быть maskInterpolationSpeed.
                // blendedColor = lerp(предыдущаяМаска, новаяМаска, maskInterpolationSpeed)
                // В нашем шейдере _BlendFactor передается из скрипта.
                // WallSegmentation.cs, вероятно, делает что-то вроде:
                // temporalBlendMaterial.SetFloat("_BlendFactor", useExponentialSmoothing ? Mathf.Exp(-Time.deltaTime / maskInterpolationSpeed) : 1.0f - maskInterpolationSpeed);
                // ИЛИ temporalBlendMaterial.SetFloat("_BlendFactor", maskInterpolationSpeed); // и тогда lerp(prev, curr, factor)
                // Давайте предположим, что _BlendFactor - это вес НОВОЙ маски.
                fixed4 blendedColor = lerp(prevColor, currColor, _BlendFactor);

                return blendedColor;
            }
            ENDCG
        }
    }
}