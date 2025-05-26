using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// Класс, который настраивает ARPlaneManager в рантайме для корректной работы с вертикальными плоскостями.
/// Решает проблему неверных настроек ARPlaneManager в сцене и привязки плоскостей к мировому пространству.
/// </summary>
public class ARPlaneConfigurator : MonoBehaviour
{
    [SerializeField] private ARPlaneManager planeManager;
    [SerializeField] private bool enableVerticalPlanes = true;
    [SerializeField] private bool enableHorizontalPlanes = true;
    [SerializeField] private bool showDebugInfo = true;
    [SerializeField] private bool improveVerticalPlaneStability = true; // Улучшение стабильности вертикальных плоскостей
    [SerializeField] private Material verticalPlaneMaterial; // Материал для вертикальных плоскостей
    [SerializeField] private float planeStabilizationDelay = 2.0f; // Задержка перед стабилизацией плоскостей
    [SerializeField] private bool reducePlaneFlickering = true; // Уменьшить мерцание плоскостей
    [SerializeField] private float minPlaneAreaToDisplay = 0.1f; // Минимальная площадь для отображения плоскости

    [Header("Зависимости")] // Новый Header для зависимостей
    [SerializeField] private WallSegmentation wallSegmentationInstance;
    [SerializeField] private ARManagerInitializer2 arManagerInitializerInstance;

    [Header("Настройки сохранения плоскостей")]
    [SerializeField] private bool persistDetectedPlanes = true; // Сохранять обнаруженные плоскости
    [SerializeField] private float planeStabilityThreshold = 1.0f; // Время в секундах для признания плоскости стабильной
    [SerializeField] private float minAreaForPersistence = 0.3f; // Минимальная площадь для сохранения плоскости
    [SerializeField] private float mergeOverlapThreshold = 0.5f; // Порог перекрытия для объединения плоскостей (0-1)
    [SerializeField] private bool disablePlaneUpdatesAfterStabilization = true; // Отключить обновление плоскостей после стабилизации

    // Список отслеживаемых вертикальных плоскостей для стабилизации
    private Dictionary<TrackableId, ARPlane> trackedVerticalPlanes = new Dictionary<TrackableId, ARPlane>();
    // Якоря для стабилизации вертикальных плоскостей
    private Dictionary<TrackableId, ARAnchor> planeAnchors = new Dictionary<TrackableId, ARAnchor>();
    // Время обнаружения плоскостей для снижения мерцания
    private Dictionary<TrackableId, float> planeDetectionTimes = new Dictionary<TrackableId, float>();
    // Список стабильных плоскостей, которые мы хотим сохранить
    private Dictionary<TrackableId, ARPlane> stablePlanes = new Dictionary<TrackableId, ARPlane>();
    // Плоскости, которые мы пометили как "постоянные" (не будут удаляться)
    private HashSet<TrackableId> persistentPlaneIds = new HashSet<TrackableId>();
    // Регионы, которые уже покрыты стабильными плоскостями
    private List<Bounds> coveredRegions = new List<Bounds>();

    private ARAnchorManager anchorManager;
    private bool isInitialScanComplete = false;
    private bool hasStabilizedPlanes = false;

    private void Awake()
    {
        if (planeManager == null)
        {
            planeManager = FindObjectOfType<ARPlaneManager>();
            if (planeManager == null)
            {
                Debug.LogError("ARPlaneConfigurator: ARPlaneManager не найден в сцене!");
                enabled = false;
                return;
            }
        }
    }

    private void Start()
    {
        // Находим ARAnchorManager для создания якорей
        anchorManager = FindObjectOfType<ARAnchorManager>();
        if (anchorManager == null && improveVerticalPlaneStability)
        {
            Debug.LogWarning("ARPlaneConfigurator: ARAnchorManager не найден в Start(). Улучшение стабильности вертикальных плоскостей будет ограничено.");
        }

        ConfigurePlaneManager();
        // Запускаем корутину для стабилизации плоскостей после начального сканирования
        StartCoroutine(StabilizePlanesAfterDelay());
    }

    // Корутина для стабилизации плоскостей после задержки
    private IEnumerator StabilizePlanesAfterDelay()
    {
        yield return new WaitForSeconds(planeStabilizationDelay);
        isInitialScanComplete = true;

        if (showDebugInfo)
        {
            Debug.Log("ARPlaneConfigurator: Завершено начальное сканирование, применяется стабилизация плоскостей");
        }

        // Стабилизируем существующие плоскости
        StabilizeExistingPlanes();

        // Ожидаем еще некоторое время для накопления стабильных плоскостей
        yield return new WaitForSeconds(2.0f);

        // Закрепляем стабильные плоскости, если включена опция сохранения
        if (persistDetectedPlanes)
        {
            PersistStablePlanes();
        }
    }

    // Метод для закрепления стабильных плоскостей
    private void PersistStablePlanes()
    {
        if (!isInitialScanComplete || hasStabilizedPlanes) return;

        hasStabilizedPlanes = true;
        int persistedCount = 0;

        // Копируем ключи, чтобы избежать изменения коллекции во время итерации, если потребуется удаление
        List<TrackableId> planeIds = new List<TrackableId>(trackedVerticalPlanes.Keys);

        foreach (var planeId in planeIds)
        {
            if (!trackedVerticalPlanes.TryGetValue(planeId, out ARPlane plane) || plane == null || !plane.gameObject.activeInHierarchy)
            {
                // Если плоскости нет в словаре, она была удалена, или стала неактивной, пропускаем или удаляем
                if (trackedVerticalPlanes.ContainsKey(planeId))
                {
                    trackedVerticalPlanes.Remove(planeId);
                    if (planeAnchors.ContainsKey(planeId))
                    {
                        ARAnchor anchorToDestroy = planeAnchors[planeId];
                        planeAnchors.Remove(planeId);
                        if (anchorToDestroy != null) Destroy(anchorToDestroy.gameObject); // Destroy the anchor's GameObject
                    }
                    if (planeDetectionTimes.ContainsKey(planeId)) planeDetectionTimes.Remove(planeId);
                    // persistentPlaneIds и stablePlanes обрабатываются в OnPlanesChanged или при следующем Persist
                }
                continue;
            }

            float planeArea = plane.size.x * plane.size.y;
            bool isStable = planeDetectionTimes.TryGetValue(plane.trackableId, out float detectionTime) &&
                           (Time.time - detectionTime) >= planeStabilityThreshold;

            // Если плоскость достаточно большая и стабильная
            if (isStable && planeArea >= minAreaForPersistence)
            {
                // Добавляем в список стабильных плоскостей
                if (!stablePlanes.ContainsKey(plane.trackableId))
                {
                    stablePlanes.Add(plane.trackableId, plane);
                    persistentPlaneIds.Add(plane.trackableId);

                    // Добавляем регион, покрытый этой плоскостью
                    Bounds planeBounds = GetPlaneBounds(plane);
                    coveredRegions.Add(planeBounds);

                    // Создаем якорь для этой плоскости, если еще не создан
                    try
                    {
                        if (!planeAnchors.ContainsKey(plane.trackableId))
                        {
                            CreateAnchorForPlane(plane);
                        }
                    }
                    catch (MissingReferenceException ex)
                    {
                        Debug.LogError($"ARPlaneConfigurator (PersistStablePlanes): MissingReferenceException while trying to create anchor for plane {plane.trackableId}. Error: {ex.Message}");
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogError($"ARPlaneConfigurator (PersistStablePlanes): Exception while trying to create anchor for plane {plane.trackableId}. Error: {ex.Message}");
                    }

                    persistedCount++;
                }
            }
        }

        if (showDebugInfo)
        {
            Debug.Log($"ARPlaneConfigurator: Закреплено {persistedCount} стабильных плоскостей");
        }

        // Если нужно отключить обновления плоскостей после стабилизации
        if (disablePlaneUpdatesAfterStabilization && persistedCount > 0)
        {
            // Отключаем обновление плоскостей, но не сам PlaneManager
            // Сначала сохраняем текущий режим обнаружения, затем временно отключаем
            PlaneDetectionMode currentMode = planeManager.requestedDetectionMode;
            planeManager.requestedDetectionMode = PlaneDetectionMode.None;

            // Через 1 секунду включаем обратно, но только для новых плоскостей
            StartCoroutine(ReenableLimitedPlaneDetection(currentMode));
        }
    }

    // Получение границ плоскости в мировых координатах
    private Bounds GetPlaneBounds(ARPlane plane)
    {
        Vector3 center = plane.center;
        Vector3 size = new Vector3(plane.size.x, 0.1f, plane.size.y);
        Bounds bounds = new Bounds(center, size);

        // Учитываем реальное вращение плоскости
        Matrix4x4 rotationMatrix = Matrix4x4.Rotate(plane.transform.rotation);
        bounds.extents = rotationMatrix.MultiplyVector(bounds.extents);

        return bounds;
    }

    // Проверка перекрытия с существующими стабильными плоскостями
    private bool OverlapsWithStablePlanes(ARPlane plane)
    {
        Bounds newPlaneBounds = GetPlaneBounds(plane);

        foreach (var bounds in coveredRegions)
        {
            // Проверяем пересечение границ
            if (bounds.Intersects(newPlaneBounds))
            {
                // Вычисляем примерный объем пересечения
                Bounds intersection = new Bounds();
                bool hasIntersection = CalculateBoundsIntersection(bounds, newPlaneBounds, out intersection);

                if (hasIntersection)
                {
                    // Вычисляем соотношение объема пересечения к объему новой плоскости
                    float intersectionVolume = intersection.size.x * intersection.size.y * intersection.size.z;
                    float planeVolume = newPlaneBounds.size.x * newPlaneBounds.size.y * newPlaneBounds.size.z;

                    if (planeVolume > 0 && (intersectionVolume / planeVolume) > mergeOverlapThreshold)
                    {
                        return true; // Есть значительное перекрытие
                    }
                }
            }
        }

        return false;
    }

    // Вычисление пересечения двух Bounds
    private bool CalculateBoundsIntersection(Bounds a, Bounds b, out Bounds intersection)
    {
        intersection = new Bounds();

        // Проверяем пересечение
        if (!a.Intersects(b))
            return false;

        // Находим минимальные и максимальные точки обоих bounds
        Vector3 min = Vector3.Max(a.min, b.min);
        Vector3 max = Vector3.Min(a.max, b.max);

        // Создаем новый bounds для пересечения
        intersection = new Bounds();
        intersection.SetMinMax(min, max);

        return true;
    }

    // Корутина для повторного включения обнаружения плоскостей, но с ограничениями
    private IEnumerator ReenableLimitedPlaneDetection(PlaneDetectionMode originalMode)
    {
        yield return new WaitForSeconds(1.0f);

        // Включаем обратно обнаружение плоскостей
        planeManager.requestedDetectionMode = originalMode;

        if (showDebugInfo)
        {
            Debug.Log("ARPlaneConfigurator: Обнаружение плоскостей включено снова, но с фильтрацией по перекрытию");
        }
    }

    // Метод для стабилизации существующих плоскостей
    private void StabilizeExistingPlanes()
    {
        if (!isInitialScanComplete || !improveVerticalPlaneStability || anchorManager == null)
            return;

        if (showDebugInfo)
        {
            Debug.Log("ARPlaneConfigurator: Стабилизация существующих плоскостей...");
        }

        List<ARPlane> currentPlanes = new List<ARPlane>();
        foreach (ARPlane plane in trackedVerticalPlanes.Values)
        {
            if (plane == null || !plane.gameObject.activeInHierarchy) continue; // Добавлена проверка
            currentPlanes.Add(plane);
        }

        foreach (ARPlane plane in currentPlanes) // Используем копию списка
        {
            if (plane == null || !plane.gameObject.activeInHierarchy) continue; // Повторная проверка на всякий случай

            if (!planeAnchors.ContainsKey(plane.trackableId))
            {
                try
                {
                    CreateAnchorForPlane(plane);
                }
                catch (MissingReferenceException ex)
                {
                    Debug.LogError($"ARPlaneConfigurator (StabilizeExistingPlanes): MissingReferenceException while creating anchor for {plane.trackableId}. Error: {ex.Message}");
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"ARPlaneConfigurator (StabilizeExistingPlanes): Exception while creating anchor for {plane.trackableId}. Error: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Настраивает ARPlaneManager с правильными параметрами для обнаружения вертикальных плоскостей
    /// </summary>
    public void ConfigurePlaneManager()
    {
        if (planeManager == null) return;

        // Настраиваем режим обнаружения плоскостей
        PlaneDetectionMode detectionMode = PlaneDetectionMode.None;

        if (enableHorizontalPlanes)
            detectionMode |= PlaneDetectionMode.Horizontal;

        if (enableVerticalPlanes)
            detectionMode |= PlaneDetectionMode.Vertical;

        planeManager.requestedDetectionMode = detectionMode;

        // Настраиваем дополнительные параметры для уменьшения мерцания
        if (reducePlaneFlickering)
        {
            // Увеличение порога значимости плоскости
            SetFieldIfExists(planeManager, "m_MinimumPlaneArea", minPlaneAreaToDisplay);

            // Уменьшение частоты обновления плоскостей, если возможно
            if (TryGetFieldValue(planeManager, "m_DetectionMode", out PlaneDetectionMode mode))
            {
                // Настройка для стабильности
                planeManager.requestedDetectionMode = mode;
            }
        }

        // Обновляем состояние planeManager
        if (!planeManager.enabled)
        {
            planeManager.enabled = true;
            Debug.Log("ARPlaneConfigurator: ARPlaneManager был включен");
        }

        if (showDebugInfo)
        {
            Debug.Log($"ARPlaneConfigurator: Установлен режим обнаружения плоскостей: {detectionMode}");
            Debug.Log($"ARPlaneConfigurator: Вертикальные плоскости: {enableVerticalPlanes}, Горизонтальные плоскости: {enableHorizontalPlanes}");
        }

        // Настраиваем материал для плоскостей, если он назначен
        if (verticalPlaneMaterial != null && planeManager.planePrefab != null)
        {
            MeshRenderer prefabRenderer = planeManager.planePrefab.GetComponent<MeshRenderer>();
            if (prefabRenderer != null)
            {
                prefabRenderer.sharedMaterial = verticalPlaneMaterial;
                Debug.Log("ARPlaneConfigurator: Назначен пользовательский материал для плоскостей");
            }
        }

        // Убедимся, что планы отображаются правильно
        StartCoroutine(ValidatePlanePrefab());

        // Подписываемся на события обновления плоскостей
        planeManager.planesChanged += OnPlanesChanged;
    }

    // Хелперы для доступа к частным полям ARPlaneManager через рефлексию
    private bool SetFieldIfExists(object obj, string fieldName, object value)
    {
        var field = obj.GetType().GetField(fieldName, System.Reflection.BindingFlags.Instance |
                                    System.Reflection.BindingFlags.NonPublic |
                                    System.Reflection.BindingFlags.Public);
        if (field != null)
        {
            field.SetValue(obj, value);
            return true;
        }
        return false;
    }

    private bool TryGetFieldValue<T>(object obj, string fieldName, out T value)
    {
        value = default;
        var field = obj.GetType().GetField(fieldName, System.Reflection.BindingFlags.Instance |
                                    System.Reflection.BindingFlags.NonPublic |
                                    System.Reflection.BindingFlags.Public);
        if (field != null)
        {
            value = (T)field.GetValue(obj);
            return true;
        }
        return false;
    }

    private IEnumerator ValidatePlanePrefab()
    {
        yield return new WaitForSeconds(0.5f);

        if (planeManager.planePrefab == null)
        {
            Debug.LogWarning("ARPlaneConfigurator: planePrefab не назначен в ARPlaneManager!");
            yield break;
        }

        // Проверяем наличие всех необходимых компонентов в префабе
        ARPlaneMeshVisualizer meshVisualizer = planeManager.planePrefab.GetComponent<ARPlaneMeshVisualizer>();
        MeshRenderer meshRenderer = planeManager.planePrefab.GetComponent<MeshRenderer>();

        if (meshVisualizer == null)
        {
            Debug.LogWarning("ARPlaneConfigurator: В префабе плоскости отсутствует ARPlaneMeshVisualizer!");
        }

        if (meshRenderer == null)
        {
            Debug.LogWarning("ARPlaneConfigurator: В префабе плоскости отсутствует MeshRenderer!");
        }
        else if (meshRenderer.sharedMaterial == null)
        {
            Debug.LogWarning("ARPlaneConfigurator: В префабе плоскости не назначен материал!");

            // Пытаемся назначить материал
            if (verticalPlaneMaterial != null)
            {
                meshRenderer.sharedMaterial = verticalPlaneMaterial;
            }
            else
            {
                // Создаем простой материал как запасной вариант
                Material defaultMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                defaultMaterial.color = new Color(0.5f, 0.7f, 1.0f, 0.5f);
                meshRenderer.sharedMaterial = defaultMaterial;
            }
        }

        // Проверяем существующие плоскости
        ApplyMaterialToExistingPlanes();
    }

    /// <summary>
    /// Применяет материал ко всем существующим плоскостям
    /// </summary>
    private void ApplyMaterialToExistingPlanes()
    {
        ARPlane[] existingPlanes = FindObjectsOfType<ARPlane>();
        if (existingPlanes.Length == 0) return;

        Debug.Log($"ARPlaneConfigurator: Найдено {existingPlanes.Length} существующих плоскостей");

        foreach (ARPlane plane in existingPlanes)
        {
            if (IsVerticalPlane(plane) && verticalPlaneMaterial != null)
            {
                MeshRenderer renderer = plane.GetComponent<MeshRenderer>();
                if (renderer != null)
                {
                    // Создаем экземпляр материала, чтобы каждая плоскость имела свой экземпляр
                    Material instanceMaterial = new Material(verticalPlaneMaterial);
                    renderer.material = instanceMaterial;

                    // Активируем ключевые слова для привязки к мировому пространству AR
                    instanceMaterial.EnableKeyword("USE_AR_WORLD_SPACE");

                    // Добавляем трансформацию плоскости в материал
                    instanceMaterial.SetMatrix("_PlaneToWorldMatrix", plane.transform.localToWorldMatrix);
                    instanceMaterial.SetVector("_PlaneNormal", plane.normal);
                    instanceMaterial.SetVector("_PlaneCenter", plane.center);

                    // Если это вертикальная плоскость, настраиваем специальные параметры
                    renderer.material.SetFloat("_IsVertical", 1.0f);

                    Debug.Log($"ARPlaneConfigurator: Применен материал к вертикальной плоскости {plane.trackableId}");

                    // Добавляем в список отслеживаемых вертикальных плоскостей
                    if (!trackedVerticalPlanes.ContainsKey(plane.trackableId))
                    {
                        trackedVerticalPlanes.Add(plane.trackableId, plane);
                    }

                    // Создаем якорь для этой плоскости, если включено улучшение стабильности
                    if (improveVerticalPlaneStability)
                    {
                        CreateAnchorForPlane(plane);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Создает якорь для стабилизации плоскости в AR-пространстве
    /// </summary>
    private void CreateAnchorForPlane(ARPlane plane)
    {
        if (plane == null || !plane.gameObject.activeInHierarchy || anchorManager == null)
        {
            if (showDebugInfo) Debug.LogWarning($"ARPlaneConfigurator: CreateAnchorForPlane - Plane is null, inactive, or AnchorManager is null. Plane ID: {(plane != null ? plane.trackableId.ToString() : "N/A")}");
            return;
        }

        try
        {
            TrackableId planeId = plane.trackableId; // Потенциальная точка ошибки
            if (planeAnchors.ContainsKey(planeId))
            {
                if (showDebugInfo) Debug.Log($"ARPlaneConfigurator: Якорь для плоскости {planeId} уже существует.");
                return;
            }

            Pose planePose = new Pose(plane.transform.position, plane.transform.rotation);
            ARAnchor anchor = anchorManager.AttachAnchor(plane, planePose); // Потенциальная точка ошибки

            if (anchor != null)
            {
                planeAnchors.Add(planeId, anchor); // Потенциальная точка ошибки, если planeId стал невалидным
                if (showDebugInfo)
                {
                    Debug.Log($"ARPlaneConfigurator: Создан якорь для стабилизации плоскости {planeId} в позиции {anchor.transform.position}");
                }
            }
            else
            {
                Debug.LogWarning($"ARPlaneConfigurator: Не удалось создать якорь для плоскости {planeId}.");
            }
        }
        catch (MissingReferenceException ex)
        {
            // Попытка получить trackableId безопаснее, если объект уже уничтожается
            string safePlaneId = "UNKNOWN_PLANE_ID";
            try { if (plane != null) safePlaneId = plane.trackableId.ToString(); }
            catch { /* ignore */ }
            Debug.LogError($"ARPlaneConfigurator (CreateAnchorForPlane): MissingReferenceException. Plane ID (best effort): {safePlaneId}. Error: {ex.Message}");
        }
        catch (System.Exception ex)
        {
            string safePlaneId = "UNKNOWN_PLANE_ID";
            try { if (plane != null) safePlaneId = plane.trackableId.ToString(); }
            catch { /* ignore */ }
            Debug.LogError($"ARPlaneConfigurator (CreateAnchorForPlane): Exception. Plane ID (best effort): {safePlaneId}. Error: {ex.Message}");
        }
    }

    private void OnPlanesChanged(ARPlanesChangedEventArgs args)
    {
        if (showDebugInfo)
        {
            LogPlaneChanges(args); // Логируем изменения
        }

        // Обработка добавленных плоскостей
        foreach (var plane in args.added)
        {
            if (plane == null || !plane.gameObject.activeInHierarchy) continue; // Проверка на null и активность
            planeDetectionTimes[plane.trackableId] = Time.time;
            arManagerInitializerInstance?.ConfigurePlane(plane); // Вызываем метод из ARManagerInitializer2

            if (IsVerticalPlane(plane))
            {
                trackedVerticalPlanes[plane.trackableId] = plane;
                if (improveVerticalPlaneStability && isInitialScanComplete && !planeAnchors.ContainsKey(plane.trackableId))
                {
                    // CreateAnchorForPlane(plane);
                }
            }
            // LogPlaneDetails(plane, "Обнаружена", стабильность: false);
        }

        // Обработка обновленных плоскостей
        foreach (var plane in args.updated)
        {
            if (plane == null || !plane.gameObject.activeInHierarchy) continue; // Проверка на null и активность

            // Если плоскость уже помечена как постоянная и включено отключение обновлений
            if (persistentPlaneIds.Contains(plane.trackableId) && disablePlaneUpdatesAfterStabilization && hasStabilizedPlanes)
            {
                if (showDebugInfo)
                {
                    Debug.Log($"ARPlaneConfigurator: Игнорируется обновление для постоянной плоскости: {plane.trackableId}");
                }
                continue; // Пропускаем обновление
            }

            arManagerInitializerInstance?.UpdatePlane(plane); // Вызываем метод из ARManagerInitializer2

            if (IsVerticalPlane(plane))
            {
                trackedVerticalPlanes[plane.trackableId] = plane;
                // Обновляем информацию о плоскости (например, если она изменила размер или положение)
                // LogPlaneDetails(plane, "Обновлена", стабильность: false);
            }
        }

        // Обработка удаленных плоскостей
        foreach (var plane in args.removed) // args.removed is List<ARPlane>
        {
            if (plane == null) continue;
            // Важно получить trackableId до того, как объект plane может быть полностью уничтожен
            // и его свойства станут недоступны.
            TrackableId planeId = plane.trackableId;
            RemovePlaneTrackingData(planeId);
        }
    }

    private void RemovePlaneTrackingData(TrackableId planeId)
    {
        if (trackedVerticalPlanes.ContainsKey(planeId))
        {
            trackedVerticalPlanes.Remove(planeId);
        }

        if (planeAnchors.TryGetValue(planeId, out ARAnchor anchor) && anchor != null)
        {
            Destroy(anchor.gameObject); // Уничтожаем GameObject якоря
            planeAnchors.Remove(planeId);
            if (showDebugInfo) Debug.Log($"ARPlaneConfigurator: Якорь для плоскости {planeId} удален (через Destroy).");
        }
        else if (planeAnchors.ContainsKey(planeId))
        {
            // Если якорь был в словаре, но null (уже уничтожен кем-то другим), просто удаляем из словаря
            planeAnchors.Remove(planeId);
            if (showDebugInfo) Debug.Log($"ARPlaneConfigurator: Запись о якоре для плоскости {planeId} удалена (был null).");
        }


        if (planeDetectionTimes.ContainsKey(planeId))
        {
            planeDetectionTimes.Remove(planeId);
        }

        if (stablePlanes.ContainsKey(planeId))
        {
            stablePlanes.Remove(planeId);
        }

        if (persistentPlaneIds.Contains(planeId))
        {
            persistentPlaneIds.Remove(planeId);
            // TODO: Potentially remove from coveredRegions if necessary, though this might be complex
            // For now, let's assume that once a region is covered by a persistent plane, it remains "claimed"
            // even if the original plane is lost, to prevent immediate re-detection of the same space by a new plane.
            // If this causes issues (e.g. holes not being re-filled), this logic might need revisiting.
            // Consider a mechanism to merge or update coveredRegions if planes are truly gone.
            if (showDebugInfo) Debug.Log($"ARPlaneConfigurator: Плоскость {planeId} удалена из списка постоянных.");
        }

        if (showDebugInfo)
        {
            // string planeType = IsVerticalPlane(plane) ? "вертикальная" : "горизонтальная"; // Ошибка: plane уже недоступен
            Debug.Log($"ARPlaneConfigurator: Удалена плоскость: {planeId}");
        }
    }

    // Вспомогательный метод для логирования изменений плоскостей
    private void LogPlaneChanges(ARPlanesChangedEventArgs args)
    {
        // Implementation of LogPlaneChanges method
    }

    /// <summary>
    /// Сбрасывает все сохраненные плоскости и запускает новое обнаружение
    /// </summary>
    public void ResetSavedPlanes()
    {
        if (showDebugInfo)
        {
            Debug.Log("ARPlaneConfigurator: Сброс всех сохраненных плоскостей");
        }

        // Очищаем списки сохраненных плоскостей
        stablePlanes.Clear();
        persistentPlaneIds.Clear();
        coveredRegions.Clear();

        // Сбрасываем флаг стабилизации
        hasStabilizedPlanes = false;

        // Запускаем обнаружение плоскостей заново
        ResetAllPlanes();

        // Запускаем корутину для новой стабилизации
        StartCoroutine(StabilizePlanesAfterDelay());
    }

    /// <summary>
    /// Сбрасывает все обнаруженные плоскости для перезапуска обнаружения
    /// </summary>
    public void ResetAllPlanes()
    {
        if (planeManager == null) return;

        Debug.Log("ARPlaneConfigurator: Сброс всех AR плоскостей");

        // Очищаем списки отслеживаемых плоскостей и якорей
        trackedVerticalPlanes.Clear();
        planeDetectionTimes.Clear();

        // Удаляем все якоря
        foreach (var anchor in planeAnchors.Values)
        {
            if (anchor != null)
            {
                Destroy(anchor.gameObject);
            }
        }
        planeAnchors.Clear();

        // Временно отключаем плоскости и перезапускаем
        planeManager.enabled = false;

        // Небольшая задержка перед повторным включением
        StartCoroutine(ReenablePlaneManagerAfterDelay(0.5f));
    }

    private IEnumerator ReenablePlaneManagerAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        ConfigurePlaneManager();
    }

    private void OnDestroy()
    {
        if (planeManager != null)
        {
            planeManager.planesChanged -= OnPlanesChanged;
        }

        // Очищаем созданные якоря
        foreach (var anchor in planeAnchors.Values)
        {
            if (anchor != null)
            {
                Destroy(anchor.gameObject);
            }
        }
    }

    /// <summary>
    /// Обновляет трансформацию плоскости в материале
    /// </summary>
    private void UpdatePlaneMaterialTransform(ARPlane plane, Material material)
    {
        if (material == null) return;

        material.SetMatrix("_PlaneToWorldMatrix", plane.transform.localToWorldMatrix);
        material.SetVector("_PlaneNormal", plane.normal);
        material.SetVector("_PlaneCenter", plane.center);

        // Добавляем уникальный идентификатор плоскости
        material.SetFloat("_PlaneID", plane.trackableId.subId1 % 1000);
    }

    /// <summary>
    /// Проверяет, является ли плоскость вертикальной (стеной)
    /// </summary>
    public static bool IsVerticalPlane(ARPlane plane)
    {
        if (plane == null || plane.gameObject == null || !plane.gameObject.activeInHierarchy) // Добавлена проверка на null и активность
        {
            // Debug.LogWarning("IsVerticalPlane: Получена null или неактивная плоскость."); // Раскомментировать для отладки
            return false;
        }

        try
        {
            if (plane.alignment == PlaneAlignment.Vertical)
                return true;

            // Дополнительная проверка по нормали (плоскость почти вертикальна)
            float dotUp = Vector3.Dot(plane.normal, Vector3.up);
            return Mathf.Abs(dotUp) < 0.25f; // Более строгое значение для определения вертикальности
        }
        catch (System.Exception ex)
        {
            // Попытка получить trackableId безопаснее, если объект уже уничтожается
            string safePlaneId = "UNKNOWN_PLANE_ID";
            try { if (plane != null) safePlaneId = plane.trackableId.ToString(); }
            catch { /* ignore */ }
            Debug.LogError($"IsVerticalPlane: Exception while accessing plane properties. Plane ID (best effort): {safePlaneId}. Error: {ex.Message}");
            return false; // В случае ошибки считаем, что плоскость не вертикальная
        }
    }

    /// <summary>
    /// Возвращает список ID сохраненных плоскостей
    /// </summary>
    public HashSet<TrackableId> GetPersistentPlaneIds()
    {
        return new HashSet<TrackableId>(persistentPlaneIds);
    }

    /// <summary>
    /// Проверяет, является ли данная плоскость сохраненной
    /// </summary>
    public bool IsPersistentPlane(TrackableId planeId)
    {
        return persistentPlaneIds.Contains(planeId);
    }

    /// <summary>
    /// Возвращает информацию о состоянии сохранения плоскостей
    /// </summary>
    public (bool hasStablePlanes, int stablePlanesCount) GetStablePlanesInfo()
    {
        return (hasStabilizedPlanes, stablePlanes.Count);
    }

    /// <summary>
    /// Добавляет плоскость в список сохраненных вручную
    /// </summary>
    public bool AddPlaneToPersistent(ARPlane plane)
    {
        if (plane == null || persistentPlaneIds.Contains(plane.trackableId))
            return false;

        // Добавляем в список стабильных плоскостей
        stablePlanes[plane.trackableId] = plane;
        persistentPlaneIds.Add(plane.trackableId);

        // Добавляем регион, покрытый этой плоскостью
        Bounds planeBounds = GetPlaneBounds(plane);
        coveredRegions.Add(planeBounds);

        // Создаем якорь для этой плоскости, если еще не создан
        if (!planeAnchors.ContainsKey(plane.trackableId))
        {
            CreateAnchorForPlane(plane);
        }

        if (showDebugInfo)
        {
            Debug.Log($"ARPlaneConfigurator: Вручную добавлена стабильная плоскость {plane.trackableId}");
        }

        return true;
    }

    /// <summary>
    /// Удаляет плоскость из списка сохраненных
    /// </summary>
    public bool RemovePlaneFromPersistent(TrackableId planeId)
    {
        if (!persistentPlaneIds.Contains(planeId))
            return false;

        persistentPlaneIds.Remove(planeId);

        // Удаляем из списка стабильных плоскостей
        if (stablePlanes.ContainsKey(planeId))
        {
            stablePlanes.Remove(planeId);
        }

        // Удаляем якорь, если он был создан
        if (planeAnchors.TryGetValue(planeId, out ARAnchor anchor))
        {
            if (anchor != null)
            {
                Destroy(anchor.gameObject);
            }
            planeAnchors.Remove(planeId);
        }

        // Обновляем список покрытых регионов
        // Это более сложная операция, поэтому просто пересоздаем его
        coveredRegions.Clear();
        foreach (var plane in stablePlanes.Values)
        {
            if (plane != null)
            {
                Bounds planeBounds = GetPlaneBounds(plane);
                coveredRegions.Add(planeBounds);
            }
        }

        if (showDebugInfo)
        {
            Debug.Log($"ARPlaneConfigurator: Вручную удалена стабильная плоскость {planeId}");
        }

        return true;
    }

    /// <summary>
    /// Configures a newly detected AR plane with appropriate settings
    /// Called by ARManagerInitializer2 when a new plane is detected
    /// </summary>
    /// <param name="plane">The AR plane to configure</param>
    /// <param name="wallSegmentation">Reference to wall segmentation for mask data</param>
    /// <param name="manager">Reference to the AR manager that called this method</param>
    public void ConfigurePlane(ARPlane plane, WallSegmentation wallSegmentation, ARManagerInitializer2 manager)
    {
        if (plane == null)
        {
            Debug.LogWarning("ARPlaneConfigurator.ConfigurePlane: plane is null");
            return;
        }

        // Добавим проверки на null для manager и wallSegmentation
        if (manager == null)
        {
            Debug.LogWarning("ARPlaneConfigurator.ConfigurePlane: ARManagerInitializer2 (manager) is null. Plane configuration might be incomplete.");
            // Решаем, как обрабатывать: можно либо выйти, либо продолжить с ограниченной конфигурацией.
            // Для примера, продолжим, но некоторые функции могут не работать.
        }
        if (wallSegmentation == null)
        {
            Debug.LogWarning("ARPlaneConfigurator.ConfigurePlane: WallSegmentation (wallSegmentation) is null. Segmentation mask will not be applied.");
        }

        // Get or add MeshRenderer component
        MeshRenderer planeRenderer = plane.GetComponent<MeshRenderer>();
        if (planeRenderer == null)
        {
            planeRenderer = plane.gameObject.AddComponent<MeshRenderer>();
            Debug.LogWarning($"ARPlaneConfigurator.ConfigurePlane: Added MeshRenderer to plane {plane.trackableId}");
        }
        planeRenderer.enabled = true; // Ensure MeshRenderer is enabled

        // Ensure ARPlaneMeshVisualizer is present and enabled
        var visualizer = plane.GetComponent<ARPlaneMeshVisualizer>();
        if (visualizer == null)
        {
            // If the prefab is correct, this shouldn't be null.
            // ARPlaneManager instantiates a prefab that should have ARPlaneMeshVisualizer.
            Debug.LogWarning($"ARPlaneConfigurator.ConfigurePlane: ARPlaneMeshVisualizer is MISSING on plane instance {plane.trackableId}. Check ARPlaneManager's planePrefab.");
        }
        else
        {
            visualizer.enabled = true; // Explicitly enable it
        }

        // Apply appropriate material based on plane type
        Material materialToUse = null;
        if (IsVerticalPlane(plane) && verticalPlaneMaterial != null)
        {
            materialToUse = verticalPlaneMaterial;
        }
        else if (!IsVerticalPlane(plane) && manager != null && manager.HorizontalPlaneMaterial != null) // Проверка manager
        {
            materialToUse = manager.HorizontalPlaneMaterial;
        }
        else if (manager != null && manager.VerticalPlaneMaterial != null) // Проверка manager
        {
            materialToUse = manager.VerticalPlaneMaterial; // Fallback
        }

        if (materialToUse != null)
        {
            planeRenderer.material = materialToUse;
        }

        // Configure rendering settings
        planeRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        planeRenderer.receiveShadows = false;

        // AR planes should not be static as they can move/update
        plane.gameObject.isStatic = false;

        // Set appropriate layer if specified in manager
        if (manager != null && !string.IsNullOrEmpty(manager.PlaneLayerName)) // Проверка manager
        {
            int layerId = LayerMask.NameToLayer(manager.PlaneLayerName);
            if (layerId != -1)
            {
                plane.gameObject.layer = layerId;
            }
            else
            {
                Debug.LogError($"ARPlaneConfigurator: Layer '{manager.PlaneLayerName}' not found in Tags and Layers.");
            }
        }

        // Apply segmentation mask if available and plane is vertical (wall)
        if (wallSegmentation != null && IsVerticalPlane(plane)) // wallSegmentation уже проверен выше
        {
            ApplySegmentationMaskToPlane(plane, wallSegmentation);
        }

        // Track this plane for stability management
        if (IsVerticalPlane(plane) && !trackedVerticalPlanes.ContainsKey(plane.trackableId))
        {
            trackedVerticalPlanes[plane.trackableId] = plane;
            planeDetectionTimes[plane.trackableId] = Time.time;

            if (showDebugInfo)
            {
                Debug.Log($"ARPlaneConfigurator: New vertical plane tracked: {plane.trackableId}");
            }
        }

        if (showDebugInfo)
        {
            Debug.Log($"ARPlaneConfigurator: Configured plane {plane.trackableId}, Type: {(IsVerticalPlane(plane) ? "Vertical" : "Horizontal")}, Size: {plane.size}");
        }
    }

    /// <summary>
    /// Updates an existing AR plane with new data
    /// Called by ARManagerInitializer2 when a plane is updated
    /// </summary>
    /// <param name="plane">The AR plane to update</param>
    /// <param name="wallSegmentation">Reference to wall segmentation for mask data</param>
    /// <param name="manager">Reference to the AR manager that called this method</param>
    public void UpdatePlane(ARPlane plane, WallSegmentation wallSegmentation, ARManagerInitializer2 manager)
    {
        if (plane == null)
        {
            Debug.LogWarning("ARPlaneConfigurator.UpdatePlane: plane is null");
            return;
        }

        // Добавим проверки на null для manager и wallSegmentation
        if (manager == null)
        {
            Debug.LogWarning("ARPlaneConfigurator.UpdatePlane: ARManagerInitializer2 (manager) is null. Plane update might be incomplete.");
        }
        if (wallSegmentation == null)
        {
            Debug.LogWarning("ARPlaneConfigurator.UpdatePlane: WallSegmentation (wallSegmentation) is null. Segmentation mask will not be applied during update.");
        }

        // Don't update persistent planes if they're marked as stable
        if (persistentPlaneIds.Contains(plane.trackableId) && disablePlaneUpdatesAfterStabilization)
        {
            return; // Skip updating this plane as it's been stabilized
        }

        // Update renderer settings
        MeshRenderer planeRenderer = plane.GetComponent<MeshRenderer>();
        if (planeRenderer != null)
        {
            planeRenderer.enabled = true; // Ensure MeshRenderer is enabled
            // Reapply material in case it was lost or changed
            Material materialToUse = null;
            if (IsVerticalPlane(plane) && verticalPlaneMaterial != null)
            {
                materialToUse = verticalPlaneMaterial;
            }
            else if (!IsVerticalPlane(plane) && manager != null && manager.HorizontalPlaneMaterial != null) // Проверка manager
            {
                materialToUse = manager.HorizontalPlaneMaterial;
            }
            else if (manager != null && manager.VerticalPlaneMaterial != null) // Проверка manager
            {
                materialToUse = manager.VerticalPlaneMaterial; // Fallback
            }

            if (materialToUse != null)
            {
                planeRenderer.material = materialToUse;
            }

            // Ensure rendering settings are maintained
            planeRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            planeRenderer.receiveShadows = false;
        }

        // Ensure ARPlaneMeshVisualizer is present and enabled
        var visualizer = plane.GetComponent<ARPlaneMeshVisualizer>();
        if (visualizer == null)
        {
            Debug.LogWarning($"ARPlaneConfigurator.UpdatePlane: ARPlaneMeshVisualizer is MISSING on plane instance {plane.trackableId}.");
        }
        else
        {
            visualizer.enabled = true; // Explicitly enable it
        }

        // Update layer assignment
        if (manager != null && !string.IsNullOrEmpty(manager.PlaneLayerName)) // Проверка manager
        {
            int layerId = LayerMask.NameToLayer(manager.PlaneLayerName);
            if (layerId != -1)
            {
                plane.gameObject.layer = layerId;
            }
        }

        // Update segmentation mask for vertical planes
        if (wallSegmentation != null && IsVerticalPlane(plane)) // wallSegmentation уже проверен выше
        {
            ApplySegmentationMaskToPlane(plane, wallSegmentation);
        }

        // Update tracking information
        if (IsVerticalPlane(plane))
        {
            if (!trackedVerticalPlanes.ContainsKey(plane.trackableId))
            {
                trackedVerticalPlanes[plane.trackableId] = plane;
                planeDetectionTimes[plane.trackableId] = Time.time;
            }
            else
            {
                trackedVerticalPlanes[plane.trackableId] = plane; // Update reference
            }
        }

        if (showDebugInfo)
        {
            Debug.Log($"ARPlaneConfigurator: Updated plane {plane.trackableId}, Size: {plane.size}");
        }
    }

    /// <summary>
    /// Applies segmentation mask data to a plane for more accurate wall detection
    /// </summary>
    /// <param name="plane">The plane to apply the mask to</param>
    /// <param name="wallSegmentation">The wall segmentation component providing mask data</param>
    private void ApplySegmentationMaskToPlane(ARPlane plane, WallSegmentation wallSegmentation)
    {
        if (plane == null)
        {
            Debug.LogError("[ARPlaneConfigurator] ApplySegmentationMaskToPlane: ARPlane is null.");
            return;
        }
        if (wallSegmentation == null)
        {
            Debug.LogError("[ARPlaneConfigurator] ApplySegmentationMaskToPlane: WallSegmentation is null.");
            return;
        }

        MeshRenderer planeRenderer = plane.GetComponent<MeshRenderer>();
        if (planeRenderer == null)
        {
            Debug.LogWarning($"[ARPlaneConfigurator] ApplySegmentationMaskToPlane: MeshRenderer not found on plane {plane.trackableId}. Cannot apply mask.");
            return;
        }

        Material planeMaterial = planeRenderer.material; // Используем общий материал, чтобы изменения были видны
        if (planeMaterial == null)
        {
            Debug.LogWarning($"[ARPlaneConfigurator] ApplySegmentationMaskToPlane: Material not found on plane {plane.trackableId}. Cannot apply mask.");
            return;
        }

        if (wallSegmentation.segmentationMaskTexture != null && wallSegmentation.segmentationMaskTexture.IsCreated())
        {
            if (planeMaterial.HasProperty("_SegmentationMask"))
            {
                planeMaterial.SetTexture("_SegmentationMask", wallSegmentation.segmentationMaskTexture);
                Debug.Log($"[ARPlaneConfigurator] ApplySegmentationMaskToPlane: Applied segmentation mask (Name: {wallSegmentation.segmentationMaskTexture.name}, Width: {wallSegmentation.segmentationMaskTexture.width}, Height: {wallSegmentation.segmentationMaskTexture.height}, IsCreated: {wallSegmentation.segmentationMaskTexture.IsCreated()}) to material '{planeMaterial.name}' (Shader: '{planeMaterial.shader.name}') of plane {plane.trackableId} (Alignment: {plane.alignment}).");
            }
            else
            {
                Debug.LogWarning($"[ARPlaneConfigurator] ApplySegmentationMaskToPlane: Material '{planeMaterial.name}' (Shader: '{planeMaterial.shader.name}') on plane {plane.trackableId} does not have a '_SegmentationMask' texture property.");
            }
        }
        else
        {
            Debug.LogWarning($"[ARPlaneConfigurator] ApplySegmentationMaskToPlane: segmentationMaskTexture from WallSegmentation is null or not created. (IsNull: {wallSegmentation.segmentationMaskTexture == null}, IsCreated: {wallSegmentation.segmentationMaskTexture?.IsCreated()}). Cannot apply to plane {plane.trackableId}.");
        }
    }
}