#!/usr/bin/env bash
# Hold tracking (R2 → E): the product's estimator on the owner's 2026-09-27 session (16 clips) and the HDR pilot, in the
# two windows of the R2 research (reference 0.5 s, window 0.5–9 s; reference 6 s, window 6–14.5 s). Sequential, so the
# timings are not distorted. Outputs (JSON, a summary) go to <out_dir>, outside the repository.
# Usage: run_all.sh <out_dir>
OUT="$1"; HERE="$(cd "$(dirname "$0")" && pwd)"; mkdir -p "$OUT"
DLL="$HERE/bin/Release/net8.0/HoldTrackingCheck.dll"
S="/d/Времянка для монтажа/Боулдер 27.09.2026"
PILOT="$USERPROFILE/Desktop/AiVideoEditor-E1-Reference/clips/vivo x300 pro - боулдеринг нежные цвета контраст.mp4"
: > "$OUT/summary.txt"
for f in "$S"/video_*.mp4 "$PILOT"; do
  n=$(basename "$f" .mp4); n=${n%% *}
  for w in "0.5 0.5 9" "6 6 14.5"; do
    set -- $w
    printf 'ref %-4s ' "$1" >> "$OUT/summary.txt"
    dotnet "$DLL" "$f" "$1" "$2" "$3" "$OUT/${n}_ref$1.json" >> "$OUT/summary.txt" 2>&1 || echo "$n ref $1: failed" >> "$OUT/summary.txt"
  done
done
cat "$OUT/summary.txt"
