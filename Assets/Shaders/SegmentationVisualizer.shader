Shader "Hidden/SegmentationVisualizer"
{
    Properties
    {
        _MainTex ("Segmentation Mask", 2D) = "black" {}
        _PrevFrame ("Previous Frame", 2D) = "black" {}
        _DepthTex ("Depth Texture", 2D) = "white" {}
        _MotionVectors ("Motion Vectors", 2D) = "black" {}
        
        _WallColor ("Wall Color", Color) = (1,0,0,0.7)
        _FloorColor ("Floor Color", Color) = (0,1,0,0.7)
        
        _WallThreshold ("Wall Threshold", Range(0.01, 0.99)) = 0.5
        _FloorThreshold ("Floor Threshold", Range(0.01, 0.99)) = 0.5
        
        _EdgeSoftness ("Edge Softness", Range(0.001, 0.1)) = 0.02
        _EdgeGlow ("Edge Glow", Range(0, 1)) = 0.3
        
        _TemporalWeight ("Temporal Blend Weight", Range(0, 0.98)) = 0.8
        _Sharpness ("Sharpness", Range(0, 2)) = 1.0
        
        _DepthInfluence ("Depth Influence", Range(0, 1)) = 0.5
        _MaxDepth ("Maximum Depth", Float) = 5.0
        
        [Toggle] _DebugRawProb ("Debug: Raw Probability", Float) = 0
        [Toggle] _DebugEdges ("Debug: Edge Detection", Float) = 0
        [Toggle] _DebugTemporal ("Debug: Temporal Weight", Float) = 0
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
            #pragma multi_compile _ _DEBUG_RAWPROB _DEBUG_EDGES _DEBUG_TEMPORAL
            
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
            sampler2D _PrevFrame;
            sampler2D _DepthTex;
            sampler2D _MotionVectors;
            float4 _MainTex_ST;
            float4 _MainTex_TexelSize;
            
            float4 _WallColor;
            float4 _FloorColor;
            float _WallThreshold;
            float _FloorThreshold;
            float _EdgeSoftness;
            float _EdgeGlow;
            float _TemporalWeight;
            float _Sharpness;
            float _DepthInfluence;
            float _MaxDepth;
            
            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }
            
            // Edge detection helper function
            float3 detectEdges(float2 uv, sampler2D tex, float strength) {
                float2 texelSize = _MainTex_TexelSize.xy;
                float edge = 0;
                
                for(int x=-1; x<=1; x++) {
                    for(int y=-1; y<=1; y++) {
                        if(x==0 && y==0) continue;
                        edge += abs(tex2D(tex, uv).r - tex2D(tex, uv + float2(x,y) * texelSize).r);
                    }
                }
                
                return float3(1,1,1) * saturate(edge * strength * 8.0);
            }
            
            // Apply temporal stabilization with motion vectors
            float4 temporalStabilization(float2 uv, sampler2D currentFrame, sampler2D previousFrame, 
                                       sampler2D motionVectors, float stabilityFactor) {
                float2 motion = tex2D(motionVectors, uv).xy * 0.05; // Scale down for subtle effect
                float4 prevColor = tex2D(previousFrame, uv - motion);
                float4 currColor = tex2D(currentFrame, uv);
                
                // Adaptive blending based on motion magnitude
                float motionMagnitude = length(motion) * 10.0;
                float adaptiveBlend = max(0.2, min(0.8, stabilityFactor * (1.0 - motionMagnitude)));
                
                return lerp(currColor, prevColor, adaptiveBlend);
            }
            
            // Apply depth-based effects
            float4 applyDepthEffect(float2 uv, float4 color, sampler2D depthTex, float influence, float maxDepth) {
                float depth = tex2D(depthTex, uv).r * maxDepth;
                
                // Depth gradient to make distant walls appear darker
                float depthFactor = saturate(1.0 - (depth / maxDepth) * influence);
                
                return float4(color.rgb * depthFactor, color.a);
            }
            
            // Apply edge softness
            float4 applySoftEdges(float2 uv, sampler2D tex, float softness) {
                float2 texelSize = _MainTex_TexelSize.xy;
                
                // Sample in a small grid for soft edges
                float4 center = tex2D(tex, uv);
                float4 samples = 0;
                int count = 0;
                
                for (int x = -1; x <= 1; x++) {
                    for (int y = -1; y <= 1; y++) {
                        samples += tex2D(tex, uv + float2(x,y) * texelSize * softness * 5.0);
                        count++;
                    }
                }
                
                samples /= count;
                return lerp(center, samples, softness * 10.0);
            }
            
            // Main fragment shader
            fixed4 frag (v2f i) : SV_Target
            {
                // Get raw segmentation data
                float4 rawColor = tex2D(_MainTex, i.uv);
                float wallProb = rawColor.r;
                float floorProb = rawColor.g;
                
                // Calculate softened edges
                float4 softEdges = applySoftEdges(i.uv, _MainTex, _EdgeSoftness);
                wallProb = softEdges.r;
                floorProb = softEdges.g;
                
                // Apply edge detection
                float3 edgeGlow = detectEdges(i.uv, _MainTex, _EdgeGlow);
                
                // Thresholding with soft transition
                float wallMask = smoothstep(_WallThreshold - _EdgeSoftness, _WallThreshold + _EdgeSoftness, wallProb);
                float floorMask = smoothstep(_FloorThreshold - _EdgeSoftness, _FloorThreshold + _EdgeSoftness, floorProb);
                
                // Combine with colors
                float4 finalColor = wallMask * _WallColor + floorMask * _FloorColor;
                
                // Add edge glow
                finalColor.rgb += edgeGlow;
                
                // Apply depth effect if available
                finalColor = applyDepthEffect(i.uv, finalColor, _DepthTex, _DepthInfluence, _MaxDepth);
                
                // Apply temporal stabilization
                finalColor = temporalStabilization(i.uv, _MainTex, _PrevFrame, _MotionVectors, _TemporalWeight);
                
                // Debug visualization modes
                #ifdef _DEBUG_RAWPROB
                    return float4(wallProb, floorProb, 0, 1); // Show raw probability
                #endif
                
                #ifdef _DEBUG_EDGES
                    return float4(edgeGlow, 1); // Show edges
                #endif
                
                #ifdef _DEBUG_TEMPORAL
                    float blendFactor = max(0.2, min(0.8, _TemporalWeight * (1.0 - length(tex2D(_MotionVectors, i.uv).xy) * 10.0)));
                    return float4(blendFactor, blendFactor, blendFactor, 1); // Show temporal blend factor
                #endif
                
                return finalColor;
            }
            ENDCG
        }
    }
    FallBack "Unlit/Texture"
} 