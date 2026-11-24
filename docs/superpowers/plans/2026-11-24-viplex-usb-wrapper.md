# NovaStar T10 ViPlex USB Wrapper Implementation Plan

Build a Windows utility for the NovaStar T10 with a 256 × 768 screen. The operator can start on a blank USB, or open a ViPlex export and replace images or videos on one or more pages. The utility writes `nova/` to the chosen USB drive or exports it to a folder for manual copying. Firmware support starts with the version verified on this unit.

## What the reference establishes

The ViPlex screenshot identifies the player as `T10` and shows a 256 × 768 screen. The firmware field is blank. All five supplied exports use a `nova/` directory and a 256 × 768 playlist. Each playlist has `pickPolicy: ORDER` and one full-screen media item per page. [Reference exports](D:/Projects/references/viplex)

| Export | Selected program | Ordered pages | `isPlay` | `isImport` | `isOverride` |
| --- | --- | --- | --- | --- | --- |
| `picture` | `Arta` | One image, 10 s | `true` | `true` | `true` |
| `videoplayback` | `arta new` | One video, 100 s | `true` | `true` | `true` |
| `multipic` | `smpl` | Two images, 10 s each | `true` | `true` | `true` |
| `multivid` | `smpl` | Two videos, 180 s then 100 s | `true` | `true` | `true` |
| `plugnply` | `smpl` | Same two videos and timings | `true` | `false` | `false` |

`multivid` and `plugnply` provide the matched pair for the mode switch. The decoded commands differ only in `isImport` and `isOverride`. The active `playlist0.json`, media, thumbnails, `playSolutionRelation.json` and `schedule_constraint.json` match byte for byte. `play_solution.json` has a different `uuid`, and `planlist.json` updates the two fields derived from that file's MD5. Use the command flags for mode selection and verify on hardware that the wrapper can preserve the existing `play_solution.json` UUID.

```text
<USB root>/
└── nova/
    ├── command.json
    ├── media/
    ├── program/
    │   └── <selected program>/
    │       ├── playlist0.json
    │       ├── planlist.json
    │       ├── play_solution.json
    │       ├── playSolutionRelation.json
    │       ├── schedule_constraint.json
    │       └── <one or more thumbnails>.jpg
    └── viplex_terminal/
        └── media/
```

- The on-disk `command.json` is Base64-wrapped JSON. The selected entry's `identifier` matches the MD5 of the exact `playlist0.json` bytes in all five exports, and its `thumbnailName` points to the first page's thumbnail. The selected playlist `uuid` stays the same from `multipic` through `multivid` and `plugnply`, even when the content hash changes.
- The `planlist.json` references configuration files, media and thumbnails by filename and MD5. All 137 manifest hash checks matched across 22 program-folder instances. Command size exceeds listed resource bytes by 6,144 bytes for one page and 7,168 for two. The candidate formula adds 1,024 bytes per program JSON file and resource entry. Each sample has five JSON files and one distinct resource per page, so repeated assets need a separate check to distinguish page count from resource count.
- `sceneItems[]` stores pages in playback order. Each scene and its media widget carry matching durations in milliseconds. The two image pages in `multipic` share a thumbnail filename. Track thumbnail references by filename so changing one page does not break another page that still uses the same file. Exported thumbnail entries use `size: 0` even though the JPEG files contain data.
- The roots include extra program folders and shared media. Identify the selected program through `programInfos[].url`. Carry forward unselected programs, unrelated media and the empty `viplex_terminal/media/` directory. All five reference directories contain only `nova/` at the root.
- The command includes the player connection credential in readable form after Base64 decoding. Process it locally as part of the operator's own export. Keep it out of logs and use synthetic credentials in repository test fixtures.

## T10 editor layout

Follow the attached ViPlex screenshot's left-to-right editing flow with a playlist rail, a central preview and media strip, and a properties panel. Use a dark graphite workspace with teal selection accents and a dedicated **Prepare USB** action. The header shows **NovaStar T10 · 256 × 768** throughout the workflow.

```text
┌─ Open USB or folder ───────────── NovaStar T10 · 256 × 768 ─── Prepare USB ─────┐
│ PLAYLIST                    │ PREVIEW                     │ PAGE PROPERTIES     │
│ Active program + duration   │ Fit-to-window portrait      │ Media name and type │
│ Page thumbnails in order    │ Image or video playback     │ Page duration       │
│ Name and duration per page  │ MEDIA STRIP · Import media  │ Replace media       │
└─────────────────────────────┴─────────────────────────────┴─────────────────────┘
```

- **Playlist rail** shows the active program and its total duration. Selecting a page updates the preview and properties together. Page order follows the opened export.
- **Preview and media strip** show the selected page at its 1:3 aspect ratio, with play and pause for video. Import replacement media locally, then apply it to the selected page. Mark the edited page as pending until the export or USB write succeeds.
- **Page properties** show the selected file, its type and its playback duration. **Replace media** opens a file picker for that page. Show file validation feedback next to the replacement action and keep the current page loaded if validation fails.
- **Prepare USB** opens a review with the active program, playback mode and destination. **Write to USB** targets the selected drive. **Export folder** prepares a copyable `nova/` folder. Both show the destination and completion state after validation.

## Operator workflow

1. Choose a USB drive or folder. If it has no package, enter the program name and player connection credentials, then select media to initialize a new T10 program. If a package exists, the app shows the selected program, its pages in playback order and their durations.
2. Select any page, pick replacement media and preview it at 256 × 768. Start with image-to-image or video-to-video replacement and retain the existing page order. Enable the media formats and codec settings proven on the target player. Keep each image's hold time and update each video page's duration from its replacement.
3. Pick **Plug and play** to run from the USB, or **Copy and replace** to import the program to the player and play it. The wrapper applies the observed command flags for the corresponding ViPlex mode. A second import on the T10 will establish whether the existing solution is replaced as intended. [NovaStar ViPlex Express manual, section 3.4](https://oss.novastar.tech/uploads/2024/02/ViPlex-Express-Async-Mode-User-Manual-V2.26.0.pdf#page=21)
4. Choose **Export folder** to get a ready-to-copy `nova/` folder, or **Write to USB** to apply the same package to the selected drive. The destination is `<USB root>/nova/`. Confirm the target when replacing an existing `nova/` folder.

## Implementation sequence

### 1. Establish the model profile

- [ ] Record the T10 firmware, ViPlex Express build and tested USB filesystem for the 256 × 768 installation.
- [ ] Play the untouched `multivid` and `plugnply` exports on the T10 to establish the behavior of both modes. Use those packages and the three other samples as comparison fixtures. Use a fresh export only to resolve remaining questions about revision identity, media acceptance or USB layout.
- [ ] Check whether retaining `play_solution.json` and its UUID while switching the command flags works on the T10. Confirm the supported package shape, file layout and file types on that firmware.

### 2. Build a template-based package core

- [x] Implement `src/ViPlex.Core/NovaPackage.cs` to open the export, resolve the selected program and its ordered pages, and validate supported layout, paths and references. Reject truncated or malformed files, missing assets, unsupported layouts and paths that escape `nova/`.
- [x] Implement `src/ViPlex.Core/PackageCreator.cs` to generate a first `nova/` package from selected media and locally supplied player credentials. Use the T10 canvas, one full-screen media item per page, and new program identifiers. Validate the complete new tree before publishing.
- [x] Implement `src/ViPlex.Core/PackageBuilder.cs` to clone the template into a local staging directory. For each edited page, generate the replacement asset's MD5 filename and update the media path, byte size and applicable scene and widget durations. Refresh manifest entries and thumbnails while preserving references used by unedited pages or programs. Keep the selected program name and playlist UUID stable across edits. Recompute the command identifier and size, set its first-page thumbnail and apply the verified mode flags. Preserve unknown fields and serialize changed files in the reference format.
- [x] Add and run the reference verification checks in [MASTER.md](../../../MASTER.md), covering unchanged exports, a mode-only switch, manifest hashes and command sizes, single- and two-page media replacement, shared thumbnails, invalid packages, new packages and recovery.

### 3. Export and write the same package

- [x] Implement `src/ViPlex.Core/PackagePublisher.cs` to publish and re-read the staged tree. For USB writes, check the selected volume and free space, back up its `nova/` to the local machine, and finish copying the replacement before switching the active folder. Report success after re-reading and validating the installed package. On interruption, retain the backup and expose a restore path. Keep other USB-root files intact.
- [x] Build the three-pane editor and **Prepare USB** review under `src/ViPlex.Windows/` using C# and WPF on .NET 10. Folder-first publishing lets the operator inspect or copy the prepared package, and direct USB writing uses the same package builder.
- [ ] Open `multipic` and `multivid` in the editor, switch between both pages and verify the preview, thumbnail selection and properties stay in sync. Replace only the second page and confirm that the first page stays intact, the edited page is marked pending and the output review shows the selected mode and destination.
- [ ] Verify both publish paths yield the same `nova/` tree and that a failed copy or removed drive leaves a recoverable backup and a visible failure state.

### 4. Verify on the actual player

- [ ] Compare wrapper output against the matched official exports. Test single- and two-page image and video packages in each mode on the T10 with its recorded firmware. Check canvas fit, page order, playback durations and looping.
- [ ] Confirm that an empty USB drive receives its first `nova/` folder and the T10 plays the new program built from scratch.
- [ ] For **Plug and play**, confirm that playback starts from the inserted drive. For **Copy and replace**, confirm that an initial import plays from internal storage according to the player's documented USB handling procedure, then use revision two to confirm it replaces the intended solution without leaving an unintended duplicate.
- [ ] Confirm that restoring the prior package yields the previous content. Mark support against the model, firmware, ViPlex build and USB filesystem that passed this matrix.
