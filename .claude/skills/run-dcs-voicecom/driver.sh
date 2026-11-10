#!/usr/bin/env bash
# Build and drive the WPF app (DcsVoiceCom.exe) under Wine on a headless Xvfb display.
#
#   driver.sh setup            one-time: apt tools (wine, xvfb, xdotool, imagemagick, fonts), .NET SDK,
#                              Wine prefix (an unfinished one is rebuilt), fonts, sample profiles
#   driver.sh build            dotnet publish -r win-x64 --self-contained
#   driver.sh launch           start Xvfb + the app, wait for the main window, pin it at 0,0
#   driver.sh ss [name]        screenshot of the main window -> $DCSVC_WORK/shots/<name>.png
#   driver.sh sswin REGEX [name] screenshot of the first visible window whose title matches (e.g. Settings)
#   driver.sh ssroot [name]    screenshot of the whole display (dialogs, menus, popups)
#   driver.sh click X Y        left click at window coordinates (= screen coords, window is at 0,0)
#   driver.sh rclick X Y       right click
#   driver.sh dclick X Y       double click
#   driver.sh type "text"      type into the focused control
#   driver.sh key ctrl+s       send a key chord (xdotool key names: Return, Tab, ctrl+n, F5...)
#   driver.sh waitwin REGEX    wait for a window title to appear, then 2s so it accepts input (dialogs)
#   driver.sh wait SECONDS     sleep (Wine repaints/closes windows 1-2s after the input)
#   driver.sh windows          list top-level windows (id, geometry, title)
#   driver.sh log [n]          last n lines of Wine's stdout/stderr (crash traces; the in-app log is on screen)
#   driver.sh quit             kill the app and the Wine server (Xvfb stays up)
#
# Paths: everything lives under $DCSVC_WORK (default /tmp/dcs-voicecom-run).
set -euo pipefail

REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)
W=${DCSVC_WORK:-/tmp/dcs-voicecom-run}
SDK=${DCSVC_SDK:-/usr/lib/dotnet}
PUB=$W/pub
export WINEPREFIX=$W/wine WINEDEBUG=-all DISPLAY=${DCSVC_DISPLAY:-:99}
APPDATA_DIR=$WINEPREFIX/drive_c/users/$(whoami)/AppData/Local/DcsVoiceCom

sleep_s() { python3 -c "import time,sys; time.sleep(float(sys.argv[1]))" "$1"; }

main_window() {
  { xdotool search --onlyvisible --name '^DCS VoiceCom' 2>/dev/null || true; } | head -1
}

ensure_xvfb() {
  if ! xdotool getmouselocation >/dev/null 2>&1; then
    (Xvfb "$DISPLAY" -screen 0 1600x1000x24 >"$W/xvfb.log" 2>&1 &)
    sleep_s 1
  fi
}

cmd_setup() {
  mkdir -p "$W/shots"
  # Ubuntu's dotnet-sdk-10.0 has the WPF (WindowsDesktop) targets; its 8.0 SDK did not.
  if ! ls "$SDK/sdk/"10.*/Sdks/Microsoft.NET.Sdk.WindowsDesktop >/dev/null 2>&1; then
    apt-get install -y dotnet-sdk-10.0
  fi
  ls "$SDK/sdk/"10.*/Sdks/Microsoft.NET.Sdk.WindowsDesktop >/dev/null

  # Many containers ship without Wine and the X tools. Install whatever is missing (the Ubuntu archive is reachable).
  local missing=()
  for t in wine:wine Xvfb:xvfb xdotool:xdotool import:imagemagick convert:imagemagick; do
    command -v "${t%%:*}" >/dev/null || missing+=("${t#*:}")
  done
  [ -d /usr/share/fonts/truetype/liberation ] || missing+=(fonts-liberation)
  [ -d /usr/share/fonts/truetype/jetbrains-mono ] || missing+=(fonts-jetbrains-mono)
  [ -d /usr/share/fonts/truetype/dejavu ] || missing+=(fonts-dejavu-core)
  if ((${#missing[@]})); then
    echo "installing: ${missing[*]}"
    apt-get install -y $(printf '%s\n' "${missing[@]}" | sort -u) >/dev/null || {
      apt-get update >/dev/null && apt-get install -y $(printf '%s\n' "${missing[@]}" | sort -u) >/dev/null; }
  fi

  ensure_xvfb
  # A prefix from a failed run (no Wine at the time, or a killed wineboot) has no Fonts folder or a broken
  # system; the font step then fails with FileNotFoundError. Only a prefix marked ready by a finished setup is kept.
  if [ -d "$WINEPREFIX" ] && [ ! -f "$WINEPREFIX/.dcsvc-ready" ]; then
    echo "removing unfinished Wine prefix"
    wineserver -k 2>/dev/null || true
    pkill -f DcsVoiceCom.exe 2>/dev/null || true
    rm -rf "$WINEPREFIX"
  fi
  if [ ! -d "$WINEPREFIX/drive_c" ]; then
    echo "creating Wine prefix (about 30 s)"
    wineboot -i >"$W/wineboot.log" 2>&1 || { tail -20 "$W/wineboot.log"; exit 1; }
    wineserver -w
  fi
  mkdir -p "$WINEPREFIX/drive_c/windows/Fonts"

  # WPF FailFasts in TypefaceMap.MapUnresolvedCharacters when no font resolves. Install Linux fonts
  # renamed to the Windows family names the app and WPF's fallback chain ask for, and register them.
  python3 -c "import fontTools" 2>/dev/null || pip install -q fonttools
  local F=$WINEPREFIX/drive_c/windows/Fonts L=/usr/share/fonts/truetype/liberation J=/usr/share/fonts/truetype/jetbrains-mono
  local rn="$W/rename_font.py"
  cat >"$rn" <<'EOF'
import sys
from fontTools.ttLib import TTFont
src, dst, fam = sys.argv[1:4]
f = TTFont(src)
sub = f['name'].getDebugName(2) or 'Regular'
for r in f['name'].names:
    if r.nameID in (1, 16):
        r.string = fam
    elif r.nameID == 4:
        r.string = fam if sub == 'Regular' else f"{fam} {sub}"
    elif r.nameID == 6:
        r.string = fam.replace(' ', '') + '-' + sub.replace(' ', '')
f.save(dst)
EOF
  for st in Regular Bold Italic BoldItalic; do
    python3 "$rn" $L/LiberationSans-$st.ttf "$F/segoe-$st.ttf" "Segoe UI"
    python3 "$rn" $L/LiberationSans-$st.ttf "$F/arial-$st.ttf" "Arial"
    python3 "$rn" $L/LiberationSans-$st.ttf "$F/tahoma-$st.ttf" "Tahoma"
    python3 "$rn" $L/LiberationSerif-$st.ttf "$F/times-$st.ttf" "Times New Roman"
    python3 "$rn" $L/LiberationMono-$st.ttf "$F/consola-$st.ttf" "Consolas"
  done
  for st in Regular Bold SemiBold; do python3 "$rn" $J/JetBrainsMono-$st.ttf "$F/cascadia-$st.ttf" "Cascadia Mono"; done
  cp /usr/share/fonts/truetype/dejavu/DejaVuSans*.ttf "$F/"
  { echo 'REGEDIT4'; echo; echo '[HKEY_LOCAL_MACHINE\Software\Microsoft\Windows NT\CurrentVersion\Fonts]'
    for f in "$F"/*.ttf; do b=$(basename "$f"); echo "\"$b (TrueType)\"=\"$b\""; done; } >"$W/fonts.reg"
  wine regedit "$W/fonts.reg"

  # Sample profiles so the lists are not empty (settings.json is created by the app on first launch).
  mkdir -p "$APPDATA_DIR/profiles"
  cp "$REPO"/profiles/*.json "$APPDATA_DIR/profiles/"
  touch "$WINEPREFIX/.dcsvc-ready"
  echo "setup done: $W"
}

cmd_build() {
  rm -rf "$PUB"  # a stale publish keeps old runtime files (newer timestamps win)
  DOTNET_ROOT=$SDK DOTNET_CLI_TELEMETRY_OPTOUT=1 "$SDK/dotnet" publish "$REPO/src/DcsVoiceCom.App" \
    -c Release -r win-x64 --self-contained -o "$PUB" 2>&1 | tail -3
  test -f "$PUB/DcsVoiceCom.exe"
}

cmd_launch() {
  ensure_xvfb
  if [ -n "$(main_window)" ]; then echo "already running"; return; fi
  (cd "$PUB" && wine DcsVoiceCom.exe >"$W/app.log" 2>&1 &)
  local wid=""
  for _ in $(seq 1 90); do
    wid=$(main_window); [ -n "$wid" ] && break
    if grep -q "Process terminated" "$W/app.log" 2>/dev/null; then head -5 "$W/app.log"; exit 1; fi
    sleep_s 1
  done
  [ -n "$wid" ] || { echo "no window after 90s"; tail -20 "$W/app.log"; exit 1; }
  sleep_s 3   # first layout pass + startup log lines
  xdotool windowmove "$wid" 0 0
  sleep_s 1
  echo "window $wid: $(xdotool getwindowgeometry "$wid" | tr '\n' ' ')"
}

cmd_ss() {
  local name=${1:-shot-$(date +%H%M%S)} wid; mkdir -p "$W/shots"
  wid=$(main_window); [ -n "$wid" ] || { echo "app not running"; exit 1; }
  import -window "$wid" "$W/shots/$name.png"; echo "$W/shots/$name.png"
}

cmd_ssroot() {
  local name=${1:-root-$(date +%H%M%S)}; mkdir -p "$W/shots"
  import -window root "$W/shots/$name.png"; echo "$W/shots/$name.png"
}

case ${1:-} in
  setup)   cmd_setup ;;
  build)   cmd_build ;;
  launch)  cmd_launch ;;
  ss)      cmd_ss "${2:-}" ;;
  ssroot)  cmd_ssroot "${2:-}" ;;
  sswin)   wid=$({ xdotool search --onlyvisible --name "$2" 2>/dev/null || true; } | tail -1)
           [ -n "$wid" ] || { echo "no window matching $2"; exit 1; }
           f="$W/shots/${3:-win-$(date +%H%M%S)}.png"; mkdir -p "$W/shots"; import -window "$wid" "$f"; echo "$f" ;;
  click)   xdotool mousemove "$2" "$3" click 1; sleep_s 0.8 ;;
  rclick)  xdotool mousemove "$2" "$3" click 3; sleep_s 0.8 ;;
  dclick)  xdotool mousemove "$2" "$3" click --repeat 2 1; sleep_s 0.8 ;;
  type)    xdotool type --delay 30 "$2"; sleep_s 0.5 ;;
  key)     xdotool key "$2"; sleep_s 0.8 ;;
  waitwin) for _ in $(seq 1 30); do
             [ -n "$({ xdotool search --onlyvisible --name "$2" 2>/dev/null || true; } | head -1)" ] && { sleep_s 2; exit 0; }
             sleep_s 0.5; done; echo "no window matching $2"; exit 1 ;;
  wait)    sleep_s "$2" ;;
  windows) for w in $(xdotool search --onlyvisible --name '.' 2>/dev/null); do
             echo "$w $(xdotool getwindowgeometry "$w" | sed -n 's/.*Position: \([^ ]*\).*/\1/p;s/.*Geometry: //p' | tr '\n' ' ') $(xdotool getwindowname "$w")"; done ;;
  log)     tail -n "${2:-30}" "$W/app.log" ;;
  quit)    wineserver -k 2>/dev/null || true; echo stopped ;;
  *)       sed -n '2,23p' "$0"; exit 1 ;;
esac
