#!/usr/bin/env bash
# install_to_unity_project.sh — drop the EmotionEngine plugin + freshly
# built libGameVoiceNativeSDK.dylib into a Unity project, ready to open
# in the Unity Editor.
#
# Usage:
#   ./scripts/install_to_unity_project.sh /path/to/3DGameKit
#
# What it does:
#   1. Builds libGameVoiceNativeSDK.dylib (Ninja + Swift toolchain).
#   2. Copies Assets/Plugins/EmotionEngine/ into <project>/Assets/Plugins/.
#   3. Drops the dylib into Assets/Plugins/EmotionEngine/macOS/.
#   4. Writes a .meta sidecar telling Unity to load it on macOS only.
#
# Idempotent: re-running just refreshes the dylib and plugin source.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

# --- args -----------------------------------------------------------

if [[ $# -lt 1 ]]; then
  echo "usage: $0 <path-to-unity-project>"
  echo
  echo "Example:"
  echo "  $0 ~/Unity/3DGameKit-Lite"
  exit 64
fi

UNITY_PROJECT="$1"
if [[ ! -d "$UNITY_PROJECT/Assets" ]]; then
  echo "error: '$UNITY_PROJECT' doesn't look like a Unity project (no Assets/ dir)"
  exit 64
fi

# Native SDK source location. Override with GAMEVOICE_NATIVE_SDK env var
# if you keep it elsewhere.
NATIVE_SDK_DIR="${GAMEVOICE_NATIVE_SDK:-/Volumes/msd512/project/GameVoiceNativeSDK}"
if [[ ! -f "$NATIVE_SDK_DIR/CMakeLists.txt" ]]; then
  echo "error: GameVoiceNativeSDK not found at '$NATIVE_SDK_DIR'."
  echo "set GAMEVOICE_NATIVE_SDK to the repo path and re-run."
  exit 1
fi

# --- preflight ------------------------------------------------------

if ! command -v cmake >/dev/null; then
  echo "error: cmake not found. Install via 'brew install cmake'."
  exit 1
fi
if ! command -v ninja >/dev/null; then
  echo "error: ninja not found. Install via 'brew install ninja'."
  echo "       (CMake's Swift support requires the Ninja generator on macOS.)"
  exit 1
fi
if ! xcrun --find swiftc >/dev/null 2>&1; then
  echo "error: swiftc not available. Install Xcode (full IDE, not just CLT)."
  exit 1
fi

# --- 1. build dylib -------------------------------------------------

BUILD_DIR="$NATIVE_SDK_DIR/build-macos"

echo "==> Building libGameVoiceNativeSDK.dylib"
if [[ ! -f "$BUILD_DIR/build.ninja" ]]; then
  cmake -S "$NATIVE_SDK_DIR" -B "$BUILD_DIR" -G Ninja >/dev/null
fi
cmake --build "$BUILD_DIR" --target GameVoiceNativeSDK

DYLIB="$BUILD_DIR/libGameVoiceNativeSDK.dylib"
if [[ ! -f "$DYLIB" ]]; then
  echo "error: build succeeded but $DYLIB is missing"
  exit 1
fi

# --- 2. copy plugin source -----------------------------------------

PLUGIN_SRC="$REPO_DIR/Assets/Plugins/EmotionEngine"
PLUGIN_DEST="$UNITY_PROJECT/Assets/Plugins/EmotionEngine"

echo "==> Copying plugin into $PLUGIN_DEST"
mkdir -p "$PLUGIN_DEST"
# Use rsync to preserve structure and overwrite changed files only.
rsync -a --delete-excluded \
  --exclude 'macOS/libGameVoiceNativeSDK.dylib' \
  "$PLUGIN_SRC/" "$PLUGIN_DEST/"

# --- 3. drop dylib --------------------------------------------------

MACOS_PLUGIN_DIR="$PLUGIN_DEST/macOS"
mkdir -p "$MACOS_PLUGIN_DIR"
cp "$DYLIB" "$MACOS_PLUGIN_DIR/libGameVoiceNativeSDK.dylib"
echo "==> Installed dylib at $MACOS_PLUGIN_DIR/libGameVoiceNativeSDK.dylib"

# --- 4. write .meta so Unity loads it on macOS only ----------------
#
# Unity uses .meta sidecars to tag PluginImporter settings. Without one,
# the import defaults to "any platform" and Editor + Standalone may not
# load it correctly. This minimal .meta matches what the Editor would
# write after manually setting "macOS standalone + editor".

META="$MACOS_PLUGIN_DIR/libGameVoiceNativeSDK.dylib.meta"
if [[ ! -f "$META" ]]; then
  GUID=$(uuidgen | tr -d '-' | tr 'A-F' 'a-f')
  cat > "$META" <<EOF
fileFormatVersion: 2
guid: $GUID
PluginImporter:
  externalObjects: {}
  serializedVersion: 2
  iconMap: {}
  executionOrder: {}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData:
  - first:
      : Any
    second:
      enabled: 0
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 1
      settings:
        CPU: ARM64
        DefaultValueInitialized: true
        OS: OSX
  - first:
      Standalone: OSXUniversal
    second:
      enabled: 1
      settings:
        CPU: ARM64
EOF
  echo "==> Wrote PluginImporter .meta (Editor + Standalone-OSX, ARM64)"
fi

# --- done -----------------------------------------------------------

echo
echo "Done."
echo
echo "Next steps:"
echo "  1. Open '$UNITY_PROJECT' in Unity Hub (recommend Unity 2022.3 LTS)."
echo "  2. In a bootstrap scene, create an empty GameObject named 'GameVoice',"
echo "     attach the EmotionEngineBridge component."
echo "  3. Fill in BackendBaseUrl, SdkApiKey, SdkKeySecret, GameId, Username"
echo "     in the Inspector (see README for credentials)."
echo "  4. Hit Play. Console should log 'runtime started; session=...'."
echo
