#!/usr/bin/env bash
set -euo pipefail

# Usage: stage-model.sh <source-path-or-url> <format-slug> [test-project-dir] [max-committed-kb]
SOURCE="${1:?Error: Provide a local file path or download URL as argument 1}"
FORMAT="${2:?Error: Provide a lowercase format slug (e.g. onnx, tflite, safetensors) as argument 2}"
TEST_DIR="${3:-tests/NetronNet.Tests}"
MAX_COMMITTED_KB="${4:-256}"

TMP_FILE="$(mktemp)"
cleanup() { rm -f "$TMP_FILE"; }
trap cleanup EXIT

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
MODEL_ID="$(echo "${FILENAME%.*}" | tr '[:upper:]. ' '[:lower:]--' | tr -cd 'a-z0-9-_')"

MAX_BYTES=$((MAX_COMMITTED_KB * 1024))

if (( BYTE_LENGTH <= MAX_BYTES )); then
  TIER="committed"
  DEST_REL="Fixtures/${FORMAT}/${FILENAME}"
else
  TIER="cached"
  DEST_REL=".model-cache/${FORMAT}/${FILENAME}"
fi

DEST_ABS="${TEST_DIR}/${DEST_REL}"
mkdir -p "$(dirname "$DEST_ABS")"
mv "$TMP_FILE" "$DEST_ABS"

# Ensure .model-cache is ignored by Git
GITIGNORE_PATH="${TEST_DIR}/.gitignore"
if [[ "$TIER" == "cached" ]]; then
  if [[ ! -f "$GITIGNORE_PATH" ]] || ! grep -q "^\.model-cache/" "$GITIGNORE_PATH"; then
    echo ".model-cache/" >> "$GITIGNORE_PATH"
  fi
fi

cat <<EOF
{
  "id": "${FORMAT}-${MODEL_ID}",
  "format": "${FORMAT}",
  "fileName": "${FILENAME}",
  "tier": "${TIER}",
  "relativePath": "${DEST_REL}",
  "sourceUrl": $( [[ -n "$SOURCE_URL" ]] && printf '"%s"' "$SOURCE_URL" || printf 'null' ),
  "sha256": "${SHA256}",
  "byteLength": ${BYTE_LENGTH},
  "magicBytesHex": "${MAGIC_HEX}",
  "expectedSnapshot": "Fixtures/${FORMAT}/${MODEL_ID}.expected.json"
}
EOF