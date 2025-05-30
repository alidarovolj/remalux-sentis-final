using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using System.Collections.Generic;
using System.Linq; // Explicitly adding System.Linq
using System;
using System.Collections;
using Random = UnityEngine.Random;
using UnityEngine.Events;
using TMPro; // If you are using TextMeshPro
using System.IO; // For file operations
using UnityEngine.Rendering; // For CommandBuffer
using Unity.XR.CoreUtils; // Ensure Unity.XR.CoreUtils is present for XROrigin
using UnityEngine.XR.Management;
using UnityEngine.InputSystem; // Added for the new Input System


[System.Flags]
public enum ARManagerDebugFlags
{
    None = 0,
    Initialization = 1 << 0,
    PlaneGeneration = 1 << 1,
    Raycasting = 1 << 2,
    DetailedRaycastLogging = 1 << 3,
    ARSystem = 1 << 4,
    System = 1 << 5,
    Performance = 1 << 6,
    SaveDebugTextures = 1 << 7,
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
    private string[] ignoredObjectNamesArray;

    [Header("Отладка ARManagerInitializer2")]
    [Tooltip("Флаги для детальной отладки различных частей ARManagerInitializer2.")]
    public ARManagerDebugFlags debugFlags = ARManagerDebugFlags.None;

    [Header("AR компоненты")]
    [Tooltip("Reference to the XROrigin in the scene. If not set, will try to find one.")]
    [SerializeField] private XROrigin xrOrigin;
    public ARCameraManager arCameraManager;
    public ARPlaneManager planeManager;
    public AROcclusionManager arOcclusionManager;
    public ARRaycastManager arRaycastManager;
    public ARAnchorManager arAnchorManager;

    [Tooltip("AR Mesh Manager для Scene Reconstruction (LiDAR сканирование)")]
    [SerializeField] private ARMeshManager arMeshManager;
    [Tooltip("Включить Scene Reconstruction на устройствах с LiDAR")]
    [SerializeField] private bool enableSceneReconstruction = true;

    [Tooltip("Directional Light, который будет обновляться AR Light Estimation")]
    [SerializeField] public Light mainLight; // Made public for external access if needed

    [Header("Настройки сегментации")]
    [Tooltip("Использовать ли выход сегментации для создания или фильтрации плоскостей AR.")]
    public bool useSegmentationForPlaneGeneration = true;
    [SerializeField] private int minPixelsDimensionForLowResArea = 10;
    [SerializeField] private int minAreaSizeInLowResPixels = 20;
    [Range(0, 255)] public byte wallMaskRedChannelMinThreshold = 250;

    [Header("Настройки Рейкастинга для Плоскостей")]
    [Tooltip("Максимальное расстояние рейкаста для поиска поверхностей.")]
    public float maxRayDistance = 10f;
    [Tooltip("Порог для определения горизонтальной поверхности (dot product с Vector3.up). Больше = строже.")]
    [Range(0.7f, 1.0f)] public float horizontalSurfaceThreshold = 0.95f;
    [Tooltip("Разрешить создание плоскостей на не строго вертикальных поверхностях.")]
    public bool allowNonVerticalWallPlanes = true;
    [Tooltip("Максимальный угол (в градусах) между направлением камеры и нормалью плоскости для её создания.")]
    [Range(0f, 90f)] public float maxCameraAngleToPlaneNormal = 75f;
    [Tooltip("Порог расстояния для слияния новой плоскости с существующей.")]
    public float planeMergeDistanceThreshold = 0.1f;
    [Tooltip("Детальное логирование параметров рейкастинга.")]
    public bool enableDetailedRaycastLogging = false; // Controlled by debugFlags as well

    public LayerMask hitLayerMask = -1;
    public float minHitDistanceThreshold = 0.1f;
    public float maxWallNormalAngleDeviation = 30f;
    public float maxFloorCeilingAngleDeviation = 15f;
    [Tooltip("Расстояние по умолчанию для создания плоскости, если рейкаст не нашел поверхности.")]
    public float defaultPlaneDistance = 2.0f;
    [Tooltip("Минимальное расстояние для создания плоскости.")]
    public float minPlaneCreationDistance = 0.3f;
    [Tooltip("Имя слоя для создаваемых плоскостей.")]
    public string planeLayerName = "ARFoundationsPlanes"; // Ensure this layer exists
    [Tooltip("Имена объектов, которые должны игнорироваться при рейкастинге (разделены запятыми).")]
    public string ignoredObjectNames = "FPSController,XR Origin,Main Camera,AR Session,AR Input Manager";


    [Header("Настройки создаваемых плоскостей")]
    [Tooltip("Префаб для создаваемой плоскости (должен иметь ARPlaneConfigurator).")]
    public GameObject arPaintPlanePrefab;
    [Tooltip("Масштаб по умолчанию для создаваемых плоскостей.")]
    public Vector3 defaultPlaneScale = Vector3.one;
    [Tooltip("Ширина плоскости по умолчанию, создаваемой, если рейкаст не нашел поверхности.")]
    public float defaultPlaneWidth = 1.0f;
    [Tooltip("Высота плоскости по умолчанию, создаваемой, если рейкаст не нашел поверхности.")]
    public float defaultPlaneHeight = 1.0f;

    [Header("Настройки сохранения плоскостей")]
    [SerializeField] private bool highlightPersistentPlanes = false;
    [SerializeField] private Color persistentPlaneColor = new Color(0.0f, 0.8f, 0.2f, 0.7f);
    private Dictionary<GameObject, bool> persistentGeneratedPlanes = new Dictionary<GameObject, bool>();
    private Dictionary<GameObject, float> planeCreationTimes = new Dictionary<GameObject, float>();
    private Dictionary<GameObject, float> planeLastVisitedTime = new Dictionary<GameObject, float>();

    [Tooltip("Материал для вертикальных плоскостей")]
    public Material verticalPlaneMaterial;
    [Tooltip("Материал для горизонтальных плоскостей")]
    public Material horizontalPlaneMaterial;


    [Header("Отладочная Визуализация Лучей")]
    public bool drawDebugRays = true;
    private MaterialPropertyBlock debugRayMaterialPropertyBlock;

    private UnityEngine.UI.RawImage отображениеМаскиUI;
    private RenderTexture currentSegmentationMask;
    private RenderTexture lowResSegmentationMask;
    private bool newMaskAvailableForProcessing = false;
    private List<GameObject> generatedPlanes = new List<GameObject>();
    private List<GameObject> allGeneratedPlanes = new List<GameObject>(); // Keeps track of all planes, including ARFoundation's
    private List<GameObject> activePlanesForPainting = new List<GameObject>(); // Planes specifically created/used by this script for painting

    private int frameCounter = 0;
    private float lastSuccessfulSegmentationTime = 0f;
    private int trackablesParentInstanceID_FromStart = 0;
    private int lowResMaskSaveCounter = 0;

    [Header("Настройки Кластеризации Рейкастов")]
    [Tooltip("Использовать ли кластеризацию рейкастов для определения центра области.")]
    public bool useRaycastClustering = false;
    public int clusteringMinHitsThreshold = 3; // Minimum hits to consider a cluster valid

    [SerializeField] private ARPlaneConfigurator planeConfigurator; // Should be on the arPaintPlanePrefab
    [SerializeField] private WallSegmentation wallSegmentation;

    [Header("Global Logging Control")]
    [Tooltip("Enable all logging messages from this ARManagerInitializer2 component.")]
    public bool enableComponentLogging = true;

    [Header("Настройки Выделения Плоскостей")]
    [Tooltip("Материал, используемый для выделения выбранной плоскости (опционально).")]
    public Material selectionMaterial;
    [Tooltip("Материал, используемый для покраски стен. Должен иметь свойство _PaintColor и _SegmentationMask.")]
    public Material wallPaintMaterial;
    private ARPlane currentlySelectedPlane;
    private Material originalSelectedPlaneMaterial;
    private Dictionary<ARPlane, Material> paintedPlaneOriginalMaterials = new Dictionary<ARPlane, Material>();

    private ARWallPaintColorManager colorManager;
    private bool isSubscribedToWallSegmentation = false;

    [Header("Tap Interaction")]
    [Tooltip("Использовать ли обнаруженные AR-плоскости для взаимодействия по тапу.")]
    public bool useDetectedPlanesForTap = true;
    [Tooltip("Всегда использовать Physics.Raycast для создаваемых плоскостей, даже если AR Raycast был успешен.")]
    public bool alwaysUsePhysicsRaycastForCustomPlanes = false;
    [Tooltip("Использовать ли Physics.Raycast для взаимодействия по тапу в дополнение к AR Raycast.")]
    public bool usePhysicsRaycastForTap = true;
    private TrackableType trackableTypesToHit = TrackableType.PlaneWithinPolygon | TrackableType.FeaturePoint;

    [Header("Автоматическое создание геометрии сцены (для симуляции)")]
    [Tooltip("Создавать ли базовую геометрию сцены для работы физического рейкастинга в редакторе.")]
    public bool createBasicSceneGeometry = true;
    [Tooltip("Размер создаваемой комнаты (X, Y, Z) в метрах.")]
    public Vector3 basicSceneRoomSize = new Vector3(10, 3, 10);
    [Tooltip("Толщина создаваемых стен для коллайдеров.")]
    public float basicSceneWallThickness = 0.1f;
    [Tooltip("Имя слоя для создаваемой базовой геометрии.")]
    public string basicSceneGeometryLayerName = "SimulatedEnvironment"; // Ensure this layer exists

    private void Log(string message, ARManagerDebugFlags flag = ARManagerDebugFlags.None, ARManagerLogLevel level = ARManagerLogLevel.Info)
    {
        if (!enableComponentLogging)
        {
            if (level == ARManagerLogLevel.Error)
            {
                if (this.debugFlags == ARManagerDebugFlags.None) return;
            }
            else
            {
                return;
            }
        }

        bool isDirectLog = flag == ARManagerDebugFlags.None && level == ARManagerLogLevel.Info;
        bool isError = level == ARManagerLogLevel.Error;
        bool isWarning = level == ARManagerLogLevel.Warning;

        if (this.debugFlags.HasFlag(flag) || isDirectLog || isError || isWarning)
        {
            string formattedMessage = $"[{GetType().Name}] {message}";
            if (!isDirectLog && !isError && !isWarning)
            {
                formattedMessage = $"[{GetType().Name}] [{flag}] {message}";
            }
            else if (isDirectLog)
            {
                // formattedMessage = $"[{GetType().Name}] {message} (Direct Log)"; // Already default
            }

            switch (level)
            {
                case ARManagerLogLevel.Error:
                    Debug.LogError(formattedMessage, this);
                    break;
                case ARManagerLogLevel.Warning:
                    Debug.LogWarning(formattedMessage, this);
                    break;
                case ARManagerLogLevel.Info:
                default:
                    Debug.Log(formattedMessage, this);
                    break;
            }
        }
    }

    private void LogError(string message, ARManagerDebugFlags flag = ARManagerDebugFlags.None) => Log(message, flag, ARManagerLogLevel.Error);
    private void LogWarning(string message, ARManagerDebugFlags flag = ARManagerDebugFlags.None) => Log(message, flag, ARManagerLogLevel.Warning);

    private void Awake()
    {
        Log($"[{GetType().Name}] AWAKE_METHOD_ENTERED_TOP (Direct Log - Before Singleton)", ARManagerDebugFlags.Initialization);
        if (Instance != null && Instance != this)
        {
            LogWarning("Multiple instances of ARManagerInitializer2 detected. Destroying this one.", ARManagerDebugFlags.Initialization);
            Destroy(gameObject);
            return;
        }
        Instance = this;
        Log("Singleton instance set.", ARManagerDebugFlags.Initialization);

        if (string.IsNullOrEmpty(ignoredObjectNames))
        {
            ignoredObjectNamesArray = new string[0];
        }
        else
        {
            ignoredObjectNamesArray = ignoredObjectNames.Split(',').Select(s => s.Trim()).ToArray();
            Log($"Ignored object names loaded: {string.Join(", ", ignoredObjectNamesArray)}", ARManagerDebugFlags.Initialization);
        }


        // Initialize hitLayerMask to exclude "UI" and the planeLayerName
        int uiLayer = LayerMask.NameToLayer("UI");
        int arPlaneLayer = LayerMask.NameToLayer(planeLayerName);
        int simulatedEnvLayer = LayerMask.NameToLayer(basicSceneGeometryLayerName);

        LayerMask defaultMask = Physics.DefaultRaycastLayers;
        if (uiLayer != -1) defaultMask &= ~(1 << uiLayer);
        if (arPlaneLayer != -1) defaultMask &= ~(1 << arPlaneLayer);
        // We WANT to hit the simulated environment, so we don't exclude it here.
        // It will be part of the default physics raycast layers unless explicitly excluded.

        hitLayerMask = defaultMask; // Start with default and then potentially add specific layers if needed.

        Log($"Awake: Initial hitLayerMask before specific additions/exclusions based on default: {LayerMaskToString(hitLayerMask)} (Raw: {hitLayerMask.value})", ARManagerDebugFlags.Initialization);


        // Ensure the simulated environment layer IS part of the hitLayerMask if it exists
        if (simulatedEnvLayer != -1)
        {
            hitLayerMask |= (1 << simulatedEnvLayer);
            Log($"Ensured '{basicSceneGeometryLayerName}' (Layer {simulatedEnvLayer}) is INCLUDED in hitLayerMask.", ARManagerDebugFlags.Initialization);
        }
        else
        {
            LogWarning($"Layer '{basicSceneGeometryLayerName}' not found. Physics raycasts might not hit simulated environment correctly.", ARManagerDebugFlags.Initialization);
        }

        // If planeLayerName is also for custom physics objects we want to hit for other reasons,
        // this setup might need adjustment. For now, assuming planeLayerName is for AR planes we *don't* want physics raycast to hit for initial placement.
        // However, for tap-painting, we might want to hit these custom planes. This is handled in HandlePlaneSelectionByTap.

        Log($"Awake: Final hitLayerMask = {LayerMaskToString(hitLayerMask)} (Raw value: {hitLayerMask.value})", ARManagerDebugFlags.Initialization);

        ARManagerDebugFlags initialDebugFlags = this.debugFlags; // Store inspector value
        Log($"Awake: Initial debugFlags from Inspector = {initialDebugFlags}", ARManagerDebugFlags.Initialization);
        this.debugFlags = initialDebugFlags; // Ensure we use the inspector value

        if (debugRayMaterialPropertyBlock == null)
        {
            debugRayMaterialPropertyBlock = new MaterialPropertyBlock();
        }

        if (wallSegmentation == null)
        {
            wallSegmentation = FindObjectOfType<WallSegmentation>();
            if (wallSegmentation != null) Log("WallSegmentation found via FindObjectOfType in Awake.", ARManagerDebugFlags.Initialization);
            else LogWarning("WallSegmentation not assigned and not found in scene via FindObjectOfType in Awake.", ARManagerDebugFlags.Initialization);
        }

        if (arPaintPlanePrefab != null && planeConfigurator == null)
        {
            planeConfigurator = arPaintPlanePrefab.GetComponent<ARPlaneConfigurator>();
            if (planeConfigurator == null)
            {
                LogWarning($"ARPaintPlanePrefab '{arPaintPlanePrefab.name}' does not have an ARPlaneConfigurator component. Plane configuration might not work as expected.", ARManagerDebugFlags.Initialization);
            }
        }
        else if (arPaintPlanePrefab == null)
        {
            LogWarning("ARPaintPlanePrefab is not assigned. Custom plane creation will not work.", ARManagerDebugFlags.Initialization);
        }


        colorManager = FindObjectOfType<ARWallPaintColorManager>();
        if (colorManager == null)
        {
            LogWarning("ARWallPaintColorManager not found in scene.", ARManagerDebugFlags.Initialization);
        }

        // FindARComponents is called in Start to ensure XROrigin and its children are fully initialized.
        InitializePersistentPlanesSystem();

        Log($"AWAKE_METHOD_EXIT. wallSegmentation is {(wallSegmentation != null ? "ASSIGNED" : "NOT ASSIGNED")}. debugFlags: {this.debugFlags}", ARManagerDebugFlags.Initialization);
    }

    private void OnEnable()
    {
        Debug.Log($"[{this.GetType().Name}] OnEnable: Method Entered. wallSegmentation is {(wallSegmentation == null ? "NULL" : "ASSIGNED")}. Current debugFlags: {this.debugFlags} (Direct Log)"); // Direct log
        SubscribeToWallSegmentation();
        InitializeLightEstimation();
        InitializeEnvironmentProbes();
        InitializeSceneReconstruction(); // Initialize (or re-initialize) scene reconstruction
        Log("OnEnable: Subscriptions and AR features initialization attempted.", ARManagerDebugFlags.System);
    }

    private void Start()
    {
        Debug.Log($"[{this.GetType().Name}] START_METHOD_ENTERED_TOP (Direct Log)"); // Direct Log
        Debug.Log($"[{this.GetType().Name}] Start: Initial debugFlags from Inspector = {this.debugFlags} (Direct Log)"); // Direct Log

        Debug.Log($"[{this.GetType().Name}] Start: Before FindARComponents. wallSegmentation is {(wallSegmentation == null ? "NULL" : "ASSIGNED")} (Direct Log)"); // Direct Log
        FindARComponents(); // Ensure all AR components are found and assigned
        Debug.Log($"[{this.GetType().Name}] Start: After FindARComponents. wallSegmentation is {(wallSegmentation == null ? "NULL" : "ASSIGNED")} (Direct Log)"); // Direct Log


        if (planeManager != null)
        {
            planeManager.planesChanged += OnPlanesChanged;
            Log("Subscribed to ARPlaneManager.planesChanged event.", ARManagerDebugFlags.System);
            // Log existing planes
            foreach (var plane in planeManager.trackables)
            {
                OnPlaneAdded(plane); // Process initially existing planes
            }
        }
        else
        {
            LogError("ARPlaneManager is null in Start. Plane detection will not work.", ARManagerDebugFlags.Initialization);
        }


        InitializeMaterials();


        if (createBasicSceneGeometry)
        {
            CreateBasicSceneGeometry();
        }

        if (xrOrigin != null && xrOrigin.TrackablesParent != null)
        {
            trackablesParentInstanceID_FromStart = xrOrigin.TrackablesParent.GetInstanceID();
            Log($"Start: Stored TrackablesParent Instance ID: {trackablesParentInstanceID_FromStart} from XROrigin: {xrOrigin.TrackablesParent.name}", ARManagerDebugFlags.Initialization);
        }
        else if (planeManager != null && planeManager.trackables.count > 0) // Check count first
        {
            ARPlane firstPlane = null;
            foreach (ARPlane plane in planeManager.trackables) // Iterate to get the first available plane
            {
                firstPlane = plane;
                break; // Exit after finding the first one
            }

            if (firstPlane != null && firstPlane.transform.parent != null)
            {
                trackablesParentInstanceID_FromStart = firstPlane.transform.parent.GetInstanceID();
                Log($"Start: Stored TrackablesParent Instance ID: {trackablesParentInstanceID_FromStart} from first ARPlane's parent: {firstPlane.transform.parent.name}", ARManagerDebugFlags.Initialization);
            }
            else
            {
                LogWarning("Start: Could not determine TrackablesParent Instance ID from existing planes.", ARManagerDebugFlags.Initialization);
            }
        }
        else
        {
            LogWarning("Start: XROrigin.TrackablesParent is null and no AR planes found to determine TrackablesParent Instance ID.", ARManagerDebugFlags.Initialization);
        }

        Log($"START_METHOD_EXIT. wallSegmentation is {(wallSegmentation != null ? "ASSIGNED" : "NOT ASSIGNED")}", ARManagerDebugFlags.Initialization);
    }


    private void Update()
    {
        frameCounter++;
        HandlePlaneSelectionByTap(); // Check for taps to select/paint planes

        if (highlightPersistentPlanes)
        {
            foreach (var entry in persistentGeneratedPlanes)
            {
                if (entry.Key != null && entry.Value) // if is persistent
                {
                    Renderer rend = entry.Key.GetComponent<Renderer>();
                    if (rend != null && rend.material.color != persistentPlaneColor)
                    {
                        rend.material.color = persistentPlaneColor;
                    }
                }
            }
        }
    }

    private void SubscribeToWallSegmentation()
    {
        if (wallSegmentation != null && !isSubscribedToWallSegmentation)
        {
            Log("Attempting to subscribe to WallSegmentation events.", ARManagerDebugFlags.System);
            wallSegmentation.OnSegmentationMaskUpdated += OnSegmentationMaskUpdated;
            // Assuming WallSegmentation has an event for when the low-resolution mask (before upscaling) is ready
            if (wallSegmentation.TryGetLowResMask(out _)) // Check if method exists (conceptual)
            {
                // wallSegmentation.OnLowResMaskReady += OnLowResSegmentationMaskReady; // Hypothetical event
            }
            isSubscribedToWallSegmentation = true;
            Log("Subscribed to WallSegmentation.OnSegmentationMaskUpdated.", ARManagerDebugFlags.System);
        }
        else if (wallSegmentation == null)
        {
            LogWarning("WallSegmentation component not found. Cannot subscribe to segmentation events.", ARManagerDebugFlags.System);
        }
        else if (isSubscribedToWallSegmentation)
        {
            // Log("Already subscribed to WallSegmentation events.", ARManagerDebugFlags.System);
        }
    }

    private IEnumerator RetrySubscriptionAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        Log("Retrying subscription to WallSegmentation after delay.", ARManagerDebugFlags.System);
        SubscribeToWallSegmentation();
    }


    private void OnDisable()
    {
        Log("OnDisable: Unsubscribing from events.", ARManagerDebugFlags.System);
        if (planeManager != null)
        {
            planeManager.planesChanged -= OnPlanesChanged;
            Log("Unsubscribed from ARPlaneManager.planesChanged.", ARManagerDebugFlags.System);
        }

        if (wallSegmentation != null && isSubscribedToWallSegmentation)
        {
            wallSegmentation.OnSegmentationMaskUpdated -= OnSegmentationMaskUpdated;
            // if (wallSegmentation.TryGetLowResMask(out _)) // conceptual
            // {
            //    // wallSegmentation.OnLowResMaskReady -= OnLowResSegmentationMaskReady;
            // }
            isSubscribedToWallSegmentation = false;
            Log("Unsubscribed from WallSegmentation.OnSegmentationMaskUpdated.", ARManagerDebugFlags.System);
        }

        if (arCameraManager != null)
        {
            arCameraManager.frameReceived -= OnARFrameReceived;
            Log("Unsubscribed from ARCameraManager.frameReceived.", ARManagerDebugFlags.System);
        }
        if (arMeshManager != null && enableSceneReconstruction)
        {
            arMeshManager.meshesChanged -= OnMeshesChanged;
            Log("Unsubscribed from ARMeshManager.meshesChanged.", ARManagerDebugFlags.System);
        }
    }

    private void InitializeLightEstimation()
    {
        if (arCameraManager != null && mainLight != null)
        {
            arCameraManager.frameReceived -= OnARFrameReceived; // Ensure no double subscription
            arCameraManager.frameReceived += OnARFrameReceived;
            Log("Light estimation initialized and subscribed to ARCameraManager.frameReceived.", ARManagerDebugFlags.ARSystem);
        }
        else
        {
            if (arCameraManager == null) LogWarning("ARCameraManager not found, cannot initialize light estimation.", ARManagerDebugFlags.Initialization | ARManagerDebugFlags.ARSystem);
            if (mainLight == null) LogWarning("AR Directional Light (mainLight) not assigned, cannot initialize light estimation.", ARManagerDebugFlags.Initialization | ARManagerDebugFlags.ARSystem);
        }
    }

    private void InitializeEnvironmentProbes()
    {
        // Placeholder for environment probe logic if needed
        // Example: if (arEnvironmentProbeManager != null) { ... }
        Log("Environment Probes initialization placeholder.", ARManagerDebugFlags.ARSystem);
    }
    private void InitializeSceneReconstruction()
    {
        if (arMeshManager != null)
        {
            if (enableSceneReconstruction && arMeshManager.subsystem != null && arMeshManager.subsystem.running)
            {
                arMeshManager.meshesChanged -= OnMeshesChanged; // Ensure no double subscription
                arMeshManager.meshesChanged += OnMeshesChanged;
                // arMeshManager.density = 0.5f; // Example: Adjust mesh density
                Log("Scene Reconstruction (ARMeshManager) initialized and subscribed to meshesChanged.", ARManagerDebugFlags.ARSystem);
            }
            else if (!enableSceneReconstruction)
            {
                Log("Scene Reconstruction is disabled in settings. ARMeshManager will not be used.", ARManagerDebugFlags.ARSystem);
                if (arMeshManager.subsystem != null && arMeshManager.subsystem.running) arMeshManager.subsystem.Stop();
                arMeshManager.enabled = false;
            }
            else if (arMeshManager.subsystem == null || !arMeshManager.subsystem.running)
            {
                LogWarning("ARMeshManager subsystem is not available or not running. Scene Reconstruction might not work.", ARManagerDebugFlags.ARSystem);
                // Optionally try to start it if it's just not started.
                // if (arMeshManager.subsystem != null && !arMeshManager.subsystem.running) arMeshManager.subsystem.Start();
                arMeshManager.enabled = false; // Disable component if subsystem not ready
            }
        }
        else
        {
            LogWarning("ARMeshManager not assigned. Scene Reconstruction cannot be initialized.", ARManagerDebugFlags.ARSystem);
        }
    }


    public void УстановитьОтображениеМаскиUI(UnityEngine.UI.RawImage rawImageДляУстановки)
    {
        отображениеМаскиUI = rawImageДляУстановки;
        if (отображениеМаскиUI != null)
        {
            Log("RawImage для отображения маски UI установлено.", ARManagerDebugFlags.System);
            if (currentSegmentationMask != null && currentSegmentationMask.IsCreated())
            {
                отображениеМаскиUI.texture = currentSegmentationMask;
                отображениеМаскиUI.color = Color.white; // Ensure it's visible
            }
        }
        else
        {
            LogWarning("Попытка установить null RawImage для отображения маски UI.", ARManagerDebugFlags.System);
        }
    }


    private void FindARComponents()
    {
        Log($"[{GetType().Name}] FindARComponents: Method Entered (Direct Log - called from Awake/Start)", ARManagerDebugFlags.Initialization);
        bool originFound = this.xrOrigin != null;
        if (this.xrOrigin == null)
        {
            this.xrOrigin = FindObjectOfType<XROrigin>();
            if (this.xrOrigin != null) Log("XROrigin found via FindObjectOfType.", ARManagerDebugFlags.Initialization);
            else LogError("XROrigin NOT FOUND in scene! Many AR functions will fail.", ARManagerDebugFlags.Initialization);
        }

        if (this.xrOrigin != null && arCameraManager == null) arCameraManager = this.xrOrigin.CameraFloorOffsetObject?.GetComponentInChildren<ARCameraManager>();
        if (arCameraManager == null) arCameraManager = FindObjectOfType<ARCameraManager>();

        if (this.xrOrigin != null && planeManager == null) planeManager = this.xrOrigin.GetComponentInChildren<ARPlaneManager>();
        if (planeManager == null) planeManager = FindObjectOfType<ARPlaneManager>();

        if (this.xrOrigin != null && arRaycastManager == null) arRaycastManager = this.xrOrigin.GetComponentInChildren<ARRaycastManager>();
        if (arRaycastManager == null) arRaycastManager = FindObjectOfType<ARRaycastManager>();

        if (this.xrOrigin != null && arOcclusionManager == null) arOcclusionManager = this.xrOrigin.CameraFloorOffsetObject?.GetComponentInChildren<AROcclusionManager>();
        if (arOcclusionManager == null) arOcclusionManager = FindObjectOfType<AROcclusionManager>();

        if (this.xrOrigin != null && arMeshManager == null) arMeshManager = this.xrOrigin.GetComponentInChildren<ARMeshManager>();
        if (arMeshManager == null) arMeshManager = FindObjectOfType<ARMeshManager>();

        if (this.xrOrigin != null && arAnchorManager == null) arAnchorManager = this.xrOrigin.GetComponentInChildren<ARAnchorManager>();
        if (arAnchorManager == null) arAnchorManager = FindObjectOfType<ARAnchorManager>();

        if (mainLight == null)
        {
            var lights = FindObjectsOfType<Light>();
            mainLight = lights.FirstOrDefault(l => l.type == LightType.Directional);
            if (mainLight != null) LogWarning("AR Directional Light (mainLight) auto-assigned. Please assign manually for reliability.", ARManagerDebugFlags.Initialization);
            else LogWarning("AR Directional Light (mainLight) not assigned and not found. Light estimation may not work.", ARManagerDebugFlags.Initialization);
        }

        // WallSegmentation is typically assigned in Awake or via Inspector.
        if (wallSegmentation == null) wallSegmentation = FindObjectOfType<WallSegmentation>();


        string report = "FindARComponents Report:\n";
        report += $"  XROrigin: {(this.xrOrigin != null ? this.xrOrigin.name : "NULL")}{(originFound ? " (was pre-assigned)" : "")}\n";
        report += $"  ARCameraManager: {(arCameraManager != null ? "Found" : "NULL")}\n";
        report += $"  ARPlaneManager: {(planeManager != null ? "Found" : "NULL")}\n";
        report += $"  ARRaycastManager: {(arRaycastManager != null ? "Found" : "NULL")}\n";
        report += $"  AROcclusionManager: {(arOcclusionManager != null ? "Found" : "NULL")}\n";
        report += $"  ARMeshManager: {(arMeshManager != null ? "Found" : "NULL")}\n";
        report += $"  ARAnchorManager: {(arAnchorManager != null ? "Found" : "NULL")}\n";
        report += $"  AR Directional Light (mainLight): {(mainLight != null ? mainLight.name : "NULL")}\n";
        report += $"  WallSegmentation: {(wallSegmentation != null ? "Found" : "NULL")}\n";
        report += $"  ARPlaneConfigurator (from prefab): {(planeConfigurator != null ? "Found on prefab" : (arPaintPlanePrefab != null ? "Not on prefab" : "Prefab NULL"))}\n";
        Log(report, ARManagerDebugFlags.Initialization);
    }


    private void InitializeMaterials()
    {
        if (verticalPlaneMaterial == null) LogWarning("VerticalPlaneMaterial is not assigned.", ARManagerDebugFlags.Initialization);
        if (horizontalPlaneMaterial == null) LogWarning("HorizontalPlaneMaterial is not assigned.", ARManagerDebugFlags.Initialization);
        if (selectionMaterial == null) Log("SelectionMaterial is not assigned (optional).", ARManagerDebugFlags.Initialization);
        if (wallPaintMaterial == null) LogWarning("WallPaintMaterial is not assigned. Painting will not work.", ARManagerDebugFlags.Initialization);

        if (debugRayMaterialPropertyBlock == null)
        {
            debugRayMaterialPropertyBlock = new MaterialPropertyBlock();
        }
        Log("Materials initialized (checked for assignment).", ARManagerDebugFlags.Initialization);
    }

    private void OnARFrameReceived(ARCameraFrameEventArgs eventArgs)
    {
        if (mainLight == null) return; // No light to update

        ARLightEstimationData lightEstimation = eventArgs.lightEstimation;

        if (lightEstimation.averageBrightness.HasValue)
        {
            mainLight.intensity = lightEstimation.averageBrightness.Value;
        }
        if (lightEstimation.averageColorTemperature.HasValue)
        {
            mainLight.colorTemperature = lightEstimation.averageColorTemperature.Value;
        }
        if (lightEstimation.colorCorrection.HasValue)
        {
            mainLight.color = lightEstimation.colorCorrection.Value;
        }
        // More advanced lighting updates can be added here, e.g., for mainLightDirection or sphericalHarmonicsL2.
        if (lightEstimation.mainLightDirection.HasValue)
        {
            mainLight.transform.rotation = Quaternion.LookRotation(lightEstimation.mainLightDirection.Value);
        }
        if (lightEstimation.mainLightIntensityLumens.HasValue)
        {
            // Convert lumens to Unity intensity (this is an approximation and might need adjustment)
            mainLight.intensity = lightEstimation.mainLightIntensityLumens.Value / 1000.0f; // Rough conversion
        }
    }


    private void OnSegmentationMaskUpdated(RenderTexture mask)
    {
        if (mask == null || !mask.IsCreated())
        {
            LogWarning("OnSegmentationMaskUpdated received a null or uncreated mask.", ARManagerDebugFlags.PlaneGeneration);
            return;
        }
        // Log($"OnSegmentationMaskUpdated: Received new mask. W:{mask.width}, H:{mask.height}. Frame: {frameCounter}", ARManagerDebugFlags.Performance);

        currentSegmentationMask = mask; // This is the high-resolution mask after WallSegmentation's processing
        newMaskAvailableForProcessing = true;

        if (отображениеМаскиUI != null)
        {
            if (отображениеМаскиUI.texture != currentSegmentationMask)
            {
                отображениеМаскиUI.texture = currentSegmentationMask;
            }
            if (отображениеМаскиUI.color != Color.white) отображениеМаскиUI.color = Color.white;
        }

        // If we're using segmentation for plane generation, process the mask
        if (useSegmentationForPlaneGeneration && newMaskAvailableForProcessing)
        {
            ProcessSegmentationMask(currentSegmentationMask);
            newMaskAvailableForProcessing = false; // Consumed the new mask
        }
    }
    private void OnMeshesChanged(ARMeshesChangedEventArgs args)
    {
        if ((this.debugFlags & ARManagerDebugFlags.ARSystem) != 0)
        {
            if (args.added.Count > 0) Log($"OnMeshesChanged: {args.added.Count} mesh(es) added.", ARManagerDebugFlags.ARSystem);
            if (args.updated.Count > 0) Log($"OnMeshesChanged: {args.updated.Count} mesh(es) updated.", ARManagerDebugFlags.ARSystem);
            if (args.removed.Count > 0) Log($"OnMeshesChanged: {args.removed.Count} mesh(es) removed.", ARManagerDebugFlags.ARSystem);
        }
        // Example: Add MeshColliders to new meshes for physics interaction
        foreach (var meshFilter in args.added) { EnsureMeshCollider(meshFilter); }
        foreach (var meshFilter in args.updated) { EnsureMeshCollider(meshFilter); }
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
        string logPrefix = $"[{this.GetType().Name}] ProcessSegmentationMask:";
        if ((this.debugFlags & (ARManagerDebugFlags.PlaneGeneration | ARManagerDebugFlags.Raycasting)) != 0)
        {
            this.debugFlags |= ARManagerDebugFlags.SaveDebugTextures; // Ensure saving is enabled if we're debugging planes/rays
        }

        if (maskToProcess == null)
        {
            LogWarning(logPrefix + " maskToProcess is null. Aborting.", ARManagerDebugFlags.PlaneGeneration);
            return;
        }

        Log($"ProcessSegmentationMask: Received mask. W:{maskToProcess.width}, H:{maskToProcess.height}. Format: {maskToProcess.format}. Frame: {frameCounter}", ARManagerDebugFlags.Performance);

        if ((this.debugFlags & ARManagerDebugFlags.SaveDebugTextures) != 0)
        {
            if (wallSegmentation != null)
            {
                wallSegmentation.SaveTextureForDebug(maskToProcess, $"ARManagerInitializer2_ReceivedMask_F{frameCounter}");
            }
            else
            {
                SaveRenderTextureToFile(maskToProcess, $"ARManagerInitializer2_ReceivedMask_F{frameCounter}_Direct.png");
            }
        }

        // Attempt to get the low-resolution mask if WallSegmentation provides it
        // This is conceptual; WallSegmentation needs to expose this.
        bool gotLowRes = false;
        if (wallSegmentation != null && wallSegmentation.TryGetLowResMask(out RenderTexture directLowResMask))
        {
            if (directLowResMask != null && directLowResMask.IsCreated())
            {
                Log($"Successfully got low-res mask ({directLowResMask.width}x{directLowResMask.height}) directly from WallSegmentation.", ARManagerDebugFlags.PlaneGeneration);
                lowResSegmentationMask = directLowResMask; // Use it directly
                if ((this.debugFlags & ARManagerDebugFlags.SaveDebugTextures) != 0)
                {
                    wallSegmentation.SaveTextureForDebug(lowResSegmentationMask, $"ARManagerInitializer2_LowResMask_F{frameCounter}_SourceDirect");
                }
                gotLowRes = true;
            }
            else
            {
                LogWarning("WallSegmentation.TryGetLowResMask returned true but mask was null or not created.", ARManagerDebugFlags.PlaneGeneration);
            }
        }


        if (!gotLowRes || lowResSegmentationMask == null) // If direct low-res not available, downsample current mask
        {
            Log("Low-res mask not directly available from WallSegmentation or failed to get. Downsampling high-res mask.", ARManagerDebugFlags.PlaneGeneration);
            int lowResWidth = Mathf.Max(minPixelsDimensionForLowResArea, maskToProcess.width / 8); // Example downscale factor
            int lowResHeight = Mathf.Max(minPixelsDimensionForLowResArea, maskToProcess.height / 8);

            if (lowResSegmentationMask == null || lowResSegmentationMask.width != lowResWidth || lowResSegmentationMask.height != lowResHeight)
            {
                if (lowResSegmentationMask != null) lowResSegmentationMask.Release();
                lowResSegmentationMask = new RenderTexture(lowResWidth, lowResHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                lowResSegmentationMask.name = "LowResSegMask_ARManager";
                lowResSegmentationMask.Create();
                Log($"Created/Recreated lowResSegmentationMask ({lowResWidth}x{lowResHeight}) for downsampling.", ARManagerDebugFlags.PlaneGeneration);
            }
            Graphics.Blit(maskToProcess, lowResSegmentationMask);
            if ((this.debugFlags & ARManagerDebugFlags.SaveDebugTextures) != 0)
            {
                SaveRenderTextureToFile(lowResSegmentationMask, $"ARManagerInitializer2_LowResMask_F{frameCounter}_Downsampled.png");
            }
        }


        if (lowResSegmentationMask == null || !lowResSegmentationMask.IsCreated())
        {
            LogError(logPrefix + " lowResSegmentationMask is null or not created after attempts. Aborting plane generation for this frame.", ARManagerDebugFlags.PlaneGeneration);
            return;
        }

        Texture2D tex2D = RenderTextureToTexture2D(lowResSegmentationMask); // Read pixels from low-res mask
        if (tex2D == null)
        {
            LogError(logPrefix + " Failed to convert lowResSegmentationMask to Texture2D.", ARManagerDebugFlags.PlaneGeneration);
            return;
        }

        Color32[] pixels = tex2D.GetPixels32();
        Destroy(tex2D); // Clean up Texture2D immediately after getting pixels

        List<Rect> wallAreas = FindWallAreas(pixels, lowResSegmentationMask.width, lowResSegmentationMask.height, wallMaskRedChannelMinThreshold);
        Log($"Found {wallAreas.Count} wall areas in low-res mask (Threshold: {wallMaskRedChannelMinThreshold}).", ARManagerDebugFlags.PlaneGeneration);

        Dictionary<GameObject, bool> visitedPlanesInCurrentMask = new Dictionary<GameObject, bool>();

        if (wallAreas.Count > 0)
        {
            foreach (Rect area in wallAreas)
            {
                if ((this.debugFlags & ARManagerDebugFlags.PlaneGeneration) != 0)
                {
                    Log($"Processing wall area: {areaToString(area)}", ARManagerDebugFlags.PlaneGeneration);
                }
                bool planeUpdatedOrCreated = UpdateOrCreatePlaneForWallArea(area, lowResSegmentationMask.width, lowResSegmentationMask.height, visitedPlanesInCurrentMask);
                // Log($"Plane updated/created for area {areaToString(area)}: {planeUpdatedOrCreated}", ARManagerDebugFlags.PlaneGeneration);
            }
        }
        else
        {
            Log("No significant wall areas found in the current segmentation mask to generate/update planes.", ARManagerDebugFlags.PlaneGeneration);
        }
        CleanupOldPlanes(visitedPlanesInCurrentMask);
    }


    public void SaveRenderTextureToFile(RenderTexture rt, string fileName)
    {
        if (rt == null) return;
        RenderTexture activeRenderTexture = RenderTexture.active;
        RenderTexture.active = rt;
        Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = activeRenderTexture;

        byte[] bytes = tex.EncodeToPNG();
        Destroy(tex);

        string path = Path.Combine(Application.persistentDataPath, "DebugTextures");
        Directory.CreateDirectory(path); // Ensure directory exists
        File.WriteAllBytes(Path.Combine(path, fileName), bytes);
        Log($"Saved debug texture to {Path.Combine(path, fileName)}", ARManagerDebugFlags.SaveDebugTextures);
    }


    private Texture2D RenderTextureToTexture2D(RenderTexture rTex)
    {
        if (rTex == null) return null;
        Texture2D tex = new Texture2D(rTex.width, rTex.height, TextureFormat.RGBA32, false);
        RenderTexture currentActiveRT = RenderTexture.active;
        RenderTexture.active = rTex;
        tex.ReadPixels(new Rect(0, 0, rTex.width, rTex.height), 0, 0);
        tex.Apply();
        RenderTexture.active = currentActiveRT;
        return tex;
    }

    private List<Rect> FindWallAreas(Color32[] pixels, int width, int height, byte threshold)
    {
        List<Rect> areas = new List<Rect>();
        bool[,] visited = new bool[width, height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!visited[x, y] && pixels[y * width + x].r >= threshold) // Assuming wall is in red channel
                {
                    Rect area = FindConnectedArea(pixels, width, height, x, y, visited, threshold);
                    if (area.width >= minPixelsDimensionForLowResArea && area.height >= minPixelsDimensionForLowResArea && (area.width * area.height) >= minAreaSizeInLowResPixels)
                    {
                        areas.Add(area);
                    }
                }
            }
        }
        return areas;
    }

    private Rect FindConnectedArea(Color32[] pixels, int width, int height, int startX, int startY, bool[,] visited, byte threshold)
    {
        Queue<Vector2Int> queue = new Queue<Vector2Int>();
        queue.Enqueue(new Vector2Int(startX, startY));
        visited[startX, startY] = true;

        int minX = startX, minY = startY, maxX = startX, maxY = startY;

        while (queue.Count > 0)
        {
            Vector2Int p = queue.Dequeue();

            minX = Mathf.Min(minX, p.x);
            minY = Mathf.Min(minY, p.y);
            maxX = Mathf.Max(maxX, p.x);
            maxY = Mathf.Max(maxY, p.y);

            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;

                    int nx = p.x + dx;
                    int ny = p.y + dy;

                    // DETAILED LOGGING FOR NEIGHBOR CHECK
                    if ((this.debugFlags & ARManagerDebugFlags.PlaneGeneration) != 0 && enableDetailedRaycastLogging && nx >= 0 && nx < width && ny >= 0 && ny < height) // GUARDED LOG
                    {
                        Log($"FindConnectedArea: Checking Neighbor({nx},{ny}) of({p.x},{p.y}). Pixel R-value: {pixels[ny * width + nx].r}, Visited: {visited[nx, ny]}, Threshold: {threshold} (Looking for R > threshold)", ARManagerDebugFlags.PlaneGeneration);
                    }


                    if (nx >= 0 && nx < width && ny >= 0 && ny < height &&
                        !visited[nx, ny] && pixels[ny * width + nx].r >= threshold)
                    {
                        visited[nx, ny] = true;
                        queue.Enqueue(new Vector2Int(nx, ny));
                    }
                }
            }
        }
        return new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private string areaToString(Rect area) => $"X:{area.x:F0}, Y:{area.y:F0}, W:{area.width:F0}, H:{area.height:F0}";

    private bool UpdateOrCreatePlaneForWallArea(Rect area, int textureWidth, int textureHeight, Dictionary<GameObject, bool> visitedPlanesInCurrentMask)
    {
        string logPrefix = $"[UpdateOrCreatePlaneForWallArea(Area:{areaToString(area)})] ";
        Camera cam = this.xrOrigin != null ? this.xrOrigin.Camera : Camera.main;

        if (cam == null)
        {
            LogError(logPrefix + "Camera not found. Cannot perform raycast.", ARManagerDebugFlags.PlaneGeneration);
            return false;
        }

        // Center of the detected area in low-res mask (normalized 0-1)
        Vector2 normalizedCenter = new Vector2((area.x + area.width / 2f) / textureWidth, (area.y + area.height / 2f) / textureHeight);
        Vector2 screenPoint = new Vector2(normalizedCenter.x * cam.pixelWidth, normalizedCenter.y * cam.pixelHeight);

        string planeIdentifier = GeneratePlaneIdentifier(area, textureWidth, textureHeight);
        GameObject hitSurfaceObject = null; // The actual world object hit by the raycast
        Pose hitPose = default;
        bool hitSuccess = false;
        Vector2 hitUV = Vector2.zero; // UV coordinates on the hit surface, if applicable

        // Perform Raycast
        List<ARRaycastHit> arHits = new List<ARRaycastHit>();
        bool arHitSuccess = false;
        if (arRaycastManager != null && useDetectedPlanesForTap) // useDetectedPlanesForTap can double for "use AR Raycast"
        {
            arHitSuccess = arRaycastManager.Raycast(screenPoint, arHits, trackableTypesToHit);
            if (arHitSuccess)
            {
                hitPose = arHits[0].pose;
                hitSuccess = true;
                // Try to get the ARPlane GameObject
                ARPlane plane = planeManager?.GetPlane(arHits[0].trackableId);
                if (plane != null) hitSurfaceObject = plane.gameObject;

                Log(logPrefix + $"AR Raycast hit AR Plane: {hitSurfaceObject?.name ?? "Unknown AR Plane"} at {hitPose.position.ToString("F3")}", ARManagerDebugFlags.Raycasting);
            }
        }

        // Physics Raycast as primary or fallback
        Ray ray = cam.ScreenPointToRay(screenPoint);
        if (alwaysUsePhysicsRaycastForCustomPlanes || !hitSuccess) // If always using physics OR AR raycast failed
        {
            if (Physics.Raycast(ray, out RaycastHit physicsHit, maxRayDistance, hitLayerMask))
            {
                // Check if we hit an ignored object first
                if (IsIgnoredObject(physicsHit.collider.gameObject))
                {
                    Log(logPrefix + $"Physics Raycast hit an IGNORED object: {physicsHit.collider.name}. No plane will be created/updated here based on this hit.", ARManagerDebugFlags.Raycasting);
                }
                else if (physicsHit.distance >= minHitDistanceThreshold)
                {
                    hitPose = new Pose(physicsHit.point, Quaternion.LookRotation(-physicsHit.normal)); // Plane normal is opposite to hit normal
                    hitSurfaceObject = physicsHit.collider.gameObject;
                    hitUV = physicsHit.textureCoord; // Store UV if available
                    hitSuccess = true;
                    Log(logPrefix + $"Physics Raycast hit: {hitSurfaceObject.name} at {hitPose.position.ToString("F3")}, Normal: {physicsHit.normal.ToString("F3")}, Dist: {physicsHit.distance:F2}. Layer: {LayerMask.LayerToName(hitSurfaceObject.layer)}", ARManagerDebugFlags.Raycasting);
                }
                else
                {
                    Log(logPrefix + $"Physics Raycast hit too close: {physicsHit.collider.name} at distance {physicsHit.distance:F2}. Min threshold: {minHitDistanceThreshold}.", ARManagerDebugFlags.Raycasting);
                }
            }
            else
            {
                Log(logPrefix + "Physics Raycast did not hit any surface within range or layer mask.", ARManagerDebugFlags.Raycasting);
            }
        }


        if ((this.debugFlags & ARManagerDebugFlags.Raycasting) != 0 && drawDebugRays)
        {
            Color rayColor = hitSuccess ? Color.green : (arHitSuccess ? Color.blue : Color.red); // Green for final phys, Blue for AR, Red for miss
            DrawDebugRay(ray, rayColor, 2.0f);
            if ((this.debugFlags & ARManagerDebugFlags.DetailedRaycastLogging) != 0)
            {
                Log(logPrefix + $"[RayDebug] Cam: {cam.name}, Pos: {cam.transform.position.ToString("F3")}, Fwd: {cam.transform.forward.ToString("F3")}", ARManagerDebugFlags.Raycasting | ARManagerDebugFlags.DetailedRaycastLogging);
                Log(logPrefix + $"[RayDebug] Ray O: {ray.origin.ToString("F3")}, D: {ray.direction.ToString("F3")}", ARManagerDebugFlags.Raycasting | ARManagerDebugFlags.DetailedRaycastLogging);
            }
            else if ((this.debugFlags & ARManagerDebugFlags.Raycasting) != 0)
            {
                Log(logPrefix + "Raycast initiated (detailed ray params not logged).", ARManagerDebugFlags.Raycasting);
            }
        }


        if (hitSuccess)
        {
            if (hitSurfaceObject != null && LayerMask.LayerToName(hitSurfaceObject.layer) == basicSceneGeometryLayerName)
            {
                Log(logPrefix + $"Hit surface '{hitSurfaceObject.name}' is part of basic scene geometry. Proceeding to create/update paint plane.", ARManagerDebugFlags.PlaneGeneration);
            }
            else if (hitSurfaceObject != null && IsARFoundationPlane(hitSurfaceObject))
            {
                Log(logPrefix + $"Hit surface '{hitSurfaceObject.name}' is an ARFoundation plane. Will use its pose.", ARManagerDebugFlags.PlaneGeneration);
            }


            // Filter by plane normal relative to camera forward
            Vector3 cameraForwardOnHitPlane = Vector3.ProjectOnPlane(cam.transform.forward, hitPose.up).normalized;
            float angle = Vector3.Angle(cameraForwardOnHitPlane, -hitPose.forward); // -hitPose.forward is the direction the plane is "facing"

            if (angle <= maxCameraAngleToPlaneNormal)
            {
                GameObject planeGO = CreateOrUpdateARPaintPlane(area, hitPose, hitSurfaceObject, visitedPlanesInCurrentMask, hitUV, planeIdentifier);
                if (planeGO != null)
                {
                    visitedPlanesInCurrentMask[planeGO] = true; // Mark as visited/updated in this frame
                    return true;
                }
            }
            else
            {
                Log(logPrefix + $"Plane creation skipped: Angle between camera forward ({cameraForwardOnHitPlane.ToString("F3")}) and plane normal ({(-hitPose.forward).ToString("F3")}) is {angle:F1}°, which is > max ({maxCameraAngleToPlaneNormal}°).", ARManagerDebugFlags.PlaneGeneration);
            }
        }
        else // No hit, create a fallback plane at a default distance
        {
            if (defaultPlaneDistance >= minPlaneCreationDistance)
            {
                Log(logPrefix + "No surface hit by raycast. Creating fallback plane.", ARManagerDebugFlags.PlaneGeneration);
                Vector3 planePosition = cam.transform.position + cam.transform.forward * defaultPlaneDistance;
                Quaternion planeRotation = Quaternion.LookRotation(-cam.transform.forward, cam.transform.up); // Facing the camera
                hitPose = new Pose(planePosition, planeRotation);

                GameObject planeGO = CreateOrUpdateARPaintPlane(area, hitPose, null, visitedPlanesInCurrentMask, Vector2.zero, planeIdentifier); // No hit surface object
                if (planeGO != null)
                {
                    visitedPlanesInCurrentMask[planeGO] = true;
                    return true;
                }
            }
            else
            {
                Log(logPrefix + $"Fallback plane creation skipped: defaultPlaneDistance {defaultPlaneDistance}m < minPlaneCreationDistance {minPlaneCreationDistance}m.", ARManagerDebugFlags.PlaneGeneration);
            }
        }
        return false;
    }

    private GameObject CreateOrUpdateARPaintPlane(Rect associatedArea, Pose hitPose, GameObject hitSurfaceObject, Dictionary<GameObject, bool> visitedPlanesInCurrentMask, Vector2 uvPoint, string planeIdentifier)
    {
        string logPrefix = $"[CreateOrUpdateARPaintPlane(Area:{areaToString(associatedArea)}, HitSurface:{hitSurfaceObject?.name ?? "Fallback/None"}, ID:{planeIdentifier})] ";
        Camera cam = this.xrOrigin != null ? this.xrOrigin.Camera : Camera.main;

        if (cam == null)
        {
            LogError(logPrefix + "Camera is null. Cannot proceed.", ARManagerDebugFlags.PlaneGeneration);
            return null;
        }

        GameObject existingPlane = FindClosestExistingPlane(hitPose.position, planeIdentifier);

        if (existingPlane != null && (hitPose.position - existingPlane.transform.position).magnitude < planeMergeDistanceThreshold)
        {
            Log(logPrefix + $"Updating existing plane '{existingPlane.name}'.", ARManagerDebugFlags.PlaneGeneration);
            existingPlane.transform.SetPositionAndRotation(hitPose.position, hitPose.rotation);
            // Optionally update scale or other properties if needed
            planeLastVisitedTime[existingPlane] = Time.time;
            return existingPlane;
        }
        else
        {
            if (arPaintPlanePrefab == null)
            {
                LogError(logPrefix + "arPaintPlanePrefab is null. Cannot create new plane.", ARManagerDebugFlags.PlaneGeneration);
                return null;
            }

            Log(logPrefix + $"Creating new plane. ID: {planeIdentifier}", ARManagerDebugFlags.PlaneGeneration);
            GameObject newPlane = Instantiate(arPaintPlanePrefab, hitPose.position, hitPose.rotation);
            newPlane.name = $"ARPaintPlane_{planeInstanceCounter++}_{planeIdentifier}";
            newPlane.transform.localScale = defaultPlaneScale;

            // Set layer
            int layer = LayerMask.NameToLayer(planeLayerName);
            if (layer != -1) newPlane.layer = layer;
            else LogWarning(logPrefix + $"Layer '{planeLayerName}' not found. Plane may not behave as expected.", ARManagerDebugFlags.PlaneGeneration);


            ARPlaneConfigurator configurator = newPlane.GetComponent<ARPlaneConfigurator>();
            if (configurator != null)
            {
                // Old calls:
                // configurator.InitializePlane(hitSurfaceObject, associatedArea, uvPoint, hitPose.up);
                // PlaneAlignment alignment = IsSurfaceVertical(hitPose.up) ? PlaneAlignment.Vertical : (Vector3.Dot(hitPose.up, Vector3.up) > horizontalSurfaceThreshold ? PlaneAlignment.HorizontalUp : PlaneAlignment.HorizontalDown);
                // configurator.ApplyMaterial(GetMaterialForPlane(hitPose.up, alignment));

                // New call:
                // Assuming newPlane might not have an ARPlane component if it's a generic prefab,
                // but ARPlaneConfigurator.ConfigurePlane expects an ARPlane.
                // This might need adjustment if newPlane is not itself an ARPlane or lacks the component.
                ARPlane arPlaneComponent = newPlane.GetComponent<ARPlane>();
                if (arPlaneComponent != null)
                {
                    configurator.ConfigurePlane(arPlaneComponent, this.wallSegmentation, this);
                }
                else
                {
                    LogWarning(logPrefix + $"ARPlane component not found on instantiated plane '{newPlane.name}'. Cannot fully configure with ARPlaneConfigurator.", ARManagerDebugFlags.PlaneGeneration);
                    // Potentially, we could call a different method on configurator that doesn't require ARPlane,
                    // or ARPlaneConfigurator needs to be more flexible for non-ARPlane GameObjects.
                    // For now, full configuration is skipped if no ARPlane component.
                }
            }
            else if (this.planeConfigurator != null) // Fallback to assign global one if prefab didn't have one
            {
                LogWarning(logPrefix + $"Instantiated arPaintPlanePrefab '{newPlane.name}' does not have an ARPlaneConfigurator. Attempting to use scene default if available, but this is not ideal.", ARManagerDebugFlags.PlaneGeneration);
                // This path is less ideal as the configurator might not be set up for this specific plane's context.
            }


            generatedPlanes.Add(newPlane);
            allGeneratedPlanes.Add(newPlane);
            activePlanesForPainting.Add(newPlane);
            planeCreationTimes[newPlane] = Time.time;
            planeLastVisitedTime[newPlane] = Time.time;
            persistentGeneratedPlanes.TryAdd(newPlane, false); // Add with non-persistent state initially

            return newPlane;
        }
    }

    private string GeneratePlaneIdentifier(Rect area, int textureWidth, int textureHeight)
    {
        // Create a simple identifier based on the quantized center of the area
        int qX = (int)((area.x + area.width / 2f) / textureWidth * 100);   // Quantize to 100 steps
        int qY = (int)((area.y + area.height / 2f) / textureHeight * 100); // Quantize to 100 steps
        return $"Area_{qX}_{qY}";
    }


    private GameObject FindClosestExistingPlane(Vector3 position, string planeIdentifier)
    {
        GameObject closest = null;
        float minDistance = float.MaxValue;

        // First, check for a plane with the exact same identifier (meaning it's likely the same semantic area)
        foreach (GameObject plane in generatedPlanes)
        {
            if (plane.name.Contains(planeIdentifier))
            {
                // If identifier matches, this is likely the one we want, even if slightly moved.
                // We could add a distance check here too if identifiers can collide for different but close areas.
                Log($"FindClosestExistingPlane: Found plane '{plane.name}' matching identifier '{planeIdentifier}'.", ARManagerDebugFlags.PlaneGeneration);
                return plane;
            }
        }

        // If no identifier match, fall back to simple distance (less reliable for semantic matching)
        foreach (GameObject plane in generatedPlanes)
        {
            float distance = Vector3.Distance(plane.transform.position, position);
            if (distance < minDistance)
            {
                minDistance = distance;
                closest = plane;
            }
        }

        if (closest != null && minDistance < planeMergeDistanceThreshold * 2) // Use a slightly larger threshold for this broader search
        {
            Log($"FindClosestExistingPlane: Found closest plane '{closest.name}' by distance ({minDistance:F2}m) for position {position.ToString("F3")}. No identifier match.", ARManagerDebugFlags.PlaneGeneration);
            return closest;
        }
        // Log($"FindClosestExistingPlane: No suitable existing plane found for identifier '{planeIdentifier}' or position {position.ToString("F3")}.", ARManagerDebugFlags.PlaneGeneration);
        return null;
    }


    private void CleanupOldPlanes(Dictionary<GameObject, bool> visitedPlanesInCurrentMask)
    {
        List<GameObject> planesToRemove = new List<GameObject>();
        float currentTime = Time.time;
        float removalDelay = GetUnvisitedPlaneRemovalDelay();

        foreach (GameObject plane in generatedPlanes)
        {
            if (plane == null) continue; // Already destroyed or invalid

            bool isPersistent = IsPlanePersistent(plane);
            if (isPersistent) continue; // Don't remove persistent planes automatically

            if (!visitedPlanesInCurrentMask.ContainsKey(plane))
            {
                // If not visited in current mask, check its last visited time
                if (planeLastVisitedTime.TryGetValue(plane, out float lastVisit))
                {
                    if (currentTime - lastVisit > removalDelay)
                    {
                        planesToRemove.Add(plane);
                        Log($"Marking plane '{plane.name}' for removal (unvisited for {currentTime - lastVisit:F1}s > {removalDelay:F1}s).", ARManagerDebugFlags.PlaneGeneration);
                    }
                }
                else
                {
                    // If it was never in planeLastVisitedTime (e.g. created but immediately not seen by mask logic again)
                    // and it's been around longer than the delay, also remove.
                    if (planeCreationTimes.TryGetValue(plane, out float creationTime))
                    {
                        if (currentTime - creationTime > removalDelay * 2) // Stricter for planes that never got a "last visit"
                        {
                            planesToRemove.Add(plane);
                            Log($"Marking plane '{plane.name}' for removal (created {currentTime - creationTime:F1}s ago, never confirmed visited by mask).", ARManagerDebugFlags.PlaneGeneration);
                        }
                    }
                }
            }
        }

        foreach (GameObject plane in planesToRemove)
        {
            Log($"Destroying unvisited plane: {plane.name}", ARManagerDebugFlags.PlaneGeneration);
            generatedPlanes.Remove(plane);
            allGeneratedPlanes.Remove(plane);
            activePlanesForPainting.Remove(plane);
            planeCreationTimes.Remove(plane);
            planeLastVisitedTime.Remove(plane);
            persistentGeneratedPlanes.Remove(plane);
            Destroy(plane);
        }
    }


    // Overload for more specific search if needed, e.g. matching normal
    private (GameObject plane, float distance, float angleDifference) FindClosestExistingPlane(
        Vector3 position, Vector3 normal, float maxDistance, float maxAngleDegrees)
    {
        GameObject closestPlane = null;
        float minDistance = maxDistance;
        float minAngleDiff = maxAngleDegrees;

        foreach (var plane in generatedPlanes) // Or use allGeneratedPlanes for a broader search
        {
            if (plane == null) continue;

            float dist = Vector3.Distance(plane.transform.position, position);
            if (dist < minDistance)
            {
                float angleDiff = Vector3.Angle(plane.transform.up, normal); // Assuming plane's 'up' is its normal
                if (angleDiff < minAngleDiff)
                {
                    minDistance = dist;
                    minAngleDiff = angleDiff;
                    closestPlane = plane;
                }
            }
        }
        return (closestPlane, minDistance, minAngleDiff);
    }


    public static string GetGameObjectPath(Transform transform)
    {
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
        var included = new List<string>();
        for (int i = 0; i < 32; i++)
        {
            if ((layerMask.value & (1 << i)) != 0)
            {
                string layerName = LayerMask.LayerToName(i);
                if (string.IsNullOrEmpty(layerName)) included.Add($"Layer{i}");
                else included.Add(layerName);
            }
        }
        return string.Join(", ", included);
    }


    public Material VerticalPlaneMaterial => verticalPlaneMaterial;
    public Material HorizontalPlaneMaterial => horizontalPlaneMaterial;
    public string PlaneLayerName => planeLayerName;

    private void InitializePersistentPlanesSystem()
    {
        // Could load saved persistent planes here if implementing save/load
        Log("Persistent planes system initialized (currently in-memory only).", ARManagerDebugFlags.System);
    }

    public bool MakePlanePersistent(GameObject plane)
    {
        if (plane == null) return false;
        if (persistentGeneratedPlanes.ContainsKey(plane))
        {
            persistentGeneratedPlanes[plane] = true;
            Log($"Made plane '{plane.name}' persistent.", ARManagerDebugFlags.System);
            if (highlightPersistentPlanes)
            {
                Renderer rend = plane.GetComponent<Renderer>();
                if (rend != null) rend.material.color = persistentPlaneColor;
            }
            return true;
        }
        LogWarning($"Cannot make plane '{plane.name}' persistent. It's not in the tracked list.", ARManagerDebugFlags.System);
        return false;
    }

    public bool IsPlanePersistent(GameObject plane)
    {
        if (plane == null) return false;
        return persistentGeneratedPlanes.TryGetValue(plane, out bool isPersistent) && isPersistent;
    }

    public bool RemovePlanePersistence(GameObject plane)
    {
        if (plane == null) return false;
        if (persistentGeneratedPlanes.ContainsKey(plane))
        {
            persistentGeneratedPlanes[plane] = false;
            Log($"Removed persistence from plane '{plane.name}'.", ARManagerDebugFlags.System);
            // Revert material if needed (ARPlaneConfigurator should handle this based on its original material logic)
            // ARPlaneConfigurator configurator = plane.GetComponent<ARPlaneConfigurator>();
            // // if (configurator != null) configurator.ResetMaterial(); // Conceptual // Commented out due to CS1061
            // else
            // {
            //     Renderer rend = plane.GetComponent<Renderer>(); // Fallback simple material reset
            //     if (rend != null && paintedPlaneOriginalMaterials.TryGetValue(plane.GetComponent<ARPlane>(), out Material originalMat))
            //     {
            //         rend.material = originalMat;
            //     }
            // }
            return true; // RESTORED THIS LINE
        }
        return false;
    }

    private bool IsARFoundationPlane(GameObject planeGo)
    {
        if (planeGo == null) return false;
        // Check if it's a direct ARPlane component
        if (planeGo.GetComponent<ARPlane>() != null)
        {
            // Check if its parent is the XROrigin's TrackablesParent or the stored trackables parent ID
            if (planeGo.transform.parent != null && this.xrOrigin != null && this.xrOrigin.TrackablesParent != null &&
                planeGo.transform.parent == this.xrOrigin.TrackablesParent) return true;
            if (planeGo.transform.parent != null && trackablesParentInstanceID_FromStart != 0 &&
                planeGo.transform.parent.GetInstanceID() == trackablesParentInstanceID_FromStart) return true;
        }
        return false;
    }

    public float GetUnvisitedPlaneRemovalDelay() => 5.0f; // Increased delay before removing planes

    private Material GetMaterialForPlane(Vector3 planeNormal)
    {
        // Simplified: Determine if vertical or horizontal based on normal
        bool isVertical = IsSurfaceVertical(planeNormal);
        return isVertical ? verticalPlaneMaterial : horizontalPlaneMaterial;
    }
    private Material GetMaterialForPlane(Vector3 planeNormal, PlaneAlignment alignment)
    {
        switch (alignment)
        {
            case PlaneAlignment.Vertical:
                return verticalPlaneMaterial;
            case PlaneAlignment.HorizontalUp:
            case PlaneAlignment.HorizontalDown:
                return horizontalPlaneMaterial;
            default: // NonAligned or other cases
                return verticalPlaneMaterial; // Default or handle as needed
        }
    }


    private void HandlePlaneSelectionByTap()
    {
        bool tapped = false;
        Vector2 tapPosition = Vector2.zero;

        if (Touchscreen.current != null && Touchscreen.current.primaryTouch.isInProgress)
        {
            if (Touchscreen.current.primaryTouch.phase.ReadValue() == UnityEngine.InputSystem.TouchPhase.Began)
            {
                tapped = true;
                tapPosition = Touchscreen.current.primaryTouch.position.ReadValue();
            }
        }
        else if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame) // Also allow mouse input for editor testing
        {
            tapped = true;
            tapPosition = Mouse.current.position.ReadValue();
        }

        if (tapped)
        {
            HandleTap(tapPosition);
        }
    }

    private void HandleTap(Vector2 touchPosition)
    {
        if (wallPaintMaterial == null || colorManager == null)
        {
            LogWarning("WallPaintMaterial or ARWallPaintColorManager not set. Cannot paint.", ARManagerDebugFlags.System);
            return;
        }

        Log($"HandleTap: Processing tap at screen position {touchPosition}", ARManagerDebugFlags.Raycasting);
        List<ARRaycastHit> arHits = new List<ARRaycastHit>();
        ARRaycastHit arHit = default;
        bool didArHit = false;

        if (arRaycastManager != null && useDetectedPlanesForTap)
        {
            if (arRaycastManager.Raycast(touchPosition, arHits, trackableTypesToHit))
            {
                arHit = arHits[0]; // Use the closest AR hit
                didArHit = true;
                Log($"AR Raycast hit trackable {arHit.trackableId} of type {arHit.hitType} at distance {arHit.distance:F2}", ARManagerDebugFlags.Raycasting);
            }
        }

        RaycastHit physicsHit = default;
        bool didPhysicsHit = false;
        Camera cam = (this.xrOrigin != null && this.xrOrigin.Camera != null) ? this.xrOrigin.Camera : Camera.main;
        if (cam == null)
        {
            LogError("No camera found for raycasting in HandleTap.");
            return;
        }
        Ray ray = cam.ScreenPointToRay(touchPosition);

        if (usePhysicsRaycastForTap)
        {
            // We want to hit our custom planes (on planeLayerName) AND the simulated environment
            LayerMask tapMask = hitLayerMask; // Start with the general mask (which includes simulated env)
            int customPlaneLayerVal = LayerMask.NameToLayer(planeLayerName);
            if (customPlaneLayerVal != -1) tapMask |= (1 << customPlaneLayerVal); // Add custom plane layer to tap mask

            Log($"Tap Physics Raycast LayerMask: {LayerMaskToString(tapMask)} (Raw: {tapMask.value})", ARManagerDebugFlags.Raycasting | ARManagerDebugFlags.DetailedRaycastLogging);


            if (Physics.Raycast(ray, out physicsHit, maxRayDistance, tapMask))
            {
                if (IsIgnoredObject(physicsHit.collider.gameObject))
                {
                    Log($"Tap Physics Raycast hit IGNORED object: {physicsHit.collider.name}", ARManagerDebugFlags.Raycasting);
                }
                else
                {
                    didPhysicsHit = true;
                    Log($"Physics Raycast hit {physicsHit.collider.name} on layer {LayerMask.LayerToName(physicsHit.collider.gameObject.layer)} at distance {physicsHit.distance:F2}", ARManagerDebugFlags.Raycasting);
                }
            }
        }

        if ((this.debugFlags & ARManagerDebugFlags.Raycasting) != 0 && drawDebugRays)
        {
            Color rayColor = (didPhysicsHit || didArHit) ? Color.magenta : Color.yellow;
            DrawDebugRay(ray, rayColor, 3.0f);
        }


        // Priority:
        // 1. Physics hit on one of our custom ARPaintPlane objects.
        // 2. ARFoundation plane hit (if useDetectedPlanesForTap is true).
        // 3. Physics hit on simulated environment.

        GameObject planeToPaintGO = null;

        if (didPhysicsHit)
        {
            // Check if the physics hit is one of our generated ARPaintPlanes
            ARPlaneConfigurator configurator = physicsHit.collider.GetComponentInParent<ARPlaneConfigurator>();
            if (configurator != null && generatedPlanes.Contains(configurator.gameObject))
            {
                planeToPaintGO = configurator.gameObject;
                Log($"Selected ARPaintPlane '{planeToPaintGO.name}' via Physics Raycast for painting.", ARManagerDebugFlags.System);
            }
            else if (LayerMask.LayerToName(physicsHit.collider.gameObject.layer) == basicSceneGeometryLayerName)
            {
                // If we hit the simulated environment, AND no AR plane was closer or prioritized
                if (!didArHit || (didArHit && physicsHit.distance < arHit.distance))
                {
                    Log($"Physics raycast hit simulated environment '{physicsHit.collider.name}'. This could be a paint target if no AR plane is better.", ARManagerDebugFlags.System);
                    // Here, you might want to create a temporary ARPaintPlane on the simulated surface or directly apply a decal.
                    // For now, let's assume we prioritize actual AR planes or our custom planes.
                    // If you want to paint on simulated env, this is where you'd handle it.
                    // planeToPaintGO = physicsHit.collider.gameObject; // Or a temporary plane
                }
            }
        }

        if (planeToPaintGO == null && didArHit) // If no custom plane hit via physics, check AR hit
        {
            ARPlane arPlane = planeManager?.GetPlane(arHit.trackableId);
            if (arPlane != null)
            {
                // Check if this ARPlane already has a corresponding ARPaintPlane managed by us
                GameObject correspondingPaintPlane = FindClosestExistingPlane(arPlane.transform.position, arPlane.trackableId.ToString());
                if (correspondingPaintPlane != null && generatedPlanes.Contains(correspondingPaintPlane) &&
                    (correspondingPaintPlane.transform.position - arPlane.transform.position).sqrMagnitude < 0.1f) // Ensure it's very close
                {
                    planeToPaintGO = correspondingPaintPlane;
                    Log($"Selected existing ARPaintPlane '{planeToPaintGO.name}' corresponding to ARFoundation plane '{arPlane.trackableId}' for painting.", ARManagerDebugFlags.System);
                }
                else
                {
                    // If we want to paint directly on ARFoundation planes or create a custom one for it:
                    // planeToPaintGO = arPlane.gameObject; // This would select the ARFoundation plane itself
                    Log($"ARFoundation plane '{arPlane.trackableId}' hit. Consider creating/finding a custom paint plane for it if not painting directly.", ARManagerDebugFlags.System);
                }
            }
        }


        if (planeToPaintGO != null)
        {
            Renderer rend = planeToPaintGO.GetComponent<Renderer>();
            if (rend != null)
            {
                Material[] materials = rend.materials; // Get a copy
                if (materials.Length > 0)
                {
                    // Assuming the first material is the one to paint
                    Material originalMaterial = materials[0]; // Store original if not already

                    // Create an instance of the wallPaintMaterial to avoid modifying the asset
                    Material paintInstance = new Material(wallPaintMaterial);
                    paintInstance.color = colorManager.GetCurrentColor(); // Set the paint color
                    if (currentSegmentationMask != null)
                    {
                        paintInstance.SetTexture("_SegmentationMask", currentSegmentationMask);
                        // Potentially set UVs or other mask parameters if needed by shader
                    }
                    materials[0] = paintInstance;
                    rend.materials = materials; // Assign the new array with the instanced material

                    Log($"Applied paint material (color: {colorManager.GetCurrentColor()}) to {planeToPaintGO.name}", ARManagerDebugFlags.System);

                    // Store original material if we want to revert later (more complex for multiple paints)
                    // if (!paintedPlaneOriginalMaterials.ContainsKey(planeToPaintGO.GetComponent<ARPlane>())) // Needs ARPlane component if keying by it
                    // {
                    //    paintedPlaneOriginalMaterials[planeToPaintGO.GetComponent<ARPlane>()] = originalMaterial;
                    // }
                }
            }
        }
        else
        {
            Log("No suitable plane selected by tap for painting.", ARManagerDebugFlags.System);
        }
    }


    private void DrawDebugRay(Ray ray, Color color, float duration)
    {
        if ((this.debugFlags & ARManagerDebugFlags.Raycasting) != 0 && enableDetailedRaycastLogging) // ensure detailed logging is on for rays
        {
            Debug.DrawRay(ray.origin, ray.direction * maxRayDistance, color, duration);
        }
    }

    private void CreateBasicSceneGeometry()
    {
        if (GameObject.Find("SimulatedRoom_Floor") != null)
        {
            Log("Basic scene geometry already exists. Skipping creation.", ARManagerDebugFlags.Initialization);
            return;
        }
        Log("Creating basic scene geometry for simulation.", ARManagerDebugFlags.Initialization);

        int simulatedLayer = LayerMask.NameToLayer(basicSceneGeometryLayerName);
        if (simulatedLayer == -1)
        {
            LogError($"Layer '{basicSceneGeometryLayerName}' not found. Cannot create basic scene geometry correctly. Please add the layer.", ARManagerDebugFlags.Initialization);
            return;
        }

        // Floor
        GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "SimulatedRoom_Floor";
        floor.transform.localScale = new Vector3(basicSceneRoomSize.x / 10f, 1, basicSceneRoomSize.z / 10f); // Plane primitive is 10x10 units
        floor.transform.position = new Vector3(0, -basicSceneRoomSize.y / 2f, 0); // Centered at origin, y is bottom
        floor.layer = simulatedLayer;
        Renderer floorRend = floor.GetComponent<Renderer>();
        if (floorRend) floorRend.material.color = new Color(0.8f, 0.8f, 0.8f);


        // Walls (simple cubes)
        Vector3 wallSizeX = new Vector3(basicSceneRoomSize.x, basicSceneRoomSize.y, basicSceneWallThickness);
        Vector3 wallSizeZ = new Vector3(basicSceneWallThickness, basicSceneRoomSize.y, basicSceneRoomSize.z + (2 * basicSceneWallThickness)); // Extend Z walls to cover corners

        // Wall -Z
        GameObject wall_NZ = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall_NZ.name = "SimulatedRoom_Wall_NZ";
        wall_NZ.transform.localScale = wallSizeX;
        wall_NZ.transform.position = new Vector3(0, 0, -basicSceneRoomSize.z / 2f - basicSceneWallThickness / 2f);
        wall_NZ.layer = simulatedLayer;
        wall_NZ.GetComponent<Renderer>().material.color = new Color(0.7f, 0.7f, 0.7f);


        // Wall +Z
        GameObject wall_PZ = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall_PZ.name = "SimulatedRoom_Wall_PZ";
        wall_PZ.transform.localScale = wallSizeX;
        wall_PZ.transform.position = new Vector3(0, 0, basicSceneRoomSize.z / 2f + basicSceneWallThickness / 2f);
        wall_PZ.layer = simulatedLayer;
        wall_PZ.GetComponent<Renderer>().material.color = new Color(0.7f, 0.7f, 0.7f);

        // Wall -X
        GameObject wall_NX = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall_NX.name = "SimulatedRoom_Wall_NX";
        wall_NX.transform.localScale = wallSizeZ;
        wall_NX.transform.position = new Vector3(-basicSceneRoomSize.x / 2f - basicSceneWallThickness / 2f, 0, 0);
        wall_NX.layer = simulatedLayer;
        wall_NX.GetComponent<Renderer>().material.color = new Color(0.75f, 0.75f, 0.75f);

        // Wall +X
        GameObject wall_PX = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall_PX.name = "SimulatedRoom_Wall_PX";
        wall_PX.transform.localScale = wallSizeZ;
        wall_PX.transform.position = new Vector3(basicSceneRoomSize.x / 2f + basicSceneWallThickness / 2f, 0, 0);
        wall_PX.layer = simulatedLayer;
        wall_PX.GetComponent<Renderer>().material.color = new Color(0.75f, 0.75f, 0.75f);

        // Parent them for organization if desired
        GameObject roomParent = new GameObject("SimulatedRoom");
        floor.transform.SetParent(roomParent.transform);
        wall_NZ.transform.SetParent(roomParent.transform);
        wall_PZ.transform.SetParent(roomParent.transform);
        wall_NX.transform.SetParent(roomParent.transform);
        wall_PX.transform.SetParent(roomParent.transform);

        // Optional: Add a ceiling
        GameObject ceiling = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ceiling.name = "SimulatedRoom_Ceiling";
        ceiling.transform.localScale = new Vector3(basicSceneRoomSize.x / 10f, 1, basicSceneRoomSize.z / 10f);
        ceiling.transform.position = new Vector3(0, basicSceneRoomSize.y / 2f, 0);
        ceiling.transform.rotation = Quaternion.Euler(180, 0, 0); // Flip to face down
        ceiling.layer = simulatedLayer;
        Renderer ceilRend = ceiling.GetComponent<Renderer>();
        if (ceilRend) ceilRend.material.color = new Color(0.8f, 0.8f, 0.8f);
        ceiling.transform.SetParent(roomParent.transform);


        Log("Basic scene geometry created.", ARManagerDebugFlags.Initialization);
    }

    private bool IsIgnoredObject(GameObject obj)
    {
        if (obj == null) return false;

        // Rule 1: Check against the string array of names
        if (ignoredObjectNamesArray.Length > 0)
        {
            if (ignoredObjectNamesArray.Contains(obj.name))
            {
                // Log($"IsIgnoredObject: '{obj.name}' IGNORED: exact name match.", ARManagerDebugFlags.Raycasting);
                return true;
            }
            // Check if obj.name starts with any of the ignored names (e.g., "ARPaintPlane_")
            foreach (string ignoredPrefix in ignoredObjectNamesArray)
            {
                if (!string.IsNullOrEmpty(ignoredPrefix) && obj.name.StartsWith(ignoredPrefix))
                {
                    // Log($"IsIgnoredObject: '{obj.name}' IGNORED: prefix match with '{ignoredPrefix}'.", ARManagerDebugFlags.Raycasting);
                    return true;
                }
            }
        }

        // Rule 2: This ARManagerInitializer2 GameObject itself
        if (obj == this.gameObject)
        {
            // Log($"IsIgnoredObject: '{obj.name}' IGNORED: self.", ARManagerDebugFlags.Raycasting);
            return true;
        }

        // Rule 3: The Main Camera if it's the AR Camera
        if (arCameraManager != null && obj == arCameraManager.gameObject)
        {
            // Log($"IsIgnoredObject: '{obj.name}' IGNORED: AR Camera object.", ARManagerDebugFlags.Raycasting);
            return true;
        }
        Camera mainCam = Camera.main;
        if (mainCam != null && obj == mainCam.gameObject)
        {
            // Log($"IsIgnoredObject: '{obj.name}' IGNORED: Main Camera object.", ARManagerDebugFlags.Raycasting);
            return true;
        }


        // Rule 4: Objects on "UI" layer
        if (obj.layer == LayerMask.NameToLayer("UI"))
        {
            // Log($"IsIgnoredObject: '{obj.name}' IGNORED: UI layer.", ARManagerDebugFlags.Raycasting);
            return true;
        }

        // Rule 5: The XROrigin itself (but not its children like TrackablesParent necessarily, unless specified in ignoredObjectNames)
        if (this.xrOrigin != null && obj == this.xrOrigin.gameObject)
        {
            // Log($"IsIgnoredObject: '{obj.name}' IGNORED: XROrigin root object.", ARManagerDebugFlags.Raycasting);
            return true;
        }

        // Rule 6: Children of XROrigin (that are not the camera itself and not part of the trackables if we want to hit those)
        // This rule might be too broad or too specific depending on the XROrigin setup.
        // For instance, if TrackablesParent is a child of XROrigin, we might NOT want to ignore it if it contains ARPlanes we want to hit.
        // This is why explicit layer checks or specific name checks are often better.
        // Let's refine this: ignore direct children of XROrigin if they are not the camera and not the trackables parent.
        if (this.xrOrigin != null && obj.transform.IsChildOf(this.xrOrigin.transform) &&
            obj != this.xrOrigin.gameObject &&
            (arCameraManager == null || obj != arCameraManager.gameObject) &&
            (this.xrOrigin.TrackablesParent == null || obj != this.xrOrigin.TrackablesParent.gameObject))
        {
            Log($"IsIgnoredObject: '{obj.name}' IGNORED: direct child of XROrigin (not camera/trackables parent).", ARManagerDebugFlags.Raycasting);
            return true;
        }


        return false;
    }


    private void OnPlanesChanged(ARPlanesChangedEventArgs args)
    {
        if ((this.debugFlags & ARManagerDebugFlags.ARSystem) != 0)
        {
            if (args.added.Count > 0) Log($"ARPlaneManager.PlanesChanged: {args.added.Count} plane(s) added.", ARManagerDebugFlags.ARSystem);
            if (args.updated.Count > 0) Log($"ARPlaneManager.PlanesChanged: {args.updated.Count} plane(s) updated.", ARManagerDebugFlags.ARSystem);
            if (args.removed.Count > 0) Log($"ARPlaneManager.PlanesChanged: {args.removed.Count} plane(s) removed.", ARManagerDebugFlags.ARSystem);
        }

        foreach (ARPlane plane in args.added) OnPlaneAdded(plane);
        foreach (ARPlane plane in args.updated) OnPlaneUpdated(plane);
        foreach (ARPlane plane in args.removed) OnPlaneRemoved(plane);
    }

    private void OnPlaneAdded(ARPlane plane)
    {
        Log($"ARPlane Added: {plane.trackableId}, Alignment: {plane.alignment}, Center: {plane.center.ToString("F3")}", ARManagerDebugFlags.ARSystem);
        if (!allGeneratedPlanes.Contains(plane.gameObject)) allGeneratedPlanes.Add(plane.gameObject);
        // Optionally configure ARFoundation planes if not creating custom ones
        // Example: plane.GetComponent<Renderer>().material = GetMaterialForPlane(plane.normal, plane.alignment);
    }

    private void OnPlaneUpdated(ARPlane plane)
    {
        // Log($"ARPlane Updated: {plane.trackableId}, Alignment: {plane.alignment}, Center: {plane.center.ToString("F3")}", ARManagerDebugFlags.ARSystem);
        // If managing materials on ARFoundation planes directly, update here
    }

    private void OnPlaneRemoved(ARPlane plane)
    {
        Log($"ARPlane Removed: {plane.trackableId}", ARManagerDebugFlags.ARSystem);
        allGeneratedPlanes.Remove(plane.gameObject);
        // If this ARPlane had a corresponding custom plane, it might also need cleanup if not handled by mask logic
    }


    private bool IsSurfaceVertical(Vector3 normal)
    {
        float angleWithUp = Vector3.Angle(normal, Vector3.up);
        // A surface is vertical if its normal is (close to) perpendicular to the world up vector.
        // So, the angle between the normal and world up should be close to 90 degrees.
        // maxWallNormalAngleDeviation is how much it can deviate from being perfectly 90 degrees to world up.
        bool isVertical = Mathf.Abs(angleWithUp - 90f) <= maxWallNormalAngleDeviation;
        if ((this.debugFlags & ARManagerDebugFlags.Raycasting) != 0) // Conditional log
        {
            Log($"[IsSurfaceVertical] Normal: {normal.ToString("F3")}, AngleWithUp: {angleWithUp:F1}°, IsVertical: {isVertical} (maxWallNormalAngleDeviation: {maxWallNormalAngleDeviation}°)", ARManagerDebugFlags.Raycasting);
        }
        return isVertical;
    }


    private Mesh CreatePlaneMesh(float width, float height)
    {
        Mesh mesh = new Mesh();
        mesh.name = "CustomARPlaneMesh";

        Vector3[] vertices = new Vector3[4]
        {
            new Vector3(-width / 2f, -height / 2f, 0),
            new Vector3(width / 2f, -height / 2f, 0),
            new Vector3(-width / 2f, height / 2f, 0),
            new Vector3(width / 2f, height / 2f, 0)
        };
        mesh.vertices = vertices;

        int[] tris = new int[6] { 0, 2, 1, 2, 3, 1 }; // Standard quad triangulation
        mesh.triangles = tris;

        Vector3[] normals = new Vector3[4]
        {
            -Vector3.forward, -Vector3.forward, -Vector3.forward, -Vector3.forward
        };
        mesh.normals = normals; // Assuming plane faces -Z locally

        Vector2[] uv = new Vector2[4]
        {
            new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1)
        };
        mesh.uv = uv;

        mesh.RecalculateBounds();
        return mesh;
    }
}