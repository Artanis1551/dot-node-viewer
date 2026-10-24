# Reference: Supported Neural Network Model Formats

> [!NOTE]
> Format detectors and streaming readers are implemented incrementally across **Stages S02–S10**. This reference matrix tracks each format's container framing, magic-byte detection strategy, and introduction stage.

---

## Format Support Matrix

| Format | Extensions | Framing / Schema | Stage | Status |
|---|---|---|---|---|
| [**ONNX**](onnx.md) | `.onnx` | Protobuf (`ModelProto`, `GraphProto`) + external data shards | **S02** | **Supported** |
| [**TensorFlow Lite**](tflite.md) | `.tflite` | FlatBuffers (`TFL3` file identifier) + affine quantization & delegate slices | **S03** | **Supported** |
| [**ExecuTorch**](executorch.md) | `.pte` | FlatBuffers (`ET12` magic header) + backend delegate blobs | **S07** | **Supported** |
| [**PyTorch (`state_dict` & TorchScript)**](pytorch.md) | `.pt`, `.pth` | ZIP container + safe `PickleMachine` VM + TorchScript IR | **S08** | **Supported** |
| [**PyTorch ExportedProgram**](pytorch.md#pytorch-2-exportedprogram-pt2) | `.pt2` | ZIP archive + `ExportedProgram` JSON (`models/*.json`) | **S09** | **Supported** |
| **Keras v3 & Legacy HDF5** | `.keras`, `.h5` | ZIP (`config.json` + `model.weights.h5`) / pure HDF5 (`PureHDF`) | **S10** | Planned |

---

## Shared Parsing Guarantees

Every format reader in `DotNodeViewer.Core` adheres to the following contracts:

- **Non-Destructive Probing (`ModelProbe`):** Format detection inspects the first 64 header bytes (and lazy ZIP central directory entries when applicable) while restoring `stream.Position`.
- **Lazy Weight Slices (`TensorDataSlice`):** Raw weight buffers are never copied onto the managed heap during `ReadAsync`.
- **Resilient Error Reporting (`ModelParseException`):** Truncated or malformed files throw `ModelParseException` with byte offset and node/tensor context rather than unhandled runtime exceptions.

