# Notes for agents

## In-app help

- Pages: `docs/help/*.md`, built into the exe. Front matter `title:` and `group:`; file name sets the order.
- Supported Markdown: headings, lists, bold, `code`, pipe tables, images, page links (`03-phrases.md`), `> **Tip:**` boxes.
- Keep pages short. Update the page when a change touches what it describes.
- `HelpTests.Shipped_help_pages_are_complete` checks images and links.

## Screenshots at release time

When preparing a release (notes in `docs/releases/`, version bump, `v*` tag), check for big UI changes (a window,
panel, button or layout) since the last screenshot commit:
`git diff --stat $(git log -1 --format=%h -- docs/help/images) -- src/DcsVoiceCom.App`

If there are any, **ask the user before retaking**. On yes:

1. `.claude/skills/run-dcs-voicecom/driver.sh setup` (installs Wine and the X tools when missing; see the `help-screenshots` skill)
2. `tools/help-screenshots/take.sh`
3. Read every PNG. Fix coordinates in `take.sh` if a click missed. Markers in `main-window.png` must match `docs/help/02-main-window.md`.
4. Commit the images with the release.
