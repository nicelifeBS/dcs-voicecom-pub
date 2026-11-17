#!/usr/bin/env bash
# UserPromptSubmit hook: the user has replied, so test edits that were
# blocked last turn may now be retried (each still shows a permission prompt).
# Earlier replies expire: only last turn's blocked edits carry over.
repo=$(git rev-parse --show-toplevel 2>/dev/null) || exit 0
state="$(git -C "$repo" rev-parse --absolute-git-dir)"
if [ -f "$state/claude-test-edits-pending" ]; then
  mv "$state/claude-test-edits-pending" "$state/claude-test-edits-replied"
else
  rm -f "$state/claude-test-edits-replied"
fi
exit 0
