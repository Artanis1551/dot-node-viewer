#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: tools/tag-release.sh [--dry-run] <version>

Creates a SemVer 2.0.0 annotated Git release tag (vX.Y.Z or vX.Y.Z-prerelease)
after verifying branch, clean working tree, solution build, and TUnit tests.

Options:
  --dry-run   Run all validations and build/test gates without modifying files,
              creating commits, or tagging.
  -h, --help  Show this help message and exit.

Examples:
  tools/tag-release.sh 0.12.0
  tools/tag-release.sh v0.13.0-preview.1
  tools/tag-release.sh --dry-run 0.12.0
EOF
}

DRY_RUN=false
VERSION_INPUT=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    -h|--help)
      usage
      exit 0
      ;;
    --dry-run)
      DRY_RUN=true
      shift
      ;;
    -*)
      echo "Error: Unknown option '$1'." >&2
      usage >&2
      exit 1
      ;;
    *)
      if [[ -n "${VERSION_INPUT}" ]]; then
        echo "Error: Unexpected extra argument '$1'." >&2
        usage >&2
        exit 1
      fi
      VERSION_INPUT="$1"
      shift
      ;;
  esac
done

if [[ -z "${VERSION_INPUT}" ]]; then
  echo "Error: Missing required <version> argument." >&2
  usage >&2
  exit 1
fi

REPO_ROOT="$(git rev-parse --show-toplevel)"
cd "${REPO_ROOT}"

# Strip optional leading 'v'
VERSION="${VERSION_INPUT#v}"

# Validate strict SemVer 2.0.0 syntax (without build metadata '+')
SEMVER_REGEX='^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$'
if [[ ! "${VERSION}" =~ ${SEMVER_REGEX} ]]; then
  echo "Error: Version '${VERSION_INPUT}' is not a valid SemVer 2.0.0 string (expected X.Y.Z or X.Y.Z-prerelease)." >&2
  exit 1
fi

VERSION_PREFIX="${BASH_REMATCH[1]}.${BASH_REMATCH[2]}.${BASH_REMATCH[3]}"
TAG="v${VERSION}"

# Verify current branch is main
CURRENT_BRANCH="$(git symbolic-ref --short HEAD 2>/dev/null || true)"
if [[ "${CURRENT_BRANCH}" != "main" ]]; then
  echo "Error: Releases must be tagged from 'main' (current branch: '${CURRENT_BRANCH:-detached HEAD}')." >&2
  exit 1
fi

# Verify working directory is completely clean
if [[ -n "$(git status --porcelain)" ]]; then
  echo "Error: Working directory is not clean. Commit or stash changes before tagging." >&2
  git status --short >&2
  exit 1
fi

# Verify tag does not already exist locally
if git rev-parse -q --verify "refs/tags/${TAG}" >/dev/null; then
  echo "Error: Tag '${TAG}' already exists." >&2
  exit 1
fi

PROPS_FILE="Directory.Build.props"
if [[ ! -f "${PROPS_FILE}" ]]; then
  echo "Error: '${PROPS_FILE}' not found at repository root." >&2
  exit 1
fi

CURRENT_PREFIX="$(python3 -c '
import re, sys
text = open("Directory.Build.props", encoding="utf-8").read()
m = re.search(r"<VersionPrefix>([^<]+)</VersionPrefix>", text)
if not m:
    sys.exit(1)
print(m.group(1).strip())
')" || {
  echo "Error: Could not find <VersionPrefix> in '${PROPS_FILE}'." >&2
  exit 1
}

echo "Running release quality gates for ${TAG}..."
dotnet build dot-node-viewer.sln -c Release
dotnet test dot-node-viewer.sln -c Release --no-build

if [[ "${DRY_RUN}" == "true" ]]; then
  echo ""
  echo "[Dry Run] All validations and quality gates passed."
  if [[ "${CURRENT_PREFIX}" != "${VERSION_PREFIX}" ]]; then
    echo "[Dry Run] Would update <VersionPrefix> in ${PROPS_FILE}: ${CURRENT_PREFIX} -> ${VERSION_PREFIX}"
    echo "[Dry Run] Would commit: chore: release ${TAG}"
  else
    echo "[Dry Run] <VersionPrefix> in ${PROPS_FILE} is already ${VERSION_PREFIX}; no version bump commit needed."
  fi
  echo "[Dry Run] Would create annotated tag: ${TAG} (message: 'Release ${TAG}')"
  exit 0
fi

if [[ "${CURRENT_PREFIX}" != "${VERSION_PREFIX}" ]]; then
  echo "Updating <VersionPrefix> in ${PROPS_FILE}: ${CURRENT_PREFIX} -> ${VERSION_PREFIX}"
  python3 - "${PROPS_FILE}" "${VERSION_PREFIX}" <<'PY'
import re, sys
path, new_prefix = sys.argv[1], sys.argv[2]
with open(path, "r", encoding="utf-8") as f:
    content = f.read()
updated, count = re.subn(
    r"(<VersionPrefix>)[^<]+(</VersionPrefix>)",
    rf"\g<1>{new_prefix}\g<2>",
    content,
    count=1,
)
if count != 1:
    sys.exit(f"Expected 1 <VersionPrefix> element in {path}, replaced {count}.")
with open(path, "w", encoding="utf-8") as f:
    f.write(updated)
PY

  if git diff --quiet -- "${PROPS_FILE}"; then
    echo "Error: Expected ${PROPS_FILE} to change, but git diff is empty." >&2
    exit 1
  fi

  git add "${PROPS_FILE}"
  git commit -m "chore: release ${TAG}"
fi

git tag -a "${TAG}" -m "Release ${TAG}"

echo ""
echo "Successfully created release tag '${TAG}'."
echo "Next steps to publish:"
echo "  git push origin main"
echo "  git push origin ${TAG}"
