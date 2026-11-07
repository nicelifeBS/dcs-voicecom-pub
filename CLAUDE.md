# Notes for agents

## In-app help

The Help window (Help button or F1) shows the Markdown pages in `docs/help/`. They are built into the exe, so a
change there ships with the next build. Keep the pages short and in plain Markdown: headings, lists, bold, `code`,
pipe tables, images, links to other pages (`03-phrases.md`), and `> **Tip:**` quotes, which show as tip boxes.
Each page starts with front matter (`title:`, `group:`); the file name sets the order.
`HelpTests.Shipped_help_pages_are_complete` checks that every image and page link exists.

When a change adds, renames or moves something the help describes, update the page in the same change.

### Screenshots: update them when a release is prepared

The screenshots in `docs/help/images/` show the real app. When you prepare a release (release notes in
`docs/releases/`, a version bump, or a `v*` tag), check whether the UI changed in a big way since the screenshots
were last taken: `git log --oneline -- docs/help/images` gives that commit, then
`git diff --stat <that commit> -- src/DcsVoiceCom.App` shows the UI changes since. Big means a window, panel, button
or the layout changed, not a colour or a word.

If it did, **ask the user first** whether to retake the screenshots. Do not retake them without a yes. Then:

1. Run `.claude/skills/run-dcs-voicecom/driver.sh setup` once in the container.
2. Run `tools/help-screenshots/take.sh`. It builds the app, runs it under Wine, and writes every PNG, with the
   numbered markers, to `docs/help/images/`.
3. Read every PNG. If a click missed after a layout change, fix the coordinates in `take.sh` (take a full shot
   with `driver.sh ss` to find them) and run it again. Check that the numbers in `main-window.png` still match the
   list in `docs/help/02-main-window.md`.
4. Commit the images with the release.
