Shader "Hidden/BlendMasks"
{
    Properties
    {
        _MainTex ("Current Texture", 2D) = "white" {}
        _BlendTex ("Blend Texture", 2D) = "white" {}
        _BlendFactor ("Blend Factor", Range(0, 1)) = 0.5
        _NormalizeFactor ("Normalize Factor", Range(0, 1)) = 1.0
    }
    
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline" = "UniversalPipeline" }
        
        Pass // Pass 0: Blend two textures
        {
            Cull Off
            ZWrite Off
            ZTest Always
            
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            
            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            
            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_BlendTex);
            SAMPLER(sampler_BlendTex);
            float _BlendFactor;
            
            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }
            
            float4 frag(Varyings IN) : SV_Target
            {
                float4 mainColor = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
                float4 blendColor = SAMPLE_TEXTURE2D(_BlendTex, sampler_BlendTex, IN.uv);
                
                // Временная стабилизация - смешиваем текущую маску с предыдущей
                // Для масок сегментации важны только значения в канале R, остальные игнорируем
                return float4(
                    lerp(mainColor.r, blendColor.r, _BlendFactor),
                    mainColor.g,
                    mainColor.b,
                    mainColor.a
                );
            }
            ENDHLSL
        }
        
        Pass // Pass 1: Normalize
        {
            Cull Off
            ZWrite Off
            ZTest Always
            
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            
            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            
            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float _NormalizeFactor;
            
            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }
            
            float4 frag(Varyings IN) : SV_Target
            {
                float4 color = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
                
                // Нормализуем значения по фактору
                return float4(
                    saturate(color.r * _NormalizeFactor),
                    color.g,
                    color.b,
                    color.a
                );
            }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/Blit"
} 