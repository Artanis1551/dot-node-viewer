#!/usr/bin/env bash
set -euo pipefail

FORMAT="${1:-}"
if [[ -z "$FORMAT" ]]; then
  echo "Usage: $0 <onnx|tflite>" >&2
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
  *)
    echo "Error: Unknown format '$FORMAT'. Expected 'onnx' or 'tflite'." >&2
    exit 1
    ;;
esac

# Display tested models report for pipeline inspection
python3 "$SCRIPT_DIR/report-tested-models.py" "$FORMAT"
