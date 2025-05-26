using UnityEngine;

public class ARWallPaintColorManager : MonoBehaviour
{
      public static ARWallPaintColorManager Instance { get; private set; }

      public Color currentColor = Color.blue; // Default color

      private void Awake()
      {
            if (Instance != null && Instance != this)
            {
                  Destroy(gameObject);
                  return;
            }
            Instance = this;
            // DontDestroyOnLoad(gameObject); // Consider if this manager should persist across scenes
      }

      public Color GetCurrentColor()
      {
            return currentColor;
      }

      public void SetDefaultColor(Color newDefaultColor)
      {
            currentColor = newDefaultColor;
            Debug.Log($"[ARWallPaintColorManager] Default color set to: {currentColor}");
      }
}