#if UNITY_EDITOR
using UnityEditor; // Required for MenuItems and other editor functionalities
#endif
using UnityEngine;
using UnityEngine.XR.ARFoundation; // For ARCameraManager (optional, if used for context)
using System.Collections.Generic; // For Dictionary and List
using System; // For Enum

[RequireComponent(typeof(WallSegmentation))]
public class GPUSegmentationProcessor : MonoBehaviour
{
      public enum GPULogLevel { Info, Warning, Error }

      [Header("Compute Shader Settings")]
      [Tooltip("Compute shader for GPU processing.")]
      public ComputeShader postProcessShader;
      [Tooltip("Use a comprehensive kernel for all post-processing steps if available.")]
      public bool useComprehensiveKernel = true;

      [Header("GPU-based Post-Processing Parameters")]
      [Tooltip("Channel index from the model output to use for segmentation (e.g., 0 for walls, 1 for floor).")]
      [Range(0, 3)] public int segmentationChannelIndex = 0;
      [Range(0f, 1f)] public float wallThreshold = 0.5f;
      [Range(0f, 1f)] public float floorThreshold = 0.5f; // Example, if your model supports it
      [Range(0.01f, 1f)] public float edgeSoftness = 0.1f;
      [Range(0f, 1f)] public float temporalWeight = 0.5f;
      [Range(0f, 5f)] public float sharpness = 1.0f;
      [Range(0f, 1f)] public float edgeGlow = 0.1f;
      [Range(0f, 1f)] public float depthInfluence = 0.5f; // Example
      [Range(0.1f, 10f)] public float maxDepth = 5.0f;   // Example

      [Header("Fallback Material-based Post-Processing")]
      public Material blurMaterial;
      public Material thresholdMaterial;
      [Range(1, 10)] public int blurSize = 3;
      [Range(0f, 1f)] public float binarizationThreshold = 0.5f;

      [Header("Performance & Quality Settings")]
      public float targetFPS = 0f; // 0 means disabled
      public int fpsAverageWindow = 10;

      [Header("Dependencies")]
      public WallSegmentation wallSegmentation;

      [Header("Global Logging Control")]
      public bool enableComponentLogging = true;

      // Fields for RenderTextures
      private RenderTexture processedMask;
      private RenderTexture tempBlurRT; // Only used if not in comprehensive mode or for specific passes
      private RenderTexture previousFrameMask;

      // Compute Shader Kernel IDs
      private int comprehensiveKernelId = -1;

      // Shader Property IDs (cached for performance)
      private int inputMaskPropertyID, previousMaskPropertyID, outputMaskPropertyID, qualityBufferPropertyID;
      private int widthPropertyID, heightPropertyID, wallThresholdPropertyID, floorThresholdPropertyID;
      private int edgeSoftnessPropertyID, temporalWeightPropertyID, sharpnessPropertyID, edgeGlowPropertyID;
      private int maxDepthPropertyID, depthInfluencePropertyID, segmentationChannelIndexPropertyID;
      private int blurSizePropertyID, binarizationThresholdPropertyID;

      // Performance Monitoring
      private float[] fpsSamples;
      private int fpsSampleIndex = 0;
      private float lastQualityAdjustTime = 0f;
      private int consecutiveLowFrameCount = 0;
      private int consecutiveHighFrameCount = 0;

      // State variables
      private bool isInitialized = false;
      private bool resourcesAllocated = false; // Tracks if textures are ready

      // Texture Pool (Simplified)
      private static readonly Dictionary<Vector2Int, Stack<RenderTexture>> texturePool = new Dictionary<Vector2Int, Stack<RenderTexture>>();
      private static readonly List<RenderTexture> allCreatedTextures = new List<RenderTexture>();

      // Constants
      private const string KERNEL_POSTPROCESS_COMPREHENSIVE_CONST = "ComprehensivePostProcess";

      void Awake()
      {
            if (wallSegmentation == null) wallSegmentation = GetComponent<WallSegmentation>();
            if (wallSegmentation == null)
            {
                  Log("WallSegmentation component not found. GPUSegmentationProcessor will be disabled.", GPULogLevel.Error);
                  enabled = false;
                  return;
            }

            CachePropertyIDs();

            if (postProcessShader == null)
            {
                  Log("PostProcessComputeShader is not assigned. GPU processing will be limited. Material fallbacks may be used.", GPULogLevel.Warning);
            }

            if (fpsAverageWindow <= 0) fpsAverageWindow = 10;
            fpsSamples = new float[fpsAverageWindow];

            Log("Awake completed.", GPULogLevel.Info);
      }

      void Start()
      {
            if (wallSegmentation != null && wallSegmentation.IsModelInitialized)
            {
                  SubscribeToSegmentationUpdates();
                  InitializeShaderAndKernelLogic(); // Initialize if WallSegmentation is already ready
            }
            else if (wallSegmentation != null)
            {
                  // Subscribe to the event for when WallSegmentation finishes initializing
                  wallSegmentation.OnModelInitialized += HandleWallSegmentationInitialized;
            }
            else
            {
                  Log("WallSegmentation is null in Start. Cannot subscribe or initialize.", GPULogLevel.Error);
            }
            Log("Start completed. Waiting for WallSegmentation to initialize if not already.", GPULogLevel.Info);
      }

      private void HandleWallSegmentationInitialized()
      {
            Log("WallSegmentation initialized. Proceeding with GPUSegmentationProcessor setup.", GPULogLevel.Info);
            wallSegmentation.OnModelInitialized -= HandleWallSegmentationInitialized; // Unsubscribe after called
            SubscribeToSegmentationUpdates();
            InitializeShaderAndKernelLogic();
      }

      private void SubscribeToSegmentationUpdates()
      {
            if (wallSegmentation != null)
            {
                  Log("Subscribing to WallSegmentation mask updates.", GPULogLevel.Info);
                  wallSegmentation.OnSegmentationMaskUpdated -= HandleSegmentationMaskUpdated; // Prevent double subscription
                  wallSegmentation.OnSegmentationMaskUpdated += HandleSegmentationMaskUpdated;
            }
      }

      private void InitializeShaderAndKernelLogic()
      {
            if (isInitialized) return;
            Log("Attempting to initialize shader and kernel logic.", GPULogLevel.Info);

            if (postProcessShader != null)
            {
                  if (useComprehensiveKernel)
                  {
                        comprehensiveKernelId = postProcessShader.FindKernel(KERNEL_POSTPROCESS_COMPREHENSIVE_CONST);
                        if (comprehensiveKernelId == -1)
                        {
                              Log($"Comprehensive kernel '{KERNEL_POSTPROCESS_COMPREHENSIVE_CONST}' not found. Will use material fallbacks if available.", GPULogLevel.Warning);
                              useComprehensiveKernel = false; // Fallback to material-based
                        }
                        else
                        {
                              Log($"Comprehensive kernel '{KERNEL_POSTPROCESS_COMPREHENSIVE_CONST}' found with ID: {comprehensiveKernelId}", GPULogLevel.Info);
                        }
                  }
            }
            else
            {
                  Log("Compute shader not available. Material fallbacks will be used if materials are assigned.", GPULogLevel.Warning);
                  useComprehensiveKernel = false;
            }

            isInitialized = true;
            Log("Shader and kernel logic initialization complete. isInitialized: true", GPULogLevel.Info);

            // Initialize textures with a default or current mask resolution if known
            if (wallSegmentation != null && wallSegmentation.segmentationMaskTexture != null && wallSegmentation.segmentationMaskTexture.IsCreated())
            {
                  InitializeTextures(wallSegmentation.segmentationMaskTexture.width, wallSegmentation.segmentationMaskTexture.height);
            }
            else
            {
                  Log("WallSegmentation or its mask texture not ready at InitializeShaderAndKernelLogic. Textures will be initialized on first mask update or with a default small size.", GPULogLevel.Warning);
                  InitializeTextures(64, 64); // Initialize with a small default to prevent nulls
            }
      }


      void Update()
      {
            if (!isInitialized && wallSegmentation != null && wallSegmentation.IsModelInitialized)
            {
                  // This ensures initialization if Start's subscription path was missed or delayed
                  InitializeShaderAndKernelLogic();
            }

            if (targetFPS > 0 && Time.time - lastQualityAdjustTime > 1f) // Adjust quality e.g. every second
            {
                  UpdateFPSSamples();
                  AdaptQualityToPerformance(GetAverageFPS());
                  lastQualityAdjustTime = Time.time;
            }
      }

      void OnDestroy()
      {
            Log("OnDestroy called. Cleaning up resources.", GPULogLevel.Info);
            if (wallSegmentation != null)
            {
                  wallSegmentation.OnModelInitialized -= HandleWallSegmentationInitialized;
                  wallSegmentation.OnSegmentationMaskUpdated -= HandleSegmentationMaskUpdated;
            }
            // ReleaseComputeResources(); // ComputeBuffer is not used in this simplified version for now
            ReleaseAllPooledTextures();
            ReleaseRenderTexture(ref processedMask);
            ReleaseRenderTexture(ref tempBlurRT);
            ReleaseRenderTexture(ref previousFrameMask);
            Log("Cleaned up resources from OnDestroy.", GPULogLevel.Info);
      }

      private void InitializeTextures(int width, int height)
      {
            if (width <= 0 || height <= 0)
            {
                  Log($"Invalid dimensions for texture initialization: {width}x{height}", GPULogLevel.Error);
                  resourcesAllocated = false;
                  return;
            }
            Log($"Initializing textures for size: {width}x{height}. Current processedMask is {(processedMask == null ? "null" : "exists")}", GPULogLevel.Info);

            bool canUseRandomWrite = useComprehensiveKernel && comprehensiveKernelId != -1;

            // Ensure main processedMask is ready
            processedMask = GetOrCreateRenderTexture(ref processedMask, width, height, RenderTextureFormat.ARGB32, FilterMode.Bilinear, "GPUP_ProcessedMask", canUseRandomWrite);
            // if (processedMask != null) processedMask.enableRandomWrite = useComprehensiveKernel && comprehensiveKernelId != -1; // Removed, handled by GetOrCreateRenderTexture

            // Ensure previousFrameMask is ready (for temporal processing)
            previousFrameMask = GetOrCreateRenderTexture(ref previousFrameMask, width, height, RenderTextureFormat.ARGB32, FilterMode.Bilinear, "GPUP_PrevFrameMask", canUseRandomWrite);
            // if (previousFrameMask != null) previousFrameMask.enableRandomWrite = useComprehensiveKernel && comprehensiveKernelId != -1; // Removed, handled by GetOrCreateRenderTexture


            // tempBlurRT would be created on-demand via GetPooledTexture if using material fallbacks that need it.
            // Or initialize it here if it's always needed for a specific fallback path.
            // Example: tempBlurRT = GetOrCreateRenderTexture(ref tempBlurRT, width, height, RenderTextureFormat.ARGB32, FilterMode.Bilinear, "GPUP_TempBlurRT");


            resourcesAllocated = processedMask != null && previousFrameMask != null; // Update based on essential textures
            if (!resourcesAllocated) LogError("Failed to allocate one or more essential textures (processedMask or previousFrameMask).");
            else Log("Essential textures allocated successfully.", GPULogLevel.Info);
      }

      // private void ReleaseComputeResources() { /* If ComputeBuffers were used */ }

      private void UpdateComputeShaderParameters()
      {
            if (!postProcessShader || comprehensiveKernelId == -1 || !useComprehensiveKernel || processedMask == null || !processedMask.IsCreated())
            {
                  // Log("Skipping UpdateComputeShaderParameters: shader/kernel not ready or processedMask invalid.", GPULogLevel.Warning); // Can be spammy
                  return;
            }
            postProcessShader.SetInt(widthPropertyID, processedMask.width);
            postProcessShader.SetInt(heightPropertyID, processedMask.height);
            postProcessShader.SetFloat(wallThresholdPropertyID, wallThreshold);
            postProcessShader.SetFloat(floorThresholdPropertyID, floorThreshold);
            postProcessShader.SetFloat(edgeSoftnessPropertyID, edgeSoftness);
            postProcessShader.SetFloat(temporalWeightPropertyID, temporalWeight);
            postProcessShader.SetFloat(sharpnessPropertyID, sharpness);
            postProcessShader.SetFloat(edgeGlowPropertyID, edgeGlow);
            postProcessShader.SetFloat(maxDepthPropertyID, maxDepth);
            postProcessShader.SetFloat(depthInfluencePropertyID, depthInfluence);
            postProcessShader.SetInt(segmentationChannelIndexPropertyID, segmentationChannelIndex);
      }

      public RenderTexture ProcessMask(RenderTexture inputMask)
      {
            if (!isInitialized)
            {
                  Log("ProcessMask called before initialization is complete. Attempting to initialize now.", GPULogLevel.Warning);
                  InitializeShaderAndKernelLogic(); // Try to initialize if not already
                  if (!isInitialized)
                  {
                        Log("Initialization failed. Cannot process mask.", GPULogLevel.Error);
                        return inputMask; // Return original if we can't process
                  }
            }

            if (inputMask == null || !inputMask.IsCreated())
            {
                  Log("Input mask is null or not created. Cannot process.", GPULogLevel.Error);
                  return null;
            }

            // Ensure textures match input mask dimensions
            if (!resourcesAllocated || processedMask == null || processedMask.width != inputMask.width || processedMask.height != inputMask.height)
            {
                  Log($"Processed mask dimensions mismatch or not allocated. Re-initializing textures to {inputMask.width}x{inputMask.height}", GPULogLevel.Info);
                  InitializeTextures(inputMask.width, inputMask.height);
                  if (!resourcesAllocated)
                  {
                        Log("Failed to re-initialize textures in ProcessMask. Returning original mask.", GPULogLevel.Error);
                        return inputMask; // Return original if re-init fails
                  }
            }

            // Option 1: Comprehensive Compute Shader Kernel
            if (useComprehensiveKernel && postProcessShader != null && comprehensiveKernelId != -1)
            {
                  UpdateComputeShaderParameters(); // Ensure parameters are fresh

                  if (inputMask == null || !inputMask.IsCreated())
                  {
                        LogError("ProcessMask: inputMask is null or not created before setting to compute shader.");
                        return processedMask; // Or inputMask, depending on desired fallback
                  }
                  if (previousFrameMask == null || !previousFrameMask.IsCreated())
                  {
                        LogError("ProcessMask: previousFrameMask is null or not created before setting to compute shader.");
                        // Potentially still proceed without temporal, or return an error/fallback
                        // For now, let's log and continue, but this might lead to artifacts or errors in shader
                  }
                  if (processedMask == null || !processedMask.IsCreated())
                  {
                        LogError("ProcessMask: processedMask (output) is null or not created before setting to compute shader.");
                        return inputMask; // Cannot dispatch if output is invalid
                  }


                  postProcessShader.SetTexture(comprehensiveKernelId, inputMaskPropertyID, inputMask);
                  // Check previousFrameMask again specifically before setting, as it might be optional depending on shader logic
                  if (previousFrameMask != null && previousFrameMask.IsCreated())
                  {
                        postProcessShader.SetTexture(comprehensiveKernelId, previousMaskPropertyID, previousFrameMask);
                  }
                  else
                  {
                        // If previousFrameMask is required by the shader and it's not available, this will cause an error.
                        // Consider setting a dummy texture or having a shader variant that doesn't require it.
                        Log("ProcessMask: previousFrameMask is not available. Temporal effects in compute shader might not work correctly.", GPULogLevel.Warning);
                  }
                  postProcessShader.SetTexture(comprehensiveKernelId, outputMaskPropertyID, processedMask);

                  // CRITICAL CHECK before dispatch
                  if (inputMask == null || !inputMask.IsCreated())
                  {
                        LogError($"CRITICAL_ERROR: inputMask became invalid right before Dispatch in ProcessMask. Name: {(inputMask != null ? inputMask.name : "null")}, Width: {(inputMask != null ? inputMask.width : 0)}, Height: {(inputMask != null ? inputMask.height : 0)}, isCreated: {(inputMask != null ? inputMask.IsCreated() : false)}. NOT DISPATCHING.");
                        return processedMask; // Or handle error more gracefully
                  }
                  if (processedMask == null || !processedMask.IsCreated())
                  {
                        LogError($"CRITICAL_ERROR: processedMask (output) became invalid right before Dispatch in ProcessMask. Name: {(processedMask != null ? processedMask.name : "null")}. NOT DISPATCHING.");
                        return inputMask; // Or handle error
                  }


                  int threadGroupsX = Mathf.CeilToInt(processedMask.width / 8.0f);
                  int threadGroupsY = Mathf.CeilToInt(processedMask.height / 8.0f);
                  postProcessShader.Dispatch(comprehensiveKernelId, threadGroupsX, threadGroupsY, 1);

                  Graphics.Blit(processedMask, previousFrameMask); // For next frame's temporal processing
            }
            // Option 2: Fallback to Material-based multi-pass processing
            else
            {
                  Log("Using fallback material-based processing or compute shader not ready.", GPULogLevel.Info);
                  RenderTexture currentSource = inputMask;
                  RenderTexture tempA = null; // Will be fetched from pool if needed

                  // Pass 1: Blur (example)
                  if (blurMaterial != null && blurSize > 0)
                  {
                        tempA = GetPooledTexture(inputMask.width, inputMask.height, inputMask.format);
                        if (tempA == null) { LogError("Failed to get pooled texture for blur."); return currentSource; }
                        blurMaterial.SetInt(blurSizePropertyID, blurSize);
                        Graphics.Blit(currentSource, tempA, blurMaterial);
                        if (currentSource != inputMask) ReleasePooledTexture(currentSource); // Release intermediate if it's from pool
                        currentSource = tempA;
                  }

                  // Pass 2: Threshold (example)
                  if (thresholdMaterial != null)
                  {
                        RenderTexture targetForThreshold = (currentSource == inputMask && tempA == null) ? GetPooledTexture(inputMask.width, inputMask.height, inputMask.format) : (tempA ?? GetPooledTexture(inputMask.width, inputMask.height, inputMask.format));
                        if (targetForThreshold == null) { LogError("Failed to get pooled texture for threshold."); return currentSource; }

                        thresholdMaterial.SetFloat(binarizationThresholdPropertyID, binarizationThreshold);
                        Graphics.Blit(currentSource, targetForThreshold, thresholdMaterial);
                        if (currentSource != inputMask) ReleasePooledTexture(currentSource);
                        currentSource = targetForThreshold;
                  }

                  Graphics.Blit(currentSource, processedMask); // Blit final result to processedMask

                  // Temporal blending (simplified: copy to previous for next frame)
                  if (previousFrameMask != null && previousFrameMask.IsCreated())
                  {
                        Graphics.Blit(processedMask, previousFrameMask);
                  }

                  if (currentSource != inputMask && currentSource != processedMask) ReleasePooledTexture(currentSource); // Release last intermediate if not input or final output
            }
            return processedMask;
      }


      public float GetMaskQuality() { /* ... placeholder ... */ return 0f; }

      private void UpdateFPSSamples()
      {
            if (fpsSamples == null) return;
            fpsSamples[fpsSampleIndex] = 1.0f / Time.unscaledDeltaTime;
            fpsSampleIndex = (fpsSampleIndex + 1) % fpsSamples.Length;
      }

      private float GetAverageFPS()
      {
            if (fpsSamples == null || fpsSamples.Length == 0) return 0f;
            float total = 0;
            foreach (float sample in fpsSamples) total += sample;
            return total / fpsSamples.Length;
      }

      private void AdaptQualityToPerformance(float currentFPS)
      {
            if (targetFPS <= 0 || !Application.isPlaying) return;
            // Simplified example: Adjust edgeSoftness
            if (currentFPS < targetFPS * 0.9f)
            {
                  consecutiveLowFrameCount++;
                  consecutiveHighFrameCount = 0;
                  if (consecutiveLowFrameCount > 3)
                  {
                        edgeSoftness = Mathf.Min(edgeSoftness + 0.05f, 1.0f);
                        Log($"FPS low ({currentFPS:F1}), reducing quality (edgeSoftness: {edgeSoftness:F2}).", GPULogLevel.Info);
                        consecutiveLowFrameCount = 0;
                  }
            }
            else if (currentFPS > targetFPS * 1.1f)
            {
                  consecutiveHighFrameCount++;
                  consecutiveLowFrameCount = 0;
                  if (consecutiveHighFrameCount > 5)
                  {
                        edgeSoftness = Mathf.Max(edgeSoftness - 0.05f, 0.01f);
                        Log($"FPS high ({currentFPS:F1}), increasing quality (edgeSoftness: {edgeSoftness:F2}).", GPULogLevel.Info);
                        consecutiveHighFrameCount = 0;
                  }
            }
            else
            {
                  consecutiveLowFrameCount = 0;
                  consecutiveHighFrameCount = 0;
            }
      }

      private void HandleSegmentationMaskUpdated(RenderTexture rawMask)
      {
            if (!enabled || !gameObject.activeInHierarchy)
            {
                  Log("Component disabled or GameObject inactive, skipping mask update.", GPULogLevel.Info);
                  return;
            }
            if (rawMask == null || !rawMask.IsCreated())
            {
                  Log("Received null or uncreated raw mask in HandleSegmentationMaskUpdated.", GPULogLevel.Warning);
                  return;
            }

            if (!isInitialized)
            {
                  Log("Not yet initialized in HandleSegmentationMaskUpdated. Trying to initialize now.", GPULogLevel.Warning);
                  InitializeShaderAndKernelLogic();
                  if (!isInitialized)
                  {
                        Log("Initialization failed during HandleSegmentationMaskUpdated. Cannot process mask.", GPULogLevel.Error);
                        return;
                  }
            }

            // Now that we are initialized, check/prepare textures
            if (!resourcesAllocated || processedMask == null || processedMask.width != rawMask.width || processedMask.height != rawMask.height)
            {
                  Log($"Re-initializing textures in HandleSegmentationMaskUpdated due to resolution change or first-time setup: {rawMask.width}x{rawMask.height}", GPULogLevel.Info);
                  InitializeTextures(rawMask.width, rawMask.height);
                  if (!resourcesAllocated)
                  {
                        LogError("Failed to re-initialize textures in HandleSegmentationMaskUpdated. Cannot process mask.");
                        return;
                  }
            }
            ProcessMask(rawMask);
      }

      private void CachePropertyIDs()
      {
            inputMaskPropertyID = Shader.PropertyToID("_InputMask");
            previousMaskPropertyID = Shader.PropertyToID("_PreviousMask");
            outputMaskPropertyID = Shader.PropertyToID("_OutputMask");
            // qualityBufferPropertyID = Shader.PropertyToID("_QualityBuffer"); // If using quality buffer
            widthPropertyID = Shader.PropertyToID("_Width");
            heightPropertyID = Shader.PropertyToID("_Height");
            wallThresholdPropertyID = Shader.PropertyToID("_WallThreshold");
            floorThresholdPropertyID = Shader.PropertyToID("_FloorThreshold");
            edgeSoftnessPropertyID = Shader.PropertyToID("_EdgeSoftness");
            temporalWeightPropertyID = Shader.PropertyToID("_TemporalWeight");
            sharpnessPropertyID = Shader.PropertyToID("_Sharpness");
            edgeGlowPropertyID = Shader.PropertyToID("_EdgeGlow");
            maxDepthPropertyID = Shader.PropertyToID("_MaxDepth");
            depthInfluencePropertyID = Shader.PropertyToID("_DepthInfluence");
            segmentationChannelIndexPropertyID = Shader.PropertyToID("_SegmentationChannelIndex");
            blurSizePropertyID = Shader.PropertyToID("_BlurSize"); // For material fallback
            binarizationThresholdPropertyID = Shader.PropertyToID("_Threshold"); // For material fallback
            Log("Shader property IDs cached.", GPULogLevel.Info);
      }

      // --- Utility Methods for Texture Management ---
      private RenderTexture GetOrCreateRenderTexture(ref RenderTexture currentRT, int width, int height, RenderTextureFormat format, FilterMode filterMode, string name, bool enableRandomWriteFlag = false)
      {
            if (width <= 0 || height <= 0)
            {
                  LogError($"GetOrCreateRenderTexture: Invalid dimensions {width}x{height} for {name}");
                  if (currentRT != null) ReleaseRenderTexture(ref currentRT); // Release if it exists but new dims are invalid
                  return null;
            }
            if (currentRT != null && currentRT.IsCreated())
            {
                  if (currentRT.width == width && currentRT.height == height && currentRT.format == format)
                  {
                        currentRT.name = name; // Ensure name is updated
                        return currentRT;
                  }
                  // Mismatch, release the old one before creating a new one
                  Log($"Releasing existing RT '{currentRT.name}' due to dimension/format mismatch for new RT '{name}'.", GPULogLevel.Info);
                  ReleaseRenderTexture(ref currentRT);
            }

            currentRT = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Default);
            currentRT.filterMode = filterMode;
            currentRT.name = name;
            currentRT.wrapMode = TextureWrapMode.Clamp; // Good default
            if (enableRandomWriteFlag)
            {
                  currentRT.enableRandomWrite = true;
            }

            if (!currentRT.Create())
            {
                  LogError($"Failed to create RenderTexture: {name} ({width}x{height})");
                  UnityEngine.Object.DestroyImmediate(currentRT); // Clean up the asset if create failed
                  currentRT = null;
                  return null;
            }
            Log($"Created RenderTexture: {name} ({width}x{height}, {format})", GPULogLevel.Info);
            return currentRT;
      }

      private void ReleaseRenderTexture(ref RenderTexture rt)
      {
            if (rt != null)
            {
                  string rtName = rt.name ?? "UnnamedRT_to_release";
                  if (rt.IsCreated())
                  {
                        rt.Release(); // Release GPU resources
                  }
                  UnityEngine.Object.DestroyImmediate(rt); // Free memory
                  Log($"Released and destroyed RenderTexture: {rtName}", GPULogLevel.Info);
                  rt = null;
            }
      }

      private static RenderTexture GetPooledTexture(int width, int height, RenderTextureFormat format)
      {
            Vector2Int key = new Vector2Int(width, height);
            RenderTexture rtToUse;
            if (texturePool.TryGetValue(key, out Stack<RenderTexture> stack) && stack.Count > 0)
            {
                  rtToUse = stack.Pop();
                  // Log($"Reusing pooled RT: {rtToUse.name} for {width}x{height}", GPULogLevel.Info); // Can be spammy
            }
            else
            {
                  rtToUse = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Default);
                  rtToUse.name = $"PooledRT_{width}x{height}_{allCreatedTextures.Count}";
                  rtToUse.wrapMode = TextureWrapMode.Clamp;
                  if (!rtToUse.Create())
                  {
                        Debug.LogError($"[GPUSegmentationProcessor] Failed to create pooled RT: {rtToUse.name}");
                        UnityEngine.Object.DestroyImmediate(rtToUse);
                        return null;
                  }
                  allCreatedTextures.Add(rtToUse); // Track for cleanup
                                                   // Log($"Created new pooled RT: {rtToUse.name}", GPULogLevel.Info); // Can be spammy
            }
            rtToUse.filterMode = FilterMode.Bilinear; // Ensure default
            return rtToUse;
      }

      private static void ReleasePooledTexture(RenderTexture rt)
      {
            if (rt == null) return;
            // Log($"Releasing pooled RT: {rt.name}", GPULogLevel.Info); // Can be spammy
            Vector2Int key = new Vector2Int(rt.width, rt.height);
            if (!texturePool.ContainsKey(key))
            {
                  texturePool[key] = new Stack<RenderTexture>();
            }
            texturePool[key].Push(rt);
      }

      private static void ReleaseAllPooledTextures()
      {
            // Log("Releasing all pooled textures.", GPULogLevel.Info); // Can be spammy
            foreach (var kvp in texturePool)
            {
                  foreach (var rt_loopVariable in kvp.Value)
                  {
                        if (rt_loopVariable.IsCreated()) rt_loopVariable.Release();
                        UnityEngine.Object.DestroyImmediate(rt_loopVariable);
                  }
            }
            texturePool.Clear();
            // Also clear allCreatedTextures as they are now destroyed
            allCreatedTextures.Clear();
      }


      // --- Logging Utility ---
      private void Log(string message, GPULogLevel level = GPULogLevel.Info)
      {
            if (!enableComponentLogging && level != GPULogLevel.Error)
            {
                  // If it's an error, it will always pass through unless enableComponentLogging is also false for errors specifically
                  // which is not the case here. So only non-errors are fully suppressed by enableComponentLogging = false.
                  if (level == GPULogLevel.Error && !enableComponentLogging) { /* Allow errors if !enableComponentLogging, but this is a bit contradictory. Usually master toggle silences all but most critical errors */ }
                  else if (level != GPULogLevel.Error) return;
            }

            string formattedMessage = $"[GPUSegmentationProcessor] {message}";
            switch (level)
            {
                  case GPULogLevel.Info:
                        Debug.Log(formattedMessage, this);
                        break;
                  case GPULogLevel.Warning:
                        Debug.LogWarning(formattedMessage, this);
                        break;
                  case GPULogLevel.Error:
                        Debug.LogError(formattedMessage, this);
                        break;
            }
      }
      private void LogError(string message) => Log(message, GPULogLevel.Error);

      public RenderTexture GetProcessedMask()
      {
            if (processedMask != null && processedMask.IsCreated())
            {
                  return processedMask;
            }
            // Log("GetProcessedMask called, but processedMask is null or not created.", GPULogLevel.Warning); // Can be spammy
            return null;
      }

#if UNITY_EDITOR
      [MenuItem("CONTEXT/GPUSegmentationProcessor/Log Current State")]
      public static void LogCurrentStateEditor(MenuCommand command)
      {
            GPUSegmentationProcessor processor = (GPUSegmentationProcessor)command.context;
            if (processor != null)
            {
                  Debug.Log($"--- GPUSegmentationProcessor State ({processor.GetInstanceID()}) ---");
                  Debug.Log($"isInitialized: {processor.isInitialized}");
                  Debug.Log($"resourcesAllocated: {processor.resourcesAllocated}");
                  Debug.Log($"Compute Shader: {(processor.postProcessShader != null ? processor.postProcessShader.name : "null")}");
                  Debug.Log($"Use Comprehensive Kernel: {processor.useComprehensiveKernel}, ID: {processor.comprehensiveKernelId}");
                  Debug.Log($"Processed Mask: {(processor.processedMask != null && processor.processedMask.IsCreated() ? $"{processor.processedMask.name} ({processor.processedMask.width}x{processor.processedMask.height})" : "null or not created")}");
                  Debug.Log($"Previous Frame Mask: {(processor.previousFrameMask != null && processor.previousFrameMask.IsCreated() ? $"{processor.previousFrameMask.name} ({processor.previousFrameMask.width}x{processor.previousFrameMask.height})" : "null or not created")}");
                  Debug.Log($"WallSegmentation ref: {(processor.wallSegmentation != null ? processor.wallSegmentation.GetInstanceID().ToString() : "null")}, IsModelInitialized: {(processor.wallSegmentation != null ? processor.wallSegmentation.IsModelInitialized.ToString() : "N/A")}");
                  Debug.Log($"Average FPS: {processor.GetAverageFPS():F1}");
                  Debug.Log($"Texture Pool - Total Pooled Stacks: {texturePool.Count}, Total Ever Created (tracked): {allCreatedTextures.Count}");
                  foreach (var poolEntry in texturePool)
                  {
                        Debug.Log($"  Pool for {poolEntry.Key.x}x{poolEntry.Key.y}: {poolEntry.Value.Count} available");
                  }
                  Debug.Log("---------------------------------------------------");
            }
      }
#endif
}
