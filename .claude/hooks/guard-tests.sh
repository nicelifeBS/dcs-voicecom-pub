#!/usr/bin/env bash
# PreToolUse hook: changing an EXISTING test file needs the user's consent.
# New test files are fine.
#
# 1st try:  blocked. Claude must stop, explain which test and why, and ask.
# After the user replies (see consent-tests.sh), a retry shows a permission
# prompt, so the user still approves the actual edit.
set -u
input=$(cat)
repo=$(git rev-parse --show-toplevel 2>/dev/null) || exit 0
state="$(git -C "$repo" rev-parse --absolute-git-dir)"
pending="$state/claude-test-edits-pending"
approved="$state/claude-test-edits-replied"

field() { # crude JSON string read, no jq needed
  printf '%s' "$input" | sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\"\(\([^\"\\\\]\|\\\\.\)*\)\".*/\1/p" | head -1
}

guard() { # $1 = key naming the test file(s)
  key="$1"
  if [ -f "$approved" ] && grep -qxF "$key" "$approved"; then
    printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"ask","permissionDecisionReason":"Change to existing test: %s. Approve only if you agreed to it."}}\n' "$key"
    exit 0
  fi
  grep -qxF "$key" "$pending" 2>/dev/null || echo "$key" >> "$pending"
  printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"STOP. %s is an existing test and needs the user'"'"'s consent. Do not work around this. End your turn now: tell the user which test you want to change, what exactly changes, and why. Ask for consent. Only retry after the user agrees."}}\n' "$key"
  exit 0
}

tool=$(field tool_name)
if [ "$tool" = "Bash" ]; then
  cmd=$(field command)
  paths=$(printf '%s' "$cmd" | grep -Eo '(\./)?tests/[A-Za-z0-9_.-]+/[^[:space:]"'"'"';|&)]*' | sed 's#^\./##' | grep -Ev '/(bin|obj)(/|$)' | sort -u | tr '\n' ' ' | sed 's/ $//')
  if [ -n "$paths" ] &&
     printf '%s' "$cmd" | grep -Eq '(sed[^|;&]*-i|>[^&]|\btee\b|\brm\b|\bmv\b|\bcp\b|\bperl[^|;&]*-i|git (checkout|restore))'; then
    guard "$paths"
  fi
  exit 0
fi

path=$(field file_path); [ -z "$path" ] && path=$(field notebook_path)
[ -z "$path" ] && exit 0
case "$path" in /*) ;; *) path="$repo/$path" ;; esac
case "$path" in
  "$repo"/tests/*/bin/*|"$repo"/tests/*/obj/*) exit 0 ;;
  "$repo"/tests/*) [ -e "$path" ] && guard "${path#$repo/}" ;;
esac
exit 0
