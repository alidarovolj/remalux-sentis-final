import onnxruntime as ort
import numpy as np
import os
import time
from PIL import Image
import matplotlib.pyplot as plt
from matplotlib import colors
import random

def visualize_segmentation(output, output_path="segmentation_visualization.png"):
    """Визуализирует результаты сегментации."""
    # Для вывода используем первый батч
    if len(output.shape) == 4:  # [batch, classes, height, width]
        # Получаем индексы классов с максимальной вероятностью для каждого пикселя
        output = output[0]  # Первый батч
        class_indices = np.argmax(output, axis=0)
        num_classes = output.shape[0]
    else:
        # Для одноклассовой сегментации
        class_indices = output[0] > 0.5  # Бинаризуем маску
        num_classes = 2  # Фон и объект
    
    # Создаем цветовую карту для визуализации
    # Генерируем случайные цвета для каждого класса
    np.random.seed(42)  # Для воспроизводимости
    colors_list = np.random.rand(num_classes, 3)
    # Убедимся, что фон (класс 0) имеет черный цвет
    if num_classes > 1:
        colors_list[0] = [0, 0, 0]
    
    # Создаем цветовую карту
    cmap = colors.ListedColormap(colors_list)
    
    # Визуализируем результаты
    plt.figure(figsize=(10, 10))
    plt.imshow(class_indices, cmap=cmap, vmin=0, vmax=num_classes-1)
    plt.colorbar(ticks=range(num_classes), label='Класс')
    plt.title(f'Сегментация: {num_classes} классов')
    plt.axis('off')
    plt.tight_layout()
    plt.savefig(output_path)
    print(f"Визуализация сохранена в {output_path}")
    plt.close()

def print_model_info(model_path):
    """Печатает основную информацию о модели ONNX."""
    print(f"Анализ модели: {model_path}")
    print("-" * 50)
    
    # Создаем сессию для инференса
    session = ort.InferenceSession(model_path)
    
    # Получаем метаданные модели
    metadata = session.get_modelmeta()
    
    # Получаем доступные атрибуты объекта metadata
    print("Метаданные модели:")
    for attr in dir(metadata):
        if not attr.startswith('_') and not callable(getattr(metadata, attr)):
            try:
                value = getattr(metadata, attr)
                print(f"  {attr}: {value}")
            except:
                print(f"  {attr}: <ошибка доступа>")
    
    # Получаем информацию о входных узлах модели
    print("\nВходные узлы:")
    for i, input_node in enumerate(session.get_inputs()):
        print(f"  [{i}] Имя: {input_node.name}")
        print(f"      Форма: {input_node.shape}")
        print(f"      Тип: {input_node.type}")
    
    # Получаем информацию о выходных узлах модели
    print("\nВыходные узлы:")
    for i, output_node in enumerate(session.get_outputs()):
        print(f"  [{i}] Имя: {output_node.name}")
        print(f"      Форма: {output_node.shape}")
        print(f"      Тип: {output_node.type}")
    
    # Генерируем статический размер входных данных для теста
    print("\nОпределение размеров входных данных для теста:")
    # Для модели семантической сегментации изображений
    # Стандартные параметры для моделей компьютерного зрения
    batch_size = 1
    num_channels = 3
    height = 320
    width = 320
    
    print(f"  Используем размеры: batch_size={batch_size}, channels={num_channels}, height={height}, width={width}")
    
    input_name = session.get_inputs()[0].name
    
    # Генерируем тестовое изображение - градиент
    test_image = np.zeros((height, width, num_channels), dtype=np.float32)
    for i in range(height):
        for j in range(width):
            # Создаем градиенты по x и y
            test_image[i, j, 0] = i / height  # R - по вертикали
            test_image[i, j, 1] = j / width   # G - по горизонтали
            test_image[i, j, 2] = (i + j) / (height + width)  # B - диагональ
    
    # Преобразуем в формат, требуемый моделью [batch, channels, height, width]
    input_tensor = np.transpose(test_image, (2, 0, 1))  # CHW формат
    input_tensor = np.expand_dims(input_tensor, axis=0)  # Добавляем измерение batch
    
    input_data = {
        input_name: input_tensor.astype(np.float32)
    }
    
    print(f"  {input_name}: shape = {input_data[input_name].shape}, dtype = {input_data[input_name].dtype}")
    
    # Сохраняем тестовое изображение для справки
    test_img_pil = Image.fromarray((test_image * 255).astype(np.uint8))
    test_img_path = "test_input_image.png"
    test_img_pil.save(test_img_path)
    print(f"  Тестовое изображение сохранено в {test_img_path}")
    
    try:
        # Замеряем время инференса
        print("\nЗапуск инференса...")
        start_time = time.time()
        outputs = session.run(None, input_data)
        inference_time = time.time() - start_time
        
        print(f"Время инференса: {inference_time:.4f} секунд")
        
        # Выводим информацию о выходных данных
        print("\nВыходные данные:")
        for i, output in enumerate(outputs):
            print(f"  [{i}] Форма: {output.shape}, Тип: {output.dtype}")
            print(f"  Мин: {output.min()}, Макс: {output.max()}, Среднее: {output.mean()}")
            
            # Если это сегментационная маска (2D или 3D), показываем дополнительную информацию
            if len(output.shape) in [3, 4]:  # [batch, classes, height, width] или [batch, height, width]
                if len(output.shape) == 4:
                    # Для многоклассовой сегментации берем класс с максимальным значением
                    print(f"  Количество классов: {output.shape[1]}")
                    if output.shape[1] > 1:
                        print("  Модель возвращает многоклассовую сегментацию")
                        # Визуализируем результаты сегментации
                        visualize_segmentation(output)
                    else:
                        print("  Модель возвращает маску сегментации с одним каналом")
                        visualize_segmentation(output)
        
        # Создадим код для Unity, который поможет с интеграцией
        print("\nРекомендации для интеграции с Unity Sentis:")
        print(f"""
Для модели с входом {input_data[input_name].shape} и выходом {outputs[0].shape}, 
в C# коде нужно использовать следующие параметры:

```csharp
// Входные данные для модели
int modelInputWidth = {width};
int modelInputHeight = {height};
int modelInputChannels = {num_channels};

// Выходные данные модели
int outputWidth = {outputs[0].shape[-1]};  
int outputHeight = {outputs[0].shape[-2]};
int numClasses = {outputs[0].shape[1]};

// Масштаб между разрешением входа и выхода
float scaleX = (float)outputWidth / modelInputWidth;
float scaleY = (float)outputHeight / modelInputHeight;

// Для обработки выходных данных:
void ProcessModelOutput(Tensor<float> outputTensor)
{{
    // Получаем размеры выходного тензора
    int batchSize = {outputs[0].shape[0]};
    int classes = {outputs[0].shape[1]};
    int height = {outputs[0].shape[2]};
    int width = {outputs[0].shape[3]};
    
    // Обработка тензора для создания маски сегментации
    // ...
}}
```
""")
    
    except Exception as e:
        print(f"\nОшибка при запуске инференса: {e}")
        print("\nПробуем с другими размерами изображения...")
        
        # Попробуем другие стандартные размеры
        for size in [(224, 224), (512, 512), (256, 256)]:
            try:
                height, width = size
                print(f"Тест с размером {height}x{width}:")
                
                # Создаем новое тестовое изображение
                test_image = np.zeros((height, width, num_channels), dtype=np.float32)
                for i in range(height):
                    for j in range(width):
                        test_image[i, j, 0] = i / height
                        test_image[i, j, 1] = j / width
                        test_image[i, j, 2] = (i + j) / (height + width)
                
                input_tensor = np.transpose(test_image, (2, 0, 1))
                input_tensor = np.expand_dims(input_tensor, axis=0)
                
                input_data = {
                    input_name: input_tensor.astype(np.float32)
                }
                
                outputs = session.run(None, input_data)
                print(f"  Успешно! Выходная форма: {outputs[0].shape}")
                
                # Визуализируем результаты
                visualize_segmentation(outputs[0], f"segmentation_size_{height}x{width}.png")
                break
            except Exception as e:
                print(f"  Ошибка: {e}")

def main():
    model_path = "Assets/Models/segformer-model.onnx"
    if not os.path.exists(model_path):
        print(f"Ошибка: Файл модели не найден: {model_path}")
        return
    
    print_model_info(model_path)

if __name__ == "__main__":
    main() 