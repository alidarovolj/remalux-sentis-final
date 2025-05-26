using UnityEngine;
using System.Collections;

/// <summary>
/// Класс для эффективной обработки маски сегментации на GPU с использованием Compute Shader
/// </summary>
[RequireComponent(typeof(WallSegmentation))]
public class GPUSegmentationProcessor : MonoBehaviour
{
      [Header("Compute Shader")]
      [Tooltip("Compute shader для обработки маски сегментации")]
      public ComputeShader segmentationProcessor;

      [Header("Параметры обработки")]
      [Tooltip("Порог обнаружения стен")]
      [Range(0.01f, 0.99f)]
      public float wallThreshold = 0.5f;

      [Tooltip("Порог обнаружения пола")]
      [Range(0.01f, 0.99f)]
      public float floorThreshold = 0.5f;

      [Tooltip("Мягкость краев")]
      [Range(0.001f, 0.1f)]
      public float edgeSoftness = 0.02f;

      [Tooltip("Вес временной стабилизации")]
      [Range(0f, 0.98f)]
      public float temporalWeight = 0.8f;

      [Tooltip("Резкость")]
      [Range(0f, 2f)]
      public float sharpness = 1.0f;

      [Tooltip("Интенсивность свечения краев")]
      [Range(0f, 1f)]
      public float edgeGlow = 0.3f;

      [Tooltip("Влияние глубины")]
      [Range(0f, 1f)]
      public float depthInfluence = 0.5f;

      [Tooltip("Максимальная глубина")]
      public float maxDepth = 5.0f;

      [Header("Динамическое качество")]
      [Tooltip("Включить динамическую адаптацию качества")]
      public bool enableDynamicQuality = true;

      [Tooltip("Целевой FPS")]
      public float targetFPS = 30f;

      // Буферы и переменные для работы с GPU
      private ComputeBuffer qualityBuffer;
      private uint[] qualityData = new uint[2]; // [0] = significant pixels, [1] = total pixels

      // ID для ядер и свойств вычислителя
      private int processMaskKernelID;
      private int analyzeQualityKernelID;
      private int temporalBlendKernelID;

      // ID для свойств compute shader
      private int inputMaskPropertyID;
      private int previousMaskPropertyID;
      private int outputMaskPropertyID;
      private int qualityBufferPropertyID;
      private int motionVectorsPropertyID;
      private int widthPropertyID;
      private int heightPropertyID;
      private int wallThresholdPropertyID;
      private int floorThresholdPropertyID;
      private int edgeSoftnessPropertyID;
      private int temporalWeightPropertyID;
      private int sharpnessPropertyID;
      private int edgeGlowPropertyID;
      private int maxDepthPropertyID;
      private int depthInfluencePropertyID;

      // Внутренние текстуры
      private RenderTexture previousFrame;
      private RenderTexture motionVectorsTexture;

      // Метрики FPS для адаптивного качества
      private float[] fpsSamples = new float[10];
      private int fpsSampleIndex = 0;
      private float lastQualityAdjustTime = 0f;
      private int consecutiveLowFrameCount = 0;
      private int consecutiveHighFrameCount = 0;

      // Статистика качества маски
      private uint currentMaskQuality = 0;

      // Ссылка на компонент WallSegmentation
      private WallSegmentation wallSegmentation;

      void Start()
      {
            wallSegmentation = GetComponent<WallSegmentation>();

            if (segmentationProcessor == null)
            {
                  Debug.LogError("[GPUSegmentationProcessor] Compute shader not assigned!");
                  enabled = false;
                  return;
            }

            // Получаем ID для ядер
            processMaskKernelID = segmentationProcessor.FindKernel("ProcessMask");
            analyzeQualityKernelID = segmentationProcessor.FindKernel("AnalyzeQuality");
            temporalBlendKernelID = segmentationProcessor.FindKernel("TemporalBlend");

            // Получаем ID для свойств
            inputMaskPropertyID = Shader.PropertyToID("InputMask");
            previousMaskPropertyID = Shader.PropertyToID("PreviousMask");
            outputMaskPropertyID = Shader.PropertyToID("OutputMask");
            qualityBufferPropertyID = Shader.PropertyToID("QualityBuffer");
            motionVectorsPropertyID = Shader.PropertyToID("MotionVectors");
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

            // Создаем буфер для анализа качества
            qualityBuffer = new ComputeBuffer(2, sizeof(uint));
            qualityData[0] = 0;
            qualityData[1] = 0;
            qualityBuffer.SetData(qualityData);

            // Инициализируем текстуры
            InitializeTextures(256, 256);

            // Подписываемся на события от WallSegmentation если доступно
            if (wallSegmentation != null)
            {
                  Debug.Log("[GPUSegmentationProcessor] Attached to WallSegmentation");
            }
      }

      void Update()
      {
            // Обновляем метрики FPS и адаптируем качество если включено
            if (enableDynamicQuality)
            {
                  UpdateFPSSamples();

                  // Адаптация качества каждые 0.5 секунды
                  if (Time.time - lastQualityAdjustTime > 0.5f)
                  {
                        AdaptQualityToPerformance(GetAverageFPS());
                        lastQualityAdjustTime = Time.time;
                  }
            }

            // Обновляем параметры compute shader
            UpdateComputeShaderParameters();

            // Обновляем моушн-вектора каждые несколько кадров
            if (Time.frameCount % 5 == 0)
            {
                  UpdateMotionVectors();
            }
      }

      void OnDestroy()
      {
            // Освобождаем ресурсы
            if (qualityBuffer != null)
            {
                  qualityBuffer.Release();
                  qualityBuffer = null;
            }

            ReleaseTextures();
      }

      // Инициализация текстур с нужным разрешением
      private void InitializeTextures(int width, int height)
      {
            ReleaseTextures();

            // Создаем текстуру для предыдущего кадра
            previousFrame = new RenderTexture(width, height, 0, GetOptimalRenderTextureFormat());
            previousFrame.enableRandomWrite = true;
            previousFrame.Create();

            // Инициализируем пустым изображением
            RenderTexture tempRT = RenderTexture.GetTemporary(width, height);
            Graphics.Blit(Texture2D.blackTexture, tempRT);
            Graphics.Blit(tempRT, previousFrame);
            RenderTexture.ReleaseTemporary(tempRT);

            // Создаем текстуру для моушн-векторов
            motionVectorsTexture = new RenderTexture(width, height, 0, RenderTextureFormat.RGHalf);
            motionVectorsTexture.enableRandomWrite = true;
            motionVectorsTexture.Create();

            // Заполняем нулями (нет движения)
            Graphics.Blit(Texture2D.blackTexture, motionVectorsTexture);

            Debug.Log($"[GPUSegmentationProcessor] Initialized textures at {width}x{height}");
      }

      // Освобождение ресурсов текстур
      private void ReleaseTextures()
      {
            if (previousFrame != null)
            {
                  previousFrame.Release();
                  previousFrame = null;
            }

            if (motionVectorsTexture != null)
            {
                  motionVectorsTexture.Release();
                  motionVectorsTexture = null;
            }
      }

      // Обновление параметров compute shader
      private void UpdateComputeShaderParameters()
      {
            if (segmentationProcessor == null) return;

            // Устанавливаем параметры обработки
            segmentationProcessor.SetFloat(wallThresholdPropertyID, wallThreshold);
            segmentationProcessor.SetFloat(floorThresholdPropertyID, floorThreshold);
            segmentationProcessor.SetFloat(edgeSoftnessPropertyID, edgeSoftness);
            segmentationProcessor.SetFloat(temporalWeightPropertyID, temporalWeight);
            segmentationProcessor.SetFloat(sharpnessPropertyID, sharpness);
            segmentationProcessor.SetFloat(edgeGlowPropertyID, edgeGlow);
            segmentationProcessor.SetFloat(maxDepthPropertyID, maxDepth);
            segmentationProcessor.SetFloat(depthInfluencePropertyID, depthInfluence);
      }

      // Обновление моушн-векторов для временной стабилизации
      private void UpdateMotionVectors()
      {
            if (motionVectorsTexture == null || !motionVectorsTexture.IsCreated()) return;

            // В этой реализации мы используем простую симуляцию движения
            // В реальном приложении здесь может быть использован оптический поток или данные ARKit/ARCore

            // Для демонстрации: просто заполняем нулями или очень малыми значениями
            Graphics.Blit(Texture2D.blackTexture, motionVectorsTexture);
      }

      // Основной метод обработки маски сегментации
      public RenderTexture ProcessMask(RenderTexture inputMask)
      {
            if (inputMask == null || segmentationProcessor == null || !inputMask.IsCreated())
            {
                  Debug.LogWarning("[GPUSegmentationProcessor] Invalid input mask or compute shader");
                  return inputMask;
            }

            // Проверяем, нужно ли пересоздать внутренние текстуры
            if (previousFrame == null || previousFrame.width != inputMask.width || previousFrame.height != inputMask.height)
            {
                  InitializeTextures(inputMask.width, inputMask.height);
            }

            // Создаем выходную текстуру
            RenderTexture outputMask = RenderTexture.GetTemporary(inputMask.width, inputMask.height, 0, inputMask.format);
            outputMask.enableRandomWrite = true;
            outputMask.Create();

            // Очищаем буфер качества
            qualityData[0] = 0;
            qualityData[1] = 0;
            qualityBuffer.SetData(qualityData);

            // Устанавливаем параметры для ядра процессора маски
            segmentationProcessor.SetTexture(processMaskKernelID, inputMaskPropertyID, inputMask);
            segmentationProcessor.SetTexture(processMaskKernelID, previousMaskPropertyID, previousFrame);
            segmentationProcessor.SetTexture(processMaskKernelID, outputMaskPropertyID, outputMask);
            segmentationProcessor.SetTexture(processMaskKernelID, motionVectorsPropertyID, motionVectorsTexture);
            segmentationProcessor.SetInt(widthPropertyID, inputMask.width);
            segmentationProcessor.SetInt(heightPropertyID, inputMask.height);

            // Запускаем обработку маски
            int threadGroupsX = Mathf.CeilToInt(inputMask.width / 8.0f);
            int threadGroupsY = Mathf.CeilToInt(inputMask.height / 8.0f);
            segmentationProcessor.Dispatch(processMaskKernelID, threadGroupsX, threadGroupsY, 1);

            // Анализируем качество маски
            segmentationProcessor.SetTexture(analyzeQualityKernelID, inputMaskPropertyID, inputMask);
            segmentationProcessor.SetBuffer(analyzeQualityKernelID, qualityBufferPropertyID, qualityBuffer);
            segmentationProcessor.Dispatch(analyzeQualityKernelID, threadGroupsX, threadGroupsY, 1);

            // Получаем результаты анализа качества
            qualityBuffer.GetData(qualityData);
            float quality = qualityData[0] > 0 ? (float)qualityData[0] / qualityData[1] : 0f;
            currentMaskQuality = qualityData[0];

            // Сохраняем текущий кадр как предыдущий для следующей итерации
            Graphics.Blit(outputMask, previousFrame);

            return outputMask;
      }

      // Получение текущего качества маски (доля значимых пикселей)
      public float GetMaskQuality()
      {
            if (qualityData[1] == 0) return 0f;
            return (float)qualityData[0] / qualityData[1];
      }

      // Обновление метрик FPS
      private void UpdateFPSSamples()
      {
            float currentFPS = 1.0f / Time.unscaledDeltaTime;
            fpsSamples[fpsSampleIndex] = currentFPS;
            fpsSampleIndex = (fpsSampleIndex + 1) % fpsSamples.Length;
      }

      // Получение среднего FPS
      private float GetAverageFPS()
      {
            float sum = 0f;
            foreach (float sample in fpsSamples)
            {
                  sum += sample;
            }
            return sum / fpsSamples.Length;
      }

      // Адаптация качества в зависимости от производительности
      private void AdaptQualityToPerformance(float currentFPS)
      {
            if (wallSegmentation == null) return;

            if (currentFPS < targetFPS - 5)
            {
                  consecutiveLowFrameCount++;
                  consecutiveHighFrameCount = 0;

                  if (consecutiveLowFrameCount > 3)
                  {
                        // Уменьшаем качество
                        edgeSoftness = Mathf.Max(0.001f, edgeSoftness * 0.9f);
                        edgeGlow = Mathf.Max(0.05f, edgeGlow * 0.9f);

                        // Предлагаем Wall Segmentation уменьшить разрешение
                        if (wallSegmentation.inputResolution.x > 128)
                        {
                              /* // Отключаем изменение разрешения WallSegmentation из GPUSegmentationProcessor
                              Vector2Int newResolution = new Vector2Int(
                                    Mathf.Max(128, (int)(wallSegmentation.inputResolution.x * 0.8f)),
                                    Mathf.Max(128, (int)(wallSegmentation.inputResolution.y * 0.8f))
                              );

                              wallSegmentation.inputResolution = newResolution;
                              Debug.Log($"[GPUSegmentationProcessor] Suggested WallSegmentation to reduce resolution to {newResolution.x}x{newResolution.y} (FPS: {currentFPS:F1})");
                              */
                        }

                        consecutiveLowFrameCount = 0;
                  }
            }
            else if (currentFPS > targetFPS + 10)
            {
                  consecutiveHighFrameCount++;
                  consecutiveLowFrameCount = 0;

                  if (consecutiveHighFrameCount > 5)
                  {
                        // Улучшаем качество
                        edgeSoftness = Mathf.Min(0.1f, edgeSoftness * 1.1f);
                        edgeGlow = Mathf.Min(0.8f, edgeGlow * 1.1f);

                        // Предлагаем Wall Segmentation увеличить разрешение
                        if (wallSegmentation.inputResolution.x < 512)
                        {
                              /* // Отключаем изменение разрешения WallSegmentation из GPUSegmentationProcessor
                              Vector2Int newResolution = new Vector2Int(
                                    Mathf.Min(512, (int)(wallSegmentation.inputResolution.x * 1.2f)),
                                    Mathf.Min(512, (int)(wallSegmentation.inputResolution.y * 1.2f))
                              );

                              wallSegmentation.inputResolution = newResolution;
                              Debug.Log($"[GPUSegmentationProcessor] Suggested WallSegmentation to increase resolution to {newResolution.x}x{newResolution.y} (FPS: {currentFPS:F1})");
                              */
                        }

                        consecutiveHighFrameCount = 0;
                  }
            }
      }

      // Получение оптимального формата текстуры для текущей платформы
      private RenderTextureFormat GetOptimalRenderTextureFormat()
      {
#if UNITY_IOS
            if (SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGHalf))
            {
                  return RenderTextureFormat.RGHalf;
            }
#endif

            return RenderTextureFormat.ARGB32;
      }
}