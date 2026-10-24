#!/usr/bin/env bash
set -euo pipefail

# Usage: stage-model.sh <source-path-or-url> <format-slug> [test-project-dir] [max-committed-kb] [companion-files...]
#
# Supported format-slug values:
#   onnx, tflite, executorch, pytorch, pytorch-pt2 (or pytorch_pt2), keras

SOURCE="${1:?Error: Provide a local file path or download URL as argument 1}"
FORMAT="${2:?Error: Provide a format slug (onnx, tflite, executorch, pytorch, pytorch-pt2, keras) as argument 2}"
TEST_DIR="${3:-tests/DotNodeViewer.Core.Tests}"
MAX_COMMITTED_KB="${4:-256}"
shift 4 2>/dev/null || shift $#

# 1. Normalize format slug and resolve folder / ID prefix
FORMAT_INPUT="$(echo "$FORMAT" | tr '[:upper:]' '[:lower:]')"
case "$FORMAT_INPUT" in
  onnx)
    CANONICAL_FORMAT="onnx"
    FOLDER_SLUG="onnx"
    ID_PREFIX="onnx"
    TEST_METHOD_FORMAT="Onnx"
    ;;
  tflite)
    CANONICAL_FORMAT="tflite"
    FOLDER_SLUG="tflite"
    ID_PREFIX="tflite"
    TEST_METHOD_FORMAT="Tflite"
    ;;
  executorch)
    CANONICAL_FORMAT="executorch"
    FOLDER_SLUG="executorch"
    ID_PREFIX="executorch"
    TEST_METHOD_FORMAT="ExecuTorch"
    ;;
  pytorch)
    CANONICAL_FORMAT="pytorch"
    FOLDER_SLUG="pytorch"
    ID_PREFIX="pytorch"
    TEST_METHOD_FORMAT="PyTorch"
    ;;
  pytorch-pt2|pytorch_pt2|pt2)
    CANONICAL_FORMAT="pytorch-pt2"
    FOLDER_SLUG="pytorch_pt2"
    ID_PREFIX="pytorch_pt2"
    TEST_METHOD_FORMAT="PyTorchPt2"
    ;;
  keras)
    CANONICAL_FORMAT="keras"
    FOLDER_SLUG="keras"
    ID_PREFIX="keras"
    TEST_METHOD_FORMAT="Keras"
    ;;
  *)
    echo "Error: Unknown format '$FORMAT'. Expected one of: onnx, tflite, executorch, pytorch, pytorch-pt2 (or pytorch_pt2), keras." >&2
    exit 1
    ;;
esac

TMP_FILE="$(mktemp)"
cleanup() { rm -f "$TMP_FILE"; }
trap cleanup EXIT

# 2. Download or copy source file
SOURCE_URL=""
if [[ "$SOURCE" =~ ^https?:// ]]; then
  SOURCE_URL="$SOURCE"
  FILENAME="$(basename "${SOURCE%%\?*}")"
  echo "Downloading model from $SOURCE_URL ..." >&2
  curl -fL --retry 3 --connect-timeout 15 -o "$TMP_FILE" "$SOURCE_URL"
else
  if [[ ! -f "$SOURCE" ]]; then
    echo "Error: Local file not found: $SOURCE" >&2
    exit 1
  fi
  FILENAME="$(basename "$SOURCE")"
  cp "$SOURCE" "$TMP_FILE"
fi

BYTE_LENGTH="$(stat -c%s "$TMP_FILE" 2>/dev/null || stat -f%z "$TMP_FILE")"
SHA256="$(sha256sum "$TMP_FILE" 2>/dev/null | awk '{print $1}' || shasum -a 256 "$TMP_FILE" | awk '{print $1}')"
MAGIC_HEX="$(head -c 16 "$TMP_FILE" | od -An -tx1 | tr -s ' ' | sed 's/^ //;s/ $//')"

# 3. Strip compound and single extensions to get model stem
STEM="$FILENAME"
case "$STEM" in
  *.weights.h5) STEM="${STEM%.weights.h5}" ;;
  *.tar.gz)     STEM="${STEM%.tar.gz}" ;;
  *.*)          STEM="${STEM%.*}" ;;
esac

# Normalize stem: lowercase, preserve valid identifier chars (letters, digits, dots, hyphens, underscores)
STEM="$(echo "$STEM" | tr ' ' '_' | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9._-')"
MODEL_ID="${ID_PREFIX}-${STEM}"

MAX_BYTES=$((MAX_COMMITTED_KB * 1024))
if (( BYTE_LENGTH <= MAX_BYTES )); then
  TIER="committed"
  DEST_REL="Fixtures/${FOLDER_SLUG}/${FILENAME}"
else
  TIER="cached"
  DEST_REL=".model-cache/${FOLDER_SLUG}/${FILENAME}"
fi

DEST_ABS="${TEST_DIR}/${DEST_REL}"
mkdir -p "$(dirname "$DEST_ABS")"
mv "$TMP_FILE" "$DEST_ABS"

SNAPSHOT_REL="Fixtures/${FOLDER_SLUG}/${STEM}.expected.json"
SNAPSHOT_ABS="${TEST_DIR}/${SNAPSHOT_REL}"

# 4. Handle companion files if provided (e.g. external data buffers like .bin)
while [[ $# -gt 0 ]]; do
  COMPANION="$1"
  shift
  if [[ -f "$COMPANION" ]]; then
    COMP_BASE="$(basename "$COMPANION")"
    COMP_DEST="$(dirname "$DEST_ABS")/${COMP_BASE}"
    cp "$COMPANION" "$COMP_DEST"
    echo "Staged companion file: $COMP_DEST" >&2
  fi
done

# Ensure .model-cache is ignored by Git
GITIGNORE_PATH="${TEST_DIR}/.gitignore"
if [[ "$TIER" == "cached" ]]; then
  if [[ ! -f "$GITIGNORE_PATH" ]] || ! grep -q "^\.model-cache/" "$GITIGNORE_PATH"; then
    echo ".model-cache/" >> "$GITIGNORE_PATH"
  fi
fi

# Print human-readable summary on stderr
echo >&2
echo "=== Model Staged Successfully ===" >&2
echo "  Format:    ${CANONICAL_FORMAT} (folder: Fixtures/${FOLDER_SLUG})" >&2
echo "  Model ID:  ${MODEL_ID}" >&2
echo "  File:      ${DEST_REL} (${BYTE_LENGTH} bytes, tier: ${TIER})" >&2
echo "  SHA-256:   ${SHA256}" >&2
echo "  Magic:     ${MAGIC_HEX}" >&2
echo "  Snapshot:  ${SNAPSHOT_REL}" >&2
echo >&2
echo "=== Next Steps ===" >&2
echo "1. Add the JSON snippet below to '${TEST_DIR}/Fixtures/models.manifest.json'." >&2
echo "2. Generate golden topology snapshot:" >&2
echo "   dotnet run --project tools/DotNodeViewer.Cli -c Release --no-build -- snapshot \"${DEST_ABS}\" --id \"${MODEL_ID}\" > \"${SNAPSHOT_ABS}\"" >&2
echo "3. Verify via TUnit manifest test:" >&2
echo "   dotnet test --project ${TEST_DIR} -c Release --no-restore -- --treenode-filter \"/*/*/ModelManifestTests/ParseRegistered${TEST_METHOD_FORMAT}Model*\"" >&2
echo "4. Verify CI pipeline inspection:" >&2
echo "   python3 tools/ci/report-tested-models.py ${CANONICAL_FORMAT}" >&2
echo >&2

# Output ready-to-paste manifest entry JSON to stdout
cat <<EOF
    {
      "id": "${MODEL_ID}",
      "format": "${CANONICAL_FORMAT}",
      "fileName": "${FILENAME}",
      "tier": "${TIER}",
      "relativePath": "${DEST_REL}",
      "sourceUrl": $( [[ -n "$SOURCE_URL" ]] && printf '"%s"' "$SOURCE_URL" || printf 'null' ),
      "sha256": "${SHA256}",
      "byteLength": ${BYTE_LENGTH},
      "expectedSnapshot": "${SNAPSHOT_REL}"
    }
EOF
