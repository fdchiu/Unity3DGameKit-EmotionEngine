#!/usr/bin/env bash
# unity_clean_launch.sh — purge macOS AppleDouble sidecars (._*) from
# Unity.app and any active Unity projects, then open Unity Hub.
#
# Why: when Unity Editor lives on an exFAT or FAT32 volume (e.g. an
# external drive), macOS shadows every file with a `._*` AppleDouble
# sidecar. Unity walks its own install dir on startup and chokes on
# the sidecars ("Image is too small", "Invalid tarball filename",
# package resolver failures). Stripping them before launch fixes it.
# The sidecars regenerate on next file access, so re-run this script
# every time you want to open Unity.
#
# Usage:
#   ./scripts/unity_clean_launch.sh
#   ./scripts/unity_clean_launch.sh /path/to/UnityProject1 /path/to/Other
#
# Override the editor location by setting UNITY_APP, e.g.:
#   UNITY_APP=/Volumes/msd512/6000.4.8f1/Unity.app ./scripts/unity_clean_launch.sh

set -euo pipefail

UNITY_APP="${UNITY_APP:-/Volumes/msd512/6000.4.8f1/Unity.app}"
UNITY_HUB_APP="${UNITY_HUB_APP:-/Applications/Unity Hub.app}"
LAUNCH_PROJECT="${1:-}"

if [[ ! -d "$UNITY_APP" ]]; then
  echo "error: Unity editor not found at $UNITY_APP"
  echo "       set UNITY_APP to your editor's .app path and re-run."
  exit 1
fi

# ----- Stop any running Unity / Hub processes (otherwise stripped
# sidecars get re-created the instant Unity scans its install dir).

echo "==> Stopping any running Unity / Unity Hub processes"
pkill -x Unity 2>/dev/null || true
pkill -f "/Unity Hub.app/" 2>/dev/null || true
pkill -f "Unity Hub Helper" 2>/dev/null || true
sleep 1
if pgrep -x Unity >/dev/null || pgrep -f "Unity Hub" >/dev/null; then
  echo "  warning: some Unity processes survived; you may need to quit"
  echo "  them manually before re-running."
fi

# ----- macOS desktop-services preferences: stop creating .DS_Store /
# AppleDouble on non-APFS volumes. No sudo required.

echo "==> Setting macOS to skip DS_Store on USB/network volumes"
defaults write com.apple.desktopservices DSDontWriteUSBStores -bool true 2>/dev/null || true
defaults write com.apple.desktopservices DSDontWriteNetworkStores -bool true 2>/dev/null || true

strip_dir() {
  local dir="$1"
  if [[ ! -d "$dir" ]]; then
    return
  fi
  local before
  before=$(find "$dir" -name "._*" -type f 2>/dev/null | wc -l | tr -d ' ')
  if [[ "$before" -eq 0 ]]; then
    echo "  $dir : already clean"
    return
  fi
  find "$dir" -name "._*" -type f -delete 2>/dev/null || true
  find "$dir" -name ".DS_Store" -type f -delete 2>/dev/null || true
  echo "  $dir : stripped $before AppleDouble sidecar(s)"
}

echo "==> Stripping AppleDouble sidecars"
strip_dir "$UNITY_APP"
for proj in "$@"; do
  strip_dir "$proj"
done

# Tell macOS to stop creating new AppleDouble files in this shell.
# This affects child processes (incl. Unity Hub launched below), so the
# Hub's own file writes won't re-pollute. It does NOT prevent macOS
# from creating sidecars when other processes (Finder, Spotlight) touch
# the volume.
export COPYFILE_DISABLE=1

echo
# ----- Launch Unity Editor DIRECTLY (skip the Hub) when a project path
# was passed. The Hub re-scans the editor install dir on open and that
# scan triggers AppleDouble regeneration on exFAT volumes; launching
# Unity.app directly with -projectPath skips that entire surface.

if [[ -n "$LAUNCH_PROJECT" && -d "$LAUNCH_PROJECT" ]]; then
  echo "==> Launching Unity directly with -projectPath $LAUNCH_PROJECT"
  open -a "$UNITY_APP" --args -projectPath "$LAUNCH_PROJECT"
else
  echo "==> Opening Unity Hub"
  if [[ -d "$UNITY_HUB_APP" ]]; then
    open "$UNITY_HUB_APP"
  else
    echo "  Unity Hub not found at $UNITY_HUB_APP — opening editor directly."
    open "$UNITY_APP"
  fi
fi

echo
echo "Done."
echo
echo "If Unity still complains about ._* files, Spotlight is probably"
echo "the culprit (it traverses the volume and creates xattrs which then"
echo "force AppleDouble sidecars on exFAT). That fix needs sudo:"
echo
echo "  sudo mdutil -d /Volumes/msd512        # disable Spotlight on the volume"
echo "  killall Finder                        # restart Finder"
echo
echo "Then re-run this script."
