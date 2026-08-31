#!/usr/bin/env bash
# Copy the package out of the Unity project it is developed in, into this repo.
#
# The package is developed embedded in a Unity project, because that is the only
# place Unity will compile and run it. This repo is what the world installs. Two
# copies means they can drift, so this is the one direction that is ever allowed:
# project -> repo. Never the other way; edit in the project.
#
# Usage:  Tools~/sync-from-project.sh [path-to-unity-project]
#         Tools~/sync-from-project.sh --check   # report drift, change nothing

set -euo pipefail

DEFAULT_PROJECT="$HOME/Desktop/unity's games/puzzle Tbd - debugging"
PKG_REL="Packages/com.benchstone.scenebaselines"

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

CHECK_ONLY=0
if [ "${1:-}" = "--check" ]; then CHECK_ONLY=1; shift; fi
PROJECT="${1:-$DEFAULT_PROJECT}"
SRC="$PROJECT/$PKG_REL"

if [ ! -f "$SRC/package.json" ]; then
  echo "No package at: $SRC" >&2
  echo "Pass the Unity project path as the first argument." >&2
  exit 1
fi

# Only what the package ships. Repo infrastructure (.git*, Tools~) is not in the
# project copy and must never be deleted by a sync.
ITEMS=(Editor Tests package.json package.json.meta Editor.meta Tests.meta
       README.md README.md.meta LICENSE.md LICENSE.md.meta
       CHANGELOG.md CHANGELOG.md.meta)

drift=0
for item in "${ITEMS[@]}"; do
  if [ ! -e "$SRC/$item" ]; then
    echo "  missing in project: $item"; drift=1; continue
  fi
  if ! diff -rq "$SRC/$item" "$REPO/$item" >/dev/null 2>&1; then
    echo "  differs: $item"; drift=1
  fi
done

if [ "$drift" -eq 0 ]; then echo "In sync."; exit 0; fi
if [ "$CHECK_ONLY" -eq 1 ]; then echo "Drift found (nothing changed)."; exit 1; fi

for item in "${ITEMS[@]}"; do
  [ -e "$SRC/$item" ] || continue
  rm -rf "${REPO:?}/$item"
  cp -r "$SRC/$item" "$REPO/$item"
done

echo "Synced. Review with 'git diff' before committing."
