using UnityEngine;
using UnityEngine.XR.ARFoundation;
using Unity.XR.CoreUtils;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.XR.ARSubsystems;
using System;
using UnityEngine.Rendering;
using System.IO; // NEW: For file operations

// Basic DebugFlags and LogLevel for ARManagerInitializer2
public enum ARManagerDebugFlags
{
    None = 0,
    Initialization = 1 << 0,
    PlaneGeneration = 1 << 1,
    Raycasting = 1 << 2,
    ARSystem = 1 << 3, // For AR specific logs like light estimation
    System = 1 << 4, // For general system events like subscribe/unsubscribe
    Performance = 1 << 5,
    SaveDebugTextures = 1 << 6, // ADDED: For controlling texture saving
    All = ~0
}

public enum ARManagerLogLevel
{
    Info,
    Warning,
    Error
}

[DefaultExecutionOrder(-10)]
public class ARManagerInitializer2 : MonoBehaviour
{
    public static ARManagerInitializer2 Instance { get; private set; }
    private static int planeInstanceCounter = 0;

    [Header("AR компоненты")]
    [Tooltip("Reference to the XROrigin in the scene. If not set, will try to find one.")]
    [SerializeField] private XROrigin xrOrigin;
    public ARCameraManager arCameraManager;
    public ARPlaneManager planeManager;
    public AROcclusionManager arOcclusionManager;
    public ARRaycastManager arRaycastManager;
    public ARAnchorManager arAnchorManager;

    [Tooltip("AR Mesh Manager для Scene Reconstruction (LiDAR сканирование)")]
    public ARMeshManager arMeshManager;

    [Tooltip("Включить Scene Reconstruction на устройствах с LiDAR")]
    public bool enableSceneReconstruction = true;

    [Tooltip("Directional Light, который будет обновляться AR Light Estimation")]
    [SerializeField] private Light arDirectionalLight;

    [Header("Настройки сегментации")]
    public bool useDetectedPlanes = false;
    [SerializeField] private float minPlaneSizeInMeters = 0.1f;
    [SerializeField] private int minPixelsDimensionForLowResArea = 4;
    [SerializeField] private int minAreaSizeInLowResPixels = 16;
    [Range(0, 255)] public byte wallAreaRedChannelThreshold = 250; // Changed from 220 to 250

    [Header("Настройки Рейкастинга для Плоскостей")]
    public bool enableDetailedRaycastLogging = true; // Default to true for easier debugging
    public float maxRayDistance = 15.0f;
    public LayerMask hitLayerMask = -1; // Initialized in Awake()
    public float minHitDistanceThreshold = 0.1f;
    public float maxWallNormalAngleDeviation = 30f;
    public float maxFloorCeilingAngleDeviation = 15f;
    [Tooltip("Расстояние по умолчанию для создания плоскости, если рейкаст не нашел поверхности (в метрах)")]
    public float defaultPlaneDistance = 2.5f;
    [Tooltip("Имя слоя для создаваемых плоскостей (должен существовать в Tags and Layers)")]
    [SerializeField] private string planeLayerName = "ARPlanes";
    [Tooltip("Имена объектов, которые должны игнорироваться при рейкастинге (разделены запятыми)")]
    [SerializeField] private string ignoreObjectNames = "Player,UI,Hand";

    [Header("Настройки сохранения плоскостей")]
    [SerializeField] private bool usePersistentPlanes = true;
    [SerializeField] private bool highlightPersistentPlanes = false;
    [SerializeField] private Color persistentPlaneColor = new Color(0.0f, 0.8f, 0.2f, 0.7f);
    private Dictionary<GameObject, bool> persistentGeneratedPlanes = new Dictionary<GameObject, bool>();
    private Dictionary<GameObject, float> planeCreationTimes = new Dictionary<GameObject, float>();
    private Dictionary<GameObject, float> planeLastVisitedTime = new Dictionary<GameObject, float>();

    [Tooltip("Материал для вертикальных плоскостей")]
    [SerializeField] private Material verticalPlaneMaterial;
    [Tooltip("Материал для горизонтальных плоскостей")]
    [SerializeField] private Material horizontalPlaneMaterial;

    [Header("Отладочная Визуализация Лучей")]
    [SerializeField] private Material debugRayMaterial;
    private MaterialPropertyBlock debugRayMaterialPropertyBlock;

    private UnityEngine.UI.RawImage отображениеМаскиUI;
    private RenderTexture currentSegmentationMask;
    private RenderTexture lowResSegmentationMask;
    private bool newMaskAvailableForProcessing = false;
    private List<GameObject> generatedPlanes = new List<GameObject>();
    private int frameCounter = 0;
    private float lastSuccessfulSegmentationTime = 0f;
    private int trackablesParentInstanceID_FromStart = 0;
    private int lowResMaskSaveCounter = 0; // NEW: Counter for saved low-res masks

    [Header("Настройки Кластеризации Рейкастов")]
    public bool enableRaycastClustering = true;
    public int clusteringMinHitsThreshold = 1;

    [SerializeField] private ARPlaneConfigurator planeConfigurator;
    [SerializeField] private WallSegmentation wallSegmentation;

    [Header("Отладка ARManagerInitializer2")]
    public ARManagerDebugFlags debugFlags = ARManagerDebugFlags.None; // Default value for custom logs

    [Header("Настройки Выделения Плоскостей")]
    [Tooltip("Материал для выделенной AR плоскости. Если не задан, будет изменен цвет текущего материала.")]
    public Material selectedPlaneMaterial;
    [Tooltip("Материал, используемый для покраски стен. Должен иметь свойство _PaintColor и _SegmentationMask.")]
    public Material paintMaterial;
    private ARPlane currentlySelectedPlane;
    private Material originalSelectedPlaneMaterial;
    private Dictionary<ARPlane, Material> paintedPlaneOriginalMaterials = new Dictionary<ARPlane, Material>();

    private ARWallPaintColorManager colorManager;
    private bool isSubscribedToWallSegmentation = false; // Flag to track subscription

    // Custom Log methods (controlled by debugFlags)
    private void Log(string message, ARManagerDebugFlags flag = ARManagerDebugFlags.None, ARManagerLogLevel level = ARManagerLogLevel.Info)
    {
        if ((debugFlags & flag) == flag || flag == ARManagerDebugFlags.All || flag == ARManagerDebugFlags.None || debugFlags == ARManagerDebugFlags.All)
        {
            string prefix = $"[{this.GetType().Name}] ";
            if (level == ARManagerLogLevel.Error) Debug.LogError(prefix + message);
            else if (level == ARManagerLogLevel.Warning) Debug.LogWarning(prefix + message);
            else Debug.Log(prefix + message);
        }
    }
    private void LogError(string message, ARManagerDebugFlags flag = ARManagerDebugFlags.None) => Log(message, flag, ARManagerLogLevel.Error);
    private void LogWarning(string message, ARManagerDebugFlags flag = ARManagerDebugFlags.None) => Log(message, flag, ARManagerLogLevel.Warning);

    private void Awake()
    {
        Debug.Log($"[{this.GetType().Name}] AWAKE_METHOD_ENTERED_TOP (Direct Log)"); // Direct log
        debugRayMaterial = null; // <--- FORCE NULL FOR TESTING MATERIAL CREATION
        Debug.LogWarning($"[{this.GetType().Name}] Awake: DEBUG - Forcing debugRayMaterial to null to test default material creation. (Direct Log)");

        if (Instance != null && Instance != this)
        {
            Debug.LogWarning($"[{this.GetType().Name}] Duplicate instance of ARManagerInitializer2 detected. Destroying this one. (Direct Log)"); // Direct log
            Destroy(gameObject);
            return;
        }
        Instance = this;
        planeInstanceCounter = 0;

        hitLayerMask = ~0; // TEMPORARY TEST: Raycast against all layers
        Debug.LogWarning($"[{this.GetType().Name}] Awake: TEMPORARY TEST - hitLayerMask set to ALL LAYERS (~0). Value: {hitLayerMask.value} (Direct Log)");


        ARManagerDebugFlags initialDebugFlags = debugFlags; // Store inspector value
        Debug.Log($"[{this.GetType().Name}] Awake: Initial debugFlags from Inspector = {initialDebugFlags} (Direct Log)"); // Direct log
        debugFlags = initialDebugFlags; // Ensure we use the inspector value, not force All

        if (debugRayMaterialPropertyBlock == null)
        {
            debugRayMaterialPropertyBlock = new MaterialPropertyBlock();
            Debug.Log($"[{this.GetType().Name}] Awake: Initialized debugRayMaterialPropertyBlock. (Direct Log)");
        }

        if (enableDetailedRaycastLogging)
        {
            Debug.Log($"[{this.GetType().Name}] Awake: enableDetailedRaycastLogging is TRUE. (Direct Log)");
        }

        if (debugRayMaterial == null)
        {
            Shader unlitShader = Shader.Find("Unlit/Color");
            if (unlitShader != null)
            {
                debugRayMaterial = new Material(unlitShader);
                debugRayMaterial.color = Color.magenta; // Default color
                Debug.LogWarning($"[{this.GetType().Name}] Awake: debugRayMaterial was null and has been initialized with a default Magenta Unlit/Color material. (Direct Log)");
            }
            else
            {
                Debug.LogError($"[{this.GetType().Name}] Awake: debugRayMaterial is null and Shader.Find(\"Unlit/Color\") failed. Debug rays will not be visible. (Direct Log)");
            }
        }


        if (transform.parent == null)
        {
            Log("Making it DontDestroyOnLoad as it's a root object.", ARManagerDebugFlags.System);
        }


        if (wallSegmentation == null)
        {
            Debug.LogError($"[{this.GetType().Name}] Awake: WallSegmentation component is not assigned in the inspector! Custom plane generation will not work. (Direct Log)");
        }

        FindARComponents();
        Debug.Log($"[{this.GetType().Name}] AWAKE_METHOD_EXIT. wallSegmentation is {(wallSegmentation == null ? "NOT ASSIGNED" : "ASSIGNED")}. debugFlags: {debugFlags} (Direct Log)");
    }

    private void OnEnable()
    {
        Debug.Log($"[{this.GetType().Name}] OnEnable: Method Entered. wallSegmentation is {(wallSegmentation == null ? "NULL" : "ASSIGNED")}. Current debugFlags: {debugFlags} (Direct Log)"); // Direct log
        SubscribeToWallSegmentation();
        InitializeLightEstimation();
        InitializeEnvironmentProbes();
        InitializeSceneReconstruction();
        if (arCameraManager != null) arCameraManager.frameReceived += OnARFrameReceived;
        if (arMeshManager != null) arMeshManager.meshesChanged += OnMeshesChanged;
        Debug.Log($"[{this.GetType().Name}] OnEnable: Subscriptions attempted. wallSegmentation is {(wallSegmentation == null ? "NULL" : "ASSIGNED")} (Direct Log)"); // Direct log
    }

    private void Start()
    {
        Debug.Log($"[{this.GetType().Name}] START_METHOD_ENTERED_TOP (Direct Log)"); // Direct log
        Debug.Log($"[{this.GetType().Name}] Start: Initial debugFlags from Inspector = {debugFlags} (Direct Log)"); // Direct log

        Debug.Log($"[{this.GetType().Name}] Start: Before FindARComponents. wallSegmentation is {(wallSegmentation == null ? "NULL" : "ASSIGNED")} (Direct Log)"); // Direct log
        FindARComponents();
        Debug.Log($"[{this.GetType().Name}] Start: After FindARComponents. wallSegmentation is {(wallSegmentation == null ? "NULL" : "ASSIGNED")} (Direct Log)"); // Direct log

        InitializeMaterials();

        Debug.Log($"[{this.GetType().Name}] Start: About to call SubscribeToWallSegmentation. wallSegmentation is {(wallSegmentation == null ? "NULL" : "ASSIGNED")} (Direct Log)"); // Direct log
        SubscribeToWallSegmentation(); // Attempt subscription from Start

        InitializeLightEstimation();
        InitializeEnvironmentProbes();
        InitializeSceneReconstruction();

        int planeLayer = LayerMask.NameToLayer(planeLayerName);
        if (planeLayer != -1)
        {
            Debug.Log($"[{this.GetType().Name}] Start: Layer '{planeLayerName}' (ID: {planeLayer}) is intended for generated planes. Ensure this layer is included in general raycasts if needed or use a specific mask for plane creation raycasts. (Direct Log)");
        }
        else
        {
            Debug.LogWarning($"[{this.GetType().Name}] Start: Layer '{planeLayerName}' for generated planes not found. Please define it in Tags and Layers. (Direct Log)");
        }


        if (wallSegmentation == null)
        {
            Debug.LogError($"[{this.GetType().Name}] Start: WallSegmentation is still NULL after Awake and FindARComponents in Start! (Direct Log)");
        }

        if (!useDetectedPlanes)
        {
            if (planeManager != null)
            {
                planeManager.enabled = false;
                Debug.Log($"[{this.GetType().Name}] Start: ARPlaneManager disabled because useDetectedPlanes is false. (Direct Log)");
            }
            if (planeConfigurator != null)
            {
                planeConfigurator.enabled = false;
                Debug.Log($"[{this.GetType().Name}] Start: ARPlaneConfigurator component disabled because useDetectedPlanes is false. (Direct Log)");
            }
        }

        colorManager = FindObjectOfType<ARWallPaintColorManager>();
        if (colorManager == null)
        {
            Debug.LogError($"[{this.GetType().Name}] Start: ARWallPaintColorManager не найден на сцене! (Direct Log)");
        }
        Debug.Log($"[{this.GetType().Name}] Start() CОMPLETED. wallSegmentation is {(wallSegmentation == null ? "NULL" : "ASSIGNED")}. Raycast LayerMask (general): {LayerMaskToString(hitLayerMask)} (Direct Log)");
    }

    private void Update()
    {
        frameCounter++;

        if (newMaskAvailableForProcessing && !useDetectedPlanes)
        {
            Log("Update: Calling ProcessSegmentationMask() because newMaskAvailableForProcessing is true", ARManagerDebugFlags.PlaneGeneration);
            if (lowResSegmentationMask != null && lowResSegmentationMask.IsCreated())
            {
                ProcessSegmentationMask(lowResSegmentationMask);
            }
            else
            {
                LogWarning("Update: newMaskAvailableForProcessing is true, but lowResSegmentationMask is null or not created. Skipping ProcessSegmentationMask.", ARManagerDebugFlags.PlaneGeneration);
            }
            newMaskAvailableForProcessing = false;
        }

        if (frameCounter % 300 == 0)
        {
            CleanupOldPlanes(null);
        }

        HandlePlaneSelectionByTap();

        if (!useDetectedPlanes && planeManager != null && planeManager.enabled)
        {
            LogWarning("ARPlaneManager was found enabled in Update, despite useDetectedPlanes = false. Forcibly disabling.", ARManagerDebugFlags.Initialization);
            planeManager.enabled = false;
        }
        if (!useDetectedPlanes && planeConfigurator != null && planeConfigurator.enabled)
        {
            LogWarning("ARPlaneConfigurator was found enabled in Update, despite useDetectedPlanes = false. Forcibly disabling.", ARManagerDebugFlags.Initialization);
            planeConfigurator.enabled = false;
        }
    }

    private void SubscribeToWallSegmentation()
    {
        Debug.Log($"[{this.GetType().Name}] SubscribeToWallSegmentation: Method Entered. wallSegmentation is {(wallSegmentation == null ? "NULL" : "ASSIGNED")}, isSubscribed: {isSubscribedToWallSegmentation} (Direct Log)");

        if (wallSegmentation == null)
        {
            Debug.LogWarning($"[{this.GetType().Name}] SubscribeToWallSegmentation: wallSegmentation was null, trying FindObjectOfType. (Direct Log)");
            wallSegmentation = FindObjectOfType<WallSegmentation>();
        }

        if (wallSegmentation != null)
        {
            if (!isSubscribedToWallSegmentation)
            {
                Debug.Log($"[{this.GetType().Name}] SubscribeToWallSegmentation: Attempting to subscribe to WallSegmentation.OnSegmentationMaskUpdated. (Direct Log)");
                wallSegmentation.OnSegmentationMaskUpdated += OnSegmentationMaskUpdated;
                isSubscribedToWallSegmentation = true;
                Debug.Log($"[{this.GetType().Name}] SubscribeToWallSegmentation: Successfully subscribed. isSubscribedToWallSegmentation = {isSubscribedToWallSegmentation} (Direct Log)");

                if (wallSegmentation.IsModelInitialized && wallSegmentation.segmentationMaskTexture != null && wallSegmentation.segmentationMaskTexture.IsCreated())
                {
                    Debug.Log($"[{this.GetType().Name}] SubscribeToWallSegmentation: WallSegmentation model is initialized and has a mask. Requesting initial mask. (Direct Log)");
                    OnSegmentationMaskUpdated(wallSegmentation.segmentationMaskTexture);
                }
                else
                {
                    Debug.Log($"[{this.GetType().Name}] SubscribeToWallSegmentation: WallSegmentation model not yet initialized or mask not ready. Will wait for event. (Direct Log)");
                }
            }
            else
            {
                Debug.Log($"[{this.GetType().Name}] SubscribeToWallSegmentation: Already subscribed to WallSegmentation.OnSegmentationMaskUpdated. (Direct Log)");
            }
        }
        else
        {
            Debug.LogError($"[{this.GetType().Name}] SubscribeToWallSegmentation: WallSegmentation instance is NULL. Cannot subscribe. (Direct Log)");
            if (Application.isPlaying && gameObject.activeInHierarchy && enabled)
            {
                Debug.LogWarning($"[{this.GetType().Name}] SubscribeToWallSegmentation: Retrying subscription to WallSegmentation soon... (Direct Log)");
                StartCoroutine(RetrySubscriptionAfterDelay(1.0f));
            }
        }
    }

    private IEnumerator RetrySubscriptionAfterDelay(float delay)
    {
        Debug.LogWarning($"[{this.GetType().Name}] RetrySubscriptionAfterDelay: Will retry WallSegmentation subscription in {delay} seconds. (Direct Log)");
        yield return new WaitForSeconds(delay);
        Debug.LogWarning($"[{this.GetType().Name}] RetrySubscriptionAfterDelay: Retrying WallSegmentation subscription NOW. (Direct Log)");
        SubscribeToWallSegmentation();
    }

    private void OnDisable()
    {
        Debug.Log($"[{this.GetType().Name}] OnDisable: Method Entered. (Direct Log)");
        if (wallSegmentation != null && isSubscribedToWallSegmentation)
        {
            Debug.Log($"[{this.GetType().Name}] OnDisable: Unsubscribing from WallSegmentation.OnSegmentationMaskUpdated. (Direct Log)");
            wallSegmentation.OnSegmentationMaskUpdated -= OnSegmentationMaskUpdated;
            isSubscribedToWallSegmentation = false;
        }
        else if (wallSegmentation == null)
        {
            Debug.LogWarning($"[{this.GetType().Name}] OnDisable: wallSegmentation is null, cannot unsubscribe. (Direct Log)");
        }
        else if (!isSubscribedToWallSegmentation)
        {
            Debug.LogWarning($"[{this.GetType().Name}] OnDisable: Was not subscribed to wallSegmentation, no need to unsubscribe. (Direct Log)");
        }

        if (arCameraManager != null) arCameraManager.frameReceived -= OnARFrameReceived;
        if (arMeshManager != null) arMeshManager.meshesChanged -= OnMeshesChanged;
        Log("ARManagerInitializer2 OnDisable: Other event unsubscriptions completed.", ARManagerDebugFlags.System);
    }

    private void InitializeLightEstimation()
    {
        if (arCameraManager != null && arDirectionalLight != null)
        {
            arCameraManager.frameReceived -= OnARFrameReceived;
            arCameraManager.frameReceived += OnARFrameReceived;
            Log("Subscribed to ARCameraManager.frameReceived for light estimation.", ARManagerDebugFlags.Initialization);
        }
        else
        {
            if (arCameraManager == null) LogWarning("ARCameraManager not found, cannot initialize light estimation.", ARManagerDebugFlags.Initialization);
            if (arDirectionalLight == null) LogWarning("AR Directional Light not assigned, cannot initialize light estimation.", ARManagerDebugFlags.Initialization);
        }
    }

    private void InitializeEnvironmentProbes()
    {
        LogWarning("Environment probe initialization currently disabled.", ARManagerDebugFlags.Initialization);
    }

    private void InitializeSceneReconstruction()
    {
        if (!enableSceneReconstruction)
        {
            Log("Scene Reconstruction is administratively disabled by 'enableSceneReconstruction' flag.", ARManagerDebugFlags.Initialization);
            if (arMeshManager != null) arMeshManager.enabled = false;
            return;
        }

        if (arMeshManager == null)
        {
            LogWarning("ARMeshManager component is not assigned. Scene Reconstruction cannot be enabled.", ARManagerDebugFlags.Initialization);
            enableSceneReconstruction = false;
            return;
        }

        if (arMeshManager.subsystem != null)
        {
            try
            {
                arMeshManager.enabled = true;
                if (arMeshManager.subsystem.running)
                {
                    Log($"✅ ARMeshManager enabled and subsystem is running. Scene Reconstruction active.", ARManagerDebugFlags.ARSystem);
                }
                else
                {
                    LogWarning($"ARMeshManager enabled, but subsystem is not running. Scene Reconstruction might not be fully active. Subsystem state: {arMeshManager.subsystem.running}", ARManagerDebugFlags.ARSystem);
                }
            }
            catch (System.Exception e)
            {
                LogError($"Error enabling ARMeshManager or interacting with its subsystem: {e.Message}. Disabling Scene Reconstruction.", ARManagerDebugFlags.ARSystem);
                arMeshManager.enabled = false;
                enableSceneReconstruction = false;
            }
        }
        else
        {
            LogWarning($"ARMeshManager subsystem is not available (subsystem is null). Disabling Scene Reconstruction and ARMeshManager.", ARManagerDebugFlags.ARSystem);
            arMeshManager.enabled = false;
            enableSceneReconstruction = false;
        }
    }

    public void УстановитьОтображениеМаскиUI(UnityEngine.UI.RawImage rawImageДляУстановки)
    {
        if (rawImageДляУстановки != null)
        {
            отображениеМаскиUI = rawImageДляУстановки;
            отображениеМаскиUI.raycastTarget = false;
            Log("Raycast Target disabled for mask UI.", ARManagerDebugFlags.Initialization, ARManagerLogLevel.Info);
            if (currentSegmentationMask != null && отображениеМаскиUI.texture == null)
            {
                отображениеМаскиUI.texture = currentSegmentationMask;
                отображениеМаскиUI.gameObject.SetActive(true);
            }
        }
    }

    private void FindARComponents()
    {
        Log($"[{this.GetType().Name}] FindARComponents: Method Entered (Direct Log - called from Awake/Start)", ARManagerDebugFlags.Initialization);
        bool originFound = xrOrigin != null;
        if (xrOrigin == null)
        {
            xrOrigin = FindObjectOfType<XROrigin>();
            if (xrOrigin != null) Log("XROrigin found via FindObjectOfType.", ARManagerDebugFlags.Initialization);
            else LogError("XROrigin NOT FOUND in scene! Many AR functions will fail.", ARManagerDebugFlags.Initialization);
        }

        if (xrOrigin != null && arCameraManager == null) arCameraManager = xrOrigin.CameraFloorOffsetObject?.GetComponentInChildren<ARCameraManager>();
        if (arCameraManager == null) arCameraManager = FindObjectOfType<ARCameraManager>();

        if (xrOrigin != null && planeManager == null) planeManager = xrOrigin.GetComponentInChildren<ARPlaneManager>();
        if (planeManager == null) planeManager = FindObjectOfType<ARPlaneManager>();

        if (xrOrigin != null && arRaycastManager == null) arRaycastManager = xrOrigin.GetComponentInChildren<ARRaycastManager>();
        if (arRaycastManager == null) arRaycastManager = FindObjectOfType<ARRaycastManager>();

        if (xrOrigin != null && arOcclusionManager == null) arOcclusionManager = xrOrigin.CameraFloorOffsetObject?.GetComponentInChildren<AROcclusionManager>();
        if (arOcclusionManager == null) arOcclusionManager = FindObjectOfType<AROcclusionManager>();

        if (xrOrigin != null && arMeshManager == null) arMeshManager = xrOrigin.GetComponentInChildren<ARMeshManager>();
        if (arMeshManager == null) arMeshManager = FindObjectOfType<ARMeshManager>();

        if (xrOrigin != null && arAnchorManager == null) arAnchorManager = xrOrigin.GetComponentInChildren<ARAnchorManager>();
        if (arAnchorManager == null) arAnchorManager = FindObjectOfType<ARAnchorManager>();

        if (arDirectionalLight == null)
        {
            var lights = FindObjectsOfType<Light>();
            arDirectionalLight = lights.FirstOrDefault(l => l.type == LightType.Directional);
            if (arDirectionalLight != null) LogWarning("AR Directional Light auto-assigned. Please assign manually for reliability.", ARManagerDebugFlags.Initialization);
            else LogWarning("AR Directional Light not assigned and not found. Light estimation may not work.", ARManagerDebugFlags.Initialization);
        }

        if (wallSegmentation == null) wallSegmentation = FindObjectOfType<WallSegmentation>();
        if (planeConfigurator == null) planeConfigurator = FindObjectOfType<ARPlaneConfigurator>();

        string report = "FindARComponents Report:\n";
        report += $"  XROrigin: {(xrOrigin != null ? xrOrigin.name : "NULL")}{(originFound ? " (was pre-assigned)" : "")}\n";
        report += $"  ARCameraManager: {(arCameraManager != null ? "Found" : "NULL")}\n";
        report += $"  ARPlaneManager: {(planeManager != null ? "Found" : "NULL")}\n";
        report += $"  ARRaycastManager: {(arRaycastManager != null ? "Found" : "NULL")}\n";
        report += $"  AROcclusionManager: {(arOcclusionManager != null ? "Found" : "NULL (Optional)")}\n";
        report += $"  ARMeshManager: {(arMeshManager != null ? "Found" : "NULL")}\n";
        report += $"  ARAnchorManager: {(arAnchorManager != null ? "Found" : "NULL")}\n";
        report += $"  AR Directional Light: {(arDirectionalLight != null ? arDirectionalLight.name : "NULL")}\n";
        report += $"  WallSegmentation: {(wallSegmentation != null ? "Found" : "NULL")}\n";
        report += $"  ARPlaneConfigurator: {(planeConfigurator != null ? "Found" : "NULL")}\n";
        Log(report, ARManagerDebugFlags.Initialization);
    }

    private void InitializeMaterials()
    {
        if (verticalPlaneMaterial == null)
        {
            LogWarning("VerticalPlaneMaterial is not assigned. Using fallback Standard shader.", ARManagerDebugFlags.Initialization);
            verticalPlaneMaterial = new Material(Shader.Find("Standard"));
        }
        if (horizontalPlaneMaterial == null)
        {
            LogWarning("HorizontalPlaneMaterial is not assigned. Using fallback Standard shader.", ARManagerDebugFlags.Initialization);
            horizontalPlaneMaterial = new Material(Shader.Find("Standard"));
        }
    }

    private void OnARFrameReceived(ARCameraFrameEventArgs eventArgs)
    {
        if (arDirectionalLight == null) return;
        ARLightEstimationData lightEstimation = eventArgs.lightEstimation;
        if (lightEstimation.averageBrightness.HasValue) arDirectionalLight.intensity = lightEstimation.averageBrightness.Value;
        if (lightEstimation.averageColorTemperature.HasValue) arDirectionalLight.colorTemperature = lightEstimation.averageColorTemperature.Value;
        if (lightEstimation.colorCorrection.HasValue) arDirectionalLight.color = lightEstimation.colorCorrection.Value;
        if (lightEstimation.mainLightDirection.HasValue) arDirectionalLight.transform.rotation = Quaternion.LookRotation(lightEstimation.mainLightDirection.Value);
        if (lightEstimation.mainLightIntensityLumens.HasValue) arDirectionalLight.intensity = lightEstimation.mainLightIntensityLumens.Value / 1000.0f;
    }

    private void OnSegmentationMaskUpdated(RenderTexture mask)
    {
        // Debug.Log($"[{this.GetType().Name}] OnSegmentationMaskUpdated RECEIVED MASK. Name: {mask?.name}, Width: {mask?.width}, Height: {mask?.height}, IsCreated: {mask?.IsCreated()} (Direct Log)");
        Log($"OnSegmentationMaskUpdated RECEIVED MASK. Name: {mask?.name}, Width: {mask?.width}, Height: {mask?.height}, IsCreated: {mask?.IsCreated()}", ARManagerDebugFlags.System | ARManagerDebugFlags.PlaneGeneration);

        if (mask == null || !mask.IsCreated())
        {
            LogWarning("OnSegmentationMaskUpdated: Received null or !IsCreated mask. Aborting.", ARManagerDebugFlags.PlaneGeneration);
            return;
        }
        currentSegmentationMask = mask;

        if (!useDetectedPlanes)
        {
            if (wallSegmentation != null && wallSegmentation.TryGetLowResMask(out RenderTexture lrMask))
            {
                this.lowResSegmentationMask = lrMask;
                newMaskAvailableForProcessing = true;
                // Debug.Log($"[{this.GetType().Name}] OnSegmentationMaskUpdated: New low-res mask received (Name: {lrMask?.name}, W: {lrMask?.width}, H: {lrMask?.height}, Created: {lrMask?.IsCreated()}) and flagged for processing. (Direct Log)");
                Log($"OnSegmentationMaskUpdated: New low-res mask received (Name: {lrMask?.name}, W: {lrMask?.width}, H: {lrMask?.height}, Created: {lrMask?.IsCreated()}) and flagged for processing.", ARManagerDebugFlags.PlaneGeneration);
            }
            else
            {
                // Debug.LogWarning($"[{this.GetType().Name}] OnSegmentationMaskUpdated: Could not get low-res mask from WallSegmentation. wallSegmentation null: {(wallSegmentation == null)} (Direct Log)");
                LogWarning($"OnSegmentationMaskUpdated: Could not get low-res mask from WallSegmentation. wallSegmentation null: {(wallSegmentation == null)}", ARManagerDebugFlags.PlaneGeneration);
            }
        }
        lastSuccessfulSegmentationTime = Time.time;
        if (отображениеМаскиUI != null)
        {
            отображениеМаскиUI.texture = currentSegmentationMask;
            if (!отображениеМаскиUI.gameObject.activeSelf) отображениеМаскиUI.gameObject.SetActive(true);
        }
    }

    private void OnMeshesChanged(ARMeshesChangedEventArgs args)
    {
        Log($"OnMeshesChanged CALLED. Added: {args.added.Count}, Updated: {args.updated.Count}, Removed: {args.removed.Count}", ARManagerDebugFlags.ARSystem);
        foreach (var meshFilter in args.added) EnsureMeshCollider(meshFilter);
        foreach (var meshFilter in args.updated) EnsureMeshCollider(meshFilter);
        if (args.removed.Count > 0) Log($"OnMeshesChanged: {args.removed.Count} mesh(es) removed.", ARManagerDebugFlags.ARSystem);
    }

    private void EnsureMeshCollider(MeshFilter meshFilter)
    {
        if (meshFilter == null || meshFilter.gameObject == null)
        {
            LogWarning("EnsureMeshCollider: MeshFilter or its GameObject is null.", ARManagerDebugFlags.ARSystem);
            return;
        }
        MeshCollider meshCollider = meshFilter.gameObject.GetComponent<MeshCollider>();
        if (meshCollider == null) meshCollider = meshFilter.gameObject.AddComponent<MeshCollider>();
        if (meshFilter.sharedMesh != null) meshCollider.sharedMesh = meshFilter.sharedMesh;
        else LogWarning($"EnsureMeshCollider: MeshFilter on {meshFilter.gameObject.name} has no sharedMesh.", ARManagerDebugFlags.ARSystem);
    }

    private void ProcessSegmentationMask(RenderTexture maskToProcess)
    {
        // Log($"ProcessSegmentationMask: ENTERED. MaskToProcess Name: {maskToProcess?.name}, IsCreated: {maskToProcess?.IsCreated()} (Direct Log)", ARManagerDebugFlags.PlaneGeneration | ARManagerDebugFlags.System, ARManagerLogLevel.Info); // Direct log
        Log($"ProcessSegmentationMask: ENTERED. MaskToProcess Name: {maskToProcess?.name}, IsCreated: {maskToProcess?.IsCreated()}", ARManagerDebugFlags.PlaneGeneration | ARManagerDebugFlags.System);


        if (maskToProcess == null || !maskToProcess.IsCreated())
        {
            // LogError($"ProcessSegmentationMask: maskToProcess is null or not created. Aborting. (Direct Log)", ARManagerDebugFlags.PlaneGeneration | ARManagerDebugFlags.System);
            LogError($"ProcessSegmentationMask: maskToProcess is null or not created. Aborting.", ARManagerDebugFlags.PlaneGeneration | ARManagerDebugFlags.System);
            return;
        }

        // NEW: Save the low-resolution mask for debugging
        if ((debugFlags & ARManagerDebugFlags.SaveDebugTextures) != 0) // MODIFIED: Control saving with SaveDebugTextures flag
        {
            SaveLowResMaskForDebug(maskToProcess, $"LowResMaskInput_F{frameCounter}_C{lowResMaskSaveCounter++}.png");
        }

        Texture2D maskTexture = null;

        // Debug.Log($"[{this.GetType().Name}] ProcessSegmentationMask: About to call RenderTextureToTexture2D with mask: {maskToProcess.name}, W:{maskToProcess.width}, H:{maskToProcess.height} (Direct Log)");
        Log($"ProcessSegmentationMask: About to call RenderTextureToTexture2D with mask: {maskToProcess.name}, W:{maskToProcess.width}, H:{maskToProcess.height}", ARManagerDebugFlags.PlaneGeneration);
        maskTexture = RenderTextureToTexture2D(maskToProcess);

        if (maskTexture == null)
        {
            // Debug.LogError($"[{this.GetType().Name}] ProcessSegmentationMask: RenderTextureToTexture2D returned null for maskToProcess '{maskToProcess.name}'. Aborting. (Direct Log)");
            LogError($"ProcessSegmentationMask: RenderTextureToTexture2D returned null for maskToProcess '{maskToProcess.name}'. Aborting.", ARManagerDebugFlags.PlaneGeneration);
            return;
        }
        // Debug.Log($"[{this.GetType().Name}] ProcessSegmentationMask: RenderTextureToTexture2D returned a texture. Name: {maskTexture.name}, W:{maskTexture.width}, H:{maskTexture.height} (Direct Log)");
        Log($"ProcessSegmentationMask: RenderTextureToTexture2D returned a texture. Name: {maskTexture.name}, W:{maskTexture.width}, H:{maskTexture.height}", ARManagerDebugFlags.PlaneGeneration);

        Log($"ProcessSegmentationMask: maskTexture (from maskToProcess) created: {maskTexture.width}x{maskTexture.height}", ARManagerDebugFlags.PlaneGeneration);

        Color32[] pixels = null;
        int width = 0;
        int height = 0;

        try
        {
            // Debug.Log($"[{this.GetType().Name}] ProcessSegmentationMask: About to call maskTexture.GetPixels32(). Texture: {maskTexture.name}, W:{maskTexture.width}, H:{maskTexture.height}, Format:{maskTexture.format}, Mipmaps:{maskTexture.mipmapCount} (Direct Log)");
            Log($"ProcessSegmentationMask: About to call maskTexture.GetPixels32(). Texture: {maskTexture.name}, W:{maskTexture.width}, H:{maskTexture.height}, Format:{maskTexture.format}, Mipmaps:{maskTexture.mipmapCount}", ARManagerDebugFlags.PlaneGeneration);
            pixels = maskTexture.GetPixels32();
            Log($"[{this.GetType().Name}] ProcessSegmentationMask: maskTexture.GetPixels32() SUCCEEDED. Pixels array length: {pixels.Length}", ARManagerDebugFlags.PlaneGeneration); // Changed Debug.Log to Log

            // NEW DETAILED PIXEL LOGGING
            if (pixels != null && pixels.Length > 0)
            {
                System.Text.StringBuilder pixelLog = new System.Text.StringBuilder();
                pixelLog.AppendLine($"[{GetType().Name}] ProcessSegmentationMask: Detailed Pixel Values (first 5 and some from middle/end) before FindWallAreas. Threshold: {wallAreaRedChannelThreshold}");
                int step = pixels.Length / 10; // Log a few pixels spread out
                if (step == 0) step = 1;

                for (int i = 0; i < pixels.Length; i += (i < 20 || i > pixels.Length - 20) ? 1 : step) // Log more at start/end
                {
                    if (i < 5 || (i >= pixels.Length / 2 && i < pixels.Length / 2 + 5) || i >= pixels.Length - 5) // Check first 5, 5 from middle, last 5
                    {
                        pixelLog.AppendLine($"  Pixel[{i / (maskTexture?.width ?? 1)}, {i % (maskTexture?.width ?? 1)}] (Raw Index: {i}): R={pixels[i].r}, G={pixels[i].g}, B={pixels[i].b}, A={pixels[i].a} -> IsWall: {pixels[i].r >= wallAreaRedChannelThreshold}");
                    }
                    if (i > 200 && i < pixels.Length / 2) i = pixels.Length / 2 - 1; // Jump to middle after initial logs
                    if (i > pixels.Length / 2 + 20 && i < pixels.Length - 20) i = pixels.Length - 20 - 1; // Jump to end after middle logs
                }
                Log(pixelLog.ToString(), ARManagerDebugFlags.PlaneGeneration);
            }
            // END NEW DETAILED PIXEL LOGGING

            width = maskTexture.width;
            height = maskTexture.height;
        }
        catch (Exception e)
        {
            // Debug.LogError($"[{this.GetType().Name}] ProcessSegmentationMask: EXCEPTION during maskTexture.GetPixels32(). Message: {e.Message}\\nTexture Info: Name={maskTexture?.name}, W={maskTexture?.width}, H={maskTexture?.height}, Format={maskTexture?.format}, IsReadable={maskTexture?.isReadable}\\nStackTrace: {e.StackTrace} (Direct Log)");
            LogError($"ProcessSegmentationMask: EXCEPTION during maskTexture.GetPixels32(). Message: {e.Message}\nTexture Info: Name={maskTexture?.name}, W={maskTexture?.width}, H={maskTexture?.height}, Format={maskTexture?.format}, IsReadable={maskTexture?.isReadable}\nStackTrace: {e.StackTrace}", ARManagerDebugFlags.PlaneGeneration);
            Destroy(maskTexture); // Destroy the texture if an error occurs
            return; // Abort further processing
        }
        finally
        {
            // Always destroy the maskTexture after attempting to get pixels, regardless of success or failure, unless it was already destroyed in the catch block.
            if (maskTexture != null) // Check if it wasn't destroyed in catch
            {
                Destroy(maskTexture);
                // Debug.Log($"[{this.GetType().Name}] ProcessSegmentationMask: maskTexture destroyed in finally block. (Direct Log)");
                Log($"ProcessSegmentationMask: maskTexture destroyed in finally block.", ARManagerDebugFlags.PlaneGeneration);
            }
        }

        if (pixels == null)
        {
            // Debug.LogError($"[{this.GetType().Name}] ProcessSegmentationMask: Pixels array is null after GetPixels32 attempt. Aborting. (Direct Log)");
            LogError($"ProcessSegmentationMask: Pixels array is null after GetPixels32 attempt. Aborting.", ARManagerDebugFlags.PlaneGeneration);
            return;
        }

        List<Rect> wallAreas = FindWallAreas(pixels, width, height, wallAreaRedChannelThreshold);
        Log($"ProcessSegmentationMask: FindWallAreas found {wallAreas.Count} areas from {width}x{height} mask. Threshold: {wallAreaRedChannelThreshold}", ARManagerDebugFlags.PlaneGeneration);
        // Debug.Log($"[{this.GetType().Name}] ProcessSegmentationMask: FindWallAreas returned {wallAreas.Count} areas. (Direct Log)");
        Log($"ProcessSegmentationMask: FindWallAreas returned {wallAreas.Count} areas.", ARManagerDebugFlags.PlaneGeneration);

        Dictionary<GameObject, bool> visitedPlanesInCurrentMask = new Dictionary<GameObject, bool>();
        int processedAreas = 0;
        Debug.Log($"[{this.GetType().Name}] ProcessSegmentationMask: About to loop through {wallAreas.Count} found areas. (Direct Log)");
        foreach (Rect area in wallAreas)
        {
            Debug.Log($"[{this.GetType().Name}] ProcessSegmentationMask: LOOP_START for area {processedAreas + 1}/{wallAreas.Count}: {areaToString(area)} (Direct Log)");
            Log($"ProcessSegmentationMask: Processing area {processedAreas + 1}/{wallAreas.Count}: {areaToString(area)}", ARManagerDebugFlags.PlaneGeneration);

            Debug.Log($"[{this.GetType().Name}] ProcessSegmentationMask: Area W={area.width}, H={area.height}, Size={area.width * area.height}. MinDims={minPixelsDimensionForLowResArea}, MinAreaPx={minAreaSizeInLowResPixels} (Direct Log)");
            if (area.width < minPixelsDimensionForLowResArea || area.height < minPixelsDimensionForLowResArea || (area.width * area.height) < minAreaSizeInLowResPixels)
            {
                Debug.Log($"[{this.GetType().Name}] ProcessSegmentationMask: SKIPPING small area {areaToString(area)}. (Direct Log)");
                Log($"Skipping small area on low-res mask: {areaToString(area)}. MinDims: {minPixelsDimensionForLowResArea}, MinAreaPx: {minAreaSizeInLowResPixels}", ARManagerDebugFlags.PlaneGeneration);
                processedAreas++;
                continue;
            }
            Debug.Log($"[{this.GetType().Name}] ProcessSegmentationMask: Calling UpdateOrCreatePlaneForWallArea for area {areaToString(area)}. (Direct Log)");
            bool planeProcessed = UpdateOrCreatePlaneForWallArea(area, width, height, visitedPlanesInCurrentMask);
            Debug.Log($"[{this.GetType().Name}] ProcessSegmentationMask: UpdateOrCreatePlaneForWallArea returned {planeProcessed} for area {areaToString(area)}. (Direct Log)");
            Log($"ProcessSegmentationMask: UpdateOrCreatePlaneForWallArea returned {planeProcessed} for area {areaToString(area)}", ARManagerDebugFlags.PlaneGeneration);
            processedAreas++;
            Debug.Log($"[{this.GetType().Name}] ProcessSegmentationMask: LOOP_END for area {processedAreas}/{wallAreas.Count}. (Direct Log)");
        }
        Debug.Log($"[{this.GetType().Name}] ProcessSegmentationMask: Finished looping through areas. Processed {processedAreas} areas. About to call CleanupOldPlanes. (Direct Log)");
        CleanupOldPlanes(visitedPlanesInCurrentMask);
        Log($"ProcessSegmentationMask FINISHED. Processed {processedAreas} areas.", ARManagerDebugFlags.PlaneGeneration);
        Debug.Log($"[{this.GetType().Name}] ProcessSegmentationMask: METHOD_EXIT. (Direct Log)");
    }

    private Texture2D RenderTextureToTexture2D(RenderTexture rTex)
    {
        Log($"[{GetType().Name}] RenderTextureToTexture2D: Entered. rTex: {(rTex ? rTex.name : "null")}, W:{(rTex ? rTex.width : 0)}, H:{(rTex ? rTex.height : 0)}, Format:{(rTex ? rTex.format.ToString() : "N/A")}, IsCreated:{(rTex ? rTex.IsCreated().ToString() : "N/A")}", ARManagerDebugFlags.PlaneGeneration);

        Texture2D tex = new Texture2D(rTex.width, rTex.height, TextureFormat.RGBA32, false); // MODIFIED: RGB24 to RGBA32
        Log($"[{GetType().Name}] RenderTextureToTexture2D: Created Texture2D tex: {(tex ? tex.name : "null")}, W:{tex.width}, H:{tex.height}, Format:{tex.format}", ARManagerDebugFlags.PlaneGeneration);

        RenderTexture previousActive = RenderTexture.active;
        RenderTexture.active = rTex;
        Debug.Log($"[{this.GetType().Name}] RenderTextureToTexture2D: Set RenderTexture.active to rTex ({rTex.name}). About to ReadPixels. (Direct Log)");

        try
        {
            tex.ReadPixels(new Rect(0, 0, rTex.width, rTex.height), 0, 0);
            Debug.Log($"[{this.GetType().Name}] RenderTextureToTexture2D: tex.ReadPixels() COMPLETED. (Direct Log)");

            tex.Apply();
            Debug.Log($"[{this.GetType().Name}] RenderTextureToTexture2D: tex.Apply() COMPLETED. (Direct Log)");
        }
        catch (Exception e)
        {
            Debug.LogError($"[{this.GetType().Name}] RenderTextureToTexture2D: EXCEPTION during ReadPixels or Apply. Message: {e.Message}\nStackTrace: {e.StackTrace} (Direct Log)");
            RenderTexture.active = null; // Ensure active is cleared on error
            Destroy(tex); // Cleanup partially created texture
            return null;
        }

        RenderTexture.active = previousActive;
        Debug.Log($"[{this.GetType().Name}] RenderTextureToTexture2D: Reset RenderTexture.active to null. Returning tex. (Direct Log)");
        return tex;
    }

    private List<Rect> FindWallAreas(Color32[] pixels, int width, int height, byte threshold)
    {
        // Debug.Log($"[{this.GetType().Name}] FindWallAreas: ENTERED. Mask W:{width}, H:{height}, Threshold:{threshold} (Direct Log)");
        Log($"FindWallAreas: ENTERED. Mask W:{width}, H:{height}, Threshold:{threshold}", ARManagerDebugFlags.PlaneGeneration);
        List<Rect> areas = new List<Rect>();
        bool[,] visited = new bool[width, height];
        // Debug.Log($"[{this.GetType().Name}] FindWallAreas: Initialized visited array [{width},{height}] (Direct Log)");
        Log($"FindWallAreas: Initialized visited array [{width},{height}]", ARManagerDebugFlags.PlaneGeneration);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Debug.Log($"[{this.GetType().Name}] FindWallAreas: Checking pixel ({x},{y}) (Direct Log)"); // Potentially too spammy
                if (!visited[x, y] && pixels[y * width + x].r > threshold)
                {
                    // Debug.Log($"[{this.GetType().Name}] FindWallAreas: Found unvisited wall pixel at ({x},{y}). Value: {pixels[y * width + x].r}. Calling FindConnectedArea. (Direct Log)");
                    Log($"FindWallAreas: Found unvisited wall pixel at ({x},{y}). Value: {pixels[y * width + x].r}. Calling FindConnectedArea.", ARManagerDebugFlags.PlaneGeneration);
                    Rect area = FindConnectedArea(pixels, width, height, x, y, visited, threshold);
                    // Debug.Log($"[{this.GetType().Name}] FindWallAreas: FindConnectedArea returned {areaToString(area)} for start ({x},{y}). Adding to list. Areas count: {areas.Count + 1} (Direct Log)");
                    Log($"FindWallAreas: FindConnectedArea returned {areaToString(area)} for start ({x},{y}). Adding to list. Areas count: {areas.Count + 1}", ARManagerDebugFlags.PlaneGeneration);
                    areas.Add(area);
                }
            }
        }
        // Debug.Log($"[{this.GetType().Name}] FindWallAreas: EXITED. Found {areas.Count} areas. (Direct Log)");
        Log($"FindWallAreas: EXITED. Found {areas.Count} areas.", ARManagerDebugFlags.PlaneGeneration);
        return areas;
    }

    private Rect FindConnectedArea(Color32[] pixels, int width, int height, int startX, int startY, bool[,] visited, byte threshold)
    {
        // Debug.Log($"[{this.GetType().Name}] FindConnectedArea: ENTERED for start pixel ({startX},{startY}) (Direct Log)");
        Log($"FindConnectedArea: ENTERED for start pixel ({startX},{startY})", ARManagerDebugFlags.PlaneGeneration);
        Queue<Vector2Int> queue = new Queue<Vector2Int>();
        queue.Enqueue(new Vector2Int(startX, startY));
        visited[startX, startY] = true;
        int minX = startX, maxX = startX, minY = startY, maxY = startY;
        int processedPixelCount = 0; // For loop runaway detection

        while (queue.Count > 0)
        {
            processedPixelCount++;
            if (processedPixelCount > width * height * 2)
            { // Safety break for extremely large areas or potential infinite loops
                // Debug.LogError($"[{this.GetType().Name}] FindConnectedArea: SAFETY BREAK! Processed pixel count exceeded {width * height * 2} for area starting at ({startX},{startY}). Aborting area search. Current Rect: X:{minX}, Y:{minY}, W:{maxX - minX + 1}, H:{maxY - minY + 1} (Direct Log)");
                LogError($"FindConnectedArea: SAFETY BREAK! Processed pixel count exceeded {width * height * 2} for area starting at ({startX},{startY}). Aborting area search. Current Rect: X:{minX}, Y:{minY}, W:{maxX - minX + 1}, H:{maxY - minY + 1}", ARManagerDebugFlags.PlaneGeneration);
                break;
            }

            Vector2Int p = queue.Dequeue();
            // Debug.Log($"[{this.GetType().Name}] FindConnectedArea: Dequeued ({p.x},{p.y}). Current bounds: minX={minX},maxX={maxX},minY={minY},maxY={maxY} (Direct Log)"); // Potentially too spammy
            minX = Mathf.Min(minX, p.x);
            maxX = Mathf.Max(maxX, p.x);
            minY = Mathf.Min(minY, p.y);
            maxY = Mathf.Max(maxY, p.y);

            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = p.x + dx;
                    int ny = p.y + dy;

                    // DETAILED LOGGING FOR NEIGHBOR CHECK
                    if ((debugFlags & ARManagerDebugFlags.PlaneGeneration) != 0 && nx >= 0 && nx < width && ny >= 0 && ny < height) // GUARDED LOG
                    {
                        // Debug.Log($"[{this.GetType().Name}] FindConnectedArea: Checking Neighbor ({nx},{ny}) of ({p.x},{p.y}). Pixel R-value: {pixels[ny * width + nx].r}, Visited: {visited[nx, ny]}, Threshold: {threshold} (Direct Log)");
                        Log($"FindConnectedArea: Checking Neighbor ({nx},{ny}) of ({p.x},{p.y}). Pixel R-value: {pixels[ny * width + nx].r}, Visited: {visited[nx, ny]}, Threshold: {threshold}", ARManagerDebugFlags.PlaneGeneration);
                    }
                    // END DETAILED LOGGING

                    if (nx >= 0 && nx < width && ny >= 0 && ny < height &&
                        !visited[nx, ny] && pixels[ny * width + nx].r > threshold)
                    {
                        // Debug.Log($"[{this.GetType().Name}] FindConnectedArea: Neighbor ({nx},{ny}) IS wall and not visited. Enqueuing. (Direct Log)"); // MODIFIED LOG
                        Log($"FindConnectedArea: Neighbor ({nx},{ny}) IS wall and not visited. Enqueuing.", ARManagerDebugFlags.PlaneGeneration);
                        visited[nx, ny] = true;
                        queue.Enqueue(new Vector2Int(nx, ny));
                    }
                }
            }
        }
        Rect resultRect = new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
        // Debug.Log($"[{this.GetType().Name}] FindConnectedArea: EXITED for start pixel ({startX},{startY}). Returning Rect: {areaToString(resultRect)}. Total pixels in queue processed: {processedPixelCount} (Direct Log)");
        Log($"FindConnectedArea: EXITED for start pixel ({startX},{startY}). Returning Rect: {areaToString(resultRect)}. Total pixels in queue processed: {processedPixelCount}", ARManagerDebugFlags.PlaneGeneration);
        return resultRect;
    }

    private string areaToString(Rect area) => $"X:{area.x:F0}, Y:{area.y:F0}, W:{area.width:F0}, H:{area.height:F0}";

    private bool UpdateOrCreatePlaneForWallArea(Rect area, int textureWidth, int textureHeight, Dictionary<GameObject, bool> visitedPlanesInCurrentMask)
    {
        string logPrefix = $"[UpdateOrCreatePlaneForWallArea] Area {areaToString(area)} (Tex: {textureWidth}x{textureHeight}) - ";
        Log(logPrefix + "METHOD ENTRY.", ARManagerDebugFlags.PlaneGeneration | ARManagerDebugFlags.Raycasting);

        bool planeCreatedOrUpdatedThisCall = false;

        // Объявляем переменные здесь, чтобы они были доступны во всей области видимости метода
        Vector3 planePosition = Vector3.zero;
        Vector3 surfaceNormal = Vector3.forward; // Default to forward if no hit
        float hitDistance = float.MaxValue;
        bool validHitFound = false;
        RaycastHit physicsHitInfo = new RaycastHit(); // Initialize with a default value
        GameObject hitObject = null;


        if (arCameraManager == null || arRaycastManager == null || arAnchorManager == null || planeManager == null || xrOrigin == null)
        {
            LogError(logPrefix + "One or more required AR components are null. Aborting.", ARManagerDebugFlags.PlaneGeneration);
            return false;
        }

        Camera cam = arCameraManager.GetComponent<Camera>();
        if (cam == null)
        {
            LogError(logPrefix + "ARCameraManager does not have a Camera component. Aborting.", ARManagerDebugFlags.PlaneGeneration);
            return false;
        }

        Transform cameraTransform = cam.transform;
        Vector2 screenCenterOfArea = area.center; // Используем центр области из маски
        // Важно: ScreenPointToRay ожидает пиксельные координаты экрана, а не UV.
        // screenCenterOfArea (area.center) уже в пикселях low-res маски.
        // Нужно преобразовать их в координаты полного экрана, если arCameraManager.ScreenPointToRay этого ожидает.
        // Однако, если cam это камера AR, ScreenPointToRay(pixelCoords) обычно работает с экранными пикселями.
        // area.center относится к текстуре lowResSegmentationMask.
        // Для преобразования в экранные координаты: (area.center.x / textureWidth) * Screen.width
        // Но рейкаст должен исходить из центра ОБНАРУЖЕННОЙ области на экране.
        // Проверим, как screenCenterOfArea соотносится с cam.ScreenPointToRay.
        // Пока предполагаем, что ray из центра области на экране.
        // Если рейкаст делается по low-res маске, то и координаты должны быть для нее.
        // Но сам рейкаст идет в 3D мир из основной камеры.
        // Для корректного преобразования UV-координат (0-1) в экранные: Vector2 screenPoint = new Vector2(uv.x * cam.pixelWidth, uv.y * cam.pixelHeight);
        Vector2 uvCenter = new Vector2(area.center.x / textureWidth, area.center.y / textureHeight);
        Vector2 screenPointForRay = new Vector2(uvCenter.x * cam.pixelWidth, uvCenter.y * cam.pixelHeight);

        Ray ray = cam.ScreenPointToRay(screenPointForRay);
        Log(logPrefix + $"Ray created from UV {uvCenter.ToString("F3")} -> ScreenPt {screenPointForRay.ToString("F0")} : Origin={ray.origin.ToString("F3")}, Direction={ray.direction.ToString("F3")}", ARManagerDebugFlags.Raycasting);
        if ((debugFlags & ARManagerDebugFlags.Raycasting) != 0) Debug.DrawRay(ray.origin, ray.direction * maxRayDistance, Color.yellow, 2.0f);

        List<ARRaycastHit> arHits = new List<ARRaycastHit>();
        bool arHitDetected = false;
        Pose arHitPose = Pose.identity;
        float closestARHitDistance = float.MaxValue;
        ARRaycastHit bestARHit = new ARRaycastHit(); // Store the best AR hit

        //string trackableTypesToLog = TrackableType.PlaneWithinPolygon.ToString();
        //Log(logPrefix + $"Attempting ARRaycast against TrackableType: {trackableTypesToLog}...", ARManagerDebugFlags.Raycasting);
        //if (arRaycastManager.Raycast(ray, arHits, TrackableType.PlaneWithinPolygon))
        string trackableTypesToLog = TrackableType.All.ToString(); // MODIFIED: Broaden to All
        Log(logPrefix + $"Attempting ARRaycast against TrackableType: {trackableTypesToLog}...", ARManagerDebugFlags.Raycasting);
        if (arRaycastManager.Raycast(ray, arHits, TrackableType.All)) // MODIFIED: Use TrackableType.All
        {
            Log(logPrefix + $"ARRaycastManager.Raycast returned {arHits.Count} hit(s). Processing...", ARManagerDebugFlags.Raycasting);
            foreach (var hit in arHits)
            {
                float distanceToHit = Vector3.Distance(cameraTransform.position, hit.pose.position);
                Log(logPrefix + $"  Checking AR hit: Type={hit.hitType}, TrackableId={hit.trackableId}, PosePos={hit.pose.position.ToString("F3")}, ARDist={hit.distance:F2}, CalcDist={distanceToHit:F2}", ARManagerDebugFlags.Raycasting);

                if (Vector3.Dot(cameraTransform.forward, (hit.pose.position - cameraTransform.position).normalized) > 0.1f) // Хит должен быть достаточно впереди
                {
                    if (distanceToHit < closestARHitDistance && distanceToHit < maxRayDistance)
                    {
                        closestARHitDistance = distanceToHit;
                        bestARHit = hit;
                        arHitDetected = true;
                        Log(logPrefix + $"    Found new BEST AR candidate. Dist: {closestARHitDistance:F2}", ARManagerDebugFlags.Raycasting);
                    }
                    else
                    {
                        Log(logPrefix + $"    AR Hit not closer or too far. HitDist: {distanceToHit:F2}, ClosestKnown: {closestARHitDistance:F2}, MaxDist: {maxRayDistance:F2}", ARManagerDebugFlags.Raycasting);
                    }
                }
                else
                {
                    Log(logPrefix + "    AR Hit is behind or too close to camera forward plane.", ARManagerDebugFlags.Raycasting);
                }
            }

            if (arHitDetected)
            {
                arHitPose = bestARHit.pose;
                if (bestARHit.trackable is ARPlane planeTrackable) { surfaceNormal = planeTrackable.normal; }
                // Для FeaturePoint или других типов, нормаль может быть менее надежной.
                // Использование -cameraTransform.forward хорошее предположение, если нет других данных.
                // Однако, если позже Physics.Raycast даст более точную нормаль, она будет использована.
                else
                {
                    surfaceNormal = -cameraTransform.forward;
                    Log(logPrefix + "  AR Hit was not an ARPlane, using -camera.forward as initial normal.", ARManagerDebugFlags.Raycasting);
                }
                planePosition = arHitPose.position;
                hitDistance = closestARHitDistance; // Обновляем общую hitDistance
                validHitFound = true;
                Log(logPrefix + $"ARRaycast FINAL SUCCESS. Best Hit TrackableId: {bestARHit.trackableId}, Type: {bestARHit.hitType}, FinalDist: {hitDistance:F2}, Pos: {planePosition.ToString("F2")}, Normal: {surfaceNormal.ToString("F2")}", ARManagerDebugFlags.Raycasting);
            }
            else
            {
                Log(logPrefix + "ARRaycast hits were present but none were deemed valid (too far, behind camera, etc.).", ARManagerDebugFlags.Raycasting);
            }
        }
        else
        {
            Log(logPrefix + "ARRaycastManager found NO hits at all.", ARManagerDebugFlags.Raycasting);
        }

        Log(logPrefix + $"Attempting Physics.Raycast... Current best hitDistance (from AR): {hitDistance:F2}", ARManagerDebugFlags.Raycasting);
        if (Physics.Raycast(ray, out physicsHitInfo, maxRayDistance, hitLayerMask))
        {
            Log(logPrefix + $"Physics.Raycast hit object '{physicsHitInfo.collider.name}' at distance {physicsHitInfo.distance:F2}m. Normal: {physicsHitInfo.normal.ToString("F2")}", ARManagerDebugFlags.Raycasting);
            if (IsIgnoredObject(physicsHitInfo.collider.gameObject))
            {
                Log(logPrefix + "  Physics hit an IGNORED object. No change to validHitFound.", ARManagerDebugFlags.Raycasting);
            }
            // Если Physics.Raycast попал БЛИЖЕ, чем существующий ARRaycast (или если ARRaycast не дал валидного хита, тогда hitDistance все еще float.MaxValue)
            else if (physicsHitInfo.distance < hitDistance - 0.01f) // Добавляем небольшой порог, чтобы избежать Z-fighting при почти одинаковых расстояниях
            {
                Log(logPrefix + $"Physics.Raycast OVERRODE ARRaycast or was the ONLY valid hit. Old AR dist: {hitDistance:F2}, New Phys dist: {physicsHitInfo.distance:F2}. Updating plane info.", ARManagerDebugFlags.Raycasting);
                planePosition = physicsHitInfo.point;
                surfaceNormal = physicsHitInfo.normal;
                hitDistance = physicsHitInfo.distance;
                validHitFound = true; // Этот хит теперь валидный
                hitObject = physicsHitInfo.collider.gameObject; // Запоминаем объект, если понадобится
                arHitPose = Pose.identity; // Сбрасываем AR хит, так как физический хит имеет приоритет для позиции/нормали
            }
            else
            {
                Log(logPrefix + $"Physics.Raycast hit was NOT closer (PhysD: {physicsHitInfo.distance:F2} vs ARD: {hitDistance:F2}). Current valid hit (if any) from ARRaycast remains preferred.", ARManagerDebugFlags.Raycasting);
            }
        }
        else
        {
            Log(logPrefix + "Physics.Raycast found NO hits within range or layer.", ARManagerDebugFlags.Raycasting);
        }

        if (!validHitFound)
        {
            Log(logPrefix + "No valid AR or Physics hit found after all checks. Plane will NOT be created. METHOD EXIT.", ARManagerDebugFlags.PlaneGeneration | ARManagerDebugFlags.Raycasting);
            if ((debugFlags & ARManagerDebugFlags.Raycasting) != 0) Debug.DrawRay(ray.origin, ray.direction * maxRayDistance, Color.magenta, 2.0f);
            return false;
        }
        Log(logPrefix + $"FINAL VALID HIT CHOSEN. Position: {planePosition.ToString("F2")}, Normal: {surfaceNormal.ToString("F2")}, Distance: {hitDistance:F2}. Was AR Hit anchored: {arHitPose != Pose.identity}", ARManagerDebugFlags.PlaneGeneration);

        Log(logPrefix + "Checking if surface is vertical... Normal: " + surfaceNormal.ToString("F3"), ARManagerDebugFlags.PlaneGeneration);
        if (!IsSurfaceVertical(surfaceNormal))
        {
            Log(logPrefix + $"Surface normal {surfaceNormal.ToString("F2")} is NOT vertical enough (max angle: {maxWallNormalAngleDeviation}). Plane creation SKIPPED. METHOD EXIT.", ARManagerDebugFlags.PlaneGeneration);
            return false;
        }
        Log(logPrefix + "Surface IS vertical. Proceeding to create/update plane.", ARManagerDebugFlags.PlaneGeneration);

        // --- Существующая логика создания новой плоскости (или обновления существующей) должна быть здесь ---
        // Временно упрощенный код создания новой плоскости для теста:
        Log(logPrefix + "Proceeding to create a NEW plane (existing plane update logic temporarily bypassed for detailed logging test).", ARManagerDebugFlags.PlaneGeneration);

        GameObject newPlaneGo = new GameObject($"GeneratedWallPlane_{planeInstanceCounter++}");
        Transform parentToUse = null;
        if (xrOrigin != null && xrOrigin.TrackablesParent != null)
        {
            parentToUse = xrOrigin.TrackablesParent;
        }
        else
        {
            LogWarning(logPrefix + "xrOrigin.TrackablesParent is null. Plane will be at root.", ARManagerDebugFlags.PlaneGeneration);
            // parentToUse remains null, plane will be at root
        }

        if (parentToUse != null)
        {
            newPlaneGo.transform.SetParent(parentToUse, false);
        }

        newPlaneGo.layer = LayerMask.NameToLayer(planeLayerName);
        newPlaneGo.transform.position = planePosition;
        newPlaneGo.transform.rotation = Quaternion.LookRotation(-surfaceNormal, Vector3.up);

        Log(logPrefix + $"Set new plane '{newPlaneGo.name}' Transform: Pos={planePosition.ToString("F2")}, RotQ={newPlaneGo.transform.rotation.ToString("F3")}, Parent={(parentToUse != null ? parentToUse.name : "Root")}", ARManagerDebugFlags.PlaneGeneration);

        // TODO: Рассчитать реальные мировые размеры плоскости на основе hitDistance, FOV камеры и UV-размеров области `area`.
        // Vector2 planeSizeInWorld = CalculateWorldSizeOfArea(area, textureWidth, textureHeight, hitDistance, cam);
        // Mesh planeMesh = CreatePlaneMesh(planeSizeInWorld.x, planeSizeInWorld.y);
        // Используем временные размеры для меша, пока не будет точного расчета мировых размеров
        Mesh planeMesh = CreatePlaneMesh(0.5f, 0.5f); // Временный размер 0.5x0.5 метра для заметности
        LogWarning(logPrefix + $"Created plane with TEMPORARY {planeMesh.bounds.size.x}x{planeMesh.bounds.size.y} meter size. Implement actual world size calculation.", ARManagerDebugFlags.PlaneGeneration);

        MeshFilter meshFilter = newPlaneGo.AddComponent<MeshFilter>();
        meshFilter.mesh = planeMesh;

        MeshRenderer meshRenderer = newPlaneGo.AddComponent<MeshRenderer>();
        Material matToAssign = GetMaterialForPlane(surfaceNormal, PlaneAlignment.Vertical); // Принудительно вертикальный, т.к. IsSurfaceVertical пройдена
        if (matToAssign == null)
        {
            LogError(logPrefix + $"GetMaterialForPlane returned null for normal {surfaceNormal} (expected vertical). Cannot assign material. Aborting plane creation.", ARManagerDebugFlags.PlaneGeneration);
            Destroy(newPlaneGo);
            return false;
        }
        meshRenderer.material = matToAssign;
        Log(logPrefix + $"Assigned material '{matToAssign.name}' to plane '{newPlaneGo.name}'.", ARManagerDebugFlags.PlaneGeneration);

        MeshCollider meshCollider = newPlaneGo.AddComponent<MeshCollider>();
        meshCollider.sharedMesh = planeMesh;
        meshCollider.convex = false;

        ARAnchor anchor = null;
        // Приоритет отдаем созданию якоря на основе ARRaycastHit, если он был источником validHitFound и arHitPose не сброшен
        if (arHitPose != Pose.identity)
        {
            anchor = arAnchorManager.AddAnchor(arHitPose);
            Log(logPrefix + (anchor ? $"ARAnchor created from ARRaycastHit ({arHitPose.position.ToString("F2")}) for '{newPlaneGo.name}'."
                                     : $"Failed to create ARAnchor from ARRaycastHit for '{newPlaneGo.name}'."), ARManagerDebugFlags.PlaneGeneration);
        }
        // Если arHitPose был сброшен (т.е. Physics hit имел приоритет) или AR хит не дал якоря, пробуем создать якорь по финальной planePosition
        else if (validHitFound && anchor == null)
        {
            Pose physicsBasedPose = new Pose(planePosition, newPlaneGo.transform.rotation);
            anchor = arAnchorManager.AddAnchor(physicsBasedPose);
            Log(logPrefix + (anchor ? $"ARAnchor created from final plane pose (likely Physics based: {planePosition.ToString("F2")}) for '{newPlaneGo.name}'."
                                     : $"Failed to create ARAnchor from final plane pose for '{newPlaneGo.name}'."), ARManagerDebugFlags.PlaneGeneration);
        }

        if (anchor)
        {
            newPlaneGo.transform.SetParent(anchor.transform, true); // World position Stays
            Log(logPrefix + $"Successfully parented '{newPlaneGo.name}' to ARAnchor '{anchor.name}'.", ARManagerDebugFlags.PlaneGeneration);
        }
        else
        {
            LogWarning(logPrefix + $"Plane '{newPlaneGo.name}' created WITHOUT an ARAnchor.", ARManagerDebugFlags.PlaneGeneration);
        }

        generatedPlanes.Add(newPlaneGo);
        if (persistentGeneratedPlanes.ContainsKey(newPlaneGo)) persistentGeneratedPlanes[newPlaneGo] = false;
        else persistentGeneratedPlanes.Add(newPlaneGo, false);

        if (planeCreationTimes.ContainsKey(newPlaneGo)) planeCreationTimes[newPlaneGo] = Time.time;
        else planeCreationTimes.Add(newPlaneGo, Time.time);

        if (planeLastVisitedTime.ContainsKey(newPlaneGo)) planeLastVisitedTime[newPlaneGo] = Time.time;
        else planeLastVisitedTime.Add(newPlaneGo, Time.time);

        planeCreatedOrUpdatedThisCall = true;
        visitedPlanesInCurrentMask[newPlaneGo] = true;

        Log(logPrefix + $"New plane '{newPlaneGo.name}' created successfully. METHOD EXIT.", ARManagerDebugFlags.PlaneGeneration);
        return planeCreatedOrUpdatedThisCall;
    }

    private bool IsIgnoredObject(GameObject obj)
    {
        if (obj.CompareTag("Player")) return true;
        if (!string.IsNullOrEmpty(ignoreObjectNames))
        {
            string[] names = ignoreObjectNames.Split(',');
            foreach (string name in names)
            {
                if (obj.name.Contains(name.Trim())) return true;
            }
        }
        return false;
    }

    private bool IsSurfaceVertical(Vector3 normal)
    {
        float angleWithUp = Vector3.Angle(normal, Vector3.up);
        return angleWithUp > (90f - maxWallNormalAngleDeviation) && angleWithUp < (90f + maxWallNormalAngleDeviation);
    }

    private Mesh CreatePlaneMesh(float width, float height)
    {
        Mesh mesh = new Mesh { name = "GeneratedPlaneMesh" };
        mesh.vertices = new Vector3[]
        {
            new Vector3(-width / 2, -height / 2, 0), new Vector3(width / 2, -height / 2, 0),
            new Vector3(-width / 2, height / 2, 0), new Vector3(width / 2, height / 2, 0)
        };
        mesh.triangles = new int[] { 0, 2, 1, 2, 3, 1 };
        mesh.uv = new Vector2[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private void CleanupOldPlanes(Dictionary<GameObject, bool> visitedPlanesInCurrentMask)
    {
        List<GameObject> planesToRemove = new List<GameObject>();
        float currentTime = Time.time;
        float removalDelay = GetUnvisitedPlaneRemovalDelay();

        for (int i = generatedPlanes.Count - 1; i >= 0; i--)
        {
            GameObject plane = generatedPlanes[i];
            if (plane == null)
            {
                generatedPlanes.RemoveAt(i);
                continue;
            }

            bool visitedThisUpdate = visitedPlanesInCurrentMask?.ContainsKey(plane) ?? false;

            if (!visitedThisUpdate)
            {
                if (currentTime - planeLastVisitedTime.GetValueOrDefault(plane, currentTime) > removalDelay)
                {
                    planesToRemove.Add(plane);
                }
            }
            else
            {
                planeLastVisitedTime[plane] = currentTime;
            }
        }

        foreach (GameObject plane in planesToRemove)
        {
            Log($"Removing old/unvisited custom generated plane: {plane.name}", ARManagerDebugFlags.PlaneGeneration);
            generatedPlanes.Remove(plane);
            planeCreationTimes.Remove(plane);
            planeLastVisitedTime.Remove(plane);
            persistentGeneratedPlanes.Remove(plane);
            Destroy(plane);
        }
    }

    private (GameObject, float, float) FindClosestExistingPlane(Vector3 position, Vector3 normal, float maxDistance, float maxAngleDegrees)
    {
        GameObject closestPlane = null;
        float minDistance = float.MaxValue;
        float minAngle = float.MaxValue;

        foreach (GameObject plane in generatedPlanes)
        {
            if (plane == null) continue;
            float distance = Vector3.Distance(plane.transform.position, position);
            float angle = Vector3.Angle(plane.transform.forward, -normal);

            if (distance < maxDistance && angle < maxAngleDegrees)
            {
                if (distance < minDistance)
                {
                    minDistance = distance;
                    minAngle = angle;
                    closestPlane = plane;
                }
            }
        }
        return (closestPlane, minDistance, minAngle);
    }

    public static string GetGameObjectPath(Transform transform)
    {
        if (transform == null) return "null";
        string path = transform.name;
        while (transform.parent != null)
        {
            transform = transform.parent;
            path = transform.name + "/" + path;
        }
        return path;
    }

    public static string LayerMaskToString(LayerMask layerMask)
    {
        if (layerMask.value == 0) return "<Nothing>";
        if (layerMask.value == -1) return "<Everything>";
        string S = "";
        for (int i = 0; i < 32; i++)
            if ((layerMask.value & (1 << i)) != 0)
                S += (S == "" ? "" : " | ") + LayerMask.LayerToName(i);
        return S == "" ? "<Error: EmptyLayerNameForValue>" : S;
    }

    public Material VerticalPlaneMaterial => verticalPlaneMaterial;
    public Material HorizontalPlaneMaterial => horizontalPlaneMaterial;
    public string PlaneLayerName => planeLayerName;

    private void InitializePersistentPlanesSystem()
    {
        if (usePersistentPlanes)
        {
            Log("Система сохранения плоскостей инициализирована.", ARManagerDebugFlags.System | ARManagerDebugFlags.Initialization);
        }
    }

    public bool MakePlanePersistent(GameObject plane)
    {
        if (!usePersistentPlanes || plane == null) return false;
        if (!persistentGeneratedPlanes.ContainsKey(plane))
        {
            persistentGeneratedPlanes.Add(plane, true);
            planeCreationTimes[plane] = Time.time;
            planeLastVisitedTime[plane] = Time.time;
            if (highlightPersistentPlanes)
            {
                var renderer = plane.GetComponent<Renderer>();
                if (renderer != null)
                {
                    MaterialPropertyBlock props = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(props);
                    props.SetColor("_Color", persistentPlaneColor);
                    renderer.SetPropertyBlock(props);
                }
                Log($"Плоскость {plane.name} сделана персистентной и подсвечена.", ARManagerDebugFlags.PlaneGeneration);
            }
            else Log($"Плоскость {plane.name} сделана персистентной.", ARManagerDebugFlags.PlaneGeneration);
            return true;
        }
        return false;
    }

    public bool IsPlanePersistent(GameObject plane)
    {
        if (!usePersistentPlanes || plane == null) return false;
        return persistentGeneratedPlanes.ContainsKey(plane);
    }

    public bool RemovePlanePersistence(GameObject plane)
    {
        if (!usePersistentPlanes || plane == null) return false;
        if (persistentGeneratedPlanes.Remove(plane))
        {
            planeCreationTimes.Remove(plane);
            planeLastVisitedTime.Remove(plane);
            Log($"Персистентность удалена для плоскости {plane.name}.", ARManagerDebugFlags.PlaneGeneration);
            return true;
        }
        return false;
    }

    private bool IsARFoundationPlane(GameObject planeGo)
    {
        if (planeGo == null) return false;
        if (planeGo.GetComponent<ARPlane>() != null)
        {
            if (planeGo.transform.parent != null && xrOrigin != null && xrOrigin.TrackablesParent != null &&
                planeGo.transform.parent == xrOrigin.TrackablesParent) return true;
            if (planeGo.transform.parent != null && trackablesParentInstanceID_FromStart != 0 &&
                planeGo.transform.parent.GetInstanceID() == trackablesParentInstanceID_FromStart) return true;
        }
        return false;
    }

    public float GetUnvisitedPlaneRemovalDelay() => 1.5f;

    private Material GetMaterialForPlane(Vector3 planeNormal)
    {
        if (Vector3.Angle(planeNormal, Vector3.up) < maxFloorCeilingAngleDeviation || Vector3.Angle(planeNormal, Vector3.down) < maxFloorCeilingAngleDeviation)
        {
            return horizontalPlaneMaterial != null ? horizontalPlaneMaterial : (verticalPlaneMaterial != null ? verticalPlaneMaterial : null);
        }
        else if (IsSurfaceVertical(planeNormal))
        {
            return verticalPlaneMaterial != null ? verticalPlaneMaterial : (horizontalPlaneMaterial != null ? horizontalPlaneMaterial : null);
        }
        LogWarning($"Could not determine specific material for plane normal {planeNormal}. Defaulting to vertical.", ARManagerDebugFlags.PlaneGeneration);
        return verticalPlaneMaterial != null ? verticalPlaneMaterial : horizontalPlaneMaterial;
    }

    private Material GetMaterialForPlane(Vector3 planeNormal, PlaneAlignment alignment)
    {
        if (alignment == PlaneAlignment.Vertical) return verticalPlaneMaterial;
        else if (alignment.IsHorizontal()) return horizontalPlaneMaterial;
        LogWarning($"Не удалось определить материал для плоскости с alignment={alignment} и нормалью={planeNormal}. Используется вертикальный по умолчанию.", ARManagerDebugFlags.PlaneGeneration);
        return verticalPlaneMaterial != null ? verticalPlaneMaterial : horizontalPlaneMaterial;
    }

    private void HandlePlaneSelectionByTap()
    {
        if (Input.touchCount > 0 && Input.GetTouch(0).phase == TouchPhase.Began)
        {
            if (arRaycastManager == null)
            {
                LogWarning("ARRaycastManager is not available, cannot select planes by tap.", ARManagerDebugFlags.Raycasting);
                return;
            }

            Color paintColorToApply = Color.white;
            if (colorManager != null) paintColorToApply = colorManager.GetCurrentColor();

            List<ARRaycastHit> hits = new List<ARRaycastHit>();
            if (arRaycastManager.Raycast(Input.GetTouch(0).position, hits, TrackableType.PlaneWithinPolygon | TrackableType.PlaneWithinBounds))
            {
                ARRaycastHit hit = hits[0];
                ARPlane hitPlane = planeManager?.GetPlane(hit.trackableId);

                if (hitPlane != null)
                {
                    Log($"Tapped on ARFoundation plane: {hitPlane.trackableId}, Alignment: {hitPlane.alignment}", ARManagerDebugFlags.Raycasting);
                    if (paintMaterial != null)
                    {
                        MeshRenderer hitPlaneRenderer = hitPlane.GetComponent<MeshRenderer>();
                        if (hitPlaneRenderer != null)
                        {
                            if (!paintedPlaneOriginalMaterials.ContainsKey(hitPlane))
                            {
                                paintedPlaneOriginalMaterials[hitPlane] = hitPlaneRenderer.sharedMaterial;
                            }
                            Material materialInstance = Instantiate(paintMaterial);
                            hitPlaneRenderer.material = materialInstance;
                            materialInstance.SetColor("_PaintColor", paintColorToApply);
                            Log($"Applied paint color {paintColorToApply} to ARFoundation plane {hitPlane.trackableId}", ARManagerDebugFlags.PlaneGeneration);
                            if (wallSegmentation?.segmentationMaskTexture != null)
                            {
                                materialInstance.SetTexture("_SegmentationMask", wallSegmentation.segmentationMaskTexture);
                            }
                            else
                            {
                                materialInstance.SetTexture("_SegmentationMask", null);
                            }
                        }
                    }
                }
                else
                {
                    Ray ray = arCameraManager.GetComponent<Camera>().ScreenPointToRay(Input.GetTouch(0).position);
                    RaycastHit physicsHit;
                    if (Physics.Raycast(ray, out physicsHit, maxRayDistance, hitLayerMask))
                    {
                        if (generatedPlanes.Contains(physicsHit.collider.gameObject))
                        {
                            GameObject hitGeneratedPlane = physicsHit.collider.gameObject;
                            Log($"Tapped on generated plane: {hitGeneratedPlane.name}", ARManagerDebugFlags.Raycasting);
                            if (paintMaterial != null)
                            {
                                MeshRenderer hitRenderer = hitGeneratedPlane.GetComponent<MeshRenderer>();
                                if (hitRenderer != null)
                                {
                                    Material materialInstance = Instantiate(paintMaterial);
                                    hitRenderer.material = materialInstance;
                                    materialInstance.SetColor("_PaintColor", paintColorToApply);
                                    Log($"Applied paint color {paintColorToApply} to generated plane {hitGeneratedPlane.name}", ARManagerDebugFlags.PlaneGeneration);
                                    if (wallSegmentation?.segmentationMaskTexture != null)
                                    {
                                        materialInstance.SetTexture("_SegmentationMask", wallSegmentation.segmentationMaskTexture);
                                    }
                                    else
                                    {
                                        materialInstance.SetTexture("_SegmentationMask", null);
                                    }
                                }
                            }
                        }
                        else
                        {
                            Log($"Tap hit an object ({physicsHit.collider.name}) on a valid layer via Physics.Raycast, but it's not a recognized ARPlane or generated plane.", ARManagerDebugFlags.Raycasting);
                        }
                    }
                    else
                    {
                        Log("Tap did not hit any ARPlane trackable via ARRaycastManager nor any physics object on hitLayerMask.", ARManagerDebugFlags.Raycasting);
                    }
                }
            }
            else
            {
                Ray ray = arCameraManager.GetComponent<Camera>().ScreenPointToRay(Input.GetTouch(0).position);
                RaycastHit physicsHit;
                if (Physics.Raycast(ray, out physicsHit, maxRayDistance, hitLayerMask))
                {
                    if (generatedPlanes.Contains(physicsHit.collider.gameObject))
                    {
                        GameObject hitGeneratedPlane = physicsHit.collider.gameObject;
                        Log($"Tapped on generated plane (fallback Physics.Raycast): {hitGeneratedPlane.name}", ARManagerDebugFlags.Raycasting);
                        if (paintMaterial != null)
                        {
                            MeshRenderer hitRenderer = hitGeneratedPlane.GetComponent<MeshRenderer>();
                            if (hitRenderer != null)
                            {
                                Material materialInstance = Instantiate(paintMaterial);
                                hitRenderer.material = materialInstance;
                                materialInstance.SetColor("_PaintColor", paintColorToApply);
                                Log($"Applied paint color {paintColorToApply} to generated plane {hitGeneratedPlane.name}", ARManagerDebugFlags.PlaneGeneration);
                                if (wallSegmentation?.segmentationMaskTexture != null)
                                {
                                    materialInstance.SetTexture("_SegmentationMask", wallSegmentation.segmentationMaskTexture);
                                }
                                else
                                {
                                    materialInstance.SetTexture("_SegmentationMask", null);
                                }
                            }
                        }
                    }
                    else
                    {
                        Log($"Tap (fallback Physics.Raycast) hit an object ({physicsHit.collider.name}) on a valid layer, but it's not a recognized generated plane.", ARManagerDebugFlags.Raycasting);
                    }
                }
                else
                {
                    Log("Tap did not hit any ARPlane trackable via ARRaycastManager nor any physics object on hitLayerMask (fallback).", ARManagerDebugFlags.Raycasting);
                }
            }
        }
    }

    private void HandleTap(Vector2 touchPosition)
    {
        Log($"[LEGACY TAP] HandleTap called with touchPosition: {touchPosition}. This method might be deprecated by HandlePlaneSelectionByTap.", ARManagerDebugFlags.Raycasting, ARManagerLogLevel.Warning);
        if (paintMaterial == null)
        {
            LogWarning("paintMaterial не назначен в ARManagerInitializer2. Покраска невозможна.", ARManagerDebugFlags.Raycasting);
            return;
        }
        if (arRaycastManager == null)
        {
            LogError("arRaycastManager не назначен в ARManagerInitializer2. Рейкастинг невозможен.", ARManagerDebugFlags.Raycasting);
            return;
        }

        List<ARRaycastHit> hits = new List<ARRaycastHit>();
        if (arRaycastManager.Raycast(touchPosition, hits, TrackableType.PlaneWithinPolygon))
        {
            if (hits.Count > 0)
            {
                ARRaycastHit hit = hits[0];
                ARPlane tappedPlane = hit.trackable as ARPlane;
                if (tappedPlane != null)
                {
                    Log($"[LEGACY TAP] Коснулись плоскости: {tappedPlane.trackableId}, Alignment: {tappedPlane.alignment}, Distance: {hit.distance}", ARManagerDebugFlags.Raycasting);
                    if (tappedPlane.alignment == PlaneAlignment.Vertical)
                    {
                        if (colorManager != null) PaintPlane(tappedPlane, colorManager.currentColor);
                        else PaintPlane(tappedPlane, Color.magenta);
                    }
                    else Log($"[LEGACY TAP] Плоскость {tappedPlane.trackableId} не вертикальная (Alignment: {tappedPlane.alignment}), покраска отменена.", ARManagerDebugFlags.Raycasting);
                }
                else LogWarning($"[LEGACY TAP] Рейкаст попал в объект типа {(hit.trackable != null ? hit.trackable.GetType().Name : "null")}, но это не ARPlane.", ARManagerDebugFlags.Raycasting);
            }
            else LogWarning("[LEGACY TAP] Рейкаст вернул true, но список попаданий пуст.", ARManagerDebugFlags.Raycasting);
        }
        else Log("[LEGACY TAP] Рейкаст не попал ни в одну AR плоскость (TrackableType.PlaneWithinPolygon).", ARManagerDebugFlags.Raycasting);
    }

    private void PaintPlane(ARPlane plane, Color color)
    {
        Log($"[PaintPlane - Legacy Flow] Attempting to paint plane {plane.trackableId} with color {color}", ARManagerDebugFlags.PlaneGeneration);
        MeshRenderer renderer = plane.GetComponent<MeshRenderer>();
        if (renderer == null)
        {
            LogError($"[PaintPlane] MeshRenderer not found on plane {plane.trackableId}", ARManagerDebugFlags.PlaneGeneration);
            return;
        }
        if (paintMaterial == null)
        {
            LogError("[PaintPlane] paintMaterial is null. Cannot paint.", ARManagerDebugFlags.PlaneGeneration);
            return;
        }

        if (!paintedPlaneOriginalMaterials.ContainsKey(plane))
        {
            paintedPlaneOriginalMaterials[plane] = renderer.sharedMaterial;
        }
        Material instancedPaintMaterial = Instantiate(paintMaterial);
        instancedPaintMaterial.SetColor("_PaintColor", color);
        if (wallSegmentation != null && wallSegmentation.segmentationMaskTexture != null)
        {
            instancedPaintMaterial.SetTexture("_SegmentationMask", wallSegmentation.segmentationMaskTexture);
        }
        else
        {
            instancedPaintMaterial.SetTexture("_SegmentationMask", null);
            LogWarning("[PaintPlane] wallSegmentation or its mask is null. Painting without mask.", ARManagerDebugFlags.PlaneGeneration);
        }
        renderer.material = instancedPaintMaterial;
        Log($"[PaintPlane] Applied paint material with color {color} to plane {plane.trackableId}", ARManagerDebugFlags.PlaneGeneration);
    }

    // NEW METHOD TO SAVE LOW RESOLUTION MASK
    private void SaveLowResMaskForDebug(RenderTexture rt, string fileName)
    {
        if (rt == null)
        {
            LogWarning($"SaveLowResMaskForDebug: RenderTexture is null, cannot save {fileName}. (Direct Log)");
            return;
        }

        RenderTexture prevActive = RenderTexture.active;
        RenderTexture.active = rt;
        Texture2D tempTex = new Texture2D(rt.width, rt.height, TextureFormat.R8, false); // Assuming R8 format for single channel
        tempTex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tempTex.Apply();
        RenderTexture.active = prevActive;

        byte[] bytes = tempTex.EncodeToPNG();
        Destroy(tempTex);

        string directoryPath = Path.Combine(Application.persistentDataPath, "DebugSegmentationOutputs");
        Directory.CreateDirectory(directoryPath); // Ensures the directory exists
        string filePath = Path.Combine(directoryPath, fileName);

        try
        {
            File.WriteAllBytes(filePath, bytes);
            Log($"SaveLowResMaskForDebug: Successfully saved {fileName} to {directoryPath} (Direct Log)");
        }
        catch (System.Exception e)
        {
            LogError($"SaveLowResMaskForDebug: Failed to save {fileName}. Error: {e.Message} (Direct Log)");
        }
    }
}