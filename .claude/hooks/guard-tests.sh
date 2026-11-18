#!/usr/bin/env bash
# PreToolUse hook: changing an EXISTING test file is allowed, but needs a reason.
# New test files are fine. The edit goes ahead; Claude gets a reminder to justify
# it and to tell the user (no question, no extra prompt).
set -u
input=$(cat)
repo=$(git rev-parse --show-toplevel 2>/dev/null) || exit 0

field() { # crude JSON string read, no jq needed
  printf '%s' "$input" | sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\"\(\([^\"\\\\]\|\\\\.\)*\)\".*/\1/p" | head -1
}

remind() { # $1 = test file(s)
  printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","additionalContext":"%s is an existing test. Change it only with a real reason (the intended behavior changed, or the test is wrong), never just to make a failing test pass. In your final reply, tell the user which test changed and why."}}\n' "$1"
  exit 0
}

tool=$(field tool_name)
if [ "$tool" = "Bash" ]; then
  cmd=$(field command)
  paths=$(printf '%s' "$cmd" | grep -Eo '(\./)?tests/[A-Za-z0-9_.-]+/[^[:space:]"'"'"';|&)]*' | sed 's#^\./##' | grep -Ev '/(bin|obj)(/|$)' | sort -u | tr '\n' ' ' | sed 's/ $//')
  if [ -n "$paths" ] &&
     printf '%s' "$cmd" | grep -Eq '(sed[^|;&]*-i|>[^&]|\btee\b|\brm\b|\bmv\b|\bcp\b|\bperl[^|;&]*-i|\bpython|git (checkout|restore))'; then
    remind "$paths"
  fi
  exit 0
fi

path=$(field file_path); [ -z "$path" ] && path=$(field notebook_path)
[ -z "$path" ] && exit 0
case "$path" in /*) ;; *) path="$repo/$path" ;; esac
case "$path" in
  "$repo"/tests/*/bin/*|"$repo"/tests/*/obj/*) exit 0 ;;
  "$repo"/tests/*) [ -e "$path" ] && remind "${path#$repo/}" ;;
esac
exit 0
