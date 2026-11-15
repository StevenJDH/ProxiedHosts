#!/usr/bin/env bash
# Merge two .NET Native AOT macOS publish directories into a universal output.
# Usage: bash merge-macos-universal.sh <arm64-dir> <x64-dir> <output-dir> <executable-name>
# The caller is responsible for code signing AFTER merging the Mach-O files.
set -euo pipefail

if [[ "$#" -ne 4 || -z "$1" || -z "$2" || -z "$3" || -z "$4" ]]; then
  echo '::error::Usage: merge-macos-universal.sh <arm64-dir> <x64-dir> <output-dir> <executable-name>'
  exit 1
fi

ARM_DIR="$1"
X64_DIR="$2"
UNIVERSAL_DIR="$3"
APP_NAME="$4"

# Validate both native executables before copying anything.
for binary in "${ARM_DIR}/${APP_NAME}" "${X64_DIR}/${APP_NAME}"; do
  if [[ ! -f "$binary" ]]; then
    echo "::error::Missing macOS executable: ${binary}"
    exit 1
  fi
  if ! file "$binary" | grep -q 'Mach-O'; then
    echo "::error::Not a Mach-O executable: ${binary}"
    exit 1
  fi
done

rm -rf "$UNIVERSAL_DIR"
mkdir -p "$UNIVERSAL_DIR"

# Start with the arm64 publish output. Non-Mach-O resources
# are architecture-independent and can be copied directly.
cp -a "${ARM_DIR}/." "${UNIVERSAL_DIR}/"

FOUND=0
# Merge every Mach-O file that has arm64/x64 counterparts.
while IFS= read -r -d '' ARM_FILE; do
  if ! file "$ARM_FILE" | grep -q 'Mach-O'; then
    continue
  fi

  FOUND=$((FOUND + 1))
  RELATIVE_PATH="${ARM_FILE#${ARM_DIR}/}"
  X64_FILE="${X64_DIR}/${RELATIVE_PATH}"
  OUTPUT_FILE="${UNIVERSAL_DIR}/${RELATIVE_PATH}"

  if [[ ! -f "$X64_FILE" ]] || ! file "$X64_FILE" | grep -q 'Mach-O'; then
    echo "::error::Missing x64 Mach-O counterpart: ${RELATIVE_PATH}"
    exit 1
  fi

  ARM_ARCHS="$(lipo -archs "$ARM_FILE")"
  X64_ARCHS="$(lipo -archs "$X64_FILE")"

  if [[ "$ARM_ARCHS" != *arm64* || "$X64_ARCHS" != *x86_64* ]]; then
    echo "::error::Unexpected Mach-O architectures: ${RELATIVE_PATH} (arm='${ARM_ARCHS}', x64='${X64_ARCHS}')"
    exit 1
  fi

  # Some dependencies may already be universal.
  if [[ "$ARM_ARCHS" == *arm64* && "$ARM_ARCHS" == *x86_64* ]]; then
    cp "$ARM_FILE" "$OUTPUT_FILE"
    continue
  fi

  if [[ "$X64_ARCHS" == *arm64* && "$X64_ARCHS" == *x86_64* ]]; then
    cp "$X64_FILE" "$OUTPUT_FILE"
    continue
  fi

  # Neither input is universal, so merge the two architectures.
  lipo -create "$ARM_FILE" "$X64_FILE" -output "$OUTPUT_FILE"
done < <(find "$ARM_DIR" -type f -print0)

# Reject x64-only Mach-O dependencies that have no arm64 counterpart.
while IFS= read -r -d '' X64_FILE; do
  if ! file "$X64_FILE" | grep -q 'Mach-O'; then
    continue
  fi

  RELATIVE_PATH="${X64_FILE#${X64_DIR}/}"
  ARM_FILE="${ARM_DIR}/${RELATIVE_PATH}"

  if [[ ! -f "$ARM_FILE" ]] || ! file "$ARM_FILE" | grep -q 'Mach-O'; then
    echo "::error::Missing arm64 Mach-O counterpart: ${RELATIVE_PATH}"
    exit 1
  fi
done < <(find "$X64_DIR" -type f -print0)

if [[ "$FOUND" -eq 0 ]]; then
  echo "::error::No Mach-O files found in arm64 directory: ${ARM_DIR}"
  exit 1
fi

# Remove debug symbols from the distributable application.
find "$UNIVERSAL_DIR" -type f \( -name '*.pdb' -o -name '*.dbg' \) -delete
find "$UNIVERSAL_DIR" -type d -name '*.dSYM' -prune -exec rm -rf {} +

chmod +x "${UNIVERSAL_DIR}/${APP_NAME}"

echo "Merged universal macOS executable: ${UNIVERSAL_DIR}/${APP_NAME}"
