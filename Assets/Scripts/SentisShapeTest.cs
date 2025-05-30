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
                                    Unity.Sentis.Model.Output castedOutput; // Structs cannot be null initially unless nullable
                                    bool castSuccessful = false;

                                    try
                                    {
                                          if (firstOutputObj is Unity.Sentis.Model.Output)
                                          {
                                                castedOutput = (Unity.Sentis.Model.Output)firstOutputObj;
                                                castSuccessful = true;
                                          }
                                          else
                                          {
                                                Debug.LogError($"[SentisShapeTest] firstOutputObj is of type {firstOutputObj.GetType().FullName}, not Unity.Sentis.Model.Output. Cannot cast directly.");
                                                // Initialize to default if cast is not possible to avoid using uninitialized variable
                                                castedOutput = default(Unity.Sentis.Model.Output);
                                          }
                                    }
                                    catch (System.Exception castEx)
                                    {
                                          Debug.LogError($"[SentisShapeTest] Exception during cast to Unity.Sentis.Model.Output: {castEx.ToString()}");
                                          // Initialize to default on exception
                                          castedOutput = default(Unity.Sentis.Model.Output);
                                    }

                                    if (castSuccessful) // Check if the cast was successful
                                    {
                                          Debug.Log("[SentisShapeTest] Successfully cast to Unity.Sentis.Model.Output.");
                                          try
                                          {
                                                Debug.Log($"  Name (via cast): {castedOutput.name}");

                                                // Access DataType via reflection
                                                var dataTypeProperty = castedOutput.GetType().GetProperty("dataType");
                                                if (dataTypeProperty != null)
                                                {
                                                      object dataTypeValue = dataTypeProperty.GetValue(castedOutput);
                                                      Debug.Log($"  DataType (via reflection): {dataTypeValue}");
                                                }
                                                else
                                                {
                                                      Debug.LogWarning("  DataType property not found via reflection.");
                                                }

                                                // Access Shape via reflection
                                                var shapeProperty = castedOutput.GetType().GetProperty("shape");
                                                if (shapeProperty != null)
                                                {
                                                      object shapeValue = shapeProperty.GetValue(castedOutput);
                                                      if (shapeValue is Unity.Sentis.TensorShape tensorShape)
                                                      {
                                                            Debug.Log($"  Shape (via reflection): {(tensorShape.length > 0 ? tensorShape.ToString() : "invalid/null")}");
                                                      }
                                                      else
                                                      {
                                                            Debug.LogWarning($"  Shape property is not a TensorShape (Type: {shapeValue?.GetType().FullName}). Value: {shapeValue}");
                                                      }
                                                }
                                                else
                                                {
                                                      Debug.LogWarning("  Shape property not found via reflection.");
                                                }
                                          }
                                          catch (System.Exception accessEx)
                                          {
                                                Debug.LogError($"[SentisShapeTest] Exception accessing members after successful cast: {accessEx.ToString()}");
                                          }
                                    }
                                    else
                                    {
                                          Debug.LogError("[SentisShapeTest] Cast to Unity.Sentis.Model.Output resulted in default.");
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