// ... existing code ...
// Somewhere in your code, likely after model inference, you have the outputTensor
// and you convert it to segmentationMaskTexture.
// For example, you might have a line similar to one of these:
// Sentis.TextureConverter.RenderToTexture(outputTensor, segmentationMaskTexture);
// or
// SentisCompat.RenderTensorToTexture(outputTensor, segmentationMaskTexture);
// or
// worker.CopyOutput(outputTensorName, someTensor);
// Sentis.TextureConverter.RenderToTexture(someTensor, segmentationMaskTexture);

// Please REPLACE the existing direct conversion logic with the following:

// outputTensor dimensions are assumed to be (Batch, Channels, Height, Width), e.g., (1, C, 120, 160)
// So, for GetTemporary(width, height, ...), it's (160, 120) based on W, H from the tensor.
RenderTexture lowResMask = RenderTexture.GetTemporary(160, 120, 0, RenderTextureFormat.R8);
lowResMask.enableRandomWrite = false; // As per the ChatGPT example
lowResMask.filterMode = FilterMode.Point;    // Point filter for the initial tensor-to-texture render

// Assuming segmentationMaskTexture is an existing RenderTexture (e.g., 640x480)
// Its format should ideally match or be compatible (e.g., R8, or a format Blit can handle).
// The ChatGPT example creates its highResMask also as R8.
// We ensure its filter mode is Bilinear for the upscaling Blit.
if (segmentationMaskTexture != null) // Ensure it's not null if it's a class member
{
      segmentationMaskTexture.filterMode = FilterMode.Bilinear;
}

// Шаг 1: вывести тензор в lowResMask (1:1 без сглаживания)
// outputTensor is assumed to be available and of the correct type (e.g., TensorFloat)
// Using Sentis.TextureConverter as per the "ChatGPT" example.
// If SentisCompat.RenderTensorToTexture is standard in your project, you might need to use that.
Sentis.TextureConverter.RenderToTexture(outputTensor, lowResMask);

// Шаг 2: билинейно растянуть lowResMask в segmentationMaskTexture
Graphics.Blit(lowResMask, segmentationMaskTexture);

RenderTexture.ReleaseTemporary(lowResMask);
// ... existing code ... 