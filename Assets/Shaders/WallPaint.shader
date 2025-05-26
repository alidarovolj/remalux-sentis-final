Shader "Custom/WallPaint"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _PaintColor ("Paint Color", Color) = (1,0,0,1)
        _BlendFactor ("Blend Factor", Range(0,1)) = 0.5
        _SegmentationMask ("Segmentation Mask", 2D) = "black" {}
        [Toggle(USE_MASK)] _UseMask ("Use Segmentation Mask", Float) = 1
        [Toggle(DEBUG_OVERLAY)] _DebugOverlay ("Debug Overlay", Float) = 0
        _DebugGrid ("Debug Grid Size", Range(5, 30)) = 10
        [Toggle(USE_AR_WORLD_SPACE)] _UseARSpace ("Use AR World Space", Float) = 1
        _PlaneID ("Plane ID", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline" = "UniversalPipeline" }
        LOD 100
        
        // Transparent blending setup
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Back
        ZWrite Off
        ZTest LEqual

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            
            // Feature toggles
            #pragma multi_compile _ USE_MASK
            #pragma multi_compile _ DEBUG_OVERLAY
            #pragma multi_compile _ USE_AR_WORLD_SPACE
            
            // Platform specifics
            #pragma multi_compile_instancing
            #pragma prefer_hlslcc gles
            #pragma exclude_renderers d3d11_9x
            #pragma target 2.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float2 uv : TEXCOORD0;
                float4 positionHCS : SV_POSITION;
                float4 worldPos : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_SegmentationMask);
            SAMPLER(sampler_SegmentationMask);
            
            half4 _PaintColor;
            float _BlendFactor;
            float4 _MainTex_ST;
            float _DebugGrid;
            float4x4 _PlaneToWorldMatrix;
            float4x4 _WorldToCameraMatrix;
            float4x4 _CameraToWorldMatrix;
            float3 _PlaneNormal;
            float3 _PlaneCenter;
            float _PlaneID;

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                
                #ifdef USE_AR_WORLD_SPACE
                    float4 worldPos = mul(_PlaneToWorldMatrix, float4(IN.positionOS.xyz, 1.0));
                    
                    OUT.worldPos = worldPos;
                    
                    OUT.positionHCS = mul(UNITY_MATRIX_VP, worldPos);
                #else
                    OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                    OUT.worldPos = mul(UNITY_MATRIX_M, float4(IN.positionOS.xyz, 1.0));
                #endif
                
                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
                
                // Sample base texture
                half4 color = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
                
                // Debug overlay mode - show checkerboard pattern or world-space grid
                #ifdef DEBUG_OVERLAY
                    #ifdef USE_AR_WORLD_SPACE
                        float3 worldPos = IN.worldPos.xyz;
                        
                        float gridSize = 0.1;
                        float gridThickness = 0.005;
                        
                        float xGridXZ = step(1.0 - gridThickness, frac(abs(worldPos.x) / gridSize));
                        float zGridXZ = step(1.0 - gridThickness, frac(abs(worldPos.z) / gridSize));
                        
                        float xGridXY = step(1.0 - gridThickness, frac(abs(worldPos.x) / gridSize));
                        float yGridXY = step(1.0 - gridThickness, frac(abs(worldPos.y) / gridSize));
                        
                        float yGridYZ = step(1.0 - gridThickness, frac(abs(worldPos.y) / gridSize));
                        float zGridYZ = step(1.0 - gridThickness, frac(abs(worldPos.z) / gridSize));
                        
                        float floorGrid = max(xGridXZ, zGridXZ);
                        float wallGridX = max(yGridYZ, zGridYZ);
                        float wallGridZ = max(xGridXY, yGridXY);
                        
                        float dotUp = abs(dot(_PlaneNormal, float3(0, 1, 0)));
                        float dotRight = abs(dot(_PlaneNormal, float3(1, 0, 0)));
                        
                        float gridValue = 0;
                        if (dotUp > 0.8)
                            gridValue = floorGrid;
                        else if (dotRight > 0.8)
                            gridValue = wallGridX;
                        else
                            gridValue = wallGridZ;
                        
                        float3 planeColor = float3(frac(_PlaneID * 5.33), frac(_PlaneID * 7.77), frac(_PlaneID * 3.55));
                        
                        half4 debugColor = half4(lerp(planeColor, float3(1, 1, 1), gridValue), 0.7);
                        return debugColor;
                    #else
                        float checker = (fmod(floor(IN.uv.x * _DebugGrid), 2) == 0) ^ (fmod(floor(IN.uv.y * _DebugGrid), 2) == 0);
                        half4 debugColor = lerp(half4(1,0,0,0.5), half4(0,1,0,0.5), checker);
                        return debugColor;
                    #endif
                #endif
                
                #ifdef USE_MASK
                    // Sample segmentation mask
                    float mask = SAMPLE_TEXTURE2D(_SegmentationMask, sampler_SegmentationMask, IN.uv).r;
                    
                    // Apply color only in wall areas (mask > 0.1)
                    if (mask > 0.1)
                    {
                        // Blend with original color
                        half3 blendedColor = lerp(color.rgb, _PaintColor.rgb, _BlendFactor * mask);
                        
                        // Calculate alpha based on blend factor and mask
                        half blendedAlpha = lerp(0.0, _PaintColor.a, _BlendFactor * mask);
                        
                        return half4(blendedColor, blendedAlpha);
                    }
                    else
                    {
                        // Return transparent for non-wall areas
                        return half4(0, 0, 0, 0);
                    }
                #else
                    // Without mask - apply paint across the entire view with controlled opacity
                    half3 blendedColor = lerp(color.rgb, _PaintColor.rgb, _BlendFactor);
                    half blendedAlpha = _BlendFactor * _PaintColor.a;
                    return half4(blendedColor, blendedAlpha);
                #endif
            }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
} 