using UnityEngine;
using Unity.Sentis; // Убедитесь, что это есть
using System.IO; // Для Path.Combine
using System.Reflection; // Для рефлексии
using System.Linq; // Для LINQ (Select)

public class SentisShapeTest : MonoBehaviour
{
      [Tooltip("Path to the model file relative to the StreamingAssets folder.")]
      public string modelPath = "segformer-model.sentis"; // Пример, измените на ваш путь
      public BackendType backend = BackendType.GPUCompute;

      void Start()
      {
            string fullModelPath = Path.Combine(Application.streamingAssetsPath, modelPath);
            Debug.Log($"[SentisShapeTest] Attempting to load model from: {fullModelPath}");

            if (!File.Exists(fullModelPath))
            {
                  Debug.LogError($"[SentisShapeTest] Model file not found at: {fullModelPath}");
                  return;
            }

            Model runtimeModel = null;
            try
            {
                  runtimeModel = ModelLoader.Load(fullModelPath);
                  if (runtimeModel != null)
                  {
                        Debug.Log($"[SentisShapeTest] Model loaded successfully. Output count: {runtimeModel.outputs.Count}");

                        if (runtimeModel.outputs != null && runtimeModel.outputs.Count > 0)
                        {
                              object firstOutputObj = runtimeModel.outputs[0];

                              if (!object.ReferenceEquals(firstOutputObj, null))
                              {
                                    Debug.Log($"[SentisShapeTest] runtimeModel.outputs[0] is NOT null. Actual type: {firstOutputObj.GetType().FullName}");

                                    // Выводим все public свойства
                                    PropertyInfo[] properties = firstOutputObj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
                                    Debug.Log($"[SentisShapeTest] Public Properties ({properties.Length}) on '{firstOutputObj.GetType().FullName}':");
                                    foreach (var prop in properties)
                                    {
                                          try
                                          {
                                                object value = prop.GetValue(firstOutputObj);
                                                Debug.Log($"  - {prop.Name} (Type: {prop.PropertyType.FullName}, Value: {(value != null ? value.ToString() : "null")})");
                                          }
                                          catch (TargetInvocationException tie)
                                          {
                                                Debug.LogWarning($"  - {prop.Name} (Type: {prop.PropertyType.FullName}, Value: Getter threw exception: {tie.InnerException?.Message ?? tie.Message})");
                                          }
                                          catch (System.Exception ex)
                                          {
                                                Debug.LogWarning($"  - {prop.Name} (Type: {prop.PropertyType.FullName}, Value: Could not get value: {ex.Message})");
                                          }
                                    }

                                    // Выводим все public методы
                                    MethodInfo[] methods = firstOutputObj.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
                                    Debug.Log($"[SentisShapeTest] Public Methods ({methods.Length}) on '{firstOutputObj.GetType().FullName}':");
                                    foreach (var meth in methods.Where(m => !m.IsSpecialName)) // Исключаем get/set аксессоры
                                    {
                                          Debug.Log($"  - {meth.Name} (Return Type: {meth.ReturnType.FullName}, Parameters: {string.Join(", ", meth.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
                                    }

                                    // Попытка явного приведения и доступа к свойствам
                                    Unity.Sentis.Model.Output castedOutput = null;
                                    try
                                    {
                                          castedOutput = firstOutputObj as Unity.Sentis.Model.Output;
                                    }
                                    catch (System.Exception castEx)
                                    {
                                          Debug.LogError($"[SentisShapeTest] Exception during cast to Unity.Sentis.Model.Output: {castEx.ToString()}");
                                    }

                                    if (!object.ReferenceEquals(castedOutput, null))
                                    {
                                          Debug.Log("[SentisShapeTest] Successfully cast to Unity.Sentis.Model.Output.");
                                          try
                                          {
                                                Debug.Log($"  Name (via cast): {castedOutput.name}");
                                                Debug.Log($"  DataType (via cast): {castedOutput.dataType}");
                                                Debug.Log($"  Shape (via cast): {(castedOutput.shape != null ? castedOutput.shape.ToString() : "null")}");
                                          }
                                          catch (System.Exception accessEx)
                                          {
                                                Debug.LogError($"[SentisShapeTest] Exception accessing members after successful cast: {accessEx.ToString()}");
                                          }
                                    }
                                    else
                                    {
                                          Debug.LogError("[SentisShapeTest] Cast to Unity.Sentis.Model.Output resulted in null.");
                                    }
                              }
                              else
                              {
                                    Debug.LogError("[SentisShapeTest] runtimeModel.outputs[0] IS null (checked with ReferenceEquals).");
                              }
                        }
                        else
                        {
                              Debug.LogError("[SentisShapeTest] Model outputs are null or empty.");
                        }
                  }
                  else
                  {
                        Debug.LogError("[SentisShapeTest] ModelLoader.Load returned null.");
                  }
            }
            catch (System.Exception e)
            {
                  Debug.LogError($"[SentisShapeTest] Outer exception: {e.ToString()}");
            }
            finally
            {
                  if (runtimeModel is System.IDisposable disposableModel)
                  {
                        disposableModel.Dispose();
                        Debug.Log("[SentisShapeTest] Model disposed.");
                  }
            }
      }
}