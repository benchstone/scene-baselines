#!/usr/bin/env bash
#
# Replays a repository's history through Scene Baselines, newest commit first.
#
# The editor decides everything; this only does the one thing an editor cannot do for itself —
# check a commit out while no editor is running. Checking out underneath a live editor crashes
# Unity outright when the commit changes scripts ([SerializeReference] state cannot survive a
# reload that swaps assemblies), so each step gets its own editor process.
#
# Usage:
#   replay-history.sh --project <path> --unity <path/to/Unity> [--commits 10]
#                     [--scene Assets/Foo.unity]... [--out <dir>] [--baselines <dir>]
#                     [--scan 500] [--all-commits]
#
# --commits counts commits worth replaying, not commits back: one that changed nothing these scenes
# depend on cannot have changed how they look, and is never opened. --scan bounds how far back the
# editor may look for them, and --all-commits replays every commit instead.
#
# The report, the baselines and the run's state file all live outside the inspected project.

set -u

COMMITS=10
SCAN=""
FILTER=""
SCENES=()
PROJECT=""
UNITY=""
OUT=""
BASELINES=""

while [ $# -gt 0 ]; do
  case "$1" in
    --project)   PROJECT="$2"; shift 2 ;;
    --unity)     UNITY="$2"; shift 2 ;;
    --commits)   COMMITS="$2"; shift 2 ;;
    --scene)     SCENES+=("$2"); shift 2 ;;
    --out)       OUT="$2"; shift 2 ;;
    --baselines) BASELINES="$2"; shift 2 ;;
    --scan)      SCAN="$2"; shift 2 ;;
    --all-commits) FILTER="off"; shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

if [ -z "$PROJECT" ] || [ -z "$UNITY" ]; then
  echo "usage: $0 --project <path> --unity <path/to/Unity> [--commits n] [--scene path]..." >&2
  echo "       [--out dir] [--baselines dir] [--scan n] [--all-commits]" >&2
  exit 2
fi

OUT="${OUT:-$PWD/scene-baselines-replay}"
BASELINES="${BASELINES:-$OUT/baselines}"
STATE="$OUT/replay-state.json"
LOG="$OUT/replay.log"

mkdir -p "$OUT" "$BASELINES"
rm -f "$STATE"

SCENE_ARGS=()
for scene in ${SCENES+"${SCENES[@]}"}; do
  SCENE_ARGS+=(-replayScene "$scene")
done

# Only commits that changed something the scenes depend on are replayed, so --commits counts
# commits worth opening rather than a distance back through history, and --scan bounds how far the
# editor may look to find them. --all-commits turns the choosing off.
CHOICE_ARGS=()
[ -n "$SCAN" ]   && CHOICE_ARGS+=(-replayScan "$SCAN")
[ -n "$FILTER" ] && CHOICE_ARGS+=(-replayFilter "$FILTER")

# The way back, read before anything moves. The editor records it too and asks for it as the last
# checkout of the run; this is the belt to that pair of braces, for a run killed halfway.
ORIGINAL="$(git -C "$PROJECT" rev-parse --abbrev-ref HEAD)"
if [ "$ORIGINAL" = "HEAD" ]; then
  ORIGINAL="$(git -C "$PROJECT" rev-parse HEAD)"
fi
echo "Replaying $COMMITS commit(s) in $PROJECT. Will return to '$ORIGINAL'."

restore() {
  echo "Restoring '$PROJECT' to '$ORIGINAL'."
  git -C "$PROJECT" checkout --force "$ORIGINAL" >/dev/null 2>&1 ||
    echo "COULD NOT RESTORE. Run: git -C '$PROJECT' checkout --force '$ORIGINAL'" >&2
}
trap restore INT TERM

step=0
while : ; do
  step=$((step + 1))

  "$UNITY" -batchmode -nographics \
    -projectPath "$PROJECT" \
    -executeMethod SceneBaselines.HistoryReplay.RunStep \
    -replayState "$STATE" \
    -replayCommits "$COMMITS" \
    ${SCENE_ARGS+"${SCENE_ARGS[@]}"} \
    ${CHOICE_ARGS+"${CHOICE_ARGS[@]}"} \
    -baselineDir "$BASELINES" \
    -reportDir "$OUT" \
    -logFile "$LOG"
  code=$?

  # Streamed as it happens: the number that decides whether this is usable is time to the FIRST
  # finding, so the driver echoes each step's lines rather than waiting for the run to end.
  grep -a "\[Scene Baselines\]" "$LOG" | tail -5

  if [ $code -eq 2 ]; then
    echo "Replay refused or failed at step $step — see $LOG" >&2
    restore
    exit 2
  fi

  if [ $code -ne 0 ] && [ $code -ne 7 ]; then
    echo "Unity exited $code at step $step — see $LOG" >&2
    restore
    exit "$code"
  fi

  # Both "continue" and "complete" name the next checkout; on the last step it is the way back.
  NEXT="$(sed -n 's/.*"nextCheckout": "\([^"]*\)".*/\1/p' "$STATE" | head -1)"
  DONE="$(sed -n 's/.*"done": \(true\|false\).*/\1/p' "$STATE" | head -1)"

  if [ -n "$NEXT" ]; then
    git -C "$PROJECT" -c advice.detachedHead=false checkout --force "$NEXT" >/dev/null 2>&1 || {
      echo "Could not check out $NEXT" >&2
      restore
      exit 3
    }
  fi

  if [ "$DONE" = "true" ]; then
    echo "Replay complete. Report and state in $OUT"
    exit 0
  fi
done
