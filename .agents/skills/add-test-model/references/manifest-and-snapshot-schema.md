# Test Model Manifest & Snapshot Schema Reference

## 1. `Fixtures/models.manifest.json` Schema

Maintain a single manifest at `tests/<TestProject>/Fixtures/models.manifest.json` indexing all committed and cached models:

```json
{
  "schemaVersion": 1,
  "models": [
    {
      "id": "tflite-mobilenet-v2-quant",
      "format": "tflite",
      "fileName": "mobilenet_v2_1.0_224_quant.tflite",
      "tier": "cached",
      "relativePath": ".model-cache/tflite/mobilenet_v2_1.0_224_quant.tflite",
      "sourceUrl": "https://raw.githubusercontent.com/google-coral/test_data/master/mobilenet_v2_1.0_224_quant.tflite",
      "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
      "byteLength": 3423112,
      "expectedSnapshot": "Fixtures/tflite/mobilenet_v2_1.0_224_quant.expected.json"
    }
  ]
}
```

## 2. `<model-id>.expected.json` Topology Snapshot Schema

Store the golden expected topology in `Fixtures/<format>/<model-id>.expected.json` (committed to Git even when the model binary itself is `cached`):

```json
{
  "modelId": "tflite-mobilenet-v2-quant",
  "format": "TensorFlow Lite v3",
  "subgraphCount": 1,
  "primarySubgraph": {
    "name": "main",
    "nodeCount": 65,
    "operatorHistogram": {
      "CONV_2D": 35,
      "DEPTHWISE_CONV_2D": 17,
      "ADD": 10,
      "AVERAGE_POOL_2D": 1,
      "RESHAPE": 1,
      "SOFTMAX": 1
    },
    "inputs": [
      {
        "name": "input",
        "dataType": "UInt8",
        "shape": ["1", "224", "224", "3"],
        "quantization": { "scale": [0.0078125], "zeroPoint": [128] }
      }
    ],
    "outputs": [
      {
        "name": "MobilenetV2/Predictions/Reshape_1",
        "dataType": "UInt8",
        "shape": ["1", "1001"]
      }
    ],
    "sampleWeightSlice": {
      "tensorName": "MobilenetV2/Conv/weights_quant",
      "dataType": "UInt8",
      "streamOffset": 1480,
      "byteLength": 864
    }
  }
}
```

## 3. C# Test Harness Resolution Pattern (`ModelFixtureLoader`)

When writing or updating the C# test helper that loads entries from `models.manifest.json`:
1. Resolve `relativePath` relative to `AppContext.BaseDirectory` (or the project root).
2. If `File.Exists(fullPath)` is `false`:
   - If `sourceUrl` is non-null, download the file via `HttpClient` into a temporary file, verify its SHA-256 hash matches `entry.Sha256`, and atomically move it to `fullPath`.
   - If `sourceUrl` is `null` (a local-only large model), skip the test cleanly with a message explaining how to place the file in `.model-cache/`.
3. Open the file as a read-only `FileStream` (`FileMode.Open`, `FileAccess.Read`, `FileShare.Read`), verify `stream.Length == entry.ByteLength`, and pass the `Stream` directly to `IModelFormatReader.ReadAsync`—never `File.ReadAllBytes`.