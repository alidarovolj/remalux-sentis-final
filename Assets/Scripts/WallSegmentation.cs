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
    public float modelLoadTimeout = 30f;

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

    [Tooltip("Сохранять отладочные маски в указанный путь")] // Добавлено
    public bool saveDebugMasks = false;

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
    // private bool isProcessingFrame = false; // ФлаГ, ЧТО КАДР В ДАННЫЙ МОМЕНТ ОБРАБАТЫВАЕТСЯ - CS0414 - REMOVING FIELD
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

    private void Awake()
    {
        Log("[WallSegmentation] Awake_Start", DebugFlags.Initialization);

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
        if (isInitializing)
        {
            LogWarning("[WallSegmentation] Инициализация модели уже выполняется", DebugFlags.Initialization);
            yield break;
        }

        isInitializing = true;
        lastErrorMessage = null;

        Log("[WallSegmentation] 🚀 Начинаем инициализацию ML модели...", DebugFlags.Initialization);

        string modelFilePath = GetModelPath();
        if (string.IsNullOrEmpty(modelFilePath))
        {
            HandleInitializationError("Не найден файл модели в StreamingAssets");
            yield break;
        }

        Log($"[WallSegmentation] 📁 Загружаем модель из: {modelFilePath}", DebugFlags.Initialization);
        yield return StartCoroutine(LoadModel(modelFilePath));

        if (runtimeModel == null)
        {
            HandleInitializationError("Не удалось загрузить модель");
            yield break;
        }

        Log($"[WallSegmentation] Original selectedBackend from Inspector: {selectedBackend}", DebugFlags.Initialization);
        BackendType backend = (selectedBackend == 1) ? BackendType.GPUCompute : BackendType.CPU;
        Log($"[WallSegmentation] ⚙️ Создаем Worker с бэкендом: {backend}", DebugFlags.Initialization);

        try
        {
            if (runtimeModel != null)
            {
                Log("[WallSegmentation] ⚖️ Попытка квантования модели до UInt8...", DebugFlags.Initialization);
                try
                {
                    // Убедимся, что у нас есть ссылка на объект Model, а не только интерфейс IModel
                    // В данном контексте runtimeModel уже должен быть типа Model.
                    Model modelToQuantize = runtimeModel;
                    ModelQuantizer.QuantizeWeights(QuantizationType.Uint8, ref modelToQuantize);
                    runtimeModel = modelToQuantize; // Присваиваем обратно, если QuantizeWeights модифицирует ref
                    Log("[WallSegmentation] ✅ Квантование модели до UInt8 успешно завершено.", DebugFlags.Initialization);
                }
                catch (System.Exception e)
                {
                    LogWarning($"[WallSegmentation] ⚠️ Не удалось выполнить квантование модели: {e.Message}", DebugFlags.Initialization);
                    // Продолжаем с неквантованной моделью
                }
            }
            else
            {
                LogWarning("[WallSegmentation] runtimeModel is null, cannot perform quantization.", DebugFlags.Initialization);
            }

            worker = SentisCompat.CreateWorker(runtimeModel, (int)backend) as Unity.Sentis.Worker; // MODIFIED: Cast result to Worker

            if (worker == null)
            {
                HandleInitializationError($"Failed to create Sentis Worker with backend {backend} (result was null after cast)");
                yield break;
            }

            // Initialize textureTransformToLowRes for converting 4-channel tensor to 1-channel R8 mask
            // The output tensor is [1, 4, H, W], target m_LowResMask is [H, W] with R8 format.
            // We want to select channel 0 from the tensor and map it to the R channel of the texture.
            // textureTransformToLowRes = new Unity.Sentis.TextureTransform().SetChannelRead(0, 0); // ORIGINAL, causes CS1061
            textureTransformToLowRes = new Unity.Sentis.TextureTransform()
                                            .SetChannelSwizzle(Unity.Sentis.Channel.R, 0) // Map tensor channel 0 to texture channel R
                                            .SetBroadcastChannels(false); // For (R,G,B,A) output, if tensor has 1 channel, this makes it (R,0,0,1)

            Log($"Created worker with backend: {backend}", DebugFlags.Initialization);
        }
        catch (System.Exception e)
        {
            HandleInitializationError($"Не удалось создать Worker: {e.Message}");
            yield break;
        }

        if (worker == null)
        {
            HandleInitializationError("Не удалось создать Worker");
            yield break;
        }

        // Шаг 3.5: Определяем РАЗМЕРЫ ВЫХОДА модели и инициализируем m_LowResMask
        int modelOutputHeight = DEFAULT_LOW_RES_HEIGHT; // Default
        int modelOutputWidth = DEFAULT_LOW_RES_WIDTH;   // Default

        try
        {
            if (runtimeModel != null && runtimeModel.outputs != null && runtimeModel.outputs.Count > 0)
            {
                Unity.Sentis.Model.Output outputInfo = runtimeModel.outputs[0]; // Явно указываем тип

                if (!object.ReferenceEquals(outputInfo, null))
                {
                    Log($"[WallSegmentation] Processing model output: {outputInfo.name} (Type: {outputInfo.GetType().FullName})", DebugFlags.Initialization);

                    object shapeObj = null;
                    System.Reflection.PropertyInfo shapeProperty = outputInfo.GetType().GetProperty("shape");

                    if (shapeProperty != null)
                    {
                        shapeObj = shapeProperty.GetValue(outputInfo);
                    }
                    else
                    {
                        LogWarning($"[WallSegmentation] Could not find 'shape' property via reflection on type {outputInfo.GetType().FullName}. Using default dimensions.", DebugFlags.Initialization);
                    }

                    if (shapeObj != null && shapeObj is Unity.Sentis.TensorShape tensorShape)
                    {
                        int[] dimensions = tensorShape.ToArray();
                        string dimsString = dimensions != null ? string.Join(", ", dimensions.Select(d => d.ToString())) : "null";
                        Log($"[WallSegmentation] Raw model output dimensions from shape.ToArray(): [{dimsString}] for output '{outputInfo.name}'", DebugFlags.Initialization);

                        if (dimensions != null && dimensions.Length >= 2)
                        {
                            if (dimensions.Length == 4) // NCHW
                            {
                                modelOutputHeight = dimensions[2];
                                modelOutputWidth = dimensions[3];
                                Log($"[WallSegmentation] 📐 Извлеченные размеры ВЫХОДА модели (предполагаем NCHW {dimensions[2]}x{dimensions[3]}): {modelOutputWidth}x{modelOutputHeight}", DebugFlags.Initialization);
                            }
                            else if (dimensions.Length == 3)
                            {
                                LogWarning($"[WallSegmentation] Не удалось однозначно определить H, W из 3-мерного тензора ({dimsString}). Используем последние два как H,W, но это может быть неверно. Рекомендуется 4D выход NCHW.", DebugFlags.Initialization);
                                modelOutputHeight = dimensions[dimensions.Length - 2];
                                modelOutputWidth = dimensions[dimensions.Length - 1];
                                Log($"[WallSegmentation] 📐 Извлеченные размеры ВЫХОДА модели (из 3D тензора как ..HW {modelOutputHeight}x{modelOutputWidth}): {modelOutputWidth}x{modelOutputHeight}", DebugFlags.Initialization);
                            }
                            else if (dimensions.Length == 2) // HW
                            {
                                modelOutputHeight = dimensions[0];
                                modelOutputWidth = dimensions[1];
                                Log($"[WallSegmentation] 📐 Извлеченные размеры ВЫХОДА модели (предполагаем HW {dimensions[0]}x{dimensions[1]}): {modelOutputWidth}x{modelOutputHeight}", DebugFlags.Initialization);
                            }
                            else
                            {
                                LogWarning($"[WallSegmentation] Неожиданное количество измерений ({dimensions.Length}) в output shape: [{dimsString}]. Используем размеры по умолчанию.", DebugFlags.Initialization);
                            }
                        }
                        else
                        {
                            LogWarning($"[WallSegmentation] Не удалось получить корректные размеры выхода из shape.ToArray(). Длина массива < 2 или null: [{dimsString}]. Используем размеры по умолчанию.", DebugFlags.Initialization);
                        }
                    }
                    else
                    {
                        string shapeErrorDetail = shapeObj == null ? "shape object retrieved via reflection is null" : $"shape object is not a TensorShape (Type: {shapeObj.GetType().FullName})";
                        LogWarning($"[WallSegmentation] Failed to get valid TensorShape for output '{outputInfo.name}'. Detail: {shapeErrorDetail}. Using default dimensions.", DebugFlags.Initialization);
                    }
                }
                else
                {
                    LogWarning("[WallSegmentation] outputInfo (runtimeModel.outputs[0]) is null. Using default dimensions.", DebugFlags.Initialization);
                }
            }
            else
            {
                string outputsError = object.ReferenceEquals(runtimeModel, null) ? "runtimeModel is null" : (object.ReferenceEquals(runtimeModel.outputs, null) ? "runtimeModel.outputs is null" : "runtimeModel.outputs is empty");
                LogWarning($"[WallSegmentation] {outputsError}. Используем размеры по умолчанию.", DebugFlags.Initialization);
            }
        }
        catch (System.Exception e)
        {
            LogWarning($"[WallSegmentation] Исключение при определении размеров ВЫХОДА модели: {e.Message}. Трассировка: {e.StackTrace}. Используем размеры по умолчанию.", DebugFlags.Initialization);
        }

        EnsureLowResMask(modelOutputWidth, modelOutputHeight);
        Log($"[WallSegmentation] m_LowResMask инициализирован с размерами: {modelOutputWidth}x{modelOutputHeight}", DebugFlags.Initialization);

        // Шаг 4: Определяем размеры входа модели
        try
        {
            var inputs = runtimeModel.inputs;
            if (inputs != null && inputs.Count > 0)
            {
                var inputInfo = inputs[0];
                var shapeProperty = inputInfo.GetType().GetProperty("shape");
                if (shapeProperty != null)
                {
                    var shape = shapeProperty.GetValue(inputInfo);
                    int[] dimensions = GetShapeDimensions(shape);

                    if (dimensions != null && dimensions.Length >= 4)
                    {
                        sentisModelHeight = dimensions[2];
                        sentisModelWidth = dimensions[3];
                        Log($"[WallSegmentation] 📐 Размеры модели: {sentisModelWidth}x{sentisModelHeight}", DebugFlags.Initialization);
                    }
                }
            }
        }
        catch (System.Exception e)
        {
            LogWarning($"[WallSegmentation] Не удалось определить размеры модели: {e.Message}. Используются значения по умолчанию.", DebugFlags.Initialization);
        }

        // Шаг 5: Инициализация текстур
        InitializeTextures();

        // Шаг 6: Финализация
        isModelInitialized = true;
        isInitializing = false;

        OnModelInitialized?.Invoke();
        Log("[WallSegmentation] ✅ ML модель успешно инициализирована!", DebugFlags.Initialization);

        // --- WARM-UP CODE START ---
        if (worker != null && isModelInitialized)
        {
            Log("[WallSegmentation] Начинаем прогрев TextureToTensor и Execute...", DebugFlags.Initialization);
            try
            {
                // Создаем маленькую фиктивную текстуру для прогрева
                // Используем размеры входа модели, если они известны, иначе небольшие стандартные
                int warmupWidth = (sentisModelWidth > 0) ? sentisModelWidth : 256;
                int warmupHeight = (sentisModelHeight > 0) ? sentisModelHeight : 256;

                Texture2D warmupTexture = new Texture2D(warmupWidth, warmupHeight, TextureFormat.RGBA32, false);
                // Заполнять пикселями не обязательно, важен сам факт вызова конвертации
                // warmupTexture.Apply(); // Apply не нужен, если не меняли пиксели

                Log($"[WallSegmentation] Прогрев: создана warmupTexture {warmupWidth}x{warmupHeight}", DebugFlags.Initialization);

                object warmupInputTensorObj = SentisCompat.TextureToTensor(warmupTexture);
                if (warmupInputTensorObj is Tensor warmupInputTensor && warmupInputTensor != null)
                {
                    Log("[WallSegmentation] Прогрев: TextureToTensor успешно выполнен.", DebugFlags.Initialization);
                    // Опционально: один фиктивный Execute
                    // string inputName = runtimeModel.inputs[0].name;
                    // worker.SetInput(inputName, warmupInputTensor);
                    // worker.Execute(); 
                    // Tensor warmupOutputTensor = worker.PeekOutput() as Tensor;
                    // if (warmupOutputTensor != null) warmupOutputTensor.Dispose();
                    // Log("[WallSegmentation] Прогрев: Фиктивный Execute выполнен.", DebugFlags.Initialization);

                    if (warmupInputTensor is IDisposable disposableWarmupTensor) disposableWarmupTensor.Dispose();
                }
                else
                {
                    LogWarning("[WallSegmentation] Прогрев: TextureToTensor не вернул валидный тензор.", DebugFlags.Initialization);
                }
                DestroyImmediate(warmupTexture); // Уничтожаем временную текстуру
                Log("[WallSegmentation] Прогрев завершен.", DebugFlags.Initialization);
            }
            catch (Exception e)
            {
                LogWarning($"[WallSegmentation] Ошибка во время прогрева: {e.Message}", DebugFlags.Initialization);
            }
        }
        // --- WARM-UP CODE END ---
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

        // Освобождаем старые текстуры через пул, если они существуют
        if (tempMask1 != null) { texturePool.ReleaseTexture(tempMask1); TrackResourceRelease("tempMask1_GPU_Recreate"); tempMask1 = null; }
        tempMask1 = texturePool.GetTexture(width, height, RenderTextureFormat.ARGB32);
        tempMask1.name = "PostProcessing_Temp1";
        if (!tempMask1.IsCreated()) tempMask1.Create();
        ClearRenderTexture(tempMask1, Color.clear);
        TrackResourceCreation("tempMask1_GPU_Recreate");

        if (tempMask2 != null) { texturePool.ReleaseTexture(tempMask2); TrackResourceRelease("tempMask2_GPU_Recreate"); tempMask2 = null; }
        tempMask2 = texturePool.GetTexture(width, height, RenderTextureFormat.ARGB32);
        tempMask2.name = "PostProcessing_Temp2";
        if (!tempMask2.IsCreated()) tempMask2.Create();
        ClearRenderTexture(tempMask2, Color.clear);
        TrackResourceCreation("tempMask2_GPU_Recreate");

        if (enableTemporalInterpolation && temporalBlendMaterial != null)
        {
            if (previousMask != null) { texturePool.ReleaseTexture(previousMask); TrackResourceRelease("previousMask_GPU_Recreate"); previousMask = null; }
            previousMask = texturePool.GetTexture(width, height, RenderTextureFormat.ARGB32);
            previousMask.name = "PostProcessing_PreviousMask";
            if (!previousMask.IsCreated()) previousMask.Create();
            ClearRenderTexture(previousMask, Color.clear);
            TrackResourceCreation("previousMask_GPU_Recreate");

            if (interpolatedMask != null) { texturePool.ReleaseTexture(interpolatedMask); TrackResourceRelease("interpolatedMask_GPU_Recreate"); interpolatedMask = null; }
            interpolatedMask = texturePool.GetTexture(width, height, RenderTextureFormat.ARGB32);
            interpolatedMask.name = "PostProcessing_InterpolatedMask";
            interpolatedMask.enableRandomWrite = true;
            if (!interpolatedMask.IsCreated()) interpolatedMask.Create();
            ClearRenderTexture(interpolatedMask, Color.clear);
            TrackResourceCreation("interpolatedMask_GPU_Recreate");
        }
        else // Если временная интерполяция отключена, убедимся, что текстуры освобождены
        {
            if (previousMask != null) { texturePool.ReleaseTexture(previousMask); TrackResourceRelease("previousMask_GPU_Disabled"); previousMask = null; }
            if (interpolatedMask != null) { texturePool.ReleaseTexture(interpolatedMask); TrackResourceRelease("interpolatedMask_GPU_Disabled"); interpolatedMask = null; }
            // Не нужно создавать interpolatedMask здесь, если интерполяция выключена
        }
        // Удален лишний блок else, который был здесь
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

        if (sentisModelWidth > 0 && sentisModelHeight > 0)
        {
            targetWidthForModel = sentisModelWidth;
            targetHeightForModel = sentisModelHeight;
        }
        else
        {
            LogWarning($"[ProcessCameraFrameCoroutine] Invalid model dimensions (W:{sentisModelWidth}, H:{sentisModelHeight}). Falling back to configured inputResolution ({inputResolution.x}x{inputResolution.y}).", DebugFlags.CameraTexture);
            targetWidthForModel = inputResolution.x;
            targetHeightForModel = inputResolution.y;
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

    private IEnumerator RunInferenceAndPostProcess(Texture2D sourceTextureForInference) // Now receives the correctly sized texture
    {
        if ((debugFlags & DebugFlags.ExecutionFlow) != 0)
            Log($"[RunInferenceAndPostProcess] Entered. sourceTextureForInference is {(sourceTextureForInference == null ? "null" : "valid")}, w:{sourceTextureForInference?.width} h:{sourceTextureForInference?.height}", DebugFlags.ExecutionFlow);

        // The rest of this method remains largely the same, as it operates on the provided sourceTextureForInference
        // which is now pre-resized to model input dimensions.
        // currentResolution is still used for the *output* segmentationMaskTexture (e.g., 640x480).

        processingStopwatch.Restart();

        Tensor inputTensor = null;
        Tensor outputTensor = null; // Объявляем здесь, чтобы использовать в finally
        bool errorOccurred = false;
        float accumulatedProcessingTimeMs = 0f;

        tensorConversionStopwatch.Restart();
        object tensorObject = SentisCompat.TextureToTensor(sourceTextureForInference);
        inputTensor = tensorObject as Tensor;
        tensorConversionStopwatch.Stop();
        accumulatedProcessingTimeMs += (float)tensorConversionStopwatch.Elapsed.TotalMilliseconds;

        if (inputTensor == null)
        {
            LogError($"[RunInferenceAndPostProcess] Failed to convert sourceTextureForInference to Tensor. Width: {sourceTextureForInference.width}, Height: {sourceTextureForInference.height}. tensorObject is {(tensorObject == null ? "null" : tensorObject.GetType().Name)}", DebugFlags.TensorProcessing);
            // Освобождаем sourceTextureForInference, так как дальше она не пойдет
            if (sourceTextureForInference != null)
            {
                texture2DPool.ReleaseTexture(sourceTextureForInference);
                LogWarning("[RunInferenceAndPostProcess] sourceTextureForInference released due to inputTensor creation failure.", DebugFlags.CameraTexture);
            }
            // isProcessingFrame = false; // CS0414 - Removed assignment
            processingCoroutine = null;
            yield break;
        }

        if ((debugFlags & DebugFlags.Performance) != 0 && (debugFlags & DebugFlags.DetailedExecution) != 0)
            Log($"[RunInferenceAndPostProcess] TextureToTensor completed in {tensorConversionStopwatch.Elapsed.TotalMilliseconds:F2}ms. Input Tensor: {inputTensor.shape}", DebugFlags.Performance);

        modelExecutionStopwatch.Restart();
        if ((debugFlags & DebugFlags.ExecutionFlow) != 0) Log("[RunInferenceAndPostProcess] Starting ExecuteModelCoroutine...", DebugFlags.ExecutionFlow);

        // Вызываем корутину и ждем ее завершения
        yield return StartCoroutine(ExecuteModelCoroutine(inputTensor, result =>
        {
            outputTensor = result;
            if (result == null && (debugFlags & DebugFlags.ExecutionFlow) != 0)
            {
                LogWarning("[RunInferenceAndPostProcess] ExecuteModelCoroutine completed with null outputTensor.", DebugFlags.ExecutionFlow);
            }
        }));
        modelExecutionStopwatch.Stop();
        accumulatedProcessingTimeMs += (float)modelExecutionStopwatch.Elapsed.TotalMilliseconds;

        // Log output tensor details
        if (outputTensor != null)
        {
            Log($"[WallSegmentation] Raw outputTensor shape: {outputTensor.shape}, dataType: {outputTensor.dataType}", DebugFlags.TensorAnalysis);
        }
        else
        {
            LogWarning("[WallSegmentation] outputTensor is null immediately after ExecuteModelCoroutine and before main try block.", DebugFlags.TensorAnalysis);
        }

        // Теперь основной блок try-catch-finally для остальной обработки
        try
        {
            if (outputTensor == null)
            {
                LogError("[RunInferenceAndPostProcess] outputTensor is NULL after ExecuteModelCoroutine. Cannot proceed with post-processing.", DebugFlags.TensorProcessing);
                errorOccurred = true;
                // inputTensor будет освобожден в finally
                // sourceTextureForInference также будет освобождена в finally
                yield break;
            }

            if ((debugFlags & DebugFlags.Performance) != 0 && (debugFlags & DebugFlags.DetailedExecution) != 0)
                Log($"[RunInferenceAndPostProcess] Model Execution completed in {modelExecutionStopwatch.Elapsed.TotalMilliseconds:F2}ms. Output Tensor: {outputTensor.shape}", DebugFlags.Performance);


            if ((this.debugFlags & DebugFlags.TensorProcessing) == DebugFlags.TensorProcessing)
                Log("[WallSegmentation] ПЕРЕД двухэтапным апскейлингом (outputTensor -> m_LowResMask -> segmentationMaskTexture).", DebugFlags.TensorProcessing);

            // START OF TWO-STEP UPSCALING
            // outputTensor dimensions are now (1, 4, 80, 80) if input was 320x320.
            // m_LowResMask is now initialized to 80x80, R8, Point filter.

            // Check if m_LowResMask is valid before use
            if (m_LowResMask == null || !m_LowResMask.IsCreated())
            {
                LogError("[WallSegmentation] m_LowResMask is null or not created before use in RunInferenceAndPostProcess. Attempting reinitialization.", DebugFlags.TensorProcessing);
                InitializeTextures(); // This will attempt to recreate m_LowResMask
                if (m_LowResMask == null || !m_LowResMask.IsCreated()) // Check again
                {
                    LogError("[WallSegmentation] m_LowResMask STILL null or not created after reinitialization. Aborting frame processing.", DebugFlags.TensorProcessing);
                    errorOccurred = true;
                    yield break; // Abort if still not valid
                }
                LogWarning("[WallSegmentation] m_LowResMask was reinitialized in RunInferenceAndPostProcess.", DebugFlags.TensorProcessing);
            }

            // Ensure segmentationMaskTexture (target) has bilinear filtering enabled for the Blit
            if (segmentationMaskTexture != null)
            {
                segmentationMaskTexture.filterMode = FilterMode.Bilinear;
            }
            else
            {
                LogError("[WallSegmentation] segmentationMaskTexture is null. Cannot perform upscaling.", DebugFlags.TensorProcessing);
                errorOccurred = true;
                RenderTexture.ReleaseTemporary(m_LowResMask); // This was an error, m_LowResMask is not temporary
                                                              // Should be: if (m_LowResMask != null && m_LowResMask.IsCreated()) { texturePool.ReleaseTexture(m_LowResMask); m_LowResMask = null; }
                                                              // However, the logic above should already handle m_LowResMask not being valid.
                                                              // If segmentationMaskTexture is null, we cannot proceed.
                yield break;
            }

            // Шаг 1: вывести тензор в m_LowResMask (1:1 без сглаживания)
            tensorRenderStopwatch.Restart(); // Замеряем SentisCompat.RenderTensorToTexture
            bool lowResRenderSuccess = SentisCompat.RenderTensorToTexture(outputTensor, m_LowResMask, textureTransformToLowRes);
            tensorRenderStopwatch.Stop();
            accumulatedProcessingTimeMs += (float)tensorRenderStopwatch.Elapsed.TotalMilliseconds;
            if ((debugFlags & DebugFlags.Performance) != 0)
                Log($"[PERF] Stage 1 (TensorToLowResTexture): {tensorRenderStopwatch.Elapsed.TotalMilliseconds:F2}ms", DebugFlags.Performance);

            // DEBUG SAVE: Raw m_LowResMask
            if (saveDebugMasks && (debugFlags & DebugFlags.TensorProcessing) != 0)
            {
                SaveTextureForDebug(m_LowResMask, $"DebugMaskOutput_RawLowRes_F{Time.frameCount}.png");
            }


            bool finalRenderSuccess = false;
            if (lowResRenderSuccess)
            {
                if ((this.debugFlags & DebugFlags.TensorProcessing) == DebugFlags.TensorProcessing)
                    Log("[WallSegmentation] Успешно отрисован тензор в m_LowResMask.", DebugFlags.TensorProcessing);

                // --- START: Smooth m_LowResMask before upscaling ---
                if (gaussianBlur3x3Material != null && thresholdMaskMaterial != null)
                {
                    RenderTexture blurredLowResMask = texturePool.GetTexture(m_LowResMask.width, m_LowResMask.height, m_LowResMask.format);
                    blurredLowResMask.name = "BlurredLowResMask_Temp";
                    blurredLowResMask.filterMode = FilterMode.Point; // Blur samples point, output is point

                    // 1. Gaussian Blur
                    if (gaussianBlur3x3Material.shader.isSupported)
                    {
                        // _TexelSize ожидает float4: (1/width, 1/height, width, height)
                        gaussianBlur3x3Material.SetVector("_TexelSize", new Vector4(1.0f / m_LowResMask.width, 1.0f / m_LowResMask.height, m_LowResMask.width, m_LowResMask.height));
                        Graphics.Blit(m_LowResMask, blurredLowResMask, gaussianBlur3x3Material);
                        if ((debugFlags & DebugFlags.TensorProcessing) != 0) Log("[RunInferenceAndPostProcess] GaussianBlur3x3 applied to m_LowResMask.", DebugFlags.TensorProcessing);

                        // DEBUG SAVE: Blurred m_LowResMask (before threshold)
                        if (saveDebugMasks && (debugFlags & DebugFlags.TensorProcessing) != 0)
                        {
                            SaveTextureForDebug(blurredLowResMask, $"DebugMaskOutput_BlurredLowRes_F{Time.frameCount}.png");
                        }

                        // 2. Threshold
                        if (thresholdMaskMaterial.shader.isSupported)
                        {
                            // thresholdMaskMaterial.SetFloat("_Threshold", 0.5f); // Can be set on material asset or here
                            Graphics.Blit(blurredLowResMask, m_LowResMask, thresholdMaskMaterial); // Apply threshold back to m_LowResMask
                            if ((debugFlags & DebugFlags.TensorProcessing) != 0) Log("[RunInferenceAndPostProcess] Threshold applied to blurred m_LowResMask.", DebugFlags.TensorProcessing);

                            // ADDED DEBUG SAVE: m_LowResMask AFTER threshold material
                            if (saveDebugMasks && (debugFlags & DebugFlags.TensorProcessing) != 0)
                            {
                                SaveTextureForDebug(m_LowResMask, $"DebugMaskOutput_ThresholdedLowRes_F{Time.frameCount}.png");
                            }
                        }
                        else { LogWarning("[RunInferenceAndPostProcess] ThresholdMask shader not supported on this platform.", DebugFlags.TensorProcessing); }
                    }
                    else { LogWarning("[RunInferenceAndPostProcess] GaussianBlur3x3 shader not supported on this platform.", DebugFlags.TensorProcessing); }
                    texturePool.ReleaseTexture(blurredLowResMask);
                }
                else
                {
                    LogWarning("[RunInferenceAndPostProcess] GaussianBlur3x3Material or ThresholdMaskMaterial not assigned. Skipping low-res mask smoothing.", DebugFlags.TensorProcessing);
                }
                // --- END: Smooth m_LowResMask ---

                // Шаг 2: билинейно растянуть m_LowResMask (теперь сглаженную) в segmentationMaskTexture
                // Используем comprehensivePostProcessStopwatch для замера Graphics.Blit
                comprehensivePostProcessStopwatch.Restart();

                // --- CHANGE TO FIX BLOCKINESS ---
                FilterMode originalLowResFilterMode = m_LowResMask.filterMode;
                if (originalLowResFilterMode != FilterMode.Bilinear)
                {
                    m_LowResMask.filterMode = FilterMode.Bilinear;
                    if ((this.debugFlags & DebugFlags.TensorProcessing) == DebugFlags.TensorProcessing && (this.debugFlags & DebugFlags.DetailedTensor) == DebugFlags.DetailedTensor)
                        Log($"[RunInferenceAndPostProcess] Temporarily changed m_LowResMask.filterMode to Bilinear for Blit (was {originalLowResFilterMode}).", DebugFlags.TensorProcessing | DebugFlags.DetailedTensor);
                }
                // --- END OF CHANGE ---

                Graphics.Blit(m_LowResMask, segmentationMaskTexture);

                // --- RESTORE FILTER MODE ---
                if (m_LowResMask.filterMode != originalLowResFilterMode) // Check if we actually changed it
                {
                    m_LowResMask.filterMode = originalLowResFilterMode;
                    if ((this.debugFlags & DebugFlags.TensorProcessing) == DebugFlags.TensorProcessing && (this.debugFlags & DebugFlags.DetailedTensor) == DebugFlags.DetailedTensor)
                        Log($"[RunInferenceAndPostProcess] Restored m_LowResMask.filterMode to {originalLowResFilterMode} after Blit.", DebugFlags.TensorProcessing | DebugFlags.DetailedTensor);
                }
                // --- END OF RESTORE ---

                comprehensivePostProcessStopwatch.Stop();
                accumulatedProcessingTimeMs += (float)comprehensivePostProcessStopwatch.Elapsed.TotalMilliseconds;
                if ((debugFlags & DebugFlags.Performance) != 0)
                    Log($"[PERF] Stage 2 (BlitLowResToFinalTexture): {comprehensivePostProcessStopwatch.Elapsed.TotalMilliseconds:F2}ms", DebugFlags.Performance);

                finalRenderSuccess = true; // Assume Blit is successful
            }
            else
            {
                Log("[WallSegmentation] ОШИБКА рендеринга тензора в m_LowResMask.", DebugFlags.TensorProcessing, LogLevel.Error); // Log using m_LowResMask
            }

            // m_LowResMask is a class field, managed by InitializeTextures and OnDestroy.
            // No ReleaseTemporary here.
            // END OF TWO-STEP UPSCALING

            bool renderSuccess = finalRenderSuccess; // Use the success status from the new method

            // tensorRenderStopwatch.Stop(); // Уже остановлен выше для первого этапа
            // accumulatedProcessingTimeMs += (float)tensorRenderStopwatch.Elapsed.TotalMilliseconds; // Уже добавлено для первого этапа

            if (renderSuccess)
            {
                if ((this.debugFlags & DebugFlags.TensorProcessing) == DebugFlags.TensorProcessing)
                    Log("[WallSegmentation] ПОСЛЕ двухэтапного апскейлинга.", DebugFlags.TensorProcessing);

                if (this.useGPUPostProcessing && this.comprehensivePostProcessMaterial != null && tempMask1 != null && tempMask2 != null)
                {
                    if ((this.debugFlags & DebugFlags.ExecutionFlow) == DebugFlags.ExecutionFlow)
                        Log("[WallSegmentation] Начало GPU постобработки.", DebugFlags.ExecutionFlow);

                    EnsureGPUPostProcessingTextures(); // Make sure textures are valid

                    RenderTexture sourceForComprehensive = segmentationMaskTexture;
                    RenderTexture targetForComprehensive = tempMask1; // Use tempMask1 as intermediate

                    if (this.useComprehensiveGPUProcessing) // Check if the comprehensive kernel should be दीन
                    {
                        comprehensivePostProcessStopwatch.Restart(); // Restart timing comprehensive post-processing

                        this.comprehensivePostProcessMaterial.SetInt("_EnableBlur", this.enableGaussianBlur ? 1 : 0);
                        this.comprehensivePostProcessMaterial.SetFloat("_BlurSize", this.blurSize);
                        this.comprehensivePostProcessMaterial.SetInt("_EnableSharpen", this.enableSharpen ? 1 : 0);
                        this.comprehensivePostProcessMaterial.SetInt("_EnableContrast", this.enableContrast ? 1 : 0);
                        this.comprehensivePostProcessMaterial.SetFloat("_ContrastFactor", this.contrastFactor);
                        this.comprehensivePostProcessMaterial.SetInt("_EnableOpening", this.enableMorphologicalOpening ? 1 : 0);
                        // Add other parameters for comprehensive material if any (e.g., closing, dilation, erosion sizes)

                        Graphics.Blit(sourceForComprehensive, targetForComprehensive, this.comprehensivePostProcessMaterial, 0);

                        comprehensivePostProcessStopwatch.Stop(); // Stop timing
                        accumulatedProcessingTimeMs += (float)comprehensivePostProcessStopwatch.Elapsed.TotalMilliseconds;


                        if ((this.debugFlags & (DebugFlags.TensorProcessing | DebugFlags.DetailedExecution)) != 0)
                            Log("[WallSegmentation] GPU Comprehensive PostProcess применен.", DebugFlags.TensorProcessing);

                        // Copy result back to segmentationMaskTexture
                        Graphics.Blit(targetForComprehensive, segmentationMaskTexture);

                        if ((this.debugFlags & DebugFlags.TensorProcessing) == DebugFlags.TensorProcessing)
                            Log("[WallSegmentation] Результат GPU постобработки скопирован в segmentationMaskTexture.", DebugFlags.TensorProcessing);

                        SaveTextureForDebug(segmentationMaskTexture, "DebugMaskOutput_PostGPUComprehensive.png");
                    }
                    else // Fallback or individual GPU steps if comprehensive is off or not fully implemented
                    {
                        // If not using comprehensive kernel, copy source to output or apply individual steps
                        // This part would need specific logic if individual GPU steps were planned
                        Log("[WallSegmentation] Comprehensive GPU processing disabled, direct blit or individual steps needed here.", DebugFlags.ExecutionFlow, LogLevel.Warning);
                        Graphics.Blit(sourceForComprehensive, segmentationMaskTexture); // Default: no comprehensive effect applied
                    }
                }
                else // CPU Post-processing Path
                {
                    if ((this.debugFlags & DebugFlags.ExecutionFlow) == DebugFlags.ExecutionFlow)
                        Log("[WallSegmentation] Начало CPU постобработки.", DebugFlags.ExecutionFlow);

                    // Добавлена общая проверка на включение постобработки CPU
                    if (this.enablePostProcessing)
                    {
                        RenderTexture currentProcessingTexture = texturePool.GetTexture(currentResolution.x, currentResolution.y);
                        Graphics.Blit(segmentationMaskTexture, currentProcessingTexture); // Start with raw mask

                        RenderTexture tempTarget = texturePool.GetTexture(currentResolution.x, currentResolution.y);

                        cpuPostProcessStopwatch.Restart(); // Start CPU post-processing timing

                        if (this.enableGaussianBlur && gaussianBlurMaterial != null)
                        {
                            gaussianBlurMaterial.SetFloat("_BlurSize", this.blurSize);
                            Graphics.Blit(currentProcessingTexture, tempTarget, gaussianBlurMaterial);
                            Graphics.Blit(tempTarget, currentProcessingTexture); // Result back to current
                            if ((this.debugFlags & (DebugFlags.TensorProcessing | DebugFlags.DetailedExecution)) != 0)
                                Log("[WallSegmentation] Gaussian Blur применен (CPU).", DebugFlags.TensorProcessing);
                        }
                        if (this.enableSharpen && sharpenMaterial != null)
                        {
                            Graphics.Blit(currentProcessingTexture, tempTarget, sharpenMaterial);
                            Graphics.Blit(tempTarget, currentProcessingTexture);
                            if ((this.debugFlags & (DebugFlags.TensorProcessing | DebugFlags.DetailedExecution)) != 0)
                                Log("[WallSegmentation] Sharpen применен (CPU).", DebugFlags.TensorProcessing);
                        }
                        if (this.enableContrast && contrastMaterial != null)
                        {
                            contrastMaterial.SetFloat("_ContrastFactor", this.contrastFactor);
                            Graphics.Blit(currentProcessingTexture, tempTarget, contrastMaterial);
                            Graphics.Blit(tempTarget, currentProcessingTexture);
                            if ((this.debugFlags & (DebugFlags.TensorProcessing | DebugFlags.DetailedExecution)) != 0)
                                Log("[WallSegmentation] Contrast применен (CPU).", DebugFlags.TensorProcessing);
                        }

                        // Морфологическое открытие (Erode -> Dilate)
                        if (this.enableMorphologicalOpening && erodeMaterial != null && dilateMaterial != null)
                        {
                            Graphics.Blit(currentProcessingTexture, tempTarget, erodeMaterial);
                            Graphics.Blit(tempTarget, currentProcessingTexture, dilateMaterial);
                            if ((this.debugFlags & (DebugFlags.TensorProcessing | DebugFlags.DetailedExecution)) != 0)
                                Log("[WallSegmentation] Morphological Opening применен (CPU).", DebugFlags.TensorProcessing);
                        }

                        // Морфологическое закрытие (Dilate -> Erode)
                        if (this.enableMorphologicalClosing && dilateMaterial != null && erodeMaterial != null)
                        {
                            Graphics.Blit(currentProcessingTexture, tempTarget, dilateMaterial);
                            Graphics.Blit(tempTarget, currentProcessingTexture, erodeMaterial);
                            if ((this.debugFlags & (DebugFlags.TensorProcessing | DebugFlags.DetailedExecution)) != 0)
                                Log("[WallSegmentation] Morphological Closing применен (CPU).", DebugFlags.TensorProcessing);
                        }

                        cpuPostProcessStopwatch.Stop(); // Stop CPU post-processing timing
                        accumulatedProcessingTimeMs += (float)cpuPostProcessStopwatch.Elapsed.TotalMilliseconds;

                        Graphics.Blit(currentProcessingTexture, segmentationMaskTexture); // Final result to output
                        texturePool.ReleaseTexture(currentProcessingTexture);
                        texturePool.ReleaseTexture(tempTarget); // Освобождаем tempTarget здесь
                        SaveTextureForDebug(segmentationMaskTexture, "DebugMaskOutput_PostCPU.png"); // Восстановлена строка
                    }
                    else
                    {
                        if ((this.debugFlags & DebugFlags.ExecutionFlow) == DebugFlags.ExecutionFlow)
                            Log("[WallSegmentation] CPU постобработка отключена флагом enablePostProcessing.", DebugFlags.ExecutionFlow);
                        // Если постобработка отключена, segmentationMaskTexture уже содержит необработанную маску.
                        // Ничего дополнительно делать не нужно, кроме как возможно сохранить ее для отладки.
                        SaveTextureForDebug(segmentationMaskTexture, "DebugMaskOutput_NoCPU_Post_Processing.png");
                    }
                }
            }

            // Освобождаем входной тензор, если он был создан
            if (inputTensor != null)
            {
                if (inputTensor is IDisposable disposableInputTensor) disposableInputTensor.Dispose();
                inputTensor = null;
                if ((debugFlags & (DebugFlags.TensorProcessing | DebugFlags.DetailedTensor)) != 0)
                    Log("[RunInferenceAndPostProcess] inputTensor (original) was disposed.", DebugFlags.TensorProcessing);
            }

            // Освобождаем выходной тензор, если он был создан и еще не освобожден
            if (outputTensor != null)
            {
                if (outputTensor is IDisposable disposableOutputTensor)
                {
                    disposableOutputTensor.Dispose();
                    if ((debugFlags & (DebugFlags.TensorProcessing | DebugFlags.DetailedTensor)) != 0)
                        Log("[WallSegmentation] outputTensor освобожден.", DebugFlags.TensorProcessing);
                }
                outputTensor = null;
            }
        }
        catch (Exception e)
        {
            LogError($"[RunInferenceAndPostProcess] Exception: {e.Message}\nStackTrace: {e.StackTrace}", DebugFlags.ExecutionFlow);
            errorOccurred = true;
        }
        finally
        {
            // Освобождаем входной тензор (повторно, на всякий случай, если выход был до блока finally выше)
            if (inputTensor != null)
            {
                if (inputTensor is IDisposable disposableInput) disposableInput.Dispose();
                inputTensor = null;
                if ((debugFlags & DebugFlags.TensorProcessing) != 0) Log("[RunInferenceAndPostProcess] inputTensor disposed in finally.", DebugFlags.TensorProcessing);
            }
            // Освобождаем выходной тензор (повторно)
            if (outputTensor != null)
            {
                if (outputTensor is IDisposable disposableOutput) disposableOutput.Dispose();
                outputTensor = null;
                if ((debugFlags & DebugFlags.TensorProcessing) != 0) Log("[RunInferenceAndPostProcess] outputTensor disposed in finally.", DebugFlags.TensorProcessing);
            }

            // Освобождаем sourceTextureForInference (которая была tempTexture из ProcessCameraFrameCoroutine)
            if (sourceTextureForInference != null)
            {
                texture2DPool.ReleaseTexture(sourceTextureForInference);
                if ((debugFlags & DebugFlags.CameraTexture) != 0)
                    Log($"[RunInferenceAndPostProcess] sourceTextureForInference (ID: {sourceTextureForInference.GetInstanceID()}) released to texture2DPool.", DebugFlags.CameraTexture);
            }
            else
            {
                if ((debugFlags & DebugFlags.CameraTexture) != 0)
                    LogWarning("[RunInferenceAndPostProcess] Tried to release sourceTextureForInference in finally, but it was null.", DebugFlags.CameraTexture);
            }

            // isProcessingFrame = false; // CS0414 - Removed assignment
            processingCoroutine = null; // Сбрасываем ссылку на текущую корутину
            if ((debugFlags & DebugFlags.ExecutionFlow) != 0) Log($"[RunInferenceAndPostProcess] Exiting. isProcessingFrame = false. errorOccurred = {errorOccurred}", DebugFlags.ExecutionFlow);
        }

        // Логирование времени выполнения
        if (enablePerformanceProfiling || (debugFlags & DebugFlags.Performance) != 0)
        {
            Log($"[PERF] XRCpuImage.ConvertAsync: {cpuImageConversionStopwatch.Elapsed.TotalMilliseconds:F2}ms (from previous coroutine step)", DebugFlags.Performance); // Added note
            Log($"[PERF] TextureToTensor: {tensorConversionStopwatch.Elapsed.TotalMilliseconds:F2}ms", DebugFlags.Performance);
            Log($"[PERF] ExecuteModel: {modelExecutionStopwatch.Elapsed.TotalMilliseconds:F2}ms", DebugFlags.Performance);
            Log($"[PERF] RenderTensorToTexture: {tensorRenderStopwatch.Elapsed.TotalMilliseconds:F2}ms", DebugFlags.Performance);
            if (this.useGPUPostProcessing && this.useComprehensiveGPUProcessing && this.comprehensivePostProcessMaterial != null)
            {
                Log($"[PERF] ComprehensiveGPU PostProcess: {comprehensivePostProcessStopwatch.Elapsed.TotalMilliseconds:F2}ms", DebugFlags.Performance);
            }
            if (this.enablePostProcessing && !this.useGPUPostProcessing) // Log CPU post-processing time if it was used
            {
                Log($"[PERF] CPU PostProcess: {cpuPostProcessStopwatch.Elapsed.TotalMilliseconds:F2}ms", DebugFlags.Performance);
            }
            Log($"[PERF] ACCUMULATED STAGES TIME (RunInferenceAndPostProcess): {accumulatedProcessingTimeMs:F2}ms", DebugFlags.Performance);
            Log($"[PERF] TOTAL Coroutine Wall-Clock Time (RunInferenceAndPostProcess, includes yields): {processingStopwatch.Elapsed.TotalMilliseconds:F2}ms", DebugFlags.Performance);
        }

        totalProcessingTime += accumulatedProcessingTimeMs; // Use the sum of stages for average calculation
        processedFrameCount++;

        // Temporarily disable adaptive resolution logic
        /*
        if (this.adaptiveResolution) // Use class member for adaptiveResolution
        {
            // Adaptive resolution logic here based on currentFrameProcessingTime and targetProcessingTimeMs
            // For example:
            // Use accumulatedProcessingTimeMs for decision making
            if (accumulatedProcessingTimeMs > targetProcessingTimeMs * 1.2f && currentResolution.x > minResolution.x && currentResolution.y > minResolution.y)
            {
                currentResolution -= resolutionStep;
                currentResolution.x = Mathf.Max(currentResolution.x, minResolution.x);
                currentResolution.y = Mathf.Max(currentResolution.y, minResolution.y);
                InitializeTextures(); // Recreate textures with new resolution
                Log($"[AdaptiveRes] Decreased resolution to {currentResolution} due to slow frame ({accumulatedProcessingTimeMs:F1}ms)", DebugFlags.Performance);
            }
            else if (accumulatedProcessingTimeMs < targetProcessingTimeMs * 0.8f && currentResolution.x < maxResolution.x && currentResolution.y < maxResolution.y)
            {
                currentResolution += resolutionStep;
                currentResolution.x = Mathf.Min(currentResolution.x, maxResolution.x);
                currentResolution.y = Mathf.Min(currentResolution.y, maxResolution.y);
                InitializeTextures();
                Log($"[AdaptiveRes] Increased resolution to {currentResolution} due to fast frame ({accumulatedProcessingTimeMs:F1}ms)", DebugFlags.Performance);
            }
        }
        */

        if ((this.debugFlags & DebugFlags.Performance) == DebugFlags.Performance)
            Log($"[WallSegmentation] Время обработки кадра (сумма этапов): {accumulatedProcessingTimeMs:F2}ms (Фиксированное разрешение: {currentResolution.x}x{currentResolution.y})", DebugFlags.Performance);

        // Сообщаем подписчикам, что маска обновлена и готова к отображению
        if (!errorOccurred && segmentationMaskTexture != null && segmentationMaskTexture.IsCreated())
        {
            OnSegmentationMaskUpdated?.Invoke(segmentationMaskTexture);
            if ((debugFlags & DebugFlags.ExecutionFlow) != 0) Log("[WallSegmentation] OnSegmentationMaskUpdated invoked.", DebugFlags.ExecutionFlow);
        }
        else if (errorOccurred)
        {
            if ((debugFlags & DebugFlags.ExecutionFlow) != 0) LogWarning("[WallSegmentation] OnSegmentationMaskUpdated NOT invoked due to errorOccurred flag.", DebugFlags.ExecutionFlow);
        }
        else
        {
            if ((debugFlags & DebugFlags.ExecutionFlow) != 0) LogWarning($"[WallSegmentation] OnSegmentationMaskUpdated NOT invoked. segmentationMaskTexture is null or not created. IsNull: {segmentationMaskTexture == null}, IsCreated: {segmentationMaskTexture?.IsCreated()}", DebugFlags.ExecutionFlow);
        }

        // isProcessingFrame = false; // CS0414 - Removed assignment
        processingCoroutine = null;

        yield return null;
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

    private void SaveTextureForDebug(RenderTexture rt, string fileName)
    {
        if (!saveDebugMasks) return;
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
        // Using this.debugFlags for class member, flagContext for specific context
        // Log if the specific flag is set OR if it's an error/warning OR if general debugMode is on and it's an info message.
        if (debugMode || ((this.debugFlags & flagContext) == flagContext && flagContext != DebugFlags.None) || level == LogLevel.Error || level == LogLevel.Warning)
        {
            string prefix = "[WallSegmentation] "; // Simplified prefix
            if (level == LogLevel.Error)
            {
                Debug.LogError(prefix + message);
            }
            else if (level == LogLevel.Warning)
            {
                Debug.LogWarning(prefix + message);
            }
            else // Info
            {
                if (debugMode || ((this.debugFlags & flagContext) == flagContext && flagContext != DebugFlags.None)) // Additional check for info to respect flags if debugMode is true
                    Debug.Log(prefix + message);
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
        if (!useGPUPostProcessing) return;

        bool needsRecreation = false;
        string reason = "";

        if (tempMask1 == null || !tempMask1.IsCreated() || tempMask1.width != currentResolution.x || tempMask1.height != currentResolution.y)
        {
            needsRecreation = true;
            reason += "tempMask1 invalid; ";
        }
        if (tempMask2 == null || !tempMask2.IsCreated() || tempMask2.width != currentResolution.x || tempMask2.height != currentResolution.y)
        {
            needsRecreation = true;
            reason += "tempMask2 invalid; ";
        }

        // Check for comprehensivePostProcessMaterial only if comprehensive processing is enabled
        if (useComprehensiveGPUProcessing && comprehensivePostProcessMaterial == null)
        {
            Log("comprehensivePostProcessMaterial is null, but comprehensive GPU processing is enabled. Assign it in the Inspector.", DebugFlags.Initialization, LogLevel.Warning);
            // This might not require texture recreation but is a setup issue.
        }

        if (needsRecreation)
        {
            Log($"Recreating GPU post-processing textures. Reason: {reason}", DebugFlags.Initialization);
            CreateGPUPostProcessingTextures();
        }
    }

    /// <summary>
    /// Attempts to get the low-resolution (160x120) mask used internally.
    /// </summary>
    /// <param name="lowResMask">The output low-resolution RenderTexture.</param>
    /// <returns>True if the mask is available and valid, false otherwise.</returns>
    public bool TryGetLowResMask(out RenderTexture lowResMask)
    {
        if (m_LowResMask != null && m_LowResMask.IsCreated())
        {
            lowResMask = m_LowResMask;
            return true;
        }
        lowResMask = null;
        return false;
    }

    /// <summary>
    /// Убеждается, что m_LowResMask существует и имеет правильные размеры и формат.
    /// Если нет, (пере)создает ее из пула.
    /// </summary>
    private void EnsureLowResMask(int width, int height)
    {
        if (texturePool == null)
        {
            LogError("[EnsureLowResMask] TexturePool is null. Cannot create m_LowResMask.", DebugFlags.Initialization);
            return;
        }

        if (width <= 0 || height <= 0)
        {
            LogError($"[EnsureLowResMask] Invalid dimensions for m_LowResMask: {width}x{height}. Cannot create.", DebugFlags.Initialization);
            return;
        }

        bool needsRecreation = false;
        if (m_LowResMask == null || !m_LowResMask.IsCreated())
        {
            needsRecreation = true;
        }
        else if (m_LowResMask.width != width || m_LowResMask.height != height || m_LowResMask.format != RenderTextureFormat.R8)
        {
            LogWarning($"[EnsureLowResMask] m_LowResMask ({m_LowResMask.width}x{m_LowResMask.height}, {m_LowResMask.format}) needs recreation for new size {width}x{height} or format R8.", DebugFlags.Initialization);
            texturePool.ReleaseTexture(m_LowResMask);
            TrackResourceRelease("m_LowResMask_PreRecreate_Ensure");
            m_LowResMask = null; // Ensure it's null so it gets re-fetched
            needsRecreation = true;
        }

        if (needsRecreation)
        {
            m_LowResMask = texturePool.GetTexture(width, height, RenderTextureFormat.R8);
            m_LowResMask.name = "WallSegmentation_LowResMask_Field_Dynamic";
            m_LowResMask.enableRandomWrite = false;
            m_LowResMask.filterMode = FilterMode.Point;
            if (!m_LowResMask.IsCreated())
            {
                m_LowResMask.Create();
            }
            ClearRenderTexture(m_LowResMask, Color.clear);
            TrackResourceCreation("m_LowResMask_Field_Dynamic");
            Log($"[EnsureLowResMask] Создана/получена m_LowResMask ({width}x{height}, R8, Point).", DebugFlags.Initialization);
        }
        else
        {
            // Texture existed and is correct, ensure properties are still as expected
            m_LowResMask.enableRandomWrite = false;
            m_LowResMask.filterMode = FilterMode.Point;
            // Optionally clear if it might be dirty from previous use, though pool should return clean textures
            ClearRenderTexture(m_LowResMask, Color.clear);
            Log($"[EnsureLowResMask] m_LowResMask ({width}x{height}, R8, Point) already exists and is correct.", DebugFlags.Initialization);
        }
    }
}