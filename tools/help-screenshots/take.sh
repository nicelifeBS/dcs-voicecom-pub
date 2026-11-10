#!/usr/bin/env bash
# Retakes the screenshots of the in-app help (docs/help/images) and draws the numbered markers into them.
#
# Runs the real app under Wine + Xvfb through .claude/skills/run-dcs-voicecom/driver.sh. Run `driver.sh setup` once
# per container first. Then, from the repo root:
#
#   tools/help-screenshots/take.sh          # builds, launches, takes every shot, quits
#   tools/help-screenshots/take.sh --no-build
#
# The app gets the sample profiles (profiles/*.json) and a small hand-written DCS-BIOS module
# (sample-dcs-bios/, F-14 controls only, not real DCS-BIOS output) so the Seed window and the control panel have
# content. Coordinates below match the 1432x866 main window the driver pins at 0,0; after a big layout change,
# take a full screenshot (driver.sh ss) and update them. Read every PNG before you commit it.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
D="$ROOT/.claude/skills/run-dcs-voicecom/driver.sh"
WORK="${DCSVC_WORK:-/tmp/dcs-voicecom-run}"
SHOTS="$WORK/shots"
OUT="$ROOT/docs/help/images"
PREFIX="$WORK/wine/drive_c/users/root"
mkdir -p "$OUT"

# Numbered amber markers, the app's accent colour: marker IN OUT x y n [x y n ...]
marker() {
  local in="$1" out="$2"; shift 2
  local args=()
  while (($#)); do
    local x="$1" y="$2" n="$3"; shift 3
    args+=(-fill '#f2a93b' -stroke '#101316' -strokewidth 2 -draw "circle $x,$y $((x + 12)),$y")
    args+=(-stroke none -fill '#101316' -pointsize 15 -gravity NorthWest -annotate "+$((x - 4))+$((y - 9))" "$n")
  done
  convert "$in" -font DejaVu-Sans-Bold "${args[@]}" "$out"
}

# crop IN OUT x y w h
crop() { convert "$1" -crop "$5x$6+$3+$4" +repage "$2"; }

# A shot taken while Wine repaints can come out black: stop instead of committing it. Run it on the raw shot,
# before the markers add colours, and again on each crop.
check() {
  local colors; colors=$(identify -format %k "$1")
  if ((colors < 16)); then echo "blank screenshot: $1 ($colors colours); run again" >&2; exit 1; fi
}

if [[ "${1:-}" != "--no-build" ]]; then "$D" build; fi

# Fresh state: sample profiles, the sample DCS-BIOS module, F-14 pinned.
"$D" quit >/dev/null 2>&1 || true
mkdir -p "$PREFIX/AppData/Local/DcsVoiceCom/profiles" "$PREFIX/Saved Games/DCS/Scripts/DCS-BIOS/doc/json"
cp "$ROOT"/profiles/*.json "$PREFIX/AppData/Local/DcsVoiceCom/profiles/"
cp "$ROOT"/tools/help-screenshots/sample-dcs-bios/*.json "$PREFIX/Saved Games/DCS/Scripts/DCS-BIOS/doc/json/"
# Pin F-14, so typed tests match without DCS (the app writes settings.json on its first start).
SETTINGS="$PREFIX/AppData/Local/DcsVoiceCom/settings.json"
if [[ -f "$SETTINGS" ]]; then
  python3 -c 'import json,sys; p=sys.argv[1]; s=json.load(open(p, encoding="utf-8-sig")); s["profileOverride"]="F-14"; json.dump(s, open(p,"w"), indent=2)' "$SETTINGS"
else
  echo '{ "profileOverride": "F-14" }' > "$SETTINGS"
fi

setsid nohup "$D" launch > "$WORK/launch.log" 2>&1 < /dev/null &
for _ in $(seq 1 60); do grep -q Geometry "$WORK/launch.log" 2>/dev/null && break; sleep 2; done
grep -q Geometry "$WORK/launch.log" || { cat "$WORK/launch.log"; exit 1; }

# Main window: F-14, "gear up" selected.
"$D" click 120 104; "$D" wait 1
"$D" click 400 180; "$D" wait 2
"$D" ss main >/dev/null; check "$SHOTS/main.png"
marker "$SHOTS/main.png" "$OUT/main-window.png" \
  770 36 1   212 77 2   372 83 3   712 95 4   1240 93 5   42 636 6

# Log: two typed tests (Dry run is on, so nothing is sent).
"$D" click 600 653; "$D" type "landing gear up"; "$D" key Return; "$D" wait 1
"$D" key ctrl+a; "$D" type "cheer down"; "$D" key Return; "$D" wait 1
"$D" key ctrl+a; "$D" type "master arm"; "$D" key Return; "$D" wait 2
"$D" ss log >/dev/null; check "$SHOTS/log.png"
crop "$SHOTS/log.png" "$OUT/log.png" 16 620 1400 230

# Editor: the UHF frequency command (a slot and an Enter value step).
"$D" click 400 120; "$D" wait 1; "$D" type "uhf"; "$D" wait 2
"$D" click 400 180; "$D" wait 3
"$D" ss editor >/dev/null; check "$SHOTS/editor.png"
crop "$SHOTS/editor.png" "$OUT/voice-line.png" 584 72 418 350
crop "$SHOTS/editor.png" "$OUT/actions.png" 584 432 418 176

# Enter value window (no DCS here, so it shows its warning and empty cells).
"$D" click 912 561; "$D" waitwin 'Enter value'
"$D" sswin 'Enter value' enter-value >/dev/null
cp "$SHOTS/enter-value.png" "$OUT/enter-value.png"
"$D" click 1083 800; "$D" wait 2

# Seed window with two panels ticked.
"$D" click 442 83; "$D" waitwin 'Seed'
"$D" click 260 208; "$D" click 260 248; "$D" wait 1
"$D" sswin 'Seed' seed >/dev/null
cp "$SHOTS/seed.png" "$OUT/seed.png"
"$D" click 1003 740; "$D" wait 2

# Settings window.
"$D" click 1315 36; "$D" waitwin '^Settings$'
"$D" sswin '^Settings$' settings >/dev/null
cp "$SHOTS/settings.png" "$OUT/settings.png"

"$D" quit >/dev/null
for f in "$OUT"/*.png; do check "$f"; done
# Keep the PNGs small: 8-bit palette, no metadata.
for f in "$OUT"/*.png; do convert "$f" -strip +dither -colors 256 PNG8:"$f"; done
ls -la "$OUT"
