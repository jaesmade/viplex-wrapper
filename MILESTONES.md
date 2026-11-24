# Milestones

Record completed milestones for the NovaStar T10 USB editor here, newest first. Include each entry in the same Git commit as the work it describes.

## Completed milestones

### 2026-11-24 — Preset sets and image placement

- Added session-scoped preset sets with independent ordered pages and edits. New sets reuse player authentication from the loaded package. Export and USB write use the selected set.
- Added move, resize, and reset controls for still images. Exports render edited images and their thumbnails on the 256 × 768 display canvas.
- The Release build passed with zero warnings. All 18 core reference checks passed, including new-set playback order and preservation of source authentication.
- Four headless WPF checks passed for set switching, edit and reset controls, drag geometry, and composed folder export.
- Live pointer and dialog interaction, physical USB writes, and playback on the T10 remain unverified.

### 2026-11-24 — Milestone commit format

- Standardized milestone commit messages as `feat: <changes>`.
- Git whitespace check passed for the Markdown changes.

### 2026-11-24 — Initial USB editor

- Added the .NET 10 WPF editor and package library for creating and editing ViPlex packages, exporting to a folder, writing to USB, and restoring backups.
- The [project verification notes](MASTER.md#verification) record a warning-free Release build, 16 passing reference checks, and headless WPF checks.
- T10 hardware validation remained pending.
- Baseline commit `4bb4b2e`.

## Entry template

```markdown
### YYYY-MM-DD — Milestone name

- Summarize what changed.
- Record the checks run and their results.
- Note any pending verification, if applicable.
```
