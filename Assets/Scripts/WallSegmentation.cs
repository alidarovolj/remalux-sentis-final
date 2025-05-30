// #define REAL_MASK_PROCESSING_DISABLED // Закомментируйте эту строку, чтобы включить реальную обработку

using UnityEngine;
using Unity.Sentis;
using System.Collections;
using System.Collections.Generic; // <--- Убедитесь, что это есть
using System.IO;
using System.Linq;
using System;
using System.Reflection;          // <--- Убедитесь, что это есть
using System.Text;              // <--- Убедитесь, что это есть
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;  // <--- Убедитесь, что это есть
using UnityEngine.XR.Management;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe; // <--- Убедитесь, что это есть
using Unity.XR.CoreUtils;           // <--- Убедитесь, что это есть
using UnityEngine.Networking; // Added for UnityWebRequest
using UnityEngine.Profiling;
using System.Threading.Tasks;

// Если используете другие пакеты рендеринга, их using директивы тоже должны быть здесь
// using UnityEngine.Rendering;
// using UnityEngine.Rendering.Universal;

/// <summary>
/// Компонент для сегментации стен с использованием ML модели в Unity Sentis.
/// Обновлен для безопасной загрузки моделей, предотвращающей краш Unity.
/// </summary>
public class WallSegmentation : MonoBehaviour
{
    [Header("Настройки ML модели")]
    [Tooltip("Ссылка на ML модель в формате ONNX или Sentis")]
    public UnityEngine.Object modelAsset;

    [NonSerialized]
    public string modelFilePath;

    [Tooltip("Предпочитаемый бэкенд для исполнения модели (0 = CPU, 1 = GPUCompute)")]
    public int preferredBackend = 0;

    [Tooltip("Использовать безопасную асинхронную загрузку модели")]
    public bool useSafeModelLoading = true;

    [Tooltip("Тайм-аут загрузки модели в секундах")]
    [SerializeField] private float modelLoadTimeout = 60f;

    [Tooltip("Принудительно использовать метод захвата изображения для XR Simulation")]
    public bool forceXRSimulationCapture = true;

    [Header("Настройки Постобработки Маски")]
    [Tooltip("Включить общую постобработку маски (включая размытие, резкость, контраст, морфологию)")]
    [SerializeField] private bool enablePostProcessing = false; // ОТКЛЮЧЕНО
    [Tooltip("Включить Гауссово размытие для сглаживания маски")]
    [SerializeField] private bool enableGaussianBlur = false; // ОТКЛЮЧЕНО
    [Tooltip("Материал для Гауссова размытия")]
    [SerializeField] private Material gaussianBlurMaterial;
    [Tooltip("Размер ядра Гауссова размытия (в пикселях)")]
    [SerializeField, Range(1, 10)] private int blurSize = 3;
    [Tooltip("Включить повышение резкости краев маски")]
    [SerializeField] private bool enableSharpen = false; // ОТКЛЮЧЕНО
    [Tooltip("Материал для повышения резкости")]
    [SerializeField] private Material sharpenMaterial;
    [Tooltip("Включить повышение контраста маски")]
    [SerializeField] private bool enableContrast = true; // Это поле управляет ВКЛ/ВЫКЛ контраста
    [Tooltip("Материал для повышения контраста")]
    [SerializeField] private Material contrastMaterial;
    [Tooltip("Материал для Гауссова размытия 3x3 (для сглаживания low-res маски)")] // NEW
    public Material gaussianBlur3x3Material; // NEW
    [Tooltip("Материал для пороговой обработки (для бинаризации low-res маски после размытия)")] // NEW
    public Material thresholdMaskMaterial; // NEW

    [Header("Настройки сегментации")]
    // [Tooltip("Индекс класса стены в модели")][SerializeField] private int wallClassIndex = 1;     // Стена (ИЗМЕНЕНО для segformer-b4-wall)
    // [Tooltip("Индекс класса пола в модели")][SerializeField] private int floorClassIndex = 2; // Пол (ИЗМЕНЕНО для segformer-b4-wall, если есть, иначе -1)
    // [Tooltip("Порог вероятности для определения пола")][SerializeField, Range(0.01f, 1.0f)] private float floorConfidence = 0.15f; // ИСПРАВЛЕНО: повышен для консистентности
    [Tooltip("Обнаруживать также горизонтальные поверхности (пол)")] public bool detectFloor = false;

    [Header("Настройки качества и производительности")]
    [Tooltip("Целевое разрешение для обработки (ширина, высота)")]
    public Vector2Int inputResolution = new Vector2Int(640, 480);

    [Tooltip("Автоматически оптимизировать разрешение на основе производительности")]
    public bool adaptiveResolution = false;

    [Tooltip("Шаг изменения разрешения для адаптивного режима")] // Новое поле
    public Vector2Int resolutionStep = new Vector2Int(64, 48); // Новое поле

    [Tooltip("Максимальное разрешение для высокого качества")]
    public Vector2Int maxResolution = new Vector2Int(768, 768);

    [Tooltip("Минимальное разрешение для производительности")]
    public Vector2Int minResolution = new Vector2Int(384, 384);

    [Tooltip("Целевое время обработки в миллисекундах (для адаптивного разрешения)")]
    [Range(16f, 100f)]
    public float targetProcessingTimeMs = 50f;

    [Tooltip("Фактор качества маски (0-1), влияет на выбор разрешения")]
    [Range(0.1f, 1.0f)]
    public float qualityFactor = 0.7f;

    [Header("Ограничение частоты инференса")]
    [Tooltip("Максимальная частота выполнения сегментации (FPS). 0 = без ограничений")]
    [Range(0f, 60f)]
    public float maxSegmentationFPS = 15f;

    [Header("Temporal Interpolation (Временная интерполяция)")]
    [Tooltip("Включить плавную интерполяцию маски между инференсами для избежания мерцания")]
    public bool enableTemporalInterpolation = false;

    [Tooltip("Скорость интерполяции маски (1.0 = мгновенное обновление, 0.1 = плавное)")]
    [Range(0.1f, 1.0f)]
    public float maskInterpolationSpeed = 0.6f;

    [Tooltip("Использовать экспоненциальное сглаживание для более естественной интерполяции")]
    public bool useExponentialSmoothing = true;

    [Tooltip("Максимальное время показа старой маски без нового инференса (сек)")]
    [Range(1f, 10f)]
    public float maxMaskAgeSeconds = 3f;

    [Tooltip("Материал для временной интерполяции маски")] // Новое поле
    [SerializeField] private Material temporalBlendMaterial; // Новое поле

    [Tooltip("Использовать симуляцию, если не удаётся получить изображение с камеры")]
    public bool useSimulationIfNoCamera = true;

    [Tooltip("Количество неудачных попыток получения изображения перед включением симуляции")]
    public int failureThresholdForSimulation = 10;

    [Header("Компоненты")]
    [Tooltip("Ссылка на ARSessionManager")]
    public ARSession arSession;

    [Tooltip("Ссылка на XROrigin")] public XROrigin xrOrigin = null;

    [Tooltip("Текстура для вывода маски сегментации")] public RenderTexture segmentationMaskTexture;

    [Tooltip("Порог вероятности для определения стены")]
    public float segmentationConfidenceThreshold = 0.15f; // ИСПРАВЛЕНО: повышен с 0.01f до 0.15f для лучшего качества сегментации

    [Tooltip("Порог вероятности для определения пола")]
    public float floorConfidenceThreshold = 0.15f;    // ИСПРАВЛЕНО: повышен для консистентности

    [Tooltip("Путь к файлу модели (.sentis или .onnx) в StreamingAssets")] public string modelPath = "";

    [Tooltip("Предпочитаемый бэкенд для исполнения модели (0 = CPU, 1 = GPUCompute)")]
    public int selectedBackend = 0; // 0 = CPU, 1 = GPUCompute (через BackendType)

    [Header("Настройки материалов и отладки маски")] // Новый заголовок для инспектора
    [SerializeField]
    [Tooltip("Материал, используемый для преобразования выхода модели в маску сегментации.")]
    private Material segmentationMaterial; // Добавлено поле

    [Tooltip("Множитель контраста")]
    [SerializeField, Range(0.1f, 5.0f)] private float contrastFactor = 1.0f;

    [Header("Настройки морфологических операций")]
    [Tooltip("Включить морфологическое закрытие (Dilate -> Erode) для удаления мелких дыр")]
    [SerializeField] private bool enableMorphologicalClosing = false;
    [Tooltip("Включить морфологическое открытие (Erode -> Dilate) для удаления мелкого шума")]
    [SerializeField] private bool enableMorphologicalOpening = false;
    [Tooltip("Материал для операции расширения (Dilate)")]
    [SerializeField] private Material dilateMaterial;
    [Tooltip("Материал для операции сужения (Erode)")]
    [SerializeField] private Material erodeMaterial;

    [Tooltip("Материал для комплексной постобработки GPU (если используется)")] // Новое поле
    [SerializeField] private Material comprehensivePostProcessMaterial; // Новое поле

    [Header("Настройки пороговой обработки уверенности")]
    [Tooltip("Верхний порог вероятности для определения стены (используется как основной или высокий порог для гистерезиса)")]
    [SerializeField] private float wallConfidenceInternal = 0.15f; // Переименовано для создания публичного свойства
    public float WallConfidence { get { return wallConfidenceInternal; } } // Добавлено публичное свойство
    // [Tooltip("Включить гистерезисную пороговую обработку (использует верхний и нижний пороги)")]
    // [SerializeField] private bool enableHysteresisThresholding = false;
    // [Tooltip("Нижний порог вероятности для гистерезисной обработки (должен быть меньше wallConfidence)")]
    // [SerializeField, Range(0.0001f, 1.0f)] private float lowWallConfidence = 0.05f;

    [Header("GPU Optimization (Оптимизация GPU)")]
    [Tooltip("Compute Shader для обработки масок на GPU")]
    public ComputeShader segmentationProcessor;

    [Tooltip("Использовать GPU для анализа качества маски (намного быстрее)")]
    public bool useGPUQualityAnalysis = true;

    [Tooltip("Размер downsampling для анализа качества (больше = быстрее, меньше точности)")]
    [Range(2, 16)]
    public int qualityAnalysisDownsample = 8;

    [Tooltip("Использовать GPU для всей постобработки (максимальная производительность)")]
    public bool useGPUPostProcessing = false;

    [Tooltip("Использовать комплексное ядро постобработки (все в одном проходе)")]
    public bool useComprehensiveGPUProcessing = true;

    [Header("Performance Profiling (Профилирование производительности)")]
    [Tooltip("Включить встроенное профилирование производительности")]
    public bool enablePerformanceProfiling = true;

    [Tooltip("Интервал логирования статистики производительности (секунды)")]
    [Range(1f, 30f)]
    public float profilingLogInterval = 5f;

    [Tooltip("Показывать детальную статистику в логах")]
    public bool showDetailedProfiling = false;

    [Header("Advanced Memory Management (Продвинутое управление памятью)")]
    [Tooltip("Включить систему детекции утечек памяти")]
    public bool enableMemoryLeakDetection = true;

    [Tooltip("Максимальный размер пула текстур (MB) перед принудительной очисткой")]
    [Range(50, 500)]
    public int maxTexturePoolSizeMB = 200;

    [Tooltip("Интервал проверки памяти (секунды)")]
    [Range(5, 60)]
    public int memoryCheckIntervalSeconds = 15;

    [Tooltip("Автоматически очищать неиспользуемые ресурсы")]
    public bool enableAutomaticCleanup = true;

    [Tooltip("Включить продвинутое управление памятью")]
    public bool enableAdvancedMemoryManagement = true;

    [Tooltip("Интервал проверки памяти (секунды)")]
    public int memoryCheckInterval = 15;

    [Tooltip("Интервал логирования статистики производительности (секунды)")]
    public float performanceLogInterval = 5f;

    [Tooltip("Включить детальную отладку")]
    public bool enableDetailedDebug = false;

    [Tooltip("Флаги отладки")]
    public DebugFlags debugFlags = DebugFlags.None;

    [Header("Global Logging Control")] // NEW HEADER
    [Tooltip("Enable all logging messages from this WallSegmentation component. If false, no logs (Info, Warning, Error) will be printed from this script, regardless of individual DebugFlags.")] // NEW
    public bool enableComponentLogging = true; // NEW

    // --- ADDED FIELDS FOR DEBUG SAVE PATH ---
    [Header("Отладка Сохранения Масок")]
    [Tooltip("Включить сохранение отладочных масок. Если false, маски не будут сохраняться даже если указан путь.")]
    public bool enableDebugMaskSaving = false; // NEW: Control for saving debug masks
    [Tooltip("Путь для сохранения отладочных масок (относительно Application.persistentDataPath)")]
    [SerializeField] private string debugSavePath = "DebugSegmentationMasks";
    private bool debugSavePathValid = false;
    private string fullDebugSavePath = "";
    // --- END OF ADDED FIELDS ---

    // Добавляем enum LogLevel
    public enum LogLevel
    {
        Info,
        Warning,
        Error
    }

    // Свойства для получения AR компонентов
    public ARSession ARSession // Изменено имя свойства для ясности
    {
        get
        {
            if (arSession == null) // Используем новое поле arSession
            {
                arSession = FindObjectOfType<ARSession>(); // Ищем ARSession
            }
            return arSession;
        }
        set
        {
            arSession = value; // Устанавливаем новое поле arSession
        }
    }

    public XROrigin XROrigin
    {
        get
        {
            if (xrOrigin == null)
            {
                xrOrigin = FindObjectOfType<XROrigin>();
            }
            return xrOrigin;
        }
        set
        {
            xrOrigin = value;
        }
    }

    public ARCameraManager ARCameraManager
    {
        get
        {
            if (XROrigin != null && XROrigin.Camera != null)
            {
                return XROrigin.Camera.GetComponent<ARCameraManager>();
            }
            return FindObjectOfType<ARCameraManager>();
        }
        set
        {
            // Тут мы можем сохранить ссылку на ARCameraManager, если нужно
            // Но так как в getter мы его получаем динамически, создадим приватное поле
            arCameraManager = value;
        }
    }

    [Header("Отладка")]
    [Tooltip("Включить режим отладки с выводом логов")]
    public bool debugMode = false;

    [System.Flags]
    public enum DebugFlags
    {
        None = 0,
        Initialization = 1 << 0,
        ExecutionFlow = 1 << 1,
        TensorProcessing = 1 << 2,
        TensorAnalysis = 1 << 3, // Для вывода содержимого тензора
        CameraTexture = 1 << 4,  // Для отладки получения текстуры с камеры
        PlaneGeneration = 1 << 5, // Для отладки создания плоскостей в ARManagerInitializer
        DetailedExecution = 1 << 6, // Более детальные логи выполнения модели
        DetailedTensor = 1 << 7, // Более детальные логи обработки тензора
        Performance = 1 << 8    // Для отладки производительности и времени обработки
    }
    [Tooltip("Флаги для детальной отладки различных частей системы")]
    public DebugFlags debugLevel = DebugFlags.None;

    // [Tooltip("Сохранять отладочные маски в указанный путь")] // Добавлено --- THIS LINE AND THE NEXT WILL BE REMOVED
    // public bool saveDebugMasks = false; // THIS LINE WILL BE REMOVED

    // private bool isProcessing = false; // Флаг, показывающий, что идет обработка сегментации // Закомментировано из-за CS0414

    [Tooltip("Использовать симуляцию сегментации, если не удалось получить изображение с камеры")]
    public bool useSimulatedSegmentationFallback = true; // Переименовано для ясности

    [Tooltip("Счетчик неудачных попыток получения изображения перед активацией симуляции")]
    public int simulationFallbackThreshold = 10;

    private Worker engine; // TODO: Review usage, might be legacy or Sentis internal
    private Model runtimeModel;
    private Worker worker; // Sentis Worker
    private Texture2D cameraTexture;    // Текстура для захвата изображения с камеры
    private bool isModelInitialized = false; // Флаг, что модель успешно инициализирована
    private bool isInitializing = false;     // Флаг, что идет процесс инициализации модели
    private string lastErrorMessage = null;  // Последнее сообщение об ошибке при инициализации
    // НОВЫЙ ФЛАГ
    private bool modelOutputDimensionsKnown = false; // Флаг, что размеры выхода модели определены

    private ComputeShader maskAnalysisShader;

    [System.NonSerialized]
    private int sentisModelWidth = 512; // Значения по умолчанию, обновятся из модели
    [System.NonSerialized]
    private int sentisModelHeight = 512;
    // private int debugMaskCounter = 0; // Закомментировано из-за CS0414

    // События для уведомления о состоянии модели и обновлении маски
    public delegate void ModelInitializedHandler(); // Раскомментировано
    public event ModelInitializedHandler OnModelInitialized; // Раскомментировано

    public delegate void SegmentationMaskUpdatedHandler(RenderTexture mask); // Раскомментировано
    public event SegmentationMaskUpdatedHandler OnSegmentationMaskUpdated; // Раскомментировано

    // Свойства для доступа к состоянию инициализации модели
    public bool IsModelInitialized { get { return isModelInitialized; } private set { isModelInitialized = value; } } // Добавлено свойство
    private Model model; // Sentis Model object
    private Unity.Sentis.TextureTransform textureTransformToLowRes = new Unity.Sentis.TextureTransform(); // MODIFIED: Use full namespace

    // AR Components
    private ARCameraManager arCameraManager; // This is the original one that should be kept (around line 356 of original file)
    // private ARPlaneManager arPlaneManager; // Если потребуется для контекста

    private RenderTexture lastSuccessfulMask; // Последняя успешно полученная и обработанная маска
    // private bool hasValidMask = false; // Закомментировано из-за CS0414
    // private float lastValidMaskTime = 0f; // Закомментировано из-за CS0414
    // private int stableFrameCount = 0; // Закомментировано из-за CS0414
    private const int REQUIRED_STABLE_FRAMES = 2; // Уменьшено с 3 до 2 для более быстрой реакции

    // Параметры сглаживания маски для улучшения визуального качества
    [Header("Настройки качества маски")]
    [Tooltip("Применять сглаживание к маске сегментации")]
    public bool applyMaskSmoothing = true; // ПРОВЕРЕНО: должно быть включено для устранения зазубренных краев
    [Tooltip("Значение размытия для сглаживания маски (в пикселях)")]
    [Range(1, 10)]
    public int maskBlurSize = 4; // ИСПРАВЛЕНО: установлен в оптимальное значение (3-5) для лучшего сглаживания
    [Tooltip("Повышать резкость краев на маске")]
    public bool enhanceEdges = true; // ПРОВЕРЕНО: уже включено согласно анализу
    [Tooltip("Повышать контраст маски")]
    public bool enhanceContrast = true; // ПРОВЕРЕНО: уже включено согласно анализу
    // [Tooltip("Множитель контраста для постобработки")] // ЭТА СТРОКА И СЛЕДУЮЩАЯ БУДУТ УДАЛЕНЫ
    // [SerializeField, Range(0.1f, 5.0f)] private float contrastFactor = 1.0f; // ЭТА СТРОКА БУДЕТ УДАЛЕНА

    // --- ADDED FIELDS FOR LOW-RES MASK POST-PROCESSING ---
    [Header("Постобработка Low-Res Маски")]
    [Tooltip("Применять Гауссово размытие к low-res маске перед апскейлингом")]
    [SerializeField] private bool applyGaussianBlurToLowResMask = false;
    [Tooltip("Применять пороговую обработку к размытой low-res маске")]
    [SerializeField] private bool applyThresholdToBlurredLowResMask = false;
    [Tooltip("Значение порога для бинаризации low-res маски (0-1)")]
    [SerializeField, Range(0.01f, 1.0f)] private float lowResMaskThreshold = 0.5f;
    // --- END OF ADDED FIELDS ---

    // Добавляем оптимизированный пул текстур для уменьшения аллокаций памяти
    private class TexturePool
    {
        private Dictionary<Vector2Int, List<RenderTexture>> availableTextures = new Dictionary<Vector2Int, List<RenderTexture>>();
        private Dictionary<Vector2Int, List<RenderTexture>> inUseTextures = new Dictionary<Vector2Int, List<RenderTexture>>();
        private Dictionary<int, Vector2Int> textureToSize = new Dictionary<int, Vector2Int>();
        private RenderTextureFormat defaultFormat;

        public TexturePool(RenderTextureFormat format = RenderTextureFormat.ARGB32)
        {
            defaultFormat = format;
        }

        public RenderTexture GetTexture(int width, int height, RenderTextureFormat format = RenderTextureFormat.ARGB32)
        {
            RenderTextureFormat textureFormat = format != RenderTextureFormat.ARGB32 ? format : defaultFormat;
            Vector2Int size = new Vector2Int(width, height);

            if (availableTextures.ContainsKey(size) && availableTextures[size].Count > 0)
            {
                RenderTexture texture = availableTextures[size][0];
                availableTextures[size].RemoveAt(0);

                if (!inUseTextures.ContainsKey(size))
                {
                    inUseTextures[size] = new List<RenderTexture>();
                }

                inUseTextures[size].Add(texture);
                textureToSize[texture.GetInstanceID()] = size;
                return texture;
            }

            RenderTexture newTexture = new RenderTexture(width, height, 0, textureFormat);
            newTexture.Create();

            if (!inUseTextures.ContainsKey(size))
            {
                inUseTextures[size] = new List<RenderTexture>();
            }

            inUseTextures[size].Add(newTexture);
            textureToSize[newTexture.GetInstanceID()] = size;
            return newTexture;
        }

        public void ReleaseTexture(RenderTexture texture)
        {
            if (texture == null) return;

            int instanceId = texture.GetInstanceID();
            if (!textureToSize.TryGetValue(instanceId, out Vector2Int sizeKey))
            {
                // This texture was not from our pool or already fully released
                if (texture.IsCreated()) // Check if it's a valid RT
                {
                    // Attempt to release it directly if it's not from the pool but still exists
                    Debug.LogWarning($"[TexturePool] Attempting to release an unpooled or already released RenderTexture: {texture.name} (ID: {instanceId}). Forcing release.");
                    texture.Release();
                    UnityEngine.Object.DestroyImmediate(texture, true); // Allow destroying assets
                }
                return;
            }

            if (inUseTextures.TryGetValue(sizeKey, out List<RenderTexture> usedList) && usedList.Contains(texture))
            {
                usedList.Remove(texture);
                if (!availableTextures.TryGetValue(sizeKey, out List<RenderTexture> availableList))
                {
                    availableList = new List<RenderTexture>();
                    availableTextures[sizeKey] = availableList;
                }
                availableList.Add(texture); // Add back to available instead of destroying immediately
                // Debug.Log($"[TexturePool] Released texture {texture.name} (ID: {instanceId}) to available pool for size {sizeKey}. Available: {availableList.Count}");
            }
            else
            {
                // If it's not in "inUse" but we have a sizeKey, it might be an anomaly or already in available.
                // To be safe, if it's not in available, let's try to release it properly.
                bool wasInAvailable = false;
                if (availableTextures.TryGetValue(sizeKey, out List<RenderTexture> currentAvailableList))
                {
                    if (currentAvailableList.Contains(texture))
                    {
                        wasInAvailable = true; // It's already in the available pool, do nothing more.
                    }
                }

                if (!wasInAvailable)
                {
                    // This case implies it was known to the pool (had a sizeKey) but wasn't in 'inUse' or 'available'.
                    // This could happen if ReleaseAllCreatedTextures was called, which destroys them.
                    // Or if it's a texture that was Get'ed but then ReleaseTexture(rt) called before rt was returned to pool via ReleaseAll.
                    // The original code had RenderTexture.ReleaseTemporary(texture); here, which is wrong for non-temporary.
                    // We should only destroy it if it's still valid and created.
                    if (texture.IsCreated())
                    {
                        Debug.LogWarning($"[TexturePool] Releasing a known texture (ID: {instanceId}, Size: {sizeKey.x}x{sizeKey.y}) that was not in 'inUse' or 'available'. Destroying it.");
                        texture.Release();
                        UnityEngine.Object.DestroyImmediate(texture, true); // Allow destroying assets
                    }
                    textureToSize.Remove(instanceId); // Remove from tracking if we destroy it.
                }
            }
        }

        public void ReleaseAllCreatedTextures() // Renamed from ClearAll for clarity
        {
            Debug.Log($"[TexturePool] Releasing all textures. InUse: {inUseTextures.Sum(kvp => kvp.Value.Count)}, Available: {availableTextures.Sum(kvp => kvp.Value.Count)}");
            foreach (var kvp in inUseTextures)
            {
                foreach (var texture in kvp.Value)
                {
                    if (texture != null && texture.IsCreated())
                    {
                        // Debug.Log($"[TexturePool] Destroying in-use texture: {texture.name} (ID: {texture.GetInstanceID()})");
                        texture.Release();
                        UnityEngine.Object.DestroyImmediate(texture, true); // Allow destroying assets
                    }
                }
                kvp.Value.Clear();
            }
            inUseTextures.Clear();

            foreach (var kvp in availableTextures)
            {
                foreach (var texture in kvp.Value)
                {
                    if (texture != null && texture.IsCreated())
                    {
                        // Debug.Log($"[TexturePool] Destroying available texture: {texture.name} (ID: {texture.GetInstanceID()})");
                        texture.Release();
                        UnityEngine.Object.DestroyImmediate(texture, true); // Allow destroying assets
                    }
                }
                kvp.Value.Clear();
            }
            availableTextures.Clear();
            textureToSize.Clear(); // Clear all tracking
            Debug.Log("[TexturePool] All textures released and pool cleared.");
        }

        public int EstimatePoolSize()
        {
            int totalBytes = 0;

            foreach (var sizeGroup in availableTextures)
            {
                Vector2Int size = sizeGroup.Key;
                int count = sizeGroup.Value.Count;
                totalBytes += size.x * size.y * 4 * count; // ARGB32 = 4 bytes per pixel
            }

            foreach (var sizeGroup in inUseTextures)
            {
                Vector2Int size = sizeGroup.Key;
                int count = sizeGroup.Value.Count;
                totalBytes += size.x * size.y * 4 * count;
            }

            return totalBytes;
        }

        public int ForceCleanup()
        {
            int releasedCount = 0;

            foreach (var sizeGroup in availableTextures)
            {
                foreach (var texture in sizeGroup.Value)
                {
                    if (texture != null && texture.IsCreated())
                    {
                        texture.Release();
                        UnityEngine.Object.Destroy(texture);
                        releasedCount++;
                    }
                }
            }

            availableTextures.Clear();

            var newTextureToSize = new Dictionary<int, Vector2Int>();
            foreach (var sizeGroup in inUseTextures)
            {
                foreach (var texture in sizeGroup.Value)
                {
                    if (texture != null)
                    {
                        newTextureToSize[texture.GetInstanceID()] = sizeGroup.Key;
                    }
                }
            }
            textureToSize = newTextureToSize;

            return releasedCount;
        }
    }

    private class Texture2DPool
    {
        private Dictionary<Vector2Int, List<Texture2D>> availableTextures = new Dictionary<Vector2Int, List<Texture2D>>();
        private Dictionary<Vector2Int, List<Texture2D>> inUseTextures = new Dictionary<Vector2Int, List<Texture2D>>();
        private Dictionary<int, Vector2Int> textureToKey = new Dictionary<int, Vector2Int>();

        public Texture2D GetTexture(int width, int height, TextureFormat format = TextureFormat.ARGB32)
        {
            Vector2Int key = new Vector2Int(width, height);

            if (!availableTextures.ContainsKey(key))
                availableTextures[key] = new List<Texture2D>();
            if (!inUseTextures.ContainsKey(key))
                inUseTextures[key] = new List<Texture2D>();

            Texture2D texture;
            if (availableTextures[key].Count > 0)
            {
                texture = availableTextures[key][0];
                availableTextures[key].RemoveAt(0);
            }
            else
            {
                texture = new Texture2D(width, height, format, false);
            }

            inUseTextures[key].Add(texture);
            textureToKey[texture.GetInstanceID()] = key;
            return texture;
        }

        public void ReleaseTexture(Texture2D texture)
        {
            if (texture == null) return;

            int instanceID = texture.GetInstanceID();
            if (textureToKey.TryGetValue(instanceID, out Vector2Int key))
            {
                if (inUseTextures[key].Remove(texture))
                {
                    availableTextures[key].Add(texture);
                }
                textureToKey.Remove(instanceID);
            }
        }

        public void ClearAll()
        {
            foreach (var textureList in availableTextures.Values)
            {
                foreach (var texture in textureList)
                {
                    if (texture != null) DestroyImmediate(texture);
                }
            }
            availableTextures.Clear();
            inUseTextures.Clear();
            textureToKey.Clear();
        }

        public int EstimatePoolSize()
        {
            int totalBytes = 0;
            foreach (var sizeGroup in availableTextures)
            {
                Vector2Int size = sizeGroup.Key;
                int count = sizeGroup.Value.Count;
                totalBytes += size.x * size.y * 4 * count; // ARGB32 = 4 bytes per pixel
            }
            return totalBytes;
        }

        public int ForceCleanup()
        {
            int releasedCount = 0;
            foreach (var textureList in availableTextures.Values)
            {
                releasedCount += textureList.Count;
                foreach (var texture in textureList)
                {
                    if (texture != null) DestroyImmediate(texture);
                }
                textureList.Clear();
            }
            return releasedCount;
        }
    }

    private TexturePool texturePool;
    private Texture2DPool texture2DPool;

    // Memory Profiling Variables
    private long baselineMemoryUsage = 0;
    private Dictionary<string, int> resourceCounts = new Dictionary<string, int>();
    private Dictionary<string, float> resourceCreationTimes = new Dictionary<string, float>();
    private int totalTexturesCreated = 0;
    private int totalTexturesReleased = 0;
    // private float lastMemoryCheckTime = 0f; // Закомментировано из-за CS0414

    // Performance Profiling Variables
    private List<float> processingTimes = new List<float>();
    private float lastQualityScore = 0f;
    private System.Diagnostics.Stopwatch processingStopwatch = new System.Diagnostics.Stopwatch();
    private System.Diagnostics.Stopwatch tensorConversionStopwatch = new System.Diagnostics.Stopwatch(); // Added
    private System.Diagnostics.Stopwatch modelExecutionStopwatch = new System.Diagnostics.Stopwatch(); // Added
    private System.Diagnostics.Stopwatch tensorRenderStopwatch = new System.Diagnostics.Stopwatch(); // Added
    private System.Diagnostics.Stopwatch comprehensivePostProcessStopwatch = new System.Diagnostics.Stopwatch(); // Added for comprehensive GPU post-processing
    private System.Diagnostics.Stopwatch cpuImageConversionStopwatch = new System.Diagnostics.Stopwatch(); // Added for XRCpuImage conversion
    private System.Diagnostics.Stopwatch cpuPostProcessStopwatch = new System.Diagnostics.Stopwatch(); // Added for CPU post-processing
    private float totalProcessingTime = 0f;
    private int processedFrameCount = 0;

    // GPU Post-processing textures
    private RenderTexture tempMask1;
    private RenderTexture tempMask2;
    private RenderTexture previousMask;
    private RenderTexture interpolatedMask;
    private Vector2Int currentResolution = new Vector2Int(640, 480);

    // Добавляем переменные для управления частотой кадров
    private float lastFrameProcessTime = 0f;
    // private int cameraFrameSkipCounter = 0; // CS0414
    private const int CAMERA_FRAME_SKIP_COUNT = 2; // Пропускать 2 из 3 кадров для ~20 FPS на 60 FPS камере, если maxSegmentationFPS ~20

    private Coroutine processingCoroutine = null;
    // private bool processingCoroutineActive = false; // CS0414 - REMOVING FIELD
    private RenderTexture tempOutputMask; // Declare tempOutputMask here
    private RenderTexture m_LowResMask; // ADDED: Class field for low-resolution mask

    // --- Constants for default low-resolution mask ---
    private const int DEFAULT_LOW_RES_WIDTH = 80;
    private const int DEFAULT_LOW_RES_HEIGHT = 80;

    // Добавляем недостающие константы и поля
    private const int DEFAULT_WARMUP_WIDTH = 256;
    private const int DEFAULT_WARMUP_HEIGHT = 256;

    private int sentisModelInputHeight = DEFAULT_WARMUP_HEIGHT;
    private int sentisModelInputWidth = DEFAULT_WARMUP_WIDTH;

    private bool isProcessingFrame = false; // Флаг, что кадр обрабатывается
    private float accumulatedProcessingTimeMs = 0f; // Для агрегации времени
    private bool errorOccurred = false; // Флаг ошибки в обработке

    // Добавляем quantizeModel как поле класса, если его нет
    [Header("ML Model Settings")]
    [Tooltip("Квантовать модель до UInt8 для ускорения на некоторых бэкендах (может снизить точность)")]
    [SerializeField] private bool quantizeModel = false;

    // Добавляем modelOutputHeight и modelOutputWidth как поля класса
    private int modelOutputHeight = DEFAULT_LOW_RES_HEIGHT;
    private int modelOutputWidth = DEFAULT_LOW_RES_WIDTH;

    // --- NEW FIELD for selecting model output channel ---
    [Tooltip("Канал выхода модели, используемый для сегментации стен (например, 0, 1, 2, 3)")]
    [SerializeField] private int wallOutputChannel = 1;
    // --- END OF NEW FIELD ---

    private void Awake()
    {
        Log("[WallSegmentation] Awake_Start", DebugFlags.Initialization);

        // --- ADDED: VALIDATE DEBUG SAVE PATH ---
        ValidateDebugSavePath();
        // --- END OF ADDED ---

        // Initialize texture pools
        texturePool = new TexturePool();
        texture2DPool = new Texture2DPool();
        Log("[WallSegmentation] Texture pools initialized.", DebugFlags.Initialization);

        // Попытка найти ARSession, если он не назначен в инспекторе
        if (arSession == null)
        {
            arSession = FindObjectOfType<ARSession>();
            if (arSession == null)
            {
                LogError("[WallSegmentation] ARSession не найден на сцене!", DebugFlags.Initialization);
            }
        }

        // Попытка найти ARCameraManager
        if (xrOrigin != null && xrOrigin.CameraFloorOffsetObject != null)
        {
            arCameraManager = xrOrigin.CameraFloorOffsetObject.GetComponentInChildren<ARCameraManager>();
        }

        if (arCameraManager == null)
        {
            // Попытка найти через Camera.main, если это AR камера
            if (Camera.main != null)
            {
                arCameraManager = Camera.main.GetComponent<ARCameraManager>();
            }
        }

        if (arCameraManager == null)
        {
            // Крайний случай: поиск по всей сцене
            arCameraManager = FindObjectOfType<ARCameraManager>();
            if (arCameraManager == null)
            {
                LogError("[WallSegmentation] ARCameraManager не найден! Сегментация не сможет работать.", DebugFlags.Initialization);
            }
            else
            {
                Log("[WallSegmentation] ARCameraManager найден через FindObjectOfType.", DebugFlags.Initialization);
            }
        }
        else
        {
            Log("[WallSegmentation] ARCameraManager успешно найден и назначен.", DebugFlags.Initialization);
        }

        // Baseline memory usage for leak detection
        baselineMemoryUsage = System.GC.GetTotalMemory(false);

        // Создаем GPU текстуры для постобработки
        CreateGPUPostProcessingTextures();

        // Инициализируем материалы постобработки
        InitializePostProcessingMaterials();

        // Начинаем инициализацию ML модели
        if (!isModelInitialized && !isInitializing)
        {
            StartCoroutine(InitializeMLModel());
        }

        // Запускаем корутины для мониторинга производительности и памяти
        if (enableAdvancedMemoryManagement && memoryCheckInterval > 0)
        {
            StartCoroutine(MonitorMemoryUsage());
        }

        if (enablePerformanceProfiling && performanceLogInterval > 0)
        {
            StartCoroutine(LogPerformanceStats());
        }
    }

    private void OnEnable()
    {
        if (arCameraManager != null)
        {
            arCameraManager.frameReceived += OnCameraFrameReceived;
            Log("[WallSegmentation] Подписался на ARCameraManager.frameReceived.", DebugFlags.Initialization);
        }
        else
        {
            LogWarning("[WallSegmentation] ARCameraManager не найден в OnEnable. Не могу подписаться на события кадра.", DebugFlags.Initialization);
        }
    }

    private void OnDisable()
    {
        if (arCameraManager != null)
        {
            arCameraManager.frameReceived -= OnCameraFrameReceived;
            Log("[WallSegmentation] Отписался от ARCameraManager.frameReceived.", DebugFlags.Initialization);
        }
    }

    private void Start()
    {
        if (arCameraManager == null)
        {
            LogError("[WallSegmentation] ARCameraManager не доступен в Start! Проверьте инициализацию в Awake.", DebugFlags.Initialization);
            // Можно добавить дополнительную логику здесь, например, попытаться найти его снова или отключить компонент
        }

        // Инициализируем систему профилирования производительности
        if (enablePerformanceProfiling)
        {
            processingStopwatch = new System.Diagnostics.Stopwatch();
            Log($"[WallSegmentation] Инициализирована система отслеживания производительности. Целевое время: {targetProcessingTimeMs}ms", DebugFlags.Performance);
        }

        // Устанавливаем базовую линию использования памяти
        baselineMemoryUsage = System.GC.GetTotalMemory(false);

        // Создаем GPU текстуры для постобработки
        CreateGPUPostProcessingTextures();

        // Инициализируем материалы постобработки
        InitializePostProcessingMaterials();

        // Начинаем инициализацию ML модели
        if (!isModelInitialized && !isInitializing)
        {
            StartCoroutine(InitializeMLModel());
        }

        // Запускаем корутины для мониторинга производительности и памяти
        if (enableAdvancedMemoryManagement && memoryCheckInterval > 0)
        {
            StartCoroutine(MonitorMemoryUsage());
        }

        if (enablePerformanceProfiling && performanceLogInterval > 0)
        {
            StartCoroutine(LogPerformanceStats());
        }
    }

    /// <summary>
    /// Инициализирует материалы для постобработки
    /// </summary>
    private void InitializePostProcessingMaterials()
    {
        try
        {
            // Проверяем и создаем материалы для постобработки
            if (enableGaussianBlur && gaussianBlurMaterial != null)
            {
                if (gaussianBlurMaterial.shader.name.Contains("SegmentationPostProcess"))
                {
                    Log("[WallSegmentation] gaussianBlurMaterial использует корректный шейдер Hidden/SegmentationPostProcess", DebugFlags.Initialization);
                }
                else
                {
                    LogWarning($"[WallSegmentation] gaussianBlurMaterial использует неожиданный шейдер: {gaussianBlurMaterial.shader.name}", DebugFlags.Initialization);
                }
            }

            if (enableSharpen && sharpenMaterial != null)
            {
                if (sharpenMaterial.shader.name.Contains("SegmentationPostProcess"))
                {
                    Log("[WallSegmentation] sharpenMaterial использует корректный шейдер Hidden/SegmentationPostProcess", DebugFlags.Initialization);
                }
                else
                {
                    LogWarning($"[WallSegmentation] sharpenMaterial использует неожиданный шейдер: {sharpenMaterial.shader.name}", DebugFlags.Initialization);
                }
            }

            if (enableContrast && contrastMaterial != null)
            {
                if (contrastMaterial.shader.name.Contains("SegmentationPostProcess"))
                {
                    Log("[WallSegmentation] contrastMaterial использует корректный шейдер Hidden/SegmentationPostProcess", DebugFlags.Initialization);
                }
                else
                {
                    LogWarning($"[WallSegmentation] contrastMaterial использует неожиданный шейдер: {contrastMaterial.shader.name}", DebugFlags.Initialization);
                }
            }
        }
        catch (System.Exception e)
        {
            LogError($"[WallSegmentation] Ошибка инициализации материалов постобработки: {e.Message}", DebugFlags.Initialization);
        }
    }

    /// <summary>
    /// Корутина для инициализации ML модели
    /// </summary>
    private IEnumerator InitializeMLModel()
    {
        if (isInitializing || isModelInitialized)
        {
            Log($"[InitializeMLModel] Пропускаем инициализацию: isInitializing={isInitializing}, isModelInitialized={isModelInitialized}", DebugFlags.Initialization);
            yield break;
        }

        Log("⏳ Начинаем инициализацию ML модели...", DebugFlags.Initialization);
        isInitializing = true;
        errorOccurred = false;
        lastErrorMessage = null;
        float startTime = Time.realtimeSinceStartup;

        // --- MODIFIED: Set channel for textureTransformToLowRes based on wallOutputChannel ---
        // TextureTransform is a struct, cannot be null. We create it with the specified channel.
        // textureTransformToLowRes = new Unity.Sentis.TextureTransform(channel: wallOutputChannel);
        // Log($"[InitializeMLModel] Установлен канал для TextureTransform: {wallOutputChannel}", DebugFlags.Initialization);
        // --- END OF MODIFIED ---

        string fullModelPath = GetModelPath();
        if (string.IsNullOrEmpty(fullModelPath))
        {
            HandleInitializationError("Путь к файлу модели не указан или файл не найден.");
            yield break;
        }
        Log($"[WallSegmentation] 📁 Загружаем модель из: {fullModelPath}", DebugFlags.Initialization);
        yield return StartCoroutine(LoadModel(fullModelPath));

        if (runtimeModel == null)
        {
            HandleInitializationError($"Не удалось загрузить модель из {fullModelPath}. runtimeModel is null. Сообщение: {lastErrorMessage}");
            yield break;
        }

        // BackendType backend = (BackendType)selectedBackend; // Sentis 0.x - 1.x
        Unity.Sentis.BackendType backend = (Unity.Sentis.BackendType)selectedBackend; // Sentis 2.x

        Log($"Original selectedBackend from Inspector: {selectedBackend} ({(int)selectedBackend})", DebugFlags.Initialization);
        Log($"[WallSegmentation] ⚙️ Создаем Worker с бэкендом: {backend}", DebugFlags.Initialization);

        try
        {
            if (quantizeModel)
            {
                Log("[WallSegmentation] ⚖️ Попытка квантования модели до UInt8...", DebugFlags.Initialization);
                try
                {
                    // Unity.Sentis.Transformations.ModelQuantizer.QuantizeWeights(runtimeModel, आठ(8)); // Sentis 1.x
                    // Для Sentis 2.x, ModelTransformations.QuantizeModelWeights(runtimeModel); или аналогичный API
                    // Пока нет прямого аналога простого QuantizeWeights в 2.x, будем использовать модель как есть,
                    // или пользователь должен предоставить уже квантованную модель.
                    // Здесь можно добавить вызов ModelTransformations, если доступно и требуется.
                    Log("[WallSegmentation] ✅ Квантование модели до UInt8 успешно завершено (или пропущено для Sentis 2.x).", DebugFlags.Initialization);
                }
                catch (Exception e)
                {
                    LogWarning("[WallSegmentation] Ошибка при квантовании модели: {e.Message}. Используем неквантованную модель.");
                }
            }

            // worker = WorkerFactory.CreateWorker(backend, runtimeModel); // Sentis 0.x
            // worker = WorkerFactory.CreateWorker(runtimeModel, backend); // Sentis 1.x
            worker = (Worker)SentisCompat.CreateWorker(runtimeModel, (int)backend);


            if (worker == null)
            {
                HandleInitializationError($"Failed to create Sentis Worker with backend {backend} (result was null after cast)");
                yield break;
            }

            // Configure TextureTransform to take the specified wallOutputChannel from the tensor
            // and map it to the Red channel of the output RenderTexture.
            // Other channels of the RenderTexture (G, B, A) will be 0 unless explicitly set.
            textureTransformToLowRes = new Unity.Sentis.TextureTransform()
                                            .SetChannelSwizzle(Unity.Sentis.Channel.R, wallOutputChannel) // Use configured channel for Red
                                                                                                          // .SetChannelSwizzle(Unity.Sentis.Channel.G, SomeOtherChannelIfNeeded) // Example for Green
                                                                                                          // .SetChannelSwizzle(Unity.Sentis.Channel.B, ...) // Example for Blue
                                                                                                          // .SetChannelSwizzle(Unity.Sentis.Channel.A, ...) // Example for Alpha
                                            .SetBroadcastChannels(false); // Ensures only specified channels are written, others default to 0

            Log($"[InitializeMLModel] Configured TextureTransform to map tensor channel {wallOutputChannel} to Texture's Red channel.", DebugFlags.Initialization);
            Log($"Created worker with backend: {backend}", DebugFlags.Initialization);
        }
        catch (System.Exception e)
        {
            HandleInitializationError($"Не удалось создать Worker: {e.Message}\nТрассировка: {e.StackTrace}");
            yield break;
        }

        if (worker == null)
        {
            HandleInitializationError("Не удалось создать Worker (worker is null после SentisCompat.CreateWorker).");
            yield break;
        }

        // Шаг 3.5: Инициализация m_LowResMask с РАЗМЕРАМИ ПО УМОЛЧАНИЮ.
        // Реальные размеры будут определены после первого инференса.
        // Это сделано потому, что runtimeModel.outputs[0].shape может быть недоступен или некорректен до первого выполнения.
        modelOutputHeight = DEFAULT_LOW_RES_HEIGHT; // Default - используется поле класса
        modelOutputWidth = DEFAULT_LOW_RES_WIDTH;   // Default - используется поле класса

        EnsureLowResMask(modelOutputWidth, modelOutputHeight); // Создаем с дефолтными размерами
        Log($"[WallSegmentation] m_LowResMask инициализирован с ВРЕМЕННЫМИ размерами: {modelOutputWidth}x{modelOutputHeight}. Реальные размеры будут определены после первого инференса.", DebugFlags.Initialization | DebugFlags.TensorProcessing);

        // Определение размеров входа модели (это обычно работает)
        try
        {
            var inputs = runtimeModel.inputs;
            if (inputs != null && inputs.Count > 0)
            {
                var inputInfo = inputs[0];
                // Unity.Sentis.TensorShape inputShape = inputInfo.shape; // CS0266
                // Пытаемся получить статическую форму, если возможно
                Unity.Sentis.TensorShape staticShape;
                try
                {
                    staticShape = inputInfo.shape.ToTensorShape();
                }
                catch (System.Exception) // Если форма динамическая и не может быть преобразована // MODIFIED: Unity.Sentis.SentisException to System.Exception
                {
                    LogWarning($"[WallSegmentation] Входная форма модели ('{inputInfo.name}') является динамической и не может быть преобразована в TensorShape. Используем форму по умолчанию для определения размеров входа.", DebugFlags.Initialization);
                    // Пытаемся получить хотя бы количество измерений для базовой проверки
                    if (inputInfo.shape.rank != -1 && inputInfo.shape.rank >= 4)
                    {
                        // Если есть ранг, но размеры динамические, мы все еще не можем их использовать напрямую здесь.
                        // Оставляем размеры по умолчанию.
                        LogWarning($"[WallSegmentation] Динамическая форма имеет ранг {inputInfo.shape.rank}. Размеры будут установлены по умолчанию.", DebugFlags.Initialization);
                    }
                    staticShape = new Unity.Sentis.TensorShape(1, 3, DEFAULT_WARMUP_HEIGHT, DEFAULT_WARMUP_WIDTH); // Заглушка
                }


                if (staticShape != null && staticShape.rank >= 4) // TensorShape может быть null или иметь некорректный ранг
                {
                    int[] dimensions = staticShape.ToArray();
                    if (dimensions != null && dimensions.Length >= 4) // Ожидаем NCHW
                    {
                        sentisModelInputHeight = dimensions[2];
                        sentisModelInputWidth = dimensions[3];
                        Log($"[WallSegmentation] 📐 Размеры ВХОДА модели (NCHW): {sentisModelInputWidth}x{sentisModelInputHeight}", DebugFlags.Initialization);
                    }
                    else
                    {
                        LogWarning($"[WallSegmentation] Не удалось определить размеры ВХОДА модели из inputInfo.shape ({dimensions?.Length} измерений). Используются значения по умолчанию для прогрева: {DEFAULT_WARMUP_WIDTH}x{DEFAULT_WARMUP_HEIGHT}.", DebugFlags.Initialization);
                        sentisModelInputHeight = DEFAULT_WARMUP_HEIGHT;
                        sentisModelInputWidth = DEFAULT_WARMUP_WIDTH;
                    }
                }
                else
                {
                    LogWarning($"[WallSegmentation] inputInfo.shape is null. Используются значения по умолчанию для прогрева: {DEFAULT_WARMUP_WIDTH}x{DEFAULT_WARMUP_HEIGHT}.", DebugFlags.Initialization);
                    sentisModelInputHeight = DEFAULT_WARMUP_HEIGHT;
                    sentisModelInputWidth = DEFAULT_WARMUP_WIDTH;
                }
            }
            else
            {
                LogWarning($"[WallSegmentation] runtimeModel.inputs is null or empty. Используются значения по умолчанию для прогрева: {DEFAULT_WARMUP_WIDTH}x{DEFAULT_WARMUP_HEIGHT}.", DebugFlags.Initialization);
                sentisModelInputHeight = DEFAULT_WARMUP_HEIGHT;
                sentisModelInputWidth = DEFAULT_WARMUP_WIDTH;
            }
        }
        catch (System.Exception e)
        {
            LogWarning($"[WallSegmentation] Не удалось определить размеры ВХОДА модели: {e.Message}. Используются значения по умолчанию для прогрева: {DEFAULT_WARMUP_WIDTH}x{DEFAULT_WARMUP_HEIGHT}.", DebugFlags.Initialization);
            sentisModelInputHeight = DEFAULT_WARMUP_HEIGHT;
            sentisModelInputWidth = DEFAULT_WARMUP_WIDTH;
        }

        InitializeTextures(); // Инициализируем остальные текстуры (segmentationMaskTexture и т.д.)

        isModelInitialized = true;
        isInitializing = false;

        OnModelInitialized?.Invoke();
        Log("[WallSegmentation] ✅ ML модель успешно инициализирована! Ожидание первого инференса для определения точных размеров выхода.", DebugFlags.Initialization);

        // --- WARM-UP CODE START ---
        if (worker != null && isModelInitialized)
        {
            Log("[WallSegmentation] Начинаем прогрев TextureToTensor...", DebugFlags.Initialization);
            try
            {
                int warmupWidth = (sentisModelInputWidth > 0) ? sentisModelInputWidth : DEFAULT_WARMUP_WIDTH;
                int warmupHeight = (sentisModelInputHeight > 0) ? sentisModelInputHeight : DEFAULT_WARMUP_HEIGHT;

                Texture2D warmupTexture = new Texture2D(warmupWidth, warmupHeight, TextureFormat.RGBA32, false);
                Log($"[WallSegmentation] Прогрев: создана warmupTexture {warmupWidth}x{warmupHeight}", DebugFlags.Initialization);

                // object warmupInputTensorObj = SentisCompat.TextureToTensor(warmupTexture, warmupWidth, warmupHeight, 4); // CS1501
                // Предполагаем, что SentisCompat.TextureToTensor ожидает Texture, а не Texture2D, и без лишних параметров для базовой версии
                object warmupInputTensorObj = SentisCompat.TextureToTensor(warmupTexture);
                if (warmupInputTensorObj is Tensor warmupInputTensor && warmupInputTensor != null)
                {
                    Log("[WallSegmentation] Прогрев: TextureToTensor успешно выполнен.", DebugFlags.Initialization);
                    if (warmupInputTensor is IDisposable disposableWarmupTensor) disposableWarmupTensor.Dispose();
                }
                else
                {
                    LogWarning("[WallSegmentation] Прогрев: TextureToTensor не вернул валидный тензор.", DebugFlags.Initialization);
                }
                DestroyImmediate(warmupTexture);
                Log("[WallSegmentation] Прогрев TextureToTensor завершен.", DebugFlags.Initialization);
            }
            catch (Exception e)
            {
                LogWarning($"[WallSegmentation] Ошибка во время прогрева TextureToTensor: {e.Message}", DebugFlags.Initialization);
            }
        }
        // --- WARM-UP CODE END ---
        // Прогрев Execute() будет совмещен с первым реальным инференсом, 
        // т.к. нам нужен outputTensor для определения размеров.
    }

    /// <summary>
    /// Обрабатывает ошибку инициализации
    /// </summary>
    private void HandleInitializationError(string errorMessage)
    {
        // isInitializationFailed = false; // No longer used
        isInitializing = false;
        lastErrorMessage = errorMessage;
        LogError($"[WallSegmentation] ❌ Ошибка инициализации модели: {errorMessage}", DebugFlags.Initialization);

        // Включаем заглушку для продолжения работы
        Log("[WallSegmentation] 🔄 Активируем заглушку сегментации для продолжения работы", DebugFlags.Initialization);
    }

    /// <summary>
    /// Извлекает размерности формы тензора через reflection
    /// </summary>
    private int[] GetShapeDimensions(object shape)
    {
        if (shape == null) return null;

        try
        {
            // Если shape - это массив int[], возвращаем его напрямую
            if (shape is int[] shapeArray)
            {
                return shapeArray;
            }

            // Пробуем получить массив через свойство или метод
            var shapeType = shape.GetType();

            // Ищем свойство dimensions или shape
            var dimensionsProperty = shapeType.GetProperty("dimensions") ?? shapeType.GetProperty("shape");
            if (dimensionsProperty != null)
            {
                var dimensions = dimensionsProperty.GetValue(shape);
                if (dimensions is int[] dimensionsArray)
                {
                    return dimensionsArray;
                }
            }

            // Ищем метод ToArray()
            var toArrayMethod = shapeType.GetMethod("ToArray", new Type[0]);
            if (toArrayMethod != null)
            {
                var result = toArrayMethod.Invoke(shape, null);
                if (result is int[] resultArray)
                {
                    return resultArray;
                }
            }

            LogWarning($"[WallSegmentation] Не удалось извлечь размерности из объекта типа: {shapeType.Name}", DebugFlags.Initialization);
            return null;
        }
        catch (System.Exception e)
        {
            LogWarning($"[WallSegmentation] Ошибка при извлечении размерностей: {e.Message}", DebugFlags.Initialization);
            return null;
        }
    }

    /// <summary>
    /// Определяет путь к файлу модели
    /// </summary>
    private string GetModelPath()
    {
        string[] possiblePaths = new string[]
        {
                Path.Combine(Application.streamingAssetsPath, modelPath),
                Path.Combine(Application.streamingAssetsPath, "segformer-model.sentis"),
                Path.Combine(Application.streamingAssetsPath, "model.sentis"),
                Path.Combine(Application.streamingAssetsPath, "model.onnx")
        };

        foreach (string path in possiblePaths)
        {
            if (File.Exists(path))
            {
                Log($"[WallSegmentation] 🎯 Найден файл модели: {path}", DebugFlags.Initialization);
                return path;
            }
        }

        LogError("[WallSegmentation] ❌ Не найден ни один файл модели в StreamingAssets", DebugFlags.Initialization);
        return null;
    }

    /// <summary>
    /// Корутина для загрузки модели
    /// </summary>
    private IEnumerator LoadModel(string filePath)
    {
        string fileUrl = PathToUrl(filePath);
        if (string.IsNullOrEmpty(fileUrl))
        {
            LogError($"[WallSegmentation] ❌ Не удалось преобразовать путь к файлу в URL: {filePath}", DebugFlags.Initialization);
            runtimeModel = null;
            yield break;
        }

        Log($"[WallSegmentation] 🌐 Загрузка модели по URL: {fileUrl}", DebugFlags.Initialization);
        UnityWebRequest www = UnityWebRequest.Get(fileUrl);
        yield return www.SendWebRequest();

        if (www.result == UnityWebRequest.Result.Success)
        {
            byte[] modelData = www.downloadHandler.data;
            if (modelData == null || modelData.Length == 0)
            {
                LogError($"[WallSegmentation] ❌ Загруженные данные модели пусты для URL: {fileUrl}", DebugFlags.Initialization);
                runtimeModel = null;
                www.Dispose();
                yield break;
            }

            Log($"[WallSegmentation] ✅ Данные модели успешно загружены, размер: {modelData.Length} байт из {fileUrl}", DebugFlags.Initialization);
            try
            {
                using (var ms = new MemoryStream(modelData))
                {
                    runtimeModel = ModelLoader.Load(ms);
                }

                if (runtimeModel != null)
                {
                    Log($"[WallSegmentation] ✅ Модель успешно загружена из MemoryStream для {Path.GetFileName(filePath)}", DebugFlags.Initialization);
                }
                else
                {
                    LogError($"[WallSegmentation] ❌ ModelLoader.Load(MemoryStream) вернул null для {Path.GetFileName(filePath)}.", DebugFlags.Initialization);
                }
            }
            catch (System.Exception e)
            {
                LogError($"[WallSegmentation] ❌ Исключение при ModelLoader.Load(MemoryStream) для {Path.GetFileName(filePath)}: {e.Message}\\nStackTrace: {e.StackTrace}", DebugFlags.Initialization);
                runtimeModel = null;
            }
        }
        else
        {
            LogError($"[WallSegmentation] ❌ Ошибка UnityWebRequest при загрузке {fileUrl}: {www.error}", DebugFlags.Initialization);
            runtimeModel = null;
        }
        www.Dispose();
    }

    // Helper to convert file path to URL, especially for StreamingAssets
    private string PathToUrl(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        if (path.StartsWith("http://") || path.StartsWith("https://") || path.StartsWith("file://"))
        {
            return path;
        }

        if (Application.platform == RuntimePlatform.Android)
        {
            return $"file://{path}";
        }
        else if (Application.platform == RuntimePlatform.IPhonePlayer)
        {
            return $"file://{path}";
        }
        else
        {
            string absolutePath = path.Replace("\\", "/");
            if (Application.platform == RuntimePlatform.WindowsEditor || Application.platform == RuntimePlatform.WindowsPlayer)
            {
                return $"file:///{absolutePath}";
            }
            else
            {
                return $"file://{absolutePath}";
            }
        }
    }

    /// <summary>
    /// Инициализирует текстуры сегментации
    /// </summary>
    private void InitializeTextures()
    {
        Log($"[InitializeTextures] Начало инициализации текстур с разрешением {currentResolution.x}x{currentResolution.y}.", DebugFlags.Initialization);
        processingStopwatch.Restart();

        bool resolutionChanged = false;
        if (segmentationMaskTexture != null && (segmentationMaskTexture.width != currentResolution.x || segmentationMaskTexture.height != currentResolution.y))
        {
            Log($"[InitializeTextures] Разрешение изменилось с {segmentationMaskTexture.width}x{segmentationMaskTexture.height} на {currentResolution.x}x{currentResolution.y}. Пересоздаем текстуры.", DebugFlags.Initialization, LogLevel.Warning);
            texturePool.ReleaseTexture(segmentationMaskTexture);
            segmentationMaskTexture = null;
            resolutionChanged = true;
        }

        if (segmentationMaskTexture == null)
        {
            segmentationMaskTexture = texturePool.GetTexture(currentResolution.x, currentResolution.y, RenderTextureFormat.ARGB32);
            segmentationMaskTexture.name = "SegmentationMask_Main";
            segmentationMaskTexture.enableRandomWrite = false;
            segmentationMaskTexture.filterMode = FilterMode.Bilinear;
            if (!segmentationMaskTexture.IsCreated())
            {
                segmentationMaskTexture.Create();
            }
        }
        else
        {
            segmentationMaskTexture.enableRandomWrite = false;
            segmentationMaskTexture.filterMode = FilterMode.Bilinear;
        }
        ClearRenderTexture(segmentationMaskTexture, Color.clear); // Clear after create/property set
                                                                  // TrackResourceCreation was here, moved after ClearRenderTexture if that makes more sense or keep as is.
                                                                  // Debug.Log for segmentationMaskTexture was here

        // Вызываем CreateGPUPostProcessingTextures для инициализации/обновления остальных временных текстур
        CreateGPUPostProcessingTextures(); // This will also use the pool for tempMask1, tempMask2 etc.

        // Re-Log the main textures state AFTER all potential modifications
        if ((debugFlags & DebugFlags.Initialization) != 0)
        {
            Log($"[InitializeTextures] segmentationMaskTexture: {segmentationMaskTexture.width}x{segmentationMaskTexture.height}, RW:{segmentationMaskTexture.enableRandomWrite}, Filter:{segmentationMaskTexture.filterMode}", DebugFlags.Initialization);
            Log($"[InitializeTextures] m_LowResMask: {m_LowResMask.width}x{m_LowResMask.height}, RW:{m_LowResMask.enableRandomWrite}, Filter:{m_LowResMask.filterMode}", DebugFlags.Initialization);
        }

        Debug.Log($"[WallSegmentation] Пересозданы/обновлены текстуры с разрешением ({currentResolution.x}, {currentResolution.y})");

        if (resolutionChanged || tempOutputMask == null)
        {
            if (tempOutputMask != null) texturePool.ReleaseTexture(tempOutputMask);
            tempOutputMask = texturePool.GetTexture(currentResolution.x, currentResolution.y, RenderTextureFormat.ARGB32);
            tempOutputMask.name = "Temp_OutputMask";
            tempOutputMask.filterMode = FilterMode.Bilinear; // Also set for temp mask
            tempOutputMask.wrapMode = TextureWrapMode.Clamp;
            if (!tempOutputMask.IsCreated()) tempOutputMask.Create();
        }

        // Текстуры для CPU постобработки (если включена)
        if (enablePostProcessing || enableGaussianBlur || enableSharpen || enableContrast) // Removed enableMorphologicalOps
        {
            if (resolutionChanged || tempMask1 == null)
            {
                if (tempMask1 != null) texturePool.ReleaseTexture(tempMask1);
                tempMask1 = texturePool.GetTexture(currentResolution.x, currentResolution.y, RenderTextureFormat.ARGB32);
                tempMask1.name = "Temp_PostProcessMask1";
                tempMask1.filterMode = FilterMode.Bilinear; // Also set for temp mask
                if (!tempMask1.IsCreated()) tempMask1.Create();
            }
            if (resolutionChanged || tempMask2 == null)
            {
                if (tempMask2 != null) texturePool.ReleaseTexture(tempMask2);
                tempMask2 = texturePool.GetTexture(currentResolution.x, currentResolution.y, RenderTextureFormat.ARGB32);
                tempMask2.name = "Temp_PostProcessMask2";
                tempMask2.filterMode = FilterMode.Bilinear; // Also set for temp mask
                if (!tempMask2.IsCreated()) tempMask2.Create();
            }
        }

        if (resolutionChanged || previousMask == null)
        {
            if (previousMask != null) texturePool.ReleaseTexture(previousMask);
            previousMask = texturePool.GetTexture(currentResolution.x, currentResolution.y, RenderTextureFormat.ARGB32);
            previousMask.name = "PreviousMask_Temporal";
            previousMask.filterMode = FilterMode.Bilinear; // Also set for temp mask
            if (!previousMask.IsCreated()) previousMask.Create();
            ClearRenderTexture(previousMask, Color.clear); // Очищаем, чтобы избежать артефактов при первой интерполяции
        }

        if (resolutionChanged || interpolatedMask == null)
        {
            if (interpolatedMask != null) texturePool.ReleaseTexture(interpolatedMask);
            interpolatedMask = texturePool.GetTexture(currentResolution.x, currentResolution.y, RenderTextureFormat.ARGB32);
            interpolatedMask.name = "InterpolatedMask_Final";
            interpolatedMask.filterMode = FilterMode.Bilinear; // Also set for temp mask
            if (!interpolatedMask.IsCreated()) interpolatedMask.Create();
            ClearRenderTexture(interpolatedMask, Color.clear);
        }
    }

    /// <summary>
    /// Получает среднее время обработки сегментации в миллисекундах
    /// </summary>
    public float GetAverageProcessingTimeMs()
    {
        if (processedFrameCount == 0) return 0f;
        return (totalProcessingTime / processedFrameCount) * 1000f;
    }

    /// <summary>
    /// Получает текущее разрешение обработки
    /// </summary>
    public Vector2Int GetCurrentResolution()
    {
        return currentResolution;
    }

    /// <summary>
    /// Получает последнюю оценку качества маски
    /// </summary>
    public float GetLastQualityScore()
    {
        return lastQualityScore;
    }

    /// <summary>
    /// Устанавливает адаптивное разрешение на основе целевой производительности
    /// </summary>
    public void SetAdaptiveResolution(bool enabled)
    {
        adaptiveResolution = enabled;
        if (enabled)
        {
            Log($"[WallSegmentation] Адаптивное разрешение включено. Текущее: {currentResolution}", DebugFlags.ExecutionFlow);
        }
        else
        {
            Log($"[WallSegmentation] Адаптивное разрешение отключено. Фиксированное: {inputResolution}", DebugFlags.ExecutionFlow);
            currentResolution = inputResolution;
        }
    }

    /// <summary>
    /// Устанавливает фиксированное разрешение обработки
    /// </summary>
    public void SetFixedResolution(Vector2Int resolution)
    {
        adaptiveResolution = false;
        currentResolution = resolution;
        inputResolution = resolution;
        Log($"[WallSegmentation] Установлено фиксированное разрешение: {resolution}", DebugFlags.ExecutionFlow);

        // Пересоздаем текстуры с новым разрешением
        CreateGPUPostProcessingTextures();
    }

    /// <summary>
    /// Устанавливает фиксированное разрешение обработки (перегрузка для двух int значений)
    /// </summary>
    public void SetFixedResolution(int width, int height)
    {
        SetFixedResolution(new Vector2Int(width, height));
    }

    /// <summary>
    /// Анализирует качество маски и обновляет lastQualityScore
    /// </summary>
    private float AnalyzeMaskQuality(RenderTexture mask)
    {
        if (mask == null) return 0f;

        try
        {
            // Простой анализ качества на основе заполненности маски
            RenderTexture.active = mask;
            Texture2D tempTexture = new Texture2D(mask.width, mask.height, TextureFormat.RGBA32, false);
            tempTexture.ReadPixels(new Rect(0, 0, mask.width, mask.height), 0, 0);
            tempTexture.Apply();
            RenderTexture.active = null;

            Color[] pixels = tempTexture.GetPixels();
            int validPixels = 0;
            int totalPixels = pixels.Length;

            for (int i = 0; i < totalPixels; i++)
            {
                if (pixels[i].r > 0.1f) // Считаем пиксель валидным если его красный канал > 0.1
                {
                    validPixels++;
                }
            }

            DestroyImmediate(tempTexture);

            float quality = (float)validPixels / totalPixels;
            lastQualityScore = Mathf.Clamp01(quality);
            return lastQualityScore;
        }
        catch (System.Exception e)
        {
            LogError($"[WallSegmentation] Ошибка анализа качества маски: {e.Message}", DebugFlags.TensorAnalysis);
            lastQualityScore = 0f;
            return 0f;
        }
    }

    /// <summary>
    /// Получает текущее использование памяти текстурами в МБ
    /// </summary>
    public float GetCurrentTextureMemoryUsage()
    {
        int totalBytes = 0;

        // Подсчитываем размер текстур в пуле
        if (texturePool != null)
        {
            totalBytes += texturePool.EstimatePoolSize();
        }

        // Добавляем временные текстуры GPU
        if (tempMask1 != null && tempMask1.IsCreated())
        {
            totalBytes += tempMask1.width * tempMask1.height * 4; // RGBA32
        }
        if (tempMask2 != null && tempMask2.IsCreated())
        {
            totalBytes += tempMask2.width * tempMask2.height * 4;
        }

        // Добавляем интерполяционные текстуры
        if (previousMask != null && previousMask.IsCreated())
        {
            totalBytes += previousMask.width * previousMask.height * 4;
        }
        if (interpolatedMask != null && interpolatedMask.IsCreated())
        {
            totalBytes += interpolatedMask.width * interpolatedMask.height * 4;
        }

        return totalBytes / 1024 / 1024; // Конвертируем в MB
    }

    /// <summary>
    /// Детекция утечек памяти
    /// </summary>
    private void DetectMemoryLeaks(float memoryGrowthMB, int texturePoolSizeMB)
    {
        bool potentialLeak = false;
        string leakReason = "";

        // Проверка 1: Рост памяти больше 150MB
        if (memoryGrowthMB > 150)
        {
            potentialLeak = true;
            leakReason += $"Excessive memory growth: {memoryGrowthMB:F1}MB; ";
        }

        // Проверка 2: Размер пула текстур превышает лимит
        if (texturePoolSizeMB > maxTexturePoolSizeMB)
        {
            potentialLeak = true;
            leakReason += $"Texture pool too large: {texturePoolSizeMB}MB; ";
        }

        // Проверка 3: Дисбаланс создания/освобождения текстур
        int textureBalance = totalTexturesCreated - totalTexturesReleased;
        if (textureBalance > 20)
        {
            potentialLeak = true;
            leakReason += $"Texture leak: {textureBalance} textures not released; ";
        }

        if (potentialLeak)
        {
            LogWarning($"[MemoryManager] ⚠️ Potential memory leak detected: {leakReason}", DebugFlags.Performance);

            if (enableAutomaticCleanup)
            {
                Log("[MemoryManager] Attempting automatic cleanup...", DebugFlags.Performance);
                PerformAutomaticCleanup();
            }
        }
    }

    /// <summary>
    /// Выполняет автоматическую очистку памяти
    /// </summary>
    private void PerformAutomaticCleanup()
    {
        try
        {
            Log("[MemoryManager] 🧹 Performing automatic memory cleanup...", DebugFlags.Performance);

            // Принудительно очищаем пулы текстур
            if (texturePool != null)
            {
                int releasedTextures = texturePool.ForceCleanup();
                Log($"[MemoryManager] Released {releasedTextures} pooled textures", DebugFlags.Performance);
            }

            if (texture2DPool != null)
            {
                int released2D = texture2DPool.ForceCleanup();
                Log($"[MemoryManager] Released {released2D} 2D textures", DebugFlags.Performance);
            }

            // Пересоздаем временные текстуры GPU если они слишком большие
            if (tempMask1 != null && (tempMask1.width > currentResolution.x * 1.5f || tempMask1.height > currentResolution.y * 1.5f))
            {
                CreateGPUPostProcessingTextures();
                Log("[MemoryManager] Recreated GPU post-processing textures", DebugFlags.Performance);
            }

            // Принудительная сборка мусора
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // Обновляем baseline после очистки
            baselineMemoryUsage = GC.GetTotalMemory(false);

            Log($"[MemoryManager] ✅ Cleanup completed. New baseline: {baselineMemoryUsage / 1024 / 1024}MB", DebugFlags.Performance);
        }
        catch (System.Exception e)
        {
            LogError($"[MemoryManager] Ошибка автоматической очистки: {e.Message}", DebugFlags.Performance);
        }
    }

    /// <summary>
    /// Создает GPU текстуры для постобработки
    /// </summary>
    private void CreateGPUPostProcessingTextures()
    {
        int width = currentResolution.x;
        int height = currentResolution.y;

        // Освобождаем старые текстуры через GetOrCreateRenderTexture, который может их пересоздать или отдать из пула
        // tempMask1
        GetOrCreateRenderTexture(ref tempMask1, width, height, RenderTextureFormat.ARGB32, FilterMode.Bilinear, "PostProcessing_Temp1", false);
        // TrackResourceCreation("tempMask1_GPU_Recreate"); // Tracking handled by GetOrCreateRenderTexture

        // tempMask2
        GetOrCreateRenderTexture(ref tempMask2, width, height, RenderTextureFormat.ARGB32, FilterMode.Bilinear, "PostProcessing_Temp2", false);
        // TrackResourceCreation("tempMask2_GPU_Recreate"); // Tracking handled by GetOrCreateRenderTexture

        if (enableTemporalInterpolation && temporalBlendMaterial != null)
        {
            // previousMask
            GetOrCreateRenderTexture(ref previousMask, width, height, RenderTextureFormat.ARGB32, FilterMode.Bilinear, "PostProcessing_PreviousMask", false);
            // TrackResourceCreation("previousMask_GPU_Recreate"); // Tracking handled by GetOrCreateRenderTexture

            // interpolatedMask - THIS ONE NEEDS enableRandomWrite = true
            GetOrCreateRenderTexture(ref interpolatedMask, width, height, RenderTextureFormat.ARGB32, FilterMode.Bilinear, "PostProcessing_InterpolatedMask", true);
            // TrackResourceCreation("interpolatedMask_GPU_Recreate"); // Tracking handled by GetOrCreateRenderTexture
        }
        else // Если временная интерполяция отключена, убедимся, что текстуры освобождены
        {
            ReleaseRenderTexture(ref previousMask); // Release if not needed
            // TrackResourceRelease("previousMask_GPU_Disabled"); // ReleaseRenderTexture should handle tracking if necessary or this is a separate logic
            ReleaseRenderTexture(ref interpolatedMask); // Release if not needed
            // TrackResourceRelease("interpolatedMask_GPU_Disabled");
        }
        // Clear textures after creation/recreation
        if (tempMask1 != null && tempMask1.IsCreated()) ClearRenderTexture(tempMask1, Color.clear);
        if (tempMask2 != null && tempMask2.IsCreated()) ClearRenderTexture(tempMask2, Color.clear);
        if (previousMask != null && previousMask.IsCreated()) ClearRenderTexture(previousMask, Color.clear);
        if (interpolatedMask != null && interpolatedMask.IsCreated()) ClearRenderTexture(interpolatedMask, Color.clear);

    }

    /// <summary>
    /// Трекинг создания ресурсов
    /// </summary>
    private void TrackResourceCreation(string resourceType)
    {
        if (!enableMemoryLeakDetection) return;

        string key = resourceType;
        if (resourceCounts.ContainsKey(key))
        {
            resourceCounts[key]++;
        }
        else
        {
            resourceCounts[key] = 1;
            resourceCreationTimes[key] = Time.realtimeSinceStartup;
        }

        if (resourceType.Contains("Texture"))
        {
            totalTexturesCreated++;
        }
    }

    /// <summary>
    /// Трекинг освобождения ресурсов
    /// </summary>
    private void TrackResourceRelease(string resourceType)
    {
        totalTexturesReleased++;

        if (resourceCounts.ContainsKey(resourceType))
        {
            resourceCounts[resourceType]--;
            if (resourceCounts[resourceType] <= 0)
            {
                resourceCounts.Remove(resourceType);
                resourceCreationTimes.Remove(resourceType);
            }
        }

        if (enableDetailedDebug && (debugFlags & DebugFlags.Performance) != 0)
        {
            Log($"[WallSegmentation] Освобожден ресурс: {resourceType}. Всего освобождено: {totalTexturesReleased}", DebugFlags.Performance);
        }
    }

    /// <summary>
    /// Корутина для мониторинга использования памяти
    /// </summary>
    private IEnumerator MonitorMemoryUsage()
    {
        while (true)
        {
            yield return new WaitForSeconds(memoryCheckInterval);

            try
            {
                // Получаем текущее использование памяти
                long currentMemory = System.GC.GetTotalMemory(false);
                float memoryGrowthMB = (currentMemory - baselineMemoryUsage) / 1024f / 1024f;

                // Получаем размер пула текстур
                int texturePoolSizeMB = texturePool != null ? texturePool.EstimatePoolSize() / 1024 / 1024 : 0;

                // Проверяем на утечки памяти
                DetectMemoryLeaks(memoryGrowthMB, texturePoolSizeMB);

                // Выполняем автоматическую очистку если нужно
                if (enableAutomaticCleanup)
                {
                    PerformAutomaticCleanup();
                }

                if (enableDetailedDebug && (debugFlags & DebugFlags.Performance) != 0)
                {
                    Log($"[WallSegmentation] Память: рост {memoryGrowthMB:F1}MB, пул текстур {texturePoolSizeMB}MB", DebugFlags.Performance);
                }
            }
            catch (System.Exception e)
            {
                LogError($"[WallSegmentation] Ошибка мониторинга памяти: {e.Message}", DebugFlags.Performance);
            }
        }
    }

    /// <summary>
    /// Корутина для логирования статистики производительности
    /// </summary>
    private IEnumerator LogPerformanceStats()
    {
        while (true)
        {
            yield return new WaitForSeconds(performanceLogInterval);

            try
            {
                if (processedFrameCount > 0)
                {
                    float avgProcessingTime = GetAverageProcessingTimeMs();
                    float memoryUsage = GetCurrentTextureMemoryUsage();

                    if (enableDetailedDebug)
                    {
                        Log($"[WallSegmentation] Статистика производительности:" +
                                $"\n  • Обработано кадров: {processedFrameCount}" +
                                $"\n  • Среднее время обработки: {avgProcessingTime:F1}ms" +
                                $"\n  • Текущее разрешение: {currentResolution}" +
                                $"\n  • Использование памяти текстур: {memoryUsage:F1}MB" +
                                $"\n  • Последняя оценка качества: {lastQualityScore:F2}" +
                                $"\n  • Создано текстур: {totalTexturesCreated}" +
                                $"\n  • Освобождено текстур: {totalTexturesReleased}", DebugFlags.Performance);
                    }
                    else
                    {
                        Log($"[WallSegmentation] Производительность: {avgProcessingTime:F1}ms, {processedFrameCount} кадров, {memoryUsage:F1}MB", DebugFlags.Performance);
                    }
                }
            }
            catch (System.Exception e)
            {
                LogError($"[WallSegmentation] Ошибка логирования статистики: {e.Message}", DebugFlags.Performance);
            }
        }
    }

    private void OnCameraFrameReceived(ARCameraFrameEventArgs eventArgs)
    {
        if (!isModelInitialized || isInitializing || worker == null || !enabled || !gameObject.activeInHierarchy)
        {
            return; // Модель еще не готова, занята или компонент выключен
        }

        // Ограничение частоты обработки кадров, если maxSegmentationFPS > 0
        if (maxSegmentationFPS > 0 && Time.time < lastFrameProcessTime + (1.0f / maxSegmentationFPS))
        {
            return;
        }

        if (processingCoroutine != null)
        {
            // Предыдущая обработка еще не завершена, пропускаем этот кадр
            // Это помогает избежать накопления запросов, если обработка медленная
            if ((debugFlags & DebugFlags.ExecutionFlow) != 0) Log("[WallSegmentation] Пропускаем кадр, предыдущая обработка еще идет.", DebugFlags.ExecutionFlow);
            return;
        }

        if (!arCameraManager.TryAcquireLatestCpuImage(out XRCpuImage cpuImage))
        {
            if ((debugFlags & DebugFlags.CameraTexture) != 0) LogError("[WallSegmentation] Не удалось получить CPU изображение с камеры.", DebugFlags.CameraTexture);
            cpuImage.Dispose(); // Убедимся, что XRCpuImage освобождается, даже если она пустая
            return;
        }

        // Запускаем корутину для асинхронной обработки
        processingCoroutine = StartCoroutine(ProcessCameraFrameCoroutine(cpuImage));
        lastFrameProcessTime = Time.time; // Обновляем время последней обработки
    }

    private IEnumerator ProcessCameraFrameCoroutine(XRCpuImage cpuImage)
    {
        if ((debugFlags & DebugFlags.ExecutionFlow) != 0)
            Log("[ProcessCameraFrameCoroutine] Начало обработки кадра.", DebugFlags.ExecutionFlow);

        Texture2D sourceTextureForModel = null;
        XRCpuImage.AsyncConversion request;

        cpuImageConversionStopwatch.Restart();

        int targetWidthForModel;
        int targetHeightForModel;

        // Calculate safe dimensions that don't exceed input image size
        int maxInputWidth = cpuImage.width;
        int maxInputHeight = cpuImage.height;

        int preferredWidth, preferredHeight;

        if (sentisModelWidth > 0 && sentisModelHeight > 0)
        {
            preferredWidth = sentisModelWidth;
            preferredHeight = sentisModelHeight;
        }
        else
        {
            preferredWidth = inputResolution.x;
            preferredHeight = inputResolution.y;
        }

        // Smart scaling: don't exceed input dimensions, maintain aspect ratio if possible
        if (preferredWidth <= maxInputWidth && preferredHeight <= maxInputHeight)
        {
            // Preferred size fits within input, use it
            targetWidthForModel = preferredWidth;
            targetHeightForModel = preferredHeight;
        }
        else
        {
            // Calculate the maximum size while maintaining aspect ratio
            float preferredAspect = (float)preferredWidth / preferredHeight;
            float inputAspect = (float)maxInputWidth / maxInputHeight;

            if (inputAspect > preferredAspect)
            {
                // Input is wider, constrain by height
                targetHeightForModel = Mathf.Min(preferredHeight, maxInputHeight);
                targetWidthForModel = Mathf.RoundToInt(targetHeightForModel * preferredAspect);
                // Ensure width doesn't exceed input
                if (targetWidthForModel > maxInputWidth)
                {
                    targetWidthForModel = maxInputWidth;
                    targetHeightForModel = Mathf.RoundToInt(targetWidthForModel / preferredAspect);
                }
            }
            else
            {
                // Input is taller, constrain by width
                targetWidthForModel = Mathf.Min(preferredWidth, maxInputWidth);
                targetHeightForModel = Mathf.RoundToInt(targetWidthForModel / preferredAspect);
                // Ensure height doesn't exceed input
                if (targetHeightForModel > maxInputHeight)
                {
                    targetHeightForModel = maxInputHeight;
                    targetWidthForModel = Mathf.RoundToInt(targetHeightForModel * preferredAspect);
                }
            }

            // Final safety check
            targetWidthForModel = Mathf.Min(targetWidthForModel, maxInputWidth);
            targetHeightForModel = Mathf.Min(targetHeightForModel, maxInputHeight);

            if ((debugFlags & DebugFlags.CameraTexture) != 0)
                Log($"[ProcessCameraFrameCoroutine] Input size ({maxInputWidth}x{maxInputHeight}) smaller than preferred ({preferredWidth}x{preferredHeight}). Using scaled size: {targetWidthForModel}x{targetHeightForModel}", DebugFlags.CameraTexture, LogLevel.Warning);
        }

        if ((debugFlags & DebugFlags.CameraTexture) != 0)
            Log($"[ProcessCameraFrameCoroutine] Preparing to convert XRCpuImage ({cpuImage.width}x{cpuImage.height}) to {targetWidthForModel}x{targetHeightForModel} for model input.", DebugFlags.CameraTexture);

        var conversionParams = new XRCpuImage.ConversionParams
        {
            inputRect = new RectInt(0, 0, cpuImage.width, cpuImage.height),
            outputDimensions = new Vector2Int(targetWidthForModel, targetHeightForModel), // Use model input dimensions
            outputFormat = TextureFormat.RGBA32, // RGBA32 is common, model might only use RGB
            transformation = XRCpuImage.Transformation.None // Or MirrorY depending on model needs and camera source
        };
        request = cpuImage.ConvertAsync(conversionParams);

        while (!request.status.IsDone())
        {
#if UNITY_EDITOR
            if (Application.isEditor && !UnityEditor.EditorApplication.isPlaying)
            {
                Log("[ProcessCameraFrameCoroutine] Редактор вышел из режима воспроизведения во время асинхронной конвертации.", DebugFlags.ExecutionFlow, LogLevel.Warning);
                cpuImage.Dispose();
                if (sourceTextureForModel != null)
                {
                    texture2DPool.ReleaseTexture(sourceTextureForModel);
                    Log("[ProcessCameraFrameCoroutine] sourceTextureForModel освобождена (конвертация прервана выходом из Play Mode).", DebugFlags.CameraTexture);
                }
                processingCoroutine = null;
                yield break;
            }
#endif
            yield return null;
        }

        cpuImageConversionStopwatch.Stop();
        if ((debugFlags & DebugFlags.Performance) != 0)
        {
            Log($"[PERF] XRCpuImage.ConvertAsync to {targetWidthForModel}x{targetHeightForModel}: {cpuImageConversionStopwatch.Elapsed.TotalMilliseconds:F2}ms (Original: {cpuImage.width}x{cpuImage.height})", DebugFlags.Performance);
        }

        bool cpuImageDisposedInTryBlock = false;

        try
        {
            if (request.status == XRCpuImage.AsyncConversionStatus.Ready)
            {
                // Get or create sourceTextureForModel with model input dimensions
                if (sourceTextureForModel == null || sourceTextureForModel.width != targetWidthForModel || sourceTextureForModel.height != targetHeightForModel)
                {
                    if (sourceTextureForModel != null) texture2DPool.ReleaseTexture(sourceTextureForModel);
                    sourceTextureForModel = texture2DPool.GetTexture(targetWidthForModel, targetHeightForModel, TextureFormat.RGBA32);
                    sourceTextureForModel.name = "WallSegmentation_SourceForModelInput";
                    if ((debugFlags & DebugFlags.CameraTexture) != 0)
                        Log($"[ProcessCameraFrameCoroutine] Создана/пересоздана sourceTextureForModel ({targetWidthForModel}x{targetHeightForModel}) для конвертации.", DebugFlags.CameraTexture);
                }

                var rawTextureData = request.GetData<byte>();
                if (rawTextureData.IsCreated && rawTextureData.Length > 0)
                {
                    sourceTextureForModel.LoadRawTextureData(rawTextureData);
                    sourceTextureForModel.Apply();
                    if ((debugFlags & DebugFlags.CameraTexture) != 0)
                        Log("[ProcessCameraFrameCoroutine] Данные CPU изображения успешно скопированы в sourceTextureForModel.", DebugFlags.CameraTexture);

                    cpuImage.Dispose();
                    cpuImageDisposedInTryBlock = true;
                    if ((debugFlags & DebugFlags.ExecutionFlow) != 0)
                        Log("[ProcessCameraFrameCoroutine] XRCpuImage disposed (сразу после копирования данных).", DebugFlags.ExecutionFlow);

                    StartCoroutine(RunInferenceAndPostProcess(sourceTextureForModel));
                    sourceTextureForModel = null; // Ownership transferred
                }
                else
                {
                    Log("[ProcessCameraFrameCoroutine] Ошибка: rawTextureData не создана или пуста.", DebugFlags.CameraTexture, LogLevel.Error);
                    if (sourceTextureForModel != null)
                    {
                        texture2DPool.ReleaseTexture(sourceTextureForModel);
                        Log("[ProcessCameraFrameCoroutine] sourceTextureForModel освобождена (rawTextureData пуста).", DebugFlags.CameraTexture);
                        sourceTextureForModel = null;
                    }
                    processingCoroutine = null;
                }
            }
            else
            {
                Log($"[ProcessCameraFrameCoroutine] Ошибка конвертации XRCpuImage: {request.status}", DebugFlags.CameraTexture, LogLevel.Error);
                if (sourceTextureForModel != null)
                {
                    texture2DPool.ReleaseTexture(sourceTextureForModel);
                    Log("[ProcessCameraFrameCoroutine] sourceTextureForModel была освобождена после ошибки конвертации.", DebugFlags.CameraTexture);
                    sourceTextureForModel = null;
                }
                processingCoroutine = null;
            }
        }
        catch (Exception e)
        {
            LogError($"[ProcessCameraFrameCoroutine] Exception: {e.Message}\\nStackTrace: {e.StackTrace}", DebugFlags.ExecutionFlow);
            if (sourceTextureForModel != null)
            {
                texture2DPool.ReleaseTexture(sourceTextureForModel);
                LogWarning("[ProcessCameraFrameCoroutine] sourceTextureForModel released in catch block due to exception.", DebugFlags.CameraTexture);
                sourceTextureForModel = null;
            }
            processingCoroutine = null;
        }
        finally
        {
            if (!cpuImageDisposedInTryBlock)
            {
                cpuImage.Dispose();
                if ((debugFlags & DebugFlags.ExecutionFlow) != 0)
                    Log("[ProcessCameraFrameCoroutine] XRCpuImage disposed in finally block (fallback).", DebugFlags.ExecutionFlow);
            }

            if (sourceTextureForModel != null)
            {
                texture2DPool.ReleaseTexture(sourceTextureForModel);
                if ((debugFlags & DebugFlags.CameraTexture) != 0)
                    LogWarning("[ProcessCameraFrameCoroutine] sourceTextureForModel released in final finally block (unexpected state, error likely occurred before transfer).", DebugFlags.CameraTexture);
            }
        }

        if ((debugFlags & DebugFlags.ExecutionFlow) != 0)
            Log("[ProcessCameraFrameCoroutine] Завершение корутины.", DebugFlags.ExecutionFlow);
    }

    private IEnumerator RunInferenceAndPostProcess(Texture2D sourceTextureForInference)
    {
        if (!isModelInitialized || worker == null || sourceTextureForInference == null)
        {
            LogError("[RunInferenceAndPostProcess] Модель не инициализирована, worker null или sourceTextureForInference null.");
            isProcessingFrame = false;
            yield break;
        }

        errorOccurred = false;
        accumulatedProcessingTimeMs = 0f;
        processingStopwatch.Restart();

        Tensor inputTensor = null;
        Tensor outputTensor = null;
        RenderTexture currentSegmentationMask = null; // MODIFIED: Added declaration

        try // Главный try для всего процесса
        {
            // Этап 1: Конвертация текстуры во входной тензор
            tensorConversionStopwatch.Restart();
            try
            {
                // Пытаемся использовать TextureTransform, если SentisCompat его поддерживает для TextureToTensor
                // Это более гибкий способ, если входное разрешение модели известно и отличается от sourceTextureForInference
                var transformToInput = new Unity.Sentis.TextureTransform().SetDimensions(sentisModelInputWidth, sentisModelInputHeight, 3); // Предполагаем 3 канала (RGB)
                                                                                                                                            // object tensorObj = SentisCompat.TextureToTensor(sourceTextureForInference, transformToInput); // Если такая перегрузка есть

                // Если SentisCompat.TextureToTensor(Texture, TextureTransform) нет, используем базовый
                // object tensorObj = SentisCompat.TextureToTensor(sourceTextureForInference);
                // ИЛИ, если нужна конвертация с размерами, но без TextureTransform:
                object tensorObj = SentisCompat.TextureToTensor(sourceTextureForInference); // MODIFIED: Removed extra arguments

                if (tensorObj is Tensor tempTensor)
                {
                    inputTensor = tempTensor;
                }
                else
                {
                    LogError($"[RunInferenceAndPostProcess] SentisCompat.TextureToTensor вернул null или не Tensor. Тип: {(tensorObj?.GetType().FullName ?? "null")}");
                    errorOccurred = true;
                }
            }
            catch (Exception e)
            {
                LogError($"[RunInferenceAndPostProcess] Ошибка при конвертации Texture в Tensor: {e.Message} --- {e.StackTrace}");
                errorOccurred = true;
            }
            finally
            {
                tensorConversionStopwatch.Stop();
                if (debugFlags.HasFlag(DebugFlags.Performance)) Log($"[PERF] TextureToTensor: {tensorConversionStopwatch.Elapsed.TotalMilliseconds:F2}ms", DebugFlags.Performance);
                accumulatedProcessingTimeMs += (float)tensorConversionStopwatch.Elapsed.TotalMilliseconds;
            }

            if (errorOccurred || inputTensor == null)
            {
                // Очистка и выход, если конвертация не удалась
                // inputTensor освободится в главном finally
                // sourceTextureForInference освободится в главном finally
                yield break; // Прерываем корутину
            }

            if (debugFlags.HasFlag(DebugFlags.TensorProcessing | DebugFlags.DetailedTensor)) Log($"[RunInferenceAndPostProcess] Input tensor shape: {inputTensor.shape}", DebugFlags.TensorProcessing | DebugFlags.DetailedTensor);

            // Этап 2: Выполнение модели
            modelExecutionStopwatch.Restart();
            Tensor tempOutputTensor = null; // Локальная переменная для результата корутины
            yield return StartCoroutine(ExecuteModelCoroutine(inputTensor, (Tensor result) =>
            {
                tempOutputTensor = result; // Присваиваем результат из колбэка
            }));
            outputTensor = tempOutputTensor; // Передаем в переменную верхнего уровня
            modelExecutionStopwatch.Stop();
            if (debugFlags.HasFlag(DebugFlags.Performance)) Log($"[PERF] ExecuteModel: {modelExecutionStopwatch.Elapsed.TotalMilliseconds:F2}ms", DebugFlags.Performance);
            accumulatedProcessingTimeMs += (float)modelExecutionStopwatch.Elapsed.TotalMilliseconds;

            if (outputTensor == null)
            {
                LogError("[RunInferenceAndPostProcess] outputTensor is null after ExecuteModelCoroutine.");
                errorOccurred = true;
            }
            else
            {
                if (debugFlags.HasFlag(DebugFlags.TensorProcessing | DebugFlags.DetailedTensor)) Log($"[WallSegmentation] Raw outputTensor shape: {outputTensor.shape}, dataType: {outputTensor.dataType}", DebugFlags.TensorProcessing | DebugFlags.DetailedTensor);
            }

            if (errorOccurred) // Если на этапе выполнения модели произошла ошибка
            {
                yield break; // Прерываем корутину
            }

            // Этап 3: Постобработка (только если предыдущие этапы успешны)
            // Определение размеров выхода модели (только один раз)
            if (!modelOutputDimensionsKnown)
            {
                try
                {
                    int[] outputDims = outputTensor.shape.ToArray();
                    if (outputDims.Length == 4)
                    {
                        int newHeight = outputDims[2];
                        int newWidth = outputDims[3];
                        if (newHeight > 0 && newWidth > 0 && (newHeight != modelOutputHeight || newWidth != modelOutputWidth))
                        {
                            Log($"[WallSegmentation] ПЕРВЫЙ ИНФЕРЕНС: Определены реальные размеры выхода модели: {newWidth}x{newHeight} (были {modelOutputWidth}x{modelOutputHeight}). Обновляем m_LowResMask.", DebugFlags.Initialization | DebugFlags.TensorProcessing);
                            modelOutputHeight = newHeight;
                            modelOutputWidth = newWidth;
                            ReleaseRenderTexture(ref m_LowResMask);
                            EnsureLowResMask(modelOutputWidth, modelOutputHeight);
                            ReleaseRenderTexture(ref tempOutputMask);
                            tempOutputMask = GetOrCreateRenderTexture(ref tempOutputMask, modelOutputWidth, modelOutputHeight, m_LowResMask.format, FilterMode.Point, $"TempOutputMask_{modelOutputWidth}x{modelOutputHeight}");
                            Log($"[WallSegmentation] m_LowResMask и tempOutputMask пересозданы с размерами {modelOutputWidth}x{modelOutputHeight}.", DebugFlags.TensorProcessing);
                        }
                        else if (newHeight <= 0 || newWidth <= 0)
                        {
                            LogWarning($"[WallSegmentation] ПЕРВЫЙ ИНФЕРЕНС: Получены некорректные размеры из outputTensor.shape ({newWidth}x{newHeight}). m_LowResMask останется с размерами по умолчанию.", DebugFlags.TensorProcessing);
                        }
                        modelOutputDimensionsKnown = true;
                    }
                    else
                    {
                        LogWarning($"[WallSegmentation] ПЕРВЫЙ ИНФЕРЕНС: outputTensor.shape не имеет 4 измерений ({outputTensor.shape}). Не удалось определить H, W. m_LowResMask останется с размерами по умолчанию.", DebugFlags.TensorProcessing);
                        modelOutputDimensionsKnown = true; // Still mark as known to prevent re-evaluation, even if defaults are used.
                    }
                }
                catch (Exception e)
                {
                    LogWarning($"[WallSegmentation] ПЕРВЫЙ ИНФЕРЕНС: Исключение при определении размеров выхода модели: {e.Message}. m_LowResMask останется с размерами по умолчанию.", DebugFlags.TensorProcessing);
                    modelOutputDimensionsKnown = true; // Mark as known to prevent re-evaluation
                }
            }

            if (errorOccurred) yield break; // Если определение размеров не удалось, прерываем

            // Рендеринг в m_LowResMask
            tensorRenderStopwatch.Restart();
            bool lowResRenderSuccess = false;
            // Tensor tensorToRender = outputTensor; // По умолчанию рендерим оригинальный тензор. УДАЛЕНО: Логика ниже определит, что рендерить.
            // bool slicedTensorCreated = false; // УДАЛЕНО: Не создаем отдельный "sliced" тензор.

            try
            {
                // УДАЛЕН БЛОК IF, который пытался "резать" тензор и выводил LogWarning.
                // Теперь мы всегда используем outputTensor напрямую, а выбор канала происходит
                // за счет textureTransformToLowRes, который был настроен в InitializeMLModel.

                if (outputTensor == null)
                {
                    LogError("[WallSegmentation] outputTensor is NULL before attempting to render to m_LowResMask.");
                    lowResRenderSuccess = false;
                    errorOccurred = true;
                }
                else if (m_LowResMask == null || !m_LowResMask.IsCreated())
                {
                    LogError("[WallSegmentation] m_LowResMask is NULL or not created before attempting to render.");
                    lowResRenderSuccess = false;
                    errorOccurred = true;
                }
                else
                {
                    // Используем outputTensor и textureTransformToLowRes, который уже настроен на нужный wallOutputChannel
                    Log($"[WallSegmentation] Rendering output tensor {outputTensor.shape} to m_LowResMask using textureTransformToLowRes (configured for channel {wallOutputChannel})", DebugFlags.TensorProcessing);
                    lowResRenderSuccess = SentisCompat.RenderTensorToTexture(outputTensor, m_LowResMask, textureTransformToLowRes);
                }
            }
            catch (Exception e)
            {
                LogError($"[WallSegmentation] Exception during tensor slicing or rendering to m_LowResMask: {e.Message}\n{e.StackTrace}");
                lowResRenderSuccess = false; // Считаем операцию неуспешной
                errorOccurred = true; // Устанавливаем флаг ошибки, чтобы прервать дальнейшую обработку если нужно
            }
            finally
            {
                // УДАЛЕНА ОЧИСТКА slicedTensor, так как он больше не создается.
                // if (slicedTensorCreated && tensorToRender != null && tensorToRender != outputTensor) 
                // {
                //     tensorToRender.Dispose();
                //     Log("[WallSegmentation] Disposed sliced tensor.", DebugFlags.TensorProcessing);
                // }
            }

            tensorRenderStopwatch.Stop();
            accumulatedProcessingTimeMs += (float)tensorRenderStopwatch.Elapsed.TotalMilliseconds;
            if (debugFlags.HasFlag(DebugFlags.Performance)) Log($"[PERF] Stage 1 (TensorToLowResTexture): {tensorRenderStopwatch.Elapsed.TotalMilliseconds:F2}ms", DebugFlags.Performance);

            if (!lowResRenderSuccess)
            {
                LogError("[WallSegmentation] SentisCompat.RenderTensorToTexture в m_LowResMask не удался.");
                errorOccurred = true;
                yield break; // Прерываем
            }

            if (enableDebugMaskSaving && debugSavePathValid) SaveTextureForDebug(m_LowResMask, Path.Combine(fullDebugSavePath, $"DebugMaskOutput_RawLowRes_F{Time.frameCount}.png")); // MODIFIED
            Log("[WallSegmentation] Успешно отрисован тензор в m_LowResMask.", DebugFlags.TensorProcessing);

            tempOutputMask = GetOrCreateRenderTexture(ref tempOutputMask, modelOutputWidth, modelOutputHeight, m_LowResMask.format, FilterMode.Point, "TempOutputMask_PostProcess");

            if (applyGaussianBlurToLowResMask && gaussianBlur3x3Material != null)
            {
                Graphics.Blit(m_LowResMask, tempOutputMask, gaussianBlur3x3Material);
                Graphics.Blit(tempOutputMask, m_LowResMask);
                Log("[RunInferenceAndPostProcess] GaussianBlur3x3 applied to m_LowResMask.", DebugFlags.TensorProcessing);
                if (enableDebugMaskSaving && debugSavePathValid) SaveTextureForDebug(m_LowResMask, Path.Combine(fullDebugSavePath, $"DebugMaskOutput_BlurredLowRes_F{Time.frameCount}.png")); // MODIFIED
            }

            if (applyThresholdToBlurredLowResMask && thresholdMaskMaterial != null) // MODIFIED: thresholdMaterial to thresholdMaskMaterial
            {
                thresholdMaskMaterial.SetFloat("_Threshold", lowResMaskThreshold); // MODIFIED: thresholdMaterial to thresholdMaskMaterial
                Graphics.Blit(m_LowResMask, tempOutputMask, thresholdMaskMaterial); // MODIFIED: thresholdMaterial to thresholdMaskMaterial
                Graphics.Blit(tempOutputMask, m_LowResMask);
                Log("[RunInferenceAndPostProcess] Threshold applied to blurred m_LowResMask.", DebugFlags.TensorProcessing);
                if (enableDebugMaskSaving && debugSavePathValid) SaveTextureForDebug(m_LowResMask, Path.Combine(fullDebugSavePath, $"DebugMaskOutput_ThresholdedLowRes_F{Time.frameCount}.png")); // MODIFIED
            }

            comprehensivePostProcessStopwatch.Restart();
            FilterMode originalFilterMode = m_LowResMask.filterMode;
            if (originalFilterMode != FilterMode.Bilinear)
            {
                m_LowResMask.filterMode = FilterMode.Bilinear;
                if (debugFlags.HasFlag(DebugFlags.TensorProcessing)) Log($"[RunInferenceAndPostProcess] Temporarily changed m_LowResMask.filterMode to Bilinear for Blit (was {originalFilterMode}).", DebugFlags.TensorProcessing);
            }
            Graphics.Blit(m_LowResMask, segmentationMaskTexture);
            if (m_LowResMask.filterMode != originalFilterMode)
            {
                m_LowResMask.filterMode = originalFilterMode;
                if (debugFlags.HasFlag(DebugFlags.TensorProcessing)) Log($"[RunInferenceAndPostProcess] Restored m_LowResMask.filterMode to {originalFilterMode} after Blit.", DebugFlags.TensorProcessing);
            }
            comprehensivePostProcessStopwatch.Stop();
            accumulatedProcessingTimeMs += (float)comprehensivePostProcessStopwatch.Elapsed.TotalMilliseconds;
            if (debugFlags.HasFlag(DebugFlags.Performance)) Log($"[PERF] Stage 2 (BlitLowResToFinalTexture): {comprehensivePostProcessStopwatch.Elapsed.TotalMilliseconds:F2}ms", DebugFlags.Performance);

            if (debugFlags.HasFlag(DebugFlags.TensorProcessing)) Log("[WallSegmentation] ПОСЛЕ двухэтапного апскейлинга.", DebugFlags.TensorProcessing);

            if (!useComprehensiveGPUProcessing) // MODIFIED: useComprehensiveGPUPostProcessing to useComprehensiveGPUProcessing
            {
                if (debugFlags.HasFlag(DebugFlags.ExecutionFlow)) Log("[WallSegmentation] Начало CPU постобработки.", DebugFlags.ExecutionFlow);
                cpuPostProcessStopwatch.Restart();
                RenderTexture tempPostProcess = texturePool.GetTexture(segmentationMaskTexture.width, segmentationMaskTexture.height, segmentationMaskTexture.format);
                tempPostProcess.name = "Temp_CPU_PostProcess";
                Graphics.Blit(segmentationMaskTexture, tempPostProcess);

                if (enableSharpen && sharpenMaterial != null) // MODIFIED: applySharpen to enableSharpen
                {
                    Graphics.Blit(tempPostProcess, segmentationMaskTexture, sharpenMaterial);
                    Graphics.Blit(segmentationMaskTexture, tempPostProcess);
                    Log("[WallSegmentation] Sharpen применен (CPU).", DebugFlags.TensorProcessing);
                }
                if (enableContrast && contrastMaterial != null) // MODIFIED: applyContrast to enableContrast
                {
                    contrastMaterial.SetFloat("_Contrast", contrastFactor); // MODIFIED: segmentationContrast to contrastFactor
                    Graphics.Blit(tempPostProcess, segmentationMaskTexture, contrastMaterial);
                    Log("[WallSegmentation] Contrast применен (CPU).", DebugFlags.TensorProcessing);
                }
                texturePool.ReleaseTexture(tempPostProcess);
                cpuPostProcessStopwatch.Stop();
                accumulatedProcessingTimeMs += (float)cpuPostProcessStopwatch.Elapsed.TotalMilliseconds;
            }

            if (enableDebugMaskSaving && debugSavePathValid)
            {
                SaveTextureForDebug(segmentationMaskTexture, Path.Combine(fullDebugSavePath, $"DebugMaskOutput_PostCPU_F{Time.frameCount}.png"));
            }
            lastSuccessfulMask = segmentationMaskTexture;

            // --- MOVED: Вызов события и логирование УСПЕШНОГО завершения сюда, ВНУТРЬ try, ДО finally ---
            if (!errorOccurred) // Только если не было ошибок на предыдущих этапах
            {
                if (debugFlags.HasFlag(DebugFlags.Performance))
                {
                    // accumulatedProcessingTimeMs уже включает все этапы до этого момента
                    Log($"[PERF] ACCUMULATED STAGES TIME (RunInferenceAndPostProcess): {accumulatedProcessingTimeMs:F2}ms", DebugFlags.Performance);
                }
                // totalProcessingTime и processedFrameCount обновляются здесь, если все успешно
                totalProcessingTime += accumulatedProcessingTimeMs;
                processedFrameCount++;

                currentSegmentationMask = segmentationMaskTexture;
                // lastSuccessfulMask = currentSegmentationMask; // Уже присвоено выше
                OnSegmentationMaskUpdated?.Invoke(currentSegmentationMask);
                if (debugFlags.HasFlag(DebugFlags.ExecutionFlow)) Log("[WallSegmentation] OnSegmentationMaskUpdated invoked.", DebugFlags.ExecutionFlow);
            }
            else
            {
                if (debugFlags.HasFlag(DebugFlags.ExecutionFlow)) LogWarning("[WallSegmentation] OnSegmentationMaskUpdated НЕ вызван из-за флага errorOccurred.", DebugFlags.ExecutionFlow);
            }
            // --- END OF MOVED SECTION ---
        }
        finally
        {
            // Освобождение ресурсов
            if (inputTensor != null)
            {
                inputTensor.Dispose();
                inputTensor = null;
            }
            if (outputTensor != null)
            {
                outputTensor.Dispose();
                outputTensor = null;
            }

            if (sourceTextureForInference != null)
            {
                if (texture2DPool != null)
                {
                    texture2DPool.ReleaseTexture(sourceTextureForInference);
                }
                sourceTextureForInference = null;
            }

            isProcessingFrame = false;

            processingStopwatch.Stop(); // Останавливаем общий таймер для этой корутины
            if (debugFlags.HasFlag(DebugFlags.Performance))
            {
                // Логируем общее время выполнения этой попытки обработки кадра
                float currentFrameTotalTime = (float)processingStopwatch.Elapsed.TotalMilliseconds;
                Log($"[PERF] TOTAL RunInferenceAndPostProcess Wall-Clock: {currentFrameTotalTime:F2}ms. Error occurred: {errorOccurred}", DebugFlags.Performance);

                // Этот блок был частью добавленного ранее finally и здесь он к месту.
                // processingTimes обновляется только если не было ошибки, что логично.
                if (!errorOccurred)
                {
                    processingTimes.Add(currentFrameTotalTime);
                    if (processingTimes.Count > 100) processingTimes.RemoveAt(0); // Храним последние 100 значений
                }
            }

            // Сброс корутины, чтобы следующая могла запуститься
            processingCoroutine = null;
            if (debugFlags.HasFlag(DebugFlags.ExecutionFlow)) Log($"[RunInferenceAndPostProcess] Exiting (finally). isProcessingFrame = {isProcessingFrame}. errorOccurred = {errorOccurred}. processingCoroutine set to null.", DebugFlags.ExecutionFlow);

        }
    }

    private IEnumerator ExecuteModelCoroutine(Tensor inputTensor, System.Action<Tensor> onCompleted)
    {
        if (worker == null || runtimeModel == null || runtimeModel.inputs == null || runtimeModel.inputs.Count == 0)
        {
            LogError("[ExecuteModelCoroutine] Worker, runtimeModel или входы модели не инициализированы.", DebugFlags.ExecutionFlow);
            onCompleted?.Invoke(null);
            yield break;
        }
        if (inputTensor == null)
        {
            LogError("[ExecuteModelCoroutine] inputTensor is null. Cannot execute.", DebugFlags.ExecutionFlow);
            onCompleted?.Invoke(null);
            yield break;
        }

        bool scheduledSuccessfully = false;
        try
        {
            string inputName = runtimeModel.inputs[0].name;
            worker.SetInput(inputName, inputTensor);
            worker.Schedule();
            scheduledSuccessfully = true;
        }
        catch (Exception e)
        {
            if ((debugFlags & DebugFlags.ExecutionFlow) != 0) Debug.LogError($"[ExecuteModelCoroutine] Ошибка при SetInput/Schedule: {e.Message}\n{e.StackTrace}");
            // Сразу вызываем onCompleted с null, так как выполнение не было запланировано
            onCompleted?.Invoke(null);
            yield break; // Выходим из корутины, если планирование не удалось
        }

        // Этот yield теперь находится ВНЕ блока try...catch, который мог бы вызвать ошибку CS1626
        if (scheduledSuccessfully)
        {
            // Даем Sentis время на обработку. 
            yield return null;
        }

        Tensor output = null;
        try
        {
            // Получаем результат только если планирование прошло успешно
            if (scheduledSuccessfully)
            {
                output = worker.PeekOutput() as Tensor;
                if (output == null)
                {
                    if ((debugFlags & DebugFlags.TensorProcessing) != 0) Debug.LogError("[ExecuteModelCoroutine] PeekOutput вернул null или не Tensor после успешного Schedule.");
                }
            }
        }
        catch (Exception e)
        {
            // Эта ошибка может возникнуть при PeekOutput
            if ((debugFlags & DebugFlags.ExecutionFlow) != 0) Debug.LogError($"[ExecuteModelCoroutine] Ошибка при PeekOutput: {e.Message}\n{e.StackTrace}");
            output = null;
        }
        finally
        {
            // onCompleted вызывается в любом случае, чтобы RunInferenceAndPostProcess мог продолжить
            onCompleted?.Invoke(output);
        }
    }

    private void ClearRenderTexture(RenderTexture rt, Color clearColor)
    {
        if (rt == null || !rt.IsCreated()) return; // Добавлена проверка
        RenderTexture.active = rt;
        GL.Clear(true, true, clearColor);
        RenderTexture.active = null;
    }

    private void OnDestroy()
    {
        if (isModelInitialized && worker != null)
        {
            SentisCompat.DisposeWorker(worker);
            worker = null;
        }
        if (model != null)
        {
            // Assuming 'model' might be a Sentis Model object that needs disposal,
            // but Sentis 2.x Models are UnityEngine.Objects and managed by GC mostly.
            // If it were a raw pointer or unmanaged resource, it would need explicit release.
            // For now, let runtime handle it or add specific SentisCompat.DisposeModel if available/needed.
            model = null;
        }
        runtimeModel = null; // This is just a reference to model, so nulling it is enough.

        // Explicitly release member textures before clearing pools
        if (texturePool != null)
        {
            if (segmentationMaskTexture != null) { texturePool.ReleaseTexture(segmentationMaskTexture); segmentationMaskTexture = null; TrackResourceRelease("segmentationMaskTexture_OnDestroy"); }
            if (tempMask1 != null) { texturePool.ReleaseTexture(tempMask1); tempMask1 = null; TrackResourceRelease("tempMask1_OnDestroy"); }
            if (tempMask2 != null) { texturePool.ReleaseTexture(tempMask2); tempMask2 = null; TrackResourceRelease("tempMask2_OnDestroy"); }
            if (previousMask != null) { texturePool.ReleaseTexture(previousMask); previousMask = null; TrackResourceRelease("previousMask_OnDestroy"); }
            if (interpolatedMask != null) { texturePool.ReleaseTexture(interpolatedMask); interpolatedMask = null; TrackResourceRelease("interpolatedMask_OnDestroy"); }
            if (m_LowResMask != null) { texturePool.ReleaseTexture(m_LowResMask); m_LowResMask = null; TrackResourceRelease("m_LowResMask_Field_OnDestroy"); } // Release m_LowResMask
        }
        if (texture2DPool != null)
        {
            if (cameraTexture != null) { texture2DPool.ReleaseTexture(cameraTexture); cameraTexture = null; TrackResourceRelease("cameraTexture_OnDestroy"); }
        }

        // Release textures from the pool
        texturePool?.ReleaseAllCreatedTextures(); // Use the new method name
        texture2DPool?.ClearAll(); // Assuming Texture2DPool has a similar ClearAll or a more specific release method.

        if (arCameraManager != null) // Removed null check for OnCameraFrameReceived
        {
            arCameraManager.frameReceived -= OnCameraFrameReceived;
        }
        StopAllCoroutines(); // Stop any running coroutines like InitializeMLModel, MonitorMemoryUsage etc.
        Debug.Log("[WallSegmentation] Cleaned up resources on destroy.");
    }

    public void SaveTextureForDebug(RenderTexture rt, string fileName) // MODIFIED: Made public
    {
        // ValidateDebugSavePath(); // Ensure debugSavePathValid is up-to-date - Call this before checking the booleans

        if (!enableDebugMaskSaving || !debugSavePathValid) // UPDATED: Check new boolean and existing path validity
        {
            if (!enableDebugMaskSaving && (debugFlags & DebugFlags.TensorProcessing) != 0) Log("[SaveTextureForDebug] Skipping save: enableDebugMaskSaving is false.", DebugFlags.TensorProcessing);
            else if (!debugSavePathValid && enableDebugMaskSaving && (debugFlags & DebugFlags.TensorProcessing) != 0) Log("[SaveTextureForDebug] Skipping save: debugSavePath is not valid, though enableDebugMaskSaving is true.", DebugFlags.TensorProcessing);
            return;
        }

        if (rt == null || !rt.IsCreated())
        {
            LogWarning($"[SaveTextureForDebug] RenderTexture is null or not created. Cannot save {fileName}.", DebugFlags.TensorProcessing);
            return;
        }

        string directoryPath = Path.Combine(Application.persistentDataPath, "DebugSegmentationOutputs");
        if (!Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        string filePath = Path.Combine(directoryPath, fileName);
        RenderTexture prevActive = RenderTexture.active;
        RenderTexture.active = rt;

        Texture2D tempTex = texture2DPool.GetTexture(rt.width, rt.height, TextureFormat.RGBA32); // Get from pool

        try
        {
            tempTex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tempTex.Apply();
            byte[] bytes = tempTex.EncodeToPNG();
            File.WriteAllBytes(filePath, bytes);
            if ((debugFlags & DebugFlags.TensorProcessing) != 0 || (debugFlags & DebugFlags.DetailedTensor) != 0) // Log if any of these flags are active
                Log($"[SaveTextureForDebug] Текстура УСПЕШНО сохранена в {filePath}", DebugFlags.TensorProcessing | DebugFlags.DetailedTensor);
        }
        catch (System.Exception e)
        {
            LogError($"[SaveTextureForDebug] Ошибка сохранения текстуры {fileName}: {e.Message}", DebugFlags.TensorProcessing);
        }
        finally
        {
            RenderTexture.active = prevActive;
            texture2DPool.ReleaseTexture(tempTex); // Release back to pool
        }
    }

    private void Log(string message, DebugFlags flagContext, LogLevel level = LogLevel.Info)
    {
        if (!enableComponentLogging) // NEW: Master switch for all logs from this component
        {
            return;
        }

        if ((debugFlags & flagContext) != 0 || flagContext == DebugFlags.None) // Only log if the specific flag is enabled OR if it's a general log
        {
            string formattedMessage = $"[{this.GetType().Name}] {message}";
            switch (level)
            {
                case LogLevel.Info:
                    if (enableDetailedDebug || debugFlags.HasFlag(flagContext)) // Ensure info logs only if detailed debug or specific flag is on
                        Debug.Log(formattedMessage);
                    break;
                case LogLevel.Warning:
                    Debug.LogWarning(formattedMessage);
                    break;
                case LogLevel.Error:
                    Debug.LogError(formattedMessage);
                    break;
            }
        }
    }

    private void LogWarning(string message, DebugFlags flagContext = DebugFlags.None)
    {
        Log(message, flagContext, LogLevel.Warning);
    }

    private void LogError(string message, DebugFlags flagContext = DebugFlags.None)
    {
        Log(message, flagContext, LogLevel.Error);
    }

    private void EnsureGPUPostProcessingTextures()
    {
        if (useGPUPostProcessing || useComprehensiveGPUProcessing) // MODIFIED: useComprehensiveGPUPostProcessing to useComprehensiveGPUProcessing
        {
            if (tempMask1 == null || !tempMask1.IsCreated() || tempMask1.width != segmentationMaskTexture.width || tempMask1.height != segmentationMaskTexture.height)
            {
                ReleaseRenderTexture(ref tempMask1);
                tempMask1 = GetOrCreateRenderTexture(ref tempMask1, segmentationMaskTexture.width, segmentationMaskTexture.height, segmentationMaskTexture.format, FilterMode.Bilinear, "TempMask1_GPU");
                Log($"[EnsureGPUPostProcessingTextures] Created tempMask1 ({tempMask1.width}x{tempMask1.height})", DebugFlags.Initialization);
            }
            if (tempMask2 == null || !tempMask2.IsCreated() || tempMask2.width != segmentationMaskTexture.width || tempMask2.height != segmentationMaskTexture.height)
            {
                ReleaseRenderTexture(ref tempMask2);
                tempMask2 = GetOrCreateRenderTexture(ref tempMask2, segmentationMaskTexture.width, segmentationMaskTexture.height, segmentationMaskTexture.format, FilterMode.Bilinear, "TempMask2_GPU");
                Log($"[EnsureGPUPostProcessingTextures] Created tempMask2 ({tempMask2.width}x{tempMask2.height})", DebugFlags.Initialization);
            }
        }
    }

    // Public method to get the low-resolution mask, if available
    public bool TryGetLowResMask(out RenderTexture lowResMask)
    {
        if (m_LowResMask != null && m_LowResMask.IsCreated() && modelOutputDimensionsKnown)
        {
            lowResMask = m_LowResMask;
            return true;
        }
        lowResMask = null;
        return false;
    }

    // Ensure m_LowResMask is created with correct dimensions
    private void EnsureLowResMask(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            LogWarning($"[EnsureLowResMask] Invalid dimensions provided: {width}x{height}. Using default low-res dimensions if m_LowResMask is null.", DebugFlags.Initialization | DebugFlags.TensorProcessing);
            if (m_LowResMask == null) // Only force default if it's completely uninitialized
            {
                width = DEFAULT_LOW_RES_WIDTH;
                height = DEFAULT_LOW_RES_HEIGHT;
                LogWarning($"[EnsureLowResMask] Forcing default low-res dimensions: {width}x{height}.", DebugFlags.Initialization | DebugFlags.TensorProcessing);
            }
            else
            { // If it exists, don't overwrite with potentially wrong defaults if the input was bad
                return;
            }
        }

        if (m_LowResMask == null || !m_LowResMask.IsCreated() || m_LowResMask.width != width || m_LowResMask.height != height)
        {
            ReleaseRenderTexture(ref m_LowResMask);
            // For m_LowResMask, we usually want Point filter initially to see raw model output,
            // but Bilinear might be needed if upscaling directly from it without intermediate steps.
            // Let's default to Point and allow specific Blit operations to change it if necessary.
            m_LowResMask = GetOrCreateRenderTexture(ref m_LowResMask, width, height, RenderTextureFormat.R8, FilterMode.Point, $"m_LowResMask_{width}x{height}");
            Log($"[EnsureLowResMask] Created m_LowResMask ({m_LowResMask.width}x{m_LowResMask.height}) with format {m_LowResMask.format} and filter {m_LowResMask.filterMode}", DebugFlags.Initialization | DebugFlags.TensorProcessing);
        }
    }

    // --- ADDED HELPER METHODS FOR RENDER TEXTURE MANAGEMENT ---
    private void ReleaseRenderTexture(ref RenderTexture rt)
    {
        if (rt != null)
        {
            if (rt == RenderTexture.active)
            {
                RenderTexture.active = null;
            }
            // texturePool.ReleaseTexture(rt); // Use texture pool
            rt.Release(); // Standard release
            DestroyImmediate(rt); // Destroy the asset
            rt = null;
            // Log($"Released and destroyed RenderTexture", DebugFlags.TensorProcessing); // Can be too verbose
        }
    }

    private RenderTexture GetOrCreateRenderTexture(ref RenderTexture currentRT, int width, int height, RenderTextureFormat format, FilterMode filterMode, string name)
    {
        return GetOrCreateRenderTexture(ref currentRT, width, height, format, filterMode, name, false);
    }

    private RenderTexture GetOrCreateRenderTexture(ref RenderTexture currentRT, int width, int height, RenderTextureFormat format, FilterMode filterMode, string name, bool needsRandomWrite)
    {
        if (width <= 0 || height <= 0)
        {
            LogError($"[GetOrCreateRenderTexture] Invalid dimensions for {name}: {width}x{height}. Returning currentRT or null.");
            return currentRT; // Avoid creating texture with invalid size
        }

        if (currentRT == null || !currentRT.IsCreated() || currentRT.width != width || currentRT.height != height || currentRT.format != format || currentRT.filterMode != filterMode || currentRT.enableRandomWrite != needsRandomWrite)
        {
            if (currentRT != null)
            {
                // Log($"Releasing existing RenderTexture '{currentRT.name}' due to mismatch.", DebugFlags.Initialization);
                ReleaseRenderTexture(ref currentRT);
            }

            // currentRT = texturePool.GetTexture(width, height, format); // Use texture pool
            currentRT = new RenderTexture(width, height, 0, format);
            currentRT.name = name;
            currentRT.filterMode = filterMode;
            currentRT.enableRandomWrite = needsRandomWrite; // Set if compute shaders will write to it
            if (!currentRT.Create())
            {
                LogError($"[GetOrCreateRenderTexture] Failed to create RenderTexture '{name}' ({width}x{height}).");
                ReleaseRenderTexture(ref currentRT); // Cleanup if creation failed
                return null;
            }
            // Log($"Created RenderTexture '{currentRT.name}' ({currentRT.width}x{currentRT.height}, format: {currentRT.format}, filter: {currentRT.filterMode}, randomWrite: {currentRT.enableRandomWrite})", DebugFlags.Initialization);
        }
        return currentRT;
    }

    private void ValidateDebugSavePath()
    {
        if (string.IsNullOrEmpty(debugSavePath))
        {
            debugSavePathValid = false;
            fullDebugSavePath = "";
            // Log("Debug save path is empty, texture saving disabled.", DebugFlags.Initialization);
            return;
        }

        try
        {
            // Combine with persistentDataPath for a reliable location
            string combinedPath = Path.Combine(Application.persistentDataPath, debugSavePath);

            if (!Directory.Exists(combinedPath))
            {
                Directory.CreateDirectory(combinedPath);
                Log($"Created debug save directory: {combinedPath}", DebugFlags.Initialization);
            }
            fullDebugSavePath = combinedPath;
            debugSavePathValid = true;
            Log($"Debug textures will be saved to: {fullDebugSavePath}", DebugFlags.Initialization);
        }
        catch (Exception e)
        {
            LogError($"Error validating or creating debug save path '{debugSavePath}': {e.Message}");
            debugSavePathValid = false;
            fullDebugSavePath = "";
        }
    }
    // --- END OF ADDED HELPER METHODS ---
}