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

    void Start()
    {
        rawImage = GetComponent<RawImage>();
        if (rawImage == null)
        {
            Debug.LogError("[DebugMaskLinker] RawImage компонент не найден на этом GameObject.", gameObject);
            enabled = false; // Отключаем скрипт, если нет RawImage
            return;
        }

        // Проверяем, назначен ли материал через инспектор
        if (unlitDisplayMaterial == null)
        {
            Debug.LogError("[DebugMaskLinker] Материал 'Unlit Display Material' не назначен в инспекторе!", gameObject);
            // Можно попытаться загрузить стандартный, если основной не назначен, или оставить как есть
            // Например, можно попробовать найти стандартный UI шейдер или просто использовать то, что есть по умолчанию
        }
        else
        {
            // Применяем назначенный материал, если он отличается от текущего или если текущий - стандартный
            if (rawImage.material == null || rawImage.material.name.Contains("Default") || rawImage.material == GetDefaultUIMaterial())
            {
                rawImage.material = unlitDisplayMaterial;
                Debug.Log($"[DebugMaskLinker] Назначен кастомный материал '{unlitDisplayMaterial.name}' для RawImage.", gameObject);
            }
        }

        // Загружаем наш специальный материал, если текущий - стандартный UI материал
        // Это условие должно быть более точным, чтобы не перезаписывать уже кастомно назначенный материал
        if (unlitDisplayMaterial != null && (rawImage.material == null || rawImage.material.name.Contains("Default") || rawImage.material == GetDefaultUIMaterial()))
        {
            rawImage.material = unlitDisplayMaterial;
            Debug.Log($"[DebugMaskLinker] Назначен кастомный материал '{unlitDisplayMaterial.name}' для RawImage в Start().", gameObject);
        }
        else if (unlitDisplayMaterial == null)
        {
            Debug.LogWarning("[DebugMaskLinker] 'Unlit Display Material' не назначен в инспекторе. RawImage будет использовать свой текущий или стандартный материал.", gameObject);
        }

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
        if (unlitDisplayMaterial != null && (rawImage.material == null || rawImage.material.name.Contains("Default") || rawImage.material == GetDefaultUIMaterial()))
        {
            rawImage.material = unlitDisplayMaterial;
            Debug.Log($"[DebugMaskLinker] (UpdateMaskTexture) Назначен кастомный материал '{unlitDisplayMaterial.name}' для RawImage.", gameObject);
        }

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