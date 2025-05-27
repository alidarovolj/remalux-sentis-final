using UnityEngine;
using UnityEngine.UI;
using System.IO;

// [RequireComponent(typeof(RawImage))] // Оставляем, но можно и убрать, если RawImage всегда есть
public class DebugMaskLinker : MonoBehaviour
{
    private RawImage rawImage;
    private WallSegmentation wallSegmentation;
    private int updateCounter = 0;
    private bool hasSavedOnce = false;
    private const int SAVE_AFTER_N_UPDATES = 5; // Уменьшено для быстрой проверки

    [SerializeField] private Material unlitDisplayMaterial; // Поле для назначения материала в инспекторе
    [Tooltip("Assign the SegMaskDisplayMaterial here.")]
    public Material customMaskDisplayMaterial;

    void Start()
    {
        rawImage = GetComponent<RawImage>();
        if (rawImage == null)
        {
            Debug.LogError("[DebugMaskLinker] RawImage компонент не найден на этом GameObject.", gameObject);
            enabled = false; // Отключаем скрипт, если нет RawImage
            return;
        }

        // --- NEW: Apply customMaskDisplayMaterial ---
        if (customMaskDisplayMaterial != null)
        {
            rawImage.material = Instantiate(customMaskDisplayMaterial); // Use Instantiate to avoid shared material issues if multiple RawImages use this
            Debug.Log($"[DebugMaskLinker] Назначен customMaskDisplayMaterial ('{customMaskDisplayMaterial.name}') для RawImage.", gameObject);
        }
        else
        {
            Debug.LogWarning("[DebugMaskLinker] customMaskDisplayMaterial не назначен. RawImage будет использовать свой текущий или стандартный материал.", gameObject);
            // Fallback to old logic if custom material is not set, or simply do nothing and let it use its default.
            // For simplicity, we'll let it use its default or whatever was set before if customMaskDisplayMaterial is null.
            // The old logic for unlitDisplayMaterial can be removed or kept as a secondary fallback.
        }
        // --- END NEW ---

        /* // --- OLD MATERIAL LOGIC - Can be removed or kept as a fallback if customMaskDisplayMaterial is null ---
        // Проверяем, назначен ли материал через инспектор
        if (unlitDisplayMaterial == null)
        {
            Debug.LogError("[DebugMaskLinker] Материал 'Unlit Display Material' не назначен в инспекторе!", gameObject);
        }
        else
        {
            if (rawImage.material == null || rawImage.material.name.Contains("Default") || rawImage.material == GetDefaultUIMaterial())
            {
                rawImage.material = unlitDisplayMaterial;
                Debug.Log($"[DebugMaskLinker] Назначен кастомный материал '{unlitDisplayMaterial.name}' для RawImage.", gameObject);
            }
        }

        if (unlitDisplayMaterial != null && (rawImage.material == null || rawImage.material.name.Contains("Default") || rawImage.material == GetDefaultUIMaterial()))
        {
            rawImage.material = unlitDisplayMaterial;
            Debug.Log($"[DebugMaskLinker] Назначен кастомный материал '{unlitDisplayMaterial.name}' для RawImage в Start().", gameObject);
        }
        else if (unlitDisplayMaterial == null && customMaskDisplayMaterial == null) // Only warn if no material is set up
        {
            Debug.LogWarning("[DebugMaskLinker] Ни один из материалов (customMaskDisplayMaterial или unlitDisplayMaterial) не назначен. RawImage будет использовать свой текущий или стандартный материал.", gameObject);
        }
        */ // --- END OLD MATERIAL LOGIC ---

        // Отключаем Raycast Target, чтобы UI элемент не блокировал взаимодействие с AR объектами
        if (rawImage.raycastTarget)
        {
            rawImage.raycastTarget = false;
            Debug.Log("[DebugMaskLinker] Raycast Target отключен для предотвращения блокировки AR-взаимодействия.", gameObject);
        }

        wallSegmentation = FindObjectOfType<WallSegmentation>();
        if (wallSegmentation == null)
        {
            Debug.LogError("[DebugMaskLinker] WallSegmentation компонент не найден в сцене.", gameObject);
            enabled = false;
            return;
        }

        wallSegmentation.OnSegmentationMaskUpdated += UpdateMaskTexture;
        Debug.Log("[DebugMaskLinker] Успешно подписался на OnSegmentationMaskUpdated от WallSegmentation.", gameObject);

        // Попытка получить начальную маску, если она уже доступна
        if (wallSegmentation.IsModelInitialized && wallSegmentation.segmentationMaskTexture != null && wallSegmentation.segmentationMaskTexture.IsCreated())
        {
            UpdateMaskTexture(wallSegmentation.segmentationMaskTexture);
            Debug.Log("[DebugMaskLinker] Получена начальная маска от WallSegmentation.", gameObject);
        }
        else
        {
            Debug.LogWarning("[DebugMaskLinker] Начальная маска не доступна или модель не инициализирована при старте DebugMaskLinker. Ожидание события...", gameObject);
        }
    }

    private void UpdateMaskTexture(RenderTexture mask)
    {
        if (rawImage == null)
        {
            Debug.LogError("[DebugMaskLinker] RawImage компонент не найден (null) в UpdateMaskTexture. Невозможно обновить текстуру.", gameObject);
            return;
        }

        // Проверяем и при необходимости назначаем кастомный материал еще раз здесь, если он слетел
        // This check might be redundant if customMaskDisplayMaterial is robustly set in Start and not changed elsewhere.
        // However, if there's a possibility it could be reset, this ensures it.
        if (customMaskDisplayMaterial != null && (rawImage.material == null || rawImage.material.shader != customMaskDisplayMaterial.shader))
        {
            // If current material is not an instance of our custom shader, re-apply (or apply for the first time if Start failed silently for some reason)
            rawImage.material = Instantiate(customMaskDisplayMaterial);
            Debug.Log($"[DebugMaskLinker] (UpdateMaskTexture) ПЕРЕ-назначен customMaskDisplayMaterial ('{customMaskDisplayMaterial.name}') для RawImage.", gameObject);
        }
        /* // --- OLD MATERIAL LOGIC IN UPDATE - Can be removed ---
        else if (unlitDisplayMaterial != null && (rawImage.material == null || rawImage.material.name.Contains("Default") || rawImage.material == GetDefaultUIMaterial()))
        {
            rawImage.material = unlitDisplayMaterial;
            Debug.Log($"[DebugMaskLinker] (UpdateMaskTexture) Назначен кастомный материал '{unlitDisplayMaterial.name}' для RawImage.", gameObject);
        }
        */ // --- END OLD MATERIAL LOGIC IN UPDATE ---

        Debug.Log($"[DebugMaskLinker] Получена маска в UpdateMaskTexture. IsNull: {mask == null}. Если не null, IsCreated: {(mask != null ? mask.IsCreated().ToString() : "N/A")}", gameObject);

        if (mask == null || !mask.IsCreated())
        {
            if (rawImage.texture != null)
            {
                rawImage.texture = null; // Очищаем текстуру
                Debug.LogWarning("[DebugMaskLinker] Получена невалидная/null маска, RawImage текстура очищена.", gameObject);
            }
            // Можно также деактивировать RawImage, если маска недействительна
            // if (rawImage.gameObject.activeSelf) rawImage.gameObject.SetActive(false);
            // if (rawImage.enabled) rawImage.enabled = false;
            return;
        }

        rawImage.texture = mask;
        rawImage.color = Color.white; // << ПРОВЕРЯЕМ, ЧТО ЦВЕТ НЕ ПРОЗРАЧНЫЙ

        Debug.Log($"[DebugMaskLinker] Texture assigned to RawImage. Current texture: {(rawImage.texture != null ? rawImage.texture.name + " (InstanceID: " + rawImage.texture.GetInstanceID() + ")" : "null")}", gameObject);

        if (rawImage.material != null)
        {
            Debug.Log($"[DebugMaskLinker] RawImage Material: {rawImage.material.name} (Shader: {rawImage.material.shader.name})", gameObject);
        }
        else
        {
            Debug.Log("[DebugMaskLinker] RawImage Material: None (Default UI Material)", gameObject);
        }

        // Убедимся, что GameObject активен, если есть текстура
        if (!rawImage.gameObject.activeSelf)
        {
            rawImage.gameObject.SetActive(true);
            Debug.Log("[DebugMaskLinker] GameObject RawImage был неактивен, активирован.", gameObject);
        }
        if (!rawImage.enabled)
        {
            rawImage.enabled = true;
            Debug.Log("[DebugMaskLinker] Компонент RawImage был выключен, включен.", gameObject);
        }

        // Set the mask texture to the material if it's our custom material
        if (rawImage.material != null && customMaskDisplayMaterial != null && rawImage.material.shader == customMaskDisplayMaterial.shader)
        {
            rawImage.material.SetTexture("_MainTex", mask);
        }

        updateCounter++;
        if (updateCounter >= SAVE_AFTER_N_UPDATES && !hasSavedOnce)
        {
            // SaveRenderTextureToFile(mask, "DebugMaskOutput_Auto.png"); // Пока закомментируем, чтобы не засорять
            // hasSavedOnce = true; // Раскомментируйте, если хотите сохранять только один раз
            // updateCounter = 0; // Сброс счетчика, если нужно периодическое сохранение
            Debug.Log($"[DebugMaskLinker] Достигнуто {SAVE_AFTER_N_UPDATES} обновлений ({updateCounter}). Автоматическое сохранение маски DebugMaskOutput_Auto.png...", gameObject);
#if UNITY_EDITOR
            // Автоматическое сохранение PNG работает только в редакторе для отладки
            SaveRenderTextureToFile(mask, "DebugMaskOutput_Auto.png");
            hasSavedOnce = true; // Сохраняем только один раз для теста
#else
            Debug.Log("[DebugMaskLinker] Автоматическое сохранение PNG отключено в релизной сборке для оптимизации производительности.", gameObject);
            hasSavedOnce = true; // Помечаем как сохраненное, чтобы избежать повторных попыток
#endif
        }

        // Убеждаемся, что стандартный UI материал корректно работает с текстурой (если наш материал не назначен)
        if (rawImage.material == null || rawImage.material.name.Contains("Default") || rawImage.material == GetDefaultUIMaterial())
        {
            // Для стандартного UI материала обычно достаточно просто установить текстуру и цвет.
            // Если используется специфический UI шейдер, могут потребоваться другие настройки.
        }
    }

    // Новый метод для сохранения RenderTexture в файл
    private void SaveRenderTextureToFile(RenderTexture rt, string fileName)
    {
        RenderTexture activeRenderTexture = RenderTexture.active;
        RenderTexture.active = rt;
        Texture2D tex2D = new Texture2D(rt.width, rt.height, TextureFormat.ARGB32, false);
        tex2D.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex2D.Apply();
        RenderTexture.active = activeRenderTexture;

        byte[] bytes = tex2D.EncodeToPNG();
        string filePath = System.IO.Path.Combine(Application.persistentDataPath, fileName); // Используем Path.Combine для корректного пути

        Debug.Log($"[DebugMaskLinker] Попытка сохранить текстуру в: {filePath}", gameObject);
        try
        {
            System.IO.File.WriteAllBytes(filePath, bytes);
            Debug.Log($"[DebugMaskLinker] Текстура УСПЕШНО сохранена в {filePath}", gameObject);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[DebugMaskLinker] ОШИБКА при сохранении текстуры в {filePath}: {e.Message}\n{e.StackTrace}", gameObject);
        }
        finally // Убедимся, что tex2D уничтожается в любом случае
        {
            Object.Destroy(tex2D); // Очищаем созданную Texture2D
        }
    }

    // Вспомогательный метод для получения стандартного UI материала (может отличаться в зависимости от версии Unity)
    private Material GetDefaultUIMaterial()
    {
        // Этот метод может потребовать доработки для точного определения стандартного материала UI в вашей версии Unity
        // На практике, проверка по имени "Default UI Material" или "Sprites-Default" часто бывает достаточной
        // В новых версиях Unity это может быть материал с шейдером "UI/Default"
        var defaultMat = new Material(Shader.Find("UI/Default")); // Это создаст новый экземпляр, используйте для сравнения имени или шейдера
        return defaultMat; // Возвращаем для примера, лучше сравнивать по имени или шейдеру существующего материала
    }

    void OnDestroy()
    {
        if (wallSegmentation != null)
        {
            wallSegmentation.OnSegmentationMaskUpdated -= UpdateMaskTexture;
            Debug.Log("[DebugMaskLinker] Успешно отписался от OnSegmentationMaskUpdated.", gameObject);
        }
    }
}