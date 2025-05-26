using UnityEngine;
using UnityEngine.XR.ARFoundation;
using Unity.XR.CoreUtils;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.XR.ARSubsystems;
using System;
using UnityEngine.Rendering;

// Basic DebugFlags and LogLevel for ARManagerInitializer2 to avoid conflicts/missing references
public enum ARManagerDebugFlags
{
    None = 0,
    Initialization = 1 << 0,
    PlaneGeneration = 1 << 1,
    Raycasting = 1 << 2,
    ARSystem = 1 << 3, // For AR specific logs like light estimation
    System = 1 << 4, // For general system events like subscribe/unsubscribe
    Performance = 1 << 5,
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

    // Temporarily commented out due to version compatibility issues
    // public ARLightEstimationManager arLightManager;
    // public AREnvironmentProbeManager arEnvironmentProbeManager;

    [Tooltip("Directional Light, который будет обновляться AR Light Estimation")]
    [SerializeField] private Light arDirectionalLight;

    [Header("Настройки сегментации")]
    public bool useDetectedPlanes = false;
    [SerializeField] private float minPlaneSizeInMeters = 0.1f;
    [SerializeField] private int minPixelsDimensionForArea = 2;
    [SerializeField] private int minAreaSizeInPixels = 10;
    [Range(0, 255)] public byte wallAreaRedChannelThreshold = 30;

    [Header("Настройки Рейкастинга для Плоскостей")]
    public bool enableDetailedRaycastLogging = true;
    public float maxRayDistance = 15.0f;
    public LayerMask hitLayerMask = -1; // Will be initialized in Awake()
    public float minHitDistanceThreshold = 0.1f;
    public float maxWallNormalAngleDeviation = 30f;
    public float maxFloorCeilingAngleDeviation = 15f;
    [Tooltip("Имя слоя для создаваемых плоскостей (должен существовать в Tags and Layers)")]
    [SerializeField] private string planeLayerName = "ARPlanes";
    [Tooltip("Имена объектов, которые должны игнорироваться при рейкастинге (разделены запятыми)")]
    [SerializeField] private string ignoreObjectNames = "Player,UI,Hand"; // Added field

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
    private bool maskUpdated = false;
    private List<GameObject> generatedPlanes = new List<GameObject>();
    private int frameCounter = 0;
    private float lastSuccessfulSegmentationTime = 0f;
    private int trackablesParentInstanceID_FromStart = 0;

    [Header("Настройки Кластеризации Рейкастов")]
    public bool enableRaycastClustering = true;
    public int clusteringMinHitsThreshold = 1;

    [SerializeField] private ARPlaneConfigurator planeConfigurator;
    [SerializeField] private WallSegmentation wallSegmentation;

    [Header("Отладка ARManagerInitializer2")]
    public ARManagerDebugFlags debugFlags = ARManagerDebugFlags.None;

    [Header("Настройки Выделения Плоскостей")]
    [Tooltip("Материал для выделенной AR плоскости. Если не задан, будет изменен цвет текущего материала.")]
    public Material selectedPlaneMaterial; // Этот материал будет использоваться для ПОДСВЕТКИ выделенной плоскости
    [Tooltip("Материал, используемый для покраски стен. Должен иметь свойство _PaintColor и _SegmentationMask.")]
    public Material paintMaterial; // Новый материал для ПОКРАСКИ
    private ARPlane currentlySelectedPlane;
    private Material originalSelectedPlaneMaterial;
    private Dictionary<ARPlane, Material> paintedPlaneOriginalMaterials = new Dictionary<ARPlane, Material>(); // Для хранения оригинальных материалов покрашенных плоскостей

    private ARWallPaintColorManager colorManager; // Ссылка на менеджер цветов

    private void Log(string message, ARManagerDebugFlags flag = ARManagerDebugFlags.None, ARManagerLogLevel level = ARManagerLogLevel.Info)
    {
        if ((debugFlags & flag) == flag || flag == ARManagerDebugFlags.All || flag == ARManagerDebugFlags.None)
        {
            if (level == ARManagerLogLevel.Error) Debug.LogError("[ARManagerInitializer2] " + message);
            else if (level == ARManagerLogLevel.Warning) Debug.LogWarning("[ARManagerInitializer2] " + message);
            else Debug.Log("[ARManagerInitializer2] " + message);
        }
    }
    private void LogError(string message, ARManagerDebugFlags flag = ARManagerDebugFlags.None) => Log(message, flag, ARManagerLogLevel.Error);
    private void LogWarning(string message, ARManagerDebugFlags flag = ARManagerDebugFlags.None) => Log(message, flag, ARManagerLogLevel.Warning);

    private void Awake()
    {
        Debug.Log("[ARManagerInitializer2] AWAKE_METHOD_ENTERED_TOP"); // NEW VERY FIRST LOG
        if (Instance != null && Instance != this)
        {
            LogWarning("Duplicate instance detected. Destroying self.", ARManagerDebugFlags.Initialization);
            Destroy(gameObject);
            return;
        }
        Instance = this;
        planeInstanceCounter = 0;

        // Initialize LayerMask - must be done in Awake() not field initializer
        if (hitLayerMask.value == -1) // Only initialize if not already set in Inspector
        {
            hitLayerMask = LayerMask.GetMask("Default", "Wall", "SimulatedEnvironment");
        }

        Log("Awake() called. Instance set.", ARManagerDebugFlags.Initialization);
        if (transform.parent == null)
        {
            Log("Making it DontDestroyOnLoad as it's a root object.", ARManagerDebugFlags.Initialization);
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            LogWarning("Instance is not a root object, not setting DontDestroyOnLoad.", ARManagerDebugFlags.Initialization);
        }
        // enableDetailedRaycastLogging is public and set in Inspector or default true

        if (debugRayMaterial != null)
        {
            debugRayMaterialPropertyBlock = new MaterialPropertyBlock();
        }
        else
        {
            LogWarning("debugRayMaterial is not assigned. Debug ray visualization will be disabled.", ARManagerDebugFlags.Initialization);
        }
        FindARComponents();
    }

    private void OnEnable()
    {
        // Debug.Log("[ARManagerInitializer2] ON_ENABLE_CALLED");
        SubscribeToWallSegmentation();
        InitializeLightEstimation();
        InitializeEnvironmentProbes();
        InitializeSceneReconstruction();
        if (planeManager != null) planeManager.planesChanged += OnPlanesChanged;
        if (arCameraManager != null) arCameraManager.frameReceived += OnARFrameReceived;
        if (arMeshManager != null) arMeshManager.meshesChanged += OnMeshesChanged;
        Log("ARManagerInitializer2 OnEnable: Subscriptions attempted.", ARManagerDebugFlags.System); // Original Log
    }

    private void Start()
    {
        Debug.Log("[ARManagerInitializer2] START_METHOD_ENTERED_TOP"); // NEW VERY FIRST LOG
        Log("ARManagerInitializer2 Start(). Поиск компонентов и инициализация.", ARManagerDebugFlags.Initialization);
        FindARComponents();
        InitializeMaterials();
        SubscribeToWallSegmentation();
        InitializeLightEstimation();
        InitializeEnvironmentProbes();
        InitializeSceneReconstruction();
        // hitLayerMask = LayerMask.GetMask(planeLayerName, "Default");
        hitLayerMask = LayerMask.GetMask(planeLayerName); // Только слой плоскостей

        if (wallSegmentation == null)
        {
            LogError("WallSegmentation не назначен в инспекторе!", ARManagerDebugFlags.Initialization);
        }

        colorManager = FindObjectOfType<ARWallPaintColorManager>();
        if (colorManager == null)
        {
            LogError("ARWallPaintColorManager не найден на сцене! Создайте экземпляр.", ARManagerDebugFlags.Initialization);
            // gameObject.AddComponent<ARWallPaintColorManager>(); // Можно создать, если его нет
            // colorManager = FindObjectOfType<ARWallPaintColorManager>();
        }
        // else
        // {
        //    // Убираем подписку, так как цвет теперь фиксированный
        //    // colorManager.OnColorChanged -= HandleColorChanged; // Было +=, исправлено на -= для удаления, если ранее было добавлено
        //    // colorManager.OnColorChanged += HandleColorChanged; 
        //    // HandleColorChanged(colorManager.GetCurrentColor()); // Устанавливаем начальный цвет
        // }

        Log($"ARManagerInitializer2 Start() завершен. Raycast LayerMask: {LayerMaskToString(hitLayerMask)}", ARManagerDebugFlags.Initialization);
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
        // Temporarily disabled due to version compatibility issues
        LogWarning("Environment probe initialization disabled due to AR Foundation version compatibility.", ARManagerDebugFlags.Initialization);
    }

    private void InitializeSceneReconstruction()
    {
        if (!enableSceneReconstruction)
        {
            Log("Scene Reconstruction is administratively disabled by 'enableSceneReconstruction' flag.", ARManagerDebugFlags.Initialization);
            if (arMeshManager != null)
            {
                arMeshManager.enabled = false; // Ensure it's off if global flag is off
            }
            return;
        }

        if (arMeshManager == null)
        {
            LogWarning("ARMeshManager component is not assigned. Scene Reconstruction cannot be enabled.", ARManagerDebugFlags.Initialization);
            enableSceneReconstruction = false; // Update status to reflect that it won't work
            return;
        }

        // At this point, enableSceneReconstruction is true (or was initially) and arMeshManager is assigned.
        // Now, check the subsystem's availability.
        // A more robust check might involve arMeshManager.subsystem.subsystemDescriptor.supportsMeshClassification or similar specific features.
        if (arMeshManager.subsystem != null)
        {
            try
            {
                arMeshManager.enabled = true; // Attempt to enable the manager
                // After enabling, check if it's actually running. Behavior might vary across ARFoundation versions.
                if (arMeshManager.subsystem.running)
                {
                    Log($"✅ ARMeshManager enabled and subsystem is running. Scene Reconstruction active.", ARManagerDebugFlags.ARSystem);
                }
                else
                {
                    LogWarning($"ARMeshManager enabled, but subsystem is not running. Scene Reconstruction might not be fully active or may start shortly. Subsystem state: {arMeshManager.subsystem.running}", ARManagerDebugFlags.ARSystem);
                    // For some versions/platforms, enabling the manager is enough for the subsystem to start.
                    // If it doesn't start, further investigation might be needed (e.g., device capabilities, project settings).
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
            arMeshManager.enabled = false;       // Disable the manager component
            enableSceneReconstruction = false; // Update our flag to reflect it's not usable
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

    private void Update()
    {
        Debug.Log("[ARManagerInitializer2] UPDATE_METHOD_ENTERED_TOP"); // NEW VERY FIRST LOG
        frameCounter++;
        Log($"Update Frame: {frameCounter}, maskUpdated: {maskUpdated}, useDetectedPlanes: {useDetectedPlanes}", ARManagerDebugFlags.PlaneGeneration); // DEBUG LOG
        if (maskUpdated && !useDetectedPlanes)
        {
            Log("Update: Calling ProcessSegmentationMask()", ARManagerDebugFlags.PlaneGeneration); // DEBUG LOG
            ProcessSegmentationMask();
        }
        maskUpdated = false;

        if (frameCounter % 300 == 0) // Approx every 5 seconds if 60fps
        {
            CleanupOldPlanes(null); // Pass null to indicate it's a time-based cleanup, not mask-based
        }

        // Логика выделения плоскости по тапу
        HandlePlaneSelectionByTap();

        // <<< ДОБАВЛЕНО ДЛЯ ПРИНУДИТЕЛЬНОГО ОТКЛЮЧЕНИЯ ARPlaneManager >>>
        if (!useDetectedPlanes && planeManager != null && planeManager.enabled)
        {
            LogWarning("ARPlaneManager was found enabled in Update, despite useDetectedPlanes = false. Forcibly disabling.", ARManagerDebugFlags.Initialization);
            planeManager.enabled = false;
        }
        // <<< КОНЕЦ ПРИНУДИТЕЛЬНОГО ОТКЛЮЧЕНИЯ >>>
    }

    private void FindARComponents()
    {
        if (xrOrigin == null)
        {
            xrOrigin = FindObjectOfType<XROrigin>();
            if (xrOrigin != null)
            {
                Log("XROrigin найден в сцене.", ARManagerDebugFlags.Initialization);
            }
            else
            {
                LogError("XROrigin НЕ НАЙДЕН в сцене! Многие функции AR не будут работать.", ARManagerDebugFlags.Initialization);
            }
        }

        if (xrOrigin != null && arCameraManager == null) arCameraManager = xrOrigin.CameraFloorOffsetObject?.GetComponentInChildren<ARCameraManager>();
        if (arCameraManager == null) arCameraManager = FindObjectOfType<ARCameraManager>(); // Fallback

        if (xrOrigin != null && planeManager == null) planeManager = xrOrigin.GetComponentInChildren<ARPlaneManager>(); // ARPlaneManager часто находится на самом XROrigin или его дочерних объектах
        if (planeManager == null) planeManager = FindObjectOfType<ARPlaneManager>(); // Fallback

        if (xrOrigin != null && arOcclusionManager == null) arOcclusionManager = xrOrigin.CameraFloorOffsetObject?.GetComponentInChildren<AROcclusionManager>();
        if (arOcclusionManager == null) arOcclusionManager = FindObjectOfType<AROcclusionManager>(); // Fallback

        if (xrOrigin != null && arRaycastManager == null) arRaycastManager = xrOrigin.GetComponentInChildren<ARRaycastManager>(); // ARRaycastManager часто находится на XROrigin
        if (arRaycastManager == null) arRaycastManager = FindObjectOfType<ARRaycastManager>(); // Fallback

        if (xrOrigin != null && arMeshManager == null) arMeshManager = xrOrigin.GetComponentInChildren<ARMeshManager>();
        if (arMeshManager == null) arMeshManager = FindObjectOfType<ARMeshManager>();

        if (xrOrigin != null && arAnchorManager == null) arAnchorManager = xrOrigin.GetComponentInChildren<ARAnchorManager>();
        if (arAnchorManager == null) arAnchorManager = FindObjectOfType<ARAnchorManager>();

        // Проверка и назначение arDirectionalLight, если не установлено
        if (arDirectionalLight == null)
        {
            var lights = FindObjectsOfType<Light>();
            arDirectionalLight = lights.FirstOrDefault(l => l.type == LightType.Directional);
            if (arDirectionalLight != null)
            {
                LogWarning("AR Directional Light не был назначен и был найден автоматически. Пожалуйста, назначьте вручную для надежности.", ARManagerDebugFlags.Initialization);
            }
            else
            {
                LogWarning("AR Directional Light не был назначен и не найден в сцене. Оценка освещения может не работать.", ARManagerDebugFlags.Initialization);
            }
        }

        // WallSegmentation & ARPlaneConfigurator (usually manually assigned or found by specific logic if needed elsewhere)
        if (wallSegmentation == null) wallSegmentation = FindObjectOfType<WallSegmentation>();
        if (planeConfigurator == null) planeConfigurator = FindObjectOfType<ARPlaneConfigurator>();

        // Логирование результатов поиска, которое было ошибочно вставлено в CreatePlaneMesh
        if (xrOrigin == null) LogError("XROrigin не найден.", ARManagerDebugFlags.Initialization);
        if (arCameraManager == null) LogWarning("ARCameraManager не найден.", ARManagerDebugFlags.Initialization);
        if (planeManager == null) LogWarning("ARPlaneManager не найден.", ARManagerDebugFlags.Initialization);
        if (arOcclusionManager == null) LogWarning("AROcclusionManager не найден (может быть опциональным).", ARManagerDebugFlags.Initialization);
        if (arRaycastManager == null) LogWarning("ARRaycastManager не найден.", ARManagerDebugFlags.Initialization);
        if (arMeshManager == null && enableSceneReconstruction) LogWarning("ARMeshManager не найден, но Scene Reconstruction включена.", ARManagerDebugFlags.Initialization);
        // Log found components status (этот блок дублируется с тем, что выше, но оставлю как есть, раз он был в "ошибочной" вставке)
        Log($"XROrigin: {(xrOrigin != null ? xrOrigin.name : "NULL")}", ARManagerDebugFlags.Initialization);
        Log($"ARCameraManager: {(arCameraManager != null ? "Found" : "NULL")}", ARManagerDebugFlags.Initialization);
        Log($"ARPlaneManager: {(planeManager != null ? "Found" : "NULL")}", ARManagerDebugFlags.Initialization);
        Log($"AROcclusionManager: {(arOcclusionManager != null ? "Found" : "NULL")}", ARManagerDebugFlags.Initialization);
        Log($"ARRaycastManager: {(arRaycastManager != null ? "Found" : "NULL")}", ARManagerDebugFlags.Initialization);
        Log($"ARMeshManager: {(arMeshManager != null ? "Found" : "NULL")}", ARManagerDebugFlags.Initialization);
        Log($"ARAnchorManager: {(arAnchorManager != null ? "Found" : "NULL")}", ARManagerDebugFlags.Initialization);
        Log($"AR Directional Light: {(arDirectionalLight != null ? arDirectionalLight.name : "NULL")}", ARManagerDebugFlags.Initialization);
        Log($"WallSegmentation: {(wallSegmentation != null ? "Found" : "NULL")}", ARManagerDebugFlags.Initialization);
        Log($"ARPlaneConfigurator: {(planeConfigurator != null ? "Found" : "NULL")}", ARManagerDebugFlags.Initialization);
    }

    private void InitializeMaterials()
    {
        if (verticalPlaneMaterial == null) verticalPlaneMaterial = new Material(Shader.Find("Standard")); // Fallback shader
        if (horizontalPlaneMaterial == null) horizontalPlaneMaterial = new Material(Shader.Find("Standard")); // Fallback shader
    }

    private void SubscribeToWallSegmentation()
    {
        if (wallSegmentation == null) wallSegmentation = FindObjectOfType<WallSegmentation>(); // Try to find it again
        if (wallSegmentation != null)
        {
            wallSegmentation.OnSegmentationMaskUpdated -= OnSegmentationMaskUpdated;
            wallSegmentation.OnSegmentationMaskUpdated += OnSegmentationMaskUpdated;
            Log("Subscribed to WallSegmentation.OnSegmentationMaskUpdated.", ARManagerDebugFlags.Initialization);
        }
        else
        {
            LogError("WallSegmentation instance not found. Cannot subscribe to mask updates.", ARManagerDebugFlags.Initialization);
            StartCoroutine(RetrySubscriptionAfterDelay(1.0f));
        }
    }

    private IEnumerator RetrySubscriptionAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        Log("Retrying subscription to WallSegmentation...", ARManagerDebugFlags.Initialization);
        SubscribeToWallSegmentation();
    }

    private void OnPlanesChanged(ARPlanesChangedEventArgs args)
    {
        foreach (ARPlane plane in args.added) ConfigurePlane(plane);
        foreach (ARPlane plane in args.updated) UpdatePlane(plane);
        // Note: args.removed are typically handled by ARFoundation itself by destroying the GameObject.
        // If custom cleanup for removed planes is needed, it would go here.
    }

    public void ConfigurePlane(ARPlane plane)
    {
        if (plane == null) return;
        MeshRenderer planeRenderer = plane.GetComponent<MeshRenderer>();
        if (planeRenderer == null) planeRenderer = plane.gameObject.AddComponent<MeshRenderer>();
        var visualizer = plane.GetComponent<ARPlaneMeshVisualizer>();
        if (visualizer != null) visualizer.enabled = false;

        if (planeConfigurator != null)
        {
            planeConfigurator.ConfigurePlane(plane, wallSegmentation, this);
        }
        else
        {
            planeRenderer.material = GetMaterialForPlane(plane.normal, plane.alignment);
            planeRenderer.shadowCastingMode = ShadowCastingMode.Off;
            planeRenderer.receiveShadows = false;
            plane.gameObject.isStatic = false;
            int layerId = LayerMask.NameToLayer(planeLayerName);
            if (layerId != -1) plane.gameObject.layer = layerId;
            else LogError($"Layer '{planeLayerName}' not found. Please define it in Tags and Layers.", ARManagerDebugFlags.PlaneGeneration);
        }
        planeLastVisitedTime[plane.gameObject] = Time.time;
        if (highlightPersistentPlanes && IsPlanePersistent(plane.gameObject) && planeRenderer != null) planeRenderer.material.color = persistentPlaneColor;
    }

    public void UpdatePlane(ARPlane plane)
    {
        if (plane == null) return;
        if (planeConfigurator != null)
        {
            planeConfigurator.UpdatePlane(plane, wallSegmentation, this);
        }
        else
        {
            MeshRenderer planeRenderer = plane.GetComponent<MeshRenderer>();
            if (planeRenderer != null)
            {
                planeRenderer.material = GetMaterialForPlane(plane.normal, plane.alignment);
                planeRenderer.shadowCastingMode = ShadowCastingMode.Off;
                planeRenderer.receiveShadows = false;
            }
            plane.gameObject.isStatic = false;
            int layerId = LayerMask.NameToLayer(planeLayerName);
            if (layerId != -1) plane.gameObject.layer = layerId;
        }
        planeLastVisitedTime[plane.gameObject] = Time.time;
        if (highlightPersistentPlanes && IsPlanePersistent(plane.gameObject))
        {
            var renderer = plane.GetComponent<MeshRenderer>();
            if (renderer != null) renderer.material.color = persistentPlaneColor;
        }
    }

    private void OnDisable()
    {
        // Debug.Log("[ARManagerInitializer2] ON_DISABLE_CALLED"); // NEW VERY FIRST LOG
        if (wallSegmentation != null)
        {
            wallSegmentation.OnSegmentationMaskUpdated -= OnSegmentationMaskUpdated;
        }
        if (colorManager != null && ARWallPaintColorManager.Instance != null) // Check colorManager first
        {
            // ARWallPaintColorManager.Instance.OnColorChanged -= HandleColorChanged; // Removed this line
        }
        if (planeManager != null) planeManager.planesChanged -= OnPlanesChanged;
        if (arCameraManager != null) arCameraManager.frameReceived -= OnARFrameReceived;
        if (arMeshManager != null) arMeshManager.meshesChanged -= OnMeshesChanged;
        Log("ARManagerInitializer2 OnDisable: Unsubscribed from events.", ARManagerDebugFlags.System);
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
        // Log if spherical harmonics are available, actual application is complex
        // if (lightEstimation.ambientSphericalHarmonics.HasValue) Log("ARLight: Ambient Spherical Harmonics available.", ARManagerDebugFlags.ARSystem);
    }

    private void OnSegmentationMaskUpdated(RenderTexture mask)
    {
        if (mask == null) return;
        currentSegmentationMask = mask;
        maskUpdated = true;
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
        if (args.added.Count > 0)
        {
            Log($"OnMeshesChanged: {args.added.Count} mesh(es) added.", ARManagerDebugFlags.ARSystem);
            foreach (var meshFilter in args.added)
            {
                EnsureMeshCollider(meshFilter);
            }
        }

        if (args.updated.Count > 0)
        {
            Log($"OnMeshesChanged: {args.updated.Count} mesh(es) updated.", ARManagerDebugFlags.ARSystem);
            foreach (var meshFilter in args.updated)
            {
                EnsureMeshCollider(meshFilter);
            }
        }

        // ARMeshManager handles removal of GameObjects, so colliders are destroyed with them.
        // No specific action needed for args.removed unless custom logic is tied to it.
        if (args.removed.Count > 0)
        {
            Log($"OnMeshesChanged: {args.removed.Count} mesh(es) removed.", ARManagerDebugFlags.ARSystem);
        }
    }

    private void EnsureMeshCollider(MeshFilter meshFilter)
    {
        if (meshFilter == null || meshFilter.gameObject == null)
        {
            LogWarning("EnsureMeshCollider: MeshFilter or its GameObject is null.", ARManagerDebugFlags.ARSystem);
            return;
        }

        MeshCollider meshCollider = meshFilter.gameObject.GetComponent<MeshCollider>();
        if (meshCollider == null)
        {
            meshCollider = meshFilter.gameObject.AddComponent<MeshCollider>();
            Log($"Added MeshCollider to {meshFilter.gameObject.name}.", ARManagerDebugFlags.ARSystem);
        }

        if (meshFilter.sharedMesh != null)
        {
            meshCollider.sharedMesh = meshFilter.sharedMesh;
        }
        else
        {
            LogWarning($"EnsureMeshCollider: MeshFilter on {meshFilter.gameObject.name} has no sharedMesh. MeshCollider might not be effective.", ARManagerDebugFlags.ARSystem);
        }

        // Ensure the layer of the mesh GameObject is part of the hitLayerMask
        // This is important if meshes are generated on a different layer than the ARMeshManager itself.
        int meshLayer = meshFilter.gameObject.layer;
        if ((hitLayerMask.value & (1 << meshLayer)) == 0)
        {
            // LogWarning($"Mesh {meshFilter.gameObject.name} is on layer '{LayerMask.LayerToName(meshLayer)}' which is not in hitLayerMask ({LayerMaskToString(hitLayerMask)}). Raycasts might miss it.", ARManagerDebugFlags.ARSystem);
            // Optionally, add it:
            // hitLayerMask.value |= (1 << meshLayer);
            // Log($"Added layer '{LayerMask.LayerToName(meshLayer)}' from mesh {meshFilter.gameObject.name} to hitLayerMask.", ARManagerDebugFlags.ARSystem);
        }
    }

    private void ProcessSegmentationMask()
    {
        Log("ProcessSegmentationMask START. Mask updated: " + maskUpdated + ", currentSegmentationMask is null: " + (currentSegmentationMask == null), ARManagerDebugFlags.PlaneGeneration); // DEBUG LOG
        if (!maskUpdated || currentSegmentationMask == null)
        {
            Log("ProcessSegmentationMask: Aborting, mask not updated or null.", ARManagerDebugFlags.PlaneGeneration); // DEBUG LOG
            return;
        }

        Texture2D maskTexture = RenderTextureToTexture2D(currentSegmentationMask);
        if (maskTexture == null)
        {
            LogError("ProcessSegmentationMask: RenderTextureToTexture2D returned null. Aborting.", ARManagerDebugFlags.PlaneGeneration); // DEBUG LOG
            return;
        }
        Log($"ProcessSegmentationMask: maskTexture created: {maskTexture.width}x{maskTexture.height}", ARManagerDebugFlags.PlaneGeneration); // DEBUG LOG

        Color32[] pixels = maskTexture.GetPixels32();
        int width = maskTexture.width;
        int height = maskTexture.height;
        Destroy(maskTexture); // Avoid memory leak

        List<Rect> wallAreas = FindWallAreas(pixels, width, height, wallAreaRedChannelThreshold);
        Log($"ProcessSegmentationMask: FindWallAreas found {wallAreas.Count} areas. wallAreaRedChannelThreshold: {wallAreaRedChannelThreshold}", ARManagerDebugFlags.PlaneGeneration); // DEBUG LOG

        Dictionary<GameObject, bool> visitedPlanesInCurrentMask = new Dictionary<GameObject, bool>();
        int processedAreas = 0; // DEBUG Counter
        foreach (Rect area in wallAreas)
        {
            Log($"ProcessSegmentationMask: Processing area {processedAreas + 1}/{wallAreas.Count}: {areaToString(area)}", ARManagerDebugFlags.PlaneGeneration); // DEBUG LOG
            if (area.width < minPixelsDimensionForArea || area.height < minPixelsDimensionForArea || (area.width * area.height) < minAreaSizeInPixels)
            {
                Log($"Skipping small area: {areaToString(area)}. MinDims: {minPixelsDimensionForArea}, MinAreaPx: {minAreaSizeInPixels}", ARManagerDebugFlags.PlaneGeneration); // DEBUG LOG
                processedAreas++;
                continue;
            }
            bool planeProcessed = UpdateOrCreatePlaneForWallArea(area, width, height, visitedPlanesInCurrentMask);
            Log($"ProcessSegmentationMask: UpdateOrCreatePlaneForWallArea returned {planeProcessed} for area {areaToString(area)}", ARManagerDebugFlags.PlaneGeneration); // DEBUG LOG
            processedAreas++;
        }
        CleanupOldPlanes(visitedPlanesInCurrentMask);
    }

    private Texture2D RenderTextureToTexture2D(RenderTexture rTex)
    {
        if (rTex == null) return null;
        Texture2D tex = new Texture2D(rTex.width, rTex.height, TextureFormat.RGB24, false);
        RenderTexture.active = rTex;
        tex.ReadPixels(new Rect(0, 0, rTex.width, rTex.height), 0, 0);
        tex.Apply();
        RenderTexture.active = null;
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
                if (!visited[x, y] && pixels[y * width + x].r > threshold) // Assuming wall mask is in red channel
                {
                    Rect area = FindConnectedArea(pixels, width, height, x, y, visited, threshold);
                    areas.Add(area);
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
        int minX = startX, maxX = startX, minY = startY, maxY = startY;

        while (queue.Count > 0)
        {
            Vector2Int p = queue.Dequeue();
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
                    if (nx >= 0 && nx < width && ny >= 0 && ny < height &&
                        !visited[nx, ny] && pixels[ny * width + nx].r > threshold)
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
        Vector2 centerUV = new Vector2((area.x + area.width / 2f) / textureWidth, (area.y + area.height / 2f) / textureHeight);
        Log($"UpdateOrCreatePlaneForWallArea: Area: {areaToString(area)}, CenterUV: ({centerUV.x:F2}, {centerUV.y:F2})", ARManagerDebugFlags.PlaneGeneration);

        if (arCameraManager == null || arCameraManager.GetComponent<Camera>() == null || arRaycastManager == null)
        {
            LogError("ARCameraManager, его Camera или ARRaycastManager не найдены.", ARManagerDebugFlags.Raycasting);
            return false;
        }
        Camera currentARCamera = arCameraManager.GetComponent<Camera>();

        Ray ray = currentARCamera.ScreenPointToRay(new Vector2(centerUV.x * Screen.width, centerUV.y * Screen.height));
        Log($"UpdateOrCreatePlaneForWallArea: Ray origin: ({ray.origin.x:F2}, {ray.origin.y:F2}, {ray.origin.z:F2}), direction: ({ray.direction.x:F2}, {ray.direction.y:F2}, {ray.direction.z:F2})", ARManagerDebugFlags.Raycasting);

        List<ARRaycastHit> hits = new List<ARRaycastHit>();
        bool didHitPrimary = false; // Для основного типа рэйкаста
        bool didHitFallback = false; // Для TrackableType.AllTypes

        TrackableType primaryTrackableTypes = TrackableType.PlaneWithinPolygon | TrackableType.FeaturePoint;

        // --- Enhanced Pre-Raycast Logging ---
        Log($"Pre-Raycast: Current hitLayerMask: {LayerMaskToString(hitLayerMask)} (Value: {hitLayerMask.value})", ARManagerDebugFlags.Raycasting);
        if (arMeshManager != null && arMeshManager.gameObject != null)
        {
            Log($"Pre-Raycast: ARMeshManager GameObject Layer: {LayerMask.LayerToName(arMeshManager.gameObject.layer)} (Value: {1 << arMeshManager.gameObject.layer})", ARManagerDebugFlags.Raycasting);
            MeshCollider meshCollider = arMeshManager.gameObject.GetComponent<MeshCollider>();
            if (meshCollider != null)
            {
                Log($"Pre-Raycast: ARMeshManager MeshCollider Found. Enabled: {meshCollider.enabled}, SharedMesh IsNull: {meshCollider.sharedMesh == null}", ARManagerDebugFlags.Raycasting);
            }
            else
            {
                LogWarning("Pre-Raycast: ARMeshManager MeshCollider NOT FOUND.", ARManagerDebugFlags.Raycasting);
            }
        }
        else
        {
            LogWarning("Pre-Raycast: ARMeshManager is null or its GameObject is null.", ARManagerDebugFlags.Raycasting);
        }
        Log($"Pre-Raycast: Primary TrackableTypes for Raycast: {primaryTrackableTypes}", ARManagerDebugFlags.Raycasting);
        // --- End of Enhanced Pre-Raycast Logging ---

        if (arMeshManager != null && arMeshManager.enabled)
        {
            Log($"ARMeshManager is ENABLED. Checking for MeshCollider.", ARManagerDebugFlags.Raycasting);
            MeshFilter meshFilter = arMeshManager.gameObject.GetComponent<MeshFilter>();
            if (meshFilter != null && meshFilter.sharedMesh != null)
            {
                Log($"ARMeshManager has a mesh with {meshFilter.sharedMesh.vertexCount} vertices.", ARManagerDebugFlags.Raycasting);
                MeshCollider meshCollider = arMeshManager.gameObject.GetComponent<MeshCollider>();
                if (meshCollider != null && meshCollider.enabled)
                {
                    Log($"ARMeshManager has an ENABLED MeshCollider. SharedMesh null? {meshCollider.sharedMesh == null}. Layer: {LayerMask.LayerToName(arMeshManager.gameObject.layer)}", ARManagerDebugFlags.Raycasting);
                    if ((hitLayerMask.value & (1 << arMeshManager.gameObject.layer)) == 0)
                    {
                        LogWarning($"Layer of ARMeshManager object ('{LayerMask.LayerToName(arMeshManager.gameObject.layer)}') is NOT INCLUDED in hitLayerMask ('{LayerMaskToString(hitLayerMask)}'). Raycast against scene mesh might fail if not hitting other trackable types on correct layers.", ARManagerDebugFlags.Raycasting);
                    }
                }
                else
                {
                    LogWarning($"ARMeshManager does NOT have an enabled MeshCollider (Collider: {meshCollider != null}, Enabled: {meshCollider?.enabled}). Raycast might fail against scene mesh.", ARManagerDebugFlags.Raycasting);
                }
            }
            else
            {
                LogWarning("ARMeshManager does not have a mesh filter or its shared mesh is null.", ARManagerDebugFlags.Raycasting);
            }
        }
        else
        {
            LogWarning($"ARMeshManager is NULL or DISABLED (Manager: {arMeshManager != null}, Enabled: {arMeshManager?.enabled}). Raycast against scene mesh might fail.", ARManagerDebugFlags.Raycasting);
        }

        Log($"Attempting Raycast with Primary TrackableTypes: {primaryTrackableTypes}", ARManagerDebugFlags.Raycasting);
        didHitPrimary = arRaycastManager.Raycast(ray, hits, primaryTrackableTypes);

        if (!didHitPrimary)
        {
            Log($"Primary raycast with {primaryTrackableTypes} found no hits. Retrying with TrackableType.AllTypes.", ARManagerDebugFlags.Raycasting);
            // Очищаем hits перед повторным вызовом, если хотим только результаты от TrackableType.All
            // Однако, если мы хотим дополнить, то не очищаем. Для текущей логики лучше очистить, чтобы не смешивать.
            hits.Clear();
            didHitFallback = arRaycastManager.Raycast(ray, hits, TrackableType.AllTypes);
            if (didHitFallback) Log($"Fallback Raycast with TrackableType.AllTypes found {hits.Count} hits.", ARManagerDebugFlags.Raycasting);
            else Log($"Fallback Raycast with TrackableType.AllTypes also found no hits.", ARManagerDebugFlags.Raycasting);
        }
        else
        {
            Log($"Primary Raycast with {primaryTrackableTypes} found {hits.Count} hits.", ARManagerDebugFlags.Raycasting);
        }

        bool finalDidHit = didHitPrimary || didHitFallback;

        if (debugRayMaterial != null)
        {
            GameObject debugLine = new GameObject("DebugRayLine_" + Time.frameCount);
            LineRenderer lr = debugLine.AddComponent<LineRenderer>();
            lr.material = debugRayMaterial;
            debugRayMaterialPropertyBlock.SetColor("_Color", finalDidHit && hits.Count > 0 ? Color.green : Color.red);
            lr.SetPropertyBlock(debugRayMaterialPropertyBlock);
            lr.startWidth = 0.01f;
            lr.endWidth = 0.01f;
            lr.SetPosition(0, ray.origin);
            lr.SetPosition(1, ray.origin + ray.direction * (finalDidHit && hits.Count > 0 ? hits[0].distance : maxRayDistance));
            Destroy(debugLine, 0.5f);
        }

        if (!finalDidHit || hits.Count == 0)
        {
            Log("No surface hit for area " + areaToString(area) + ". Hits count: " + hits.Count, ARManagerDebugFlags.Raycasting);
            return false;
        }

        List<ARRaycastHit> validHits = new List<ARRaycastHit>();
        foreach (ARRaycastHit hit in hits)
        {
            // Фильтр по дистанции
            if (hit.distance > maxRayDistance || hit.distance < minHitDistanceThreshold) continue;

            if (hit.trackable is ARPlane planeHit && planeHit.alignment == PlaneAlignment.None) continue;

            GameObject hitObject = null;
            // ARTrackable является Component, поэтому это основной способ получить GameObject
            if (hit.trackable is Component componentTrackable)
            {
                hitObject = componentTrackable.gameObject;
            }
            // hit.trackable is GameObject goTrackable - эта ветка неверна, удаляем ее.

            bool layerMatch = hitObject != null ? (hitLayerMask.value & (1 << hitObject.layer)) != 0 : false;
            // Если hitObject null (например, FeaturePoint без GameObject), считаем, что слой не применим или проходит проверку,
            // если только мы не хотим явно исключать такие попадания. Для FeaturePoint слой обычно не так важен.
            // Если это попадание в меш сцены (ARMeshManager), то hitObject будет GameObject меша.
            if (hit.hitType == TrackableType.FeaturePoint) layerMatch = true; // Feature points don't live on specific layers in the same way as GameObjects.

            bool ignoredName = hitObject != null ? IsIgnoredObject(hitObject) : false;

            Log($"  Hit Candidate: TrackableID: {hit.trackableId}, Type: {hit.hitType}, Dist: {hit.distance:F2}, Layer: {(hitObject ? LayerMask.LayerToName(hitObject.layer) : "N/A")}, LayerMatch: {layerMatch}, IgnoredName: {ignoredName}", ARManagerDebugFlags.Raycasting);

            if (layerMatch && !ignoredName) // minHitDistanceThreshold уже применен выше
            {
                validHits.Add(hit);
            }
        }

        if (validHits.Count == 0)
        {
            Log("No valid surface hits after filtering for area " + areaToString(area) + $". Original hits: {hits.Count} (DistRange: {minHitDistanceThreshold}-{maxRayDistance}, LayerMask: {LayerMaskToString(hitLayerMask)})", ARManagerDebugFlags.Raycasting);
            return false;
        }

        validHits.Sort((a, b) => a.distance.CompareTo(b.distance));
        ARRaycastHit bestHit = validHits[0];

        Log($"Best hit: TrackableID: {bestHit.trackableId}, Type: {bestHit.hitType}, Dist: {bestHit.distance:F2}, Pose: {bestHit.pose.position}, Layer: {(bestHit.trackable is Component comp ? LayerMask.LayerToName(comp.gameObject.layer) : "N/A")}", ARManagerDebugFlags.Raycasting);

        Vector3 averagePosition = bestHit.pose.position;
        Vector3 averageNormal = bestHit.pose.rotation * Vector3.forward;
        if (bestHit.trackable is ARPlane plane) averageNormal = plane.normal;

        Vector3 worldP00 = currentARCamera.ViewportToWorldPoint(new Vector3(area.xMin / textureWidth, area.yMin / textureHeight, bestHit.distance));
        Vector3 worldP10 = currentARCamera.ViewportToWorldPoint(new Vector3(area.xMax / textureWidth, area.yMin / textureHeight, bestHit.distance));
        Vector3 worldP01 = currentARCamera.ViewportToWorldPoint(new Vector3(area.xMin / textureWidth, area.yMax / textureHeight, bestHit.distance));
        float planeWorldWidth = Vector3.Distance(worldP00, worldP10);
        float planeWorldHeight = Vector3.Distance(worldP00, worldP01);
        Log($"UpdateOrCreatePlaneForWallArea: Estimated plane size: {planeWorldWidth:F2}m x {planeWorldHeight:F2}m. MinPlaneSize: {minPlaneSizeInMeters}m", ARManagerDebugFlags.PlaneGeneration);

        if (planeWorldWidth < minPlaneSizeInMeters || planeWorldHeight < minPlaneSizeInMeters)
        {
            Log($"Estimated plane size too small ({planeWorldWidth:F2}x{planeWorldHeight:F2}m) for area {areaToString(area)}", ARManagerDebugFlags.PlaneGeneration);
            return false;
        }

        var (closestPlane, dist, angle) = FindClosestExistingPlane(averagePosition, averageNormal, 0.3f, 20f);
        Log($"UpdateOrCreatePlaneForWallArea: FindClosestExistingPlane result - Closest: {(closestPlane != null ? closestPlane.name : "None")}, Dist: {dist:F2}, Angle: {angle:F2}", ARManagerDebugFlags.PlaneGeneration);
        GameObject planeToUpdate = closestPlane;

        if (planeToUpdate != null && dist < 0.3f && angle < 20f) // Thresholds for matching existing plane
        {
            Log($"Updating existing plane {planeToUpdate.name} for area {areaToString(area)} at pos {averagePosition}, normal {averageNormal}", ARManagerDebugFlags.PlaneGeneration); // DEBUG LOG
            planeToUpdate.transform.position = averagePosition;
            planeToUpdate.transform.rotation = Quaternion.LookRotation(-averageNormal, Vector3.up); // Assuming walls face camera
                                                                                                    // Update mesh if size changed significantly (more complex, skipping for now)
        }
        else
        {
            planeInstanceCounter++;
            string planeName = "MyARPlane_Generated_" + planeInstanceCounter;
            GameObject planeObject = new GameObject(planeName);
            planeObject.transform.position = averagePosition;
            planeObject.transform.rotation = Quaternion.LookRotation(-averageNormal, Vector3.up);

            Mesh planeMesh = CreatePlaneMesh(planeWorldWidth, planeWorldHeight);
            MeshFilter mf = planeObject.AddComponent<MeshFilter>();
            mf.sharedMesh = planeMesh;
            MeshRenderer mr = planeObject.AddComponent<MeshRenderer>();
            mr.material = GetMaterialForPlane(averageNormal); // Use simplified GetMaterialForPlane
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            planeObject.isStatic = false;

            int layerId = LayerMask.NameToLayer(planeLayerName);
            if (layerId != -1) planeObject.layer = layerId;
            else LogError($"Layer '{planeLayerName}' not found for generated plane.", ARManagerDebugFlags.PlaneGeneration);

            generatedPlanes.Add(planeObject);
            planeCreationTimes[planeObject] = Time.time;
            planeToUpdate = planeObject;

            var parentTransform = xrOrigin?.TrackablesParent;
            if (parentTransform != null) planeObject.transform.SetParent(parentTransform, true);

            Log($"Created new plane {planeName} for area {areaToString(area)} at pos {averagePosition}, normal {averageNormal}", ARManagerDebugFlags.PlaneGeneration); // DEBUG LOG
        }

        if (planeToUpdate != null && visitedPlanesInCurrentMask != null) visitedPlanesInCurrentMask[planeToUpdate] = true;
        if (planeToUpdate != null) planeLastVisitedTime[planeToUpdate] = Time.time;
        if (usePersistentPlanes && planeToUpdate != null) MakePlanePersistent(planeToUpdate); // Example of making it persistent

        return true;
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
        Mesh mesh = new Mesh();
        mesh.vertices = new Vector3[]
        {
            new Vector3(-width / 2, -height / 2, 0),
            new Vector3(width / 2, -height / 2, 0),
            new Vector3(-width / 2, height / 2, 0),
            new Vector3(width / 2, height / 2, 0)
        };
        mesh.triangles = new int[] { 0, 2, 1, 2, 3, 1 };
        mesh.uv = new Vector2[]
        {
            new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1)
        };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private void CleanupOldPlanes(Dictionary<GameObject, bool> visitedPlanesInCurrentMask)
    {
        List<GameObject> planesToRemove = new List<GameObject>();
        float currentTime = Time.time;
        float removalDelay = GetUnvisitedPlaneRemovalDelay(); // Use the method to get the delay

        for (int i = generatedPlanes.Count - 1; i >= 0; i--)
        {
            GameObject plane = generatedPlanes[i];
            if (plane == null)
            {
                generatedPlanes.RemoveAt(i);
                continue;
            }

            bool visitedInThisMaskUpdate = visitedPlanesInCurrentMask?.ContainsKey(plane) ?? false;
            float lastSeen = planeLastVisitedTime.ContainsKey(plane) ? planeLastVisitedTime[plane] : 0;

            // If it's a timed cleanup (visitedPlanesInCurrentMask is null) or plane wasn't visited in current mask processing round
            if ((visitedPlanesInCurrentMask == null || !visitedInThisMaskUpdate) && (currentTime - lastSeen > removalDelay))
            {
                if (!IsPlanePersistent(plane)) // Only remove non-persistent planes this way
                {
                    planesToRemove.Add(plane);
                }
            }
        }

        foreach (GameObject plane in planesToRemove)
        {
            Log($"Removing old/unvisited plane: {plane.name}", ARManagerDebugFlags.PlaneGeneration);
            generatedPlanes.Remove(plane);
            planeCreationTimes.Remove(plane);
            planeLastVisitedTime.Remove(plane);
            persistentGeneratedPlanes.Remove(plane); // Also remove from persistence tracking if it was there
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
            float angle = Vector3.Angle(plane.transform.forward, -normal); // Assuming plane's forward is its visual normal direction

            if (distance < maxDistance && angle < maxAngleDegrees)
            {
                if (distance < minDistance) // Could use a combined score of distance and angle
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
        string S = "";
        for (int i = 0; i < 32; i++)
            if ((layerMask.value & (1 << i)) != 0)
                S += (S == "" ? "" : " | ") + LayerMask.LayerToName(i);
        return S == "" ? "<Nothing>" : S;
    }

    public Material VerticalPlaneMaterial => verticalPlaneMaterial;
    public Material HorizontalPlaneMaterial => horizontalPlaneMaterial;
    public string PlaneLayerName => planeLayerName;

    // --- Persistent Planes System --- //
    private void InitializePersistentPlanesSystem()
    {
        if (usePersistentPlanes)
        {
            Log("Система сохранения плоскостей инициализирована.", ARManagerDebugFlags.System | ARManagerDebugFlags.Initialization);
            // Дополнительная логика инициализации, если потребуется (например, загрузка сохраненных плоскостей)
        }
    }

    public bool MakePlanePersistent(GameObject plane)
    {
        if (!usePersistentPlanes || plane == null) return false;

        if (!persistentGeneratedPlanes.ContainsKey(plane))
        {
            persistentGeneratedPlanes.Add(plane, true);
            planeCreationTimes[plane] = Time.time; // Сохраняем время создания/персистенции
            planeLastVisitedTime[plane] = Time.time; // Инициализируем время последнего визита

            if (highlightPersistentPlanes)
            {
                var renderer = plane.GetComponent<Renderer>();
                if (renderer != null)
                {
                    // Сохраняем оригинальный материал, если нужно будет его восстановить
                    // if (!originalMaterials.ContainsKey(plane))
                    // {
                    //     originalMaterials.Add(plane, renderer.material);
                    // }
                    // renderer.material.color = persistentPlaneColor; // Пример подсветки
                    // Вместо прямого изменения цвета, лучше использовать отдельный материал или MaterialPropertyBlock
                    MaterialPropertyBlock props = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(props);
                    props.SetColor("_Color", persistentPlaneColor); // Предполагая, что шейдер использует _Color
                    renderer.SetPropertyBlock(props);

                }
                Log($"Плоскость {plane.name} сделана персистентной и подсвечена.", ARManagerDebugFlags.PlaneGeneration);
            }
            else
            {
                Log($"Плоскость {plane.name} сделана персистентной.", ARManagerDebugFlags.PlaneGeneration);
            }
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
            // if (highlightPersistentPlanes && originalMaterials.ContainsKey(plane))
            // {
            //     var renderer = plane.GetComponent<Renderer>();
            //     if (renderer != null)
            //     {
            //         renderer.material = originalMaterials[plane]; // Восстанавливаем оригинальный материал
            //     }
            //     originalMaterials.Remove(plane);
            // }
            Log($"Персистентность удалена для плоскости {plane.name}.", ARManagerDebugFlags.PlaneGeneration);
            return true;
        }
        return false;
    }

    // Helper to check if a GameObject is an ARFoundation-created plane
    private bool IsARFoundationPlane(GameObject planeGo)
    {
        if (planeGo == null) return false;
        // Проверяем, есть ли у объекта компонент ARPlane
        if (planeGo.GetComponent<ARPlane>() != null)
        {
            // Дополнительно проверяем, является ли родительский объект тем, что используется ARFoundation для трекаблов
            if (planeGo.transform.parent != null && xrOrigin != null && xrOrigin.TrackablesParent != null &&
                planeGo.transform.parent == xrOrigin.TrackablesParent)
            {
                return true;
            }
            // Fallback for older ARSessionOrigin or different setups
            if (planeGo.transform.parent != null && trackablesParentInstanceID_FromStart != 0 &&
                planeGo.transform.parent.GetInstanceID() == trackablesParentInstanceID_FromStart)
            {
                return true;
            }
        }
        return false;
    }

    public float GetUnvisitedPlaneRemovalDelay()
    {
        // Можно добавить логику, чтобы это значение было настраиваемым
        return 1.5f; // Секунды
    }

    private Material GetMaterialForPlane(Vector3 planeNormal)
    {
        // Простая логика: если нормаль близка к вертикальной, используем verticalPlaneMaterial
        // Иначе horizontalPlaneMaterial
        // Угол можно настроить
        if (Vector3.Angle(planeNormal, Vector3.up) < maxWallNormalAngleDeviation || Vector3.Angle(planeNormal, Vector3.down) < maxWallNormalAngleDeviation)
        {
            return verticalPlaneMaterial != null ? verticalPlaneMaterial : (horizontalPlaneMaterial != null ? horizontalPlaneMaterial : null); // Fallback
        }
        else
        {
            return horizontalPlaneMaterial != null ? horizontalPlaneMaterial : (verticalPlaneMaterial != null ? verticalPlaneMaterial : null); // Fallback
        }
    }

    private Material GetMaterialForPlane(Vector3 planeNormal, PlaneAlignment alignment)
    {
        if (alignment == PlaneAlignment.Vertical)
        {
            return verticalPlaneMaterial;
        }
        else if (alignment.IsHorizontal())
        {
            return horizontalPlaneMaterial;
        }
        // Fallback или более сложная логика на основе нормали, если alignment не однозначен
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

            Color paintColorToApply = Color.white; // Цвет по умолчанию
            if (colorManager != null)
            {
                paintColorToApply = colorManager.GetCurrentColor();
            }
            else
            {
                // LogWarning("ARWallPaintColorManager not found. Using default paint color (white).", ARManagerDebugFlags.System);
                // Предупреждение уже есть в Start(), не будем дублировать каждый тап
            }

            List<ARRaycastHit> hits = new List<ARRaycastHit>();
            if (arRaycastManager.Raycast(Input.GetTouch(0).position, hits, TrackableType.PlaneWithinPolygon | TrackableType.PlaneWithinBounds)) // Добавим PlaneWithinBounds для большей точности
            {
                ARRaycastHit hit = hits[0]; // Берем первое попадание
                ARPlane hitPlane = planeManager.GetPlane(hit.trackableId);

                if (hitPlane != null)
                {
                    Log($"Tapped on plane: {hitPlane.trackableId}, Alignment: {hitPlane.alignment}", ARManagerDebugFlags.Raycasting);

                    MeshRenderer hitPlaneRenderer = hitPlane.GetComponent<MeshRenderer>();
                    if (hitPlaneRenderer != null)
                    {
                        // Логика для "покраски" стены
                        if (paintMaterial != null)
                        {
                            // Сохраняем оригинальный материал, если мы его еще не сохранили для этой плоскости
                            if (!paintedPlaneOriginalMaterials.ContainsKey(hitPlane))
                            {
                                paintedPlaneOriginalMaterials[hitPlane] = hitPlaneRenderer.sharedMaterial; // sharedMaterial, чтобы не создавать лишних копий оригинала
                            }

                            // Создаем экземпляр покрасочного материала для этой плоскости, чтобы изменения не влияли на другие
                            Material materialInstance = Instantiate(paintMaterial);
                            hitPlaneRenderer.material = materialInstance; // Применяем экземпляр покрасочного материала

                            materialInstance.SetColor("_PaintColor", paintColorToApply);
                            Log($"Applied paint color {paintColorToApply} to plane {hitPlane.trackableId}", ARManagerDebugFlags.PlaneGeneration);

                            if (wallSegmentation != null && wallSegmentation.segmentationMaskTexture != null)
                            {
                                materialInstance.SetTexture("_SegmentationMask", wallSegmentation.segmentationMaskTexture);
                                Log($"Applied segmentation mask to painted plane {hitPlane.trackableId}", ARManagerDebugFlags.PlaneGeneration);
                            }
                            else
                            {
                                // Если маски нет, шейдер должен это корректно обрабатывать (например, красить всю плоскость)
                                materialInstance.SetTexture("_SegmentationMask", null); // Явно указываем null
                                LogWarning($"WallSegmentation or its mask is null for plane {hitPlane.trackableId}. Applied paint without mask.", ARManagerDebugFlags.PlaneGeneration);
                            }
                        }
                        else
                        {
                            LogWarning("Paint Material is not assigned in ARManagerInitializer2. Cannot paint the plane.", ARManagerDebugFlags.System);
                        }

                        // Логика для выделения (подсветки) выбранной плоскости (опционально, если selectedPlaneMaterial задан)
                        if (selectedPlaneMaterial != null)
                        {
                            if (currentlySelectedPlane != null && currentlySelectedPlane != hitPlane)
                            {
                                MeshRenderer prevRenderer = currentlySelectedPlane.GetComponent<MeshRenderer>();
                                if (prevRenderer != null && paintedPlaneOriginalMaterials.ContainsKey(currentlySelectedPlane))
                                {
                                    // Возвращаем оригинальный материал, который был до ПОКРАСКИ И ПОДСВЕТКИ
                                    prevRenderer.material = paintedPlaneOriginalMaterials[currentlySelectedPlane];
                                }
                                else if (prevRenderer != null && originalSelectedPlaneMaterial != null)
                                {
                                    // Если плоскость не была покрашена, но была подсвечена
                                    prevRenderer.material = originalSelectedPlaneMaterial;
                                }
                            }

                            if (currentlySelectedPlane != hitPlane)
                            {
                                // Сохраняем материал, который был ДО подсветки (это может быть оригинальный материал плоскости или уже покрашенный)
                                originalSelectedPlaneMaterial = hitPlaneRenderer.material;
                                currentlySelectedPlane = hitPlane;
                                hitPlaneRenderer.material = Instantiate(selectedPlaneMaterial); // Применяем материал подсветки
                                // Копируем нужные свойства из предыдущего материала в материал подсветки, если это необходимо
                                // Например, _PaintColor и _SegmentationMask, если шейдер подсветки их поддерживает
                                if (originalSelectedPlaneMaterial.HasProperty("_PaintColor"))
                                    hitPlaneRenderer.material.SetColor("_PaintColor", originalSelectedPlaneMaterial.GetColor("_PaintColor"));
                                if (originalSelectedPlaneMaterial.HasProperty("_SegmentationMask") && originalSelectedPlaneMaterial.GetTexture("_SegmentationMask") != null)
                                    hitPlaneRenderer.material.SetTexture("_SegmentationMask", originalSelectedPlaneMaterial.GetTexture("_SegmentationMask"));
                            }
                        }
                    }
                    else
                    {
                        LogWarning($"MeshRenderer not found on tapped plane {hitPlane.trackableId}", ARManagerDebugFlags.Raycasting);
                    }
                }
                else
                {
                    LogWarning($"ARPlane with trackableId {hit.trackableId} not found via planeManager.GetPlane().", ARManagerDebugFlags.Raycasting);
                }
            }
            else
            {
                Log("Tap did not hit any ARPlane trackable.", ARManagerDebugFlags.Raycasting);
                // Если тап не попал в плоскость, можно сбросить выделение/покраску с предыдущей плоскости
                if (currentlySelectedPlane != null)
                {
                    MeshRenderer prevRenderer = currentlySelectedPlane.GetComponent<MeshRenderer>();
                    if (prevRenderer != null && paintedPlaneOriginalMaterials.ContainsKey(currentlySelectedPlane))
                    {
                        prevRenderer.material = paintedPlaneOriginalMaterials[currentlySelectedPlane];
                        // paintedPlaneOriginalMaterials.Remove(currentlySelectedPlane); // Рассмотреть, нужно ли удалять сразу
                    }
                    else if (prevRenderer != null && originalSelectedPlaneMaterial != null)
                    {
                        prevRenderer.material = originalSelectedPlaneMaterial;
                    }
                    currentlySelectedPlane = null;
                    originalSelectedPlaneMaterial = null;
                }
            }
        }
    }

    private void HandleTap(Vector2 touchPosition)
    {
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
                    Log($"[TAP] Коснулись плоскости: {tappedPlane.trackableId}, Alignment: {tappedPlane.alignment}, Distance: {hit.distance}", ARManagerDebugFlags.Raycasting);

                    if (tappedPlane.alignment == PlaneAlignment.Vertical)
                    {
                        if (colorManager != null)
                        {
                            PaintPlane(tappedPlane, colorManager.currentColor);
                        }
                        else
                        {
                            LogError("colorManager is null in HandleTap. Cannot get paint color.", ARManagerDebugFlags.Raycasting);
                            PaintPlane(tappedPlane, Color.magenta); // Запасной цвет
                        }
                    }
                    else
                    {
                        Log($"[TAP] Плоскость {tappedPlane.trackableId} не вертикальная (Alignment: {tappedPlane.alignment}), покраска отменена.", ARManagerDebugFlags.Raycasting);
                    }
                }
                else
                {
                    // Исправляем ошибку: hit.trackable это ARTrackable, у него нет trackableId напрямую в этом контексте.
                    // Вместо этого, мы можем вывести тип и, если возможно, другую информацию.
                    string trackableInfo = hit.trackable != null ? hit.trackable.GetType().Name : "null";
                    if (hit.trackable is ARPlane plane) // Попытка получить trackableId если это все же ARPlane, но as ARPlane выше не сработал
                    {
                        trackableInfo += $", ID: {plane.trackableId}";
                    }
                    else if (hit.trackable is ARAnchor anchor)
                    {
                        trackableInfo += $", ID: {anchor.trackableId}";
                    }
                    // Добавьте другие типы ARTrackable по мере необходимости
                    LogWarning($"[TAP] Рейкаст попал в объект типа {trackableInfo}, но это не ARPlane или не удалось привести к ARPlane.", ARManagerDebugFlags.Raycasting);
                }
            }
            else
            {
                LogWarning("[TAP] Рейкаст вернул true, но список попаданий пуст.", ARManagerDebugFlags.Raycasting);
            }
        }
        else
        {
            Log("[TAP] Рейкаст не попал ни в одну AR плоскость (TrackableType.PlaneWithinPolygon).", ARManagerDebugFlags.Raycasting);
        }
    }

    private void PaintPlane(ARPlane plane, Color color)
    {
        // ... existing code ...
    }
}