#!/usr/bin/env bash
set -euo pipefail

FORMAT="${1:-}"
if [[ -z "$FORMAT" ]]; then
  echo "Usage: $0 <onnx|tflite|executorch|pytorch|pytorch-pt2|keras>" >&2
  exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

cd "$REPO_ROOT"

case "$FORMAT" in
  onnx)
    echo "=== Running ONNX format integration tests ==="
    dotnet test --project tests/DotNodeViewer.Core.Tests -c Release --no-build --output Detailed -- --treenode-filter "/*/*/OnnxModelTests/*"
    echo "=== Running ONNX manifest golden snapshot tests ==="
    dotnet test --project tests/DotNodeViewer.Core.Tests -c Release --no-build --output Detailed -- --treenode-filter "/*/*/ModelManifestTests/ParseRegisteredOnnxModel*"
    ;;
  tflite)
    echo "=== Running TensorFlow Lite format integration tests ==="
    dotnet test --project tests/DotNodeViewer.Core.Tests -c Release --no-build --output Detailed -- --treenode-filter "/*/*/TfliteModelTests/*"
    echo "=== Running TensorFlow Lite manifest golden snapshot tests ==="
    dotnet test --project tests/DotNodeViewer.Core.Tests -c Release --no-build --output Detailed -- --treenode-filter "/*/*/ModelManifestTests/ParseRegisteredTfliteModel*"
    ;;
  executorch)
    echo "=== Running ExecuTorch format integration tests ==="
    dotnet test --project tests/DotNodeViewer.Core.Tests -c Release --no-build --output Detailed -- --treenode-filter "/*/*/ExecuTorchModelTests/*"
    echo "=== Running ExecuTorch manifest golden snapshot tests ==="
    dotnet test --project tests/DotNodeViewer.Core.Tests -c Release --no-build --output Detailed -- --treenode-filter "/*/*/ModelManifestTests/ParseRegisteredExecuTorchModel*"
    ;;
  pytorch)
    echo "=== Running PyTorch format integration tests ==="
    dotnet test --project tests/DotNodeViewer.Core.Tests -c Release --no-build --output Detailed -- --treenode-filter "/*/*/PyTorchModelTests/*"
    echo "=== Running PyTorch manifest golden snapshot tests ==="
    dotnet test --project tests/DotNodeViewer.Core.Tests -c Release --no-build --output Detailed -- --treenode-filter "/*/*/ModelManifestTests/ParseRegisteredPyTorchModel*"
    ;;
  pytorch-pt2|pytorch_pt2|pt2)
    FORMAT="pytorch-pt2"
    echo "=== Running PyTorch .pt2 format integration tests ==="
    dotnet test --project tests/DotNodeViewer.Core.Tests -c Release --no-build --output Detailed -- --treenode-filter "/*/*/PyTorchPt2ModelTests/*"
    echo "=== Running PyTorch .pt2 manifest golden snapshot tests ==="
    dotnet test --project tests/DotNodeViewer.Core.Tests -c Release --no-build --output Detailed -- --treenode-filter "/*/*/ModelManifestTests/ParseRegisteredPyTorchPt2Model*"
    ;;
  keras)
    echo "=== Running Keras format integration tests ==="
    dotnet test --project tests/DotNodeViewer.Core.Tests -c Release --no-build --output Detailed -- --treenode-filter "/*/*/KerasModelTests/*"
    echo "=== Running Keras manifest golden snapshot tests ==="
    dotnet test --project tests/DotNodeViewer.Core.Tests -c Release --no-build --output Detailed -- --treenode-filter "/*/*/ModelManifestTests/ParseRegisteredKerasModel*"
    ;;
  *)
    echo "Error: Unknown format '$FORMAT'. Expected one of: onnx, tflite, executorch, pytorch, pytorch-pt2 (or pytorch_pt2), keras." >&2
    exit 1
    ;;
esac

# Display tested models report for pipeline inspection
python3 "$SCRIPT_DIR/report-tested-models.py" "$FORMAT"
