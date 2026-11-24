# NovaStar T10 USB Editor

A local Windows editor for the NovaStar T10 installation with a 256 × 768 portrait display. Start with a blank USB or open an existing ViPlex package. Use the editor to replace image or video pages, prepare a `nova/` folder, and write or restore a USB package. The [implementation plan](docs/superpowers/plans/2026-11-24-viplex-usb-wrapper.md) records how the supplied ViPlex exports established the package format.

## Build and run

Use Windows with the .NET 10 SDK.

```powershell
dotnet build ViPlexWrapper.slnx
dotnet run --project src/ViPlex.Windows
```

## Create or edit a package

1. Open a USB drive or folder. Its current program opens as **Set 1** with its pages in playback order. If the folder is blank, enter a program name, the T10 connection credentials and the media for Set 1.
2. Choose **New set…** to add a set with its own ordered pages and the same player settings. Switch sets from the **Preset sets** menu. Each set keeps its page edits during the editing session.
3. Select a page and replace its media. Image pages accept JPEG or PNG, and video pages accept supported H.264 MP4. Image hold times stay the same, and video durations come from the replacements. Choose **Edit image** to move or resize a still image in the display preview, or **Reset image** to restore its fitted placement.
4. Choose **Prepare set** to review the selected set and its pages. **Plug and play** plays from the connected USB. **Copy and replace** applies the ViPlex import and overwrite flags.
5. Choose **Export set** to get a copyable `nova/` folder, or **Write to USB** to stage and verify the same package on a drive Windows reports as removable. The export contains the selected set’s pages in order. If a destination package exists, the app confirms the target before replacing it and saves a local backup.

Use **Restore USB backup** to restore a prior `nova/` tree. Keep the saved `nova/` and `snapshot.json` together. The ViPlex command contains player connection credentials in its Base64-wrapped content. Keep exports and backups local.

## Code map

| Component | Responsibility |
| --- | --- |
| `src/ViPlex.Core` | Create and validate packages, inspect media, update manifests and hashes, stage exports, publish and restore |
| `src/ViPlex.Windows` | WPF playlist, preview, media controls, and USB review |
| `tests/ViPlex.Core.Tests` | Dependency-free verification against supplied ViPlex exports and temporary destinations |

## Verification

Run the reference checks using the parent folder with `picture`, `videoplayback`, `multipic`, `multivid`, and `plugnply`.

```powershell
dotnet run --project tests/ViPlex.Core.Tests -- 'D:\Projects\references\viplex'
```

The Release build compiled with zero warnings. All 18 reference checks passed, including creating the first `nova/` folder, updating existing programs, checking the drive identity and restoring a backup. The runner uses temporary directories for all writes. Headless WPF checks covered playlist selection, replacing a page, blank-folder detection, and creating a mixed-media program.

Hardware validation remains open for the first package made from scratch, the T10 firmware and USB filesystem, Copy and replace across two revisions, and the command size rule when multiple pages reuse one media file. Test an untouched official export first, then use a spare USB to test the wrapper's output.

## Change log

### 2026-11-24 · Initial implementation

- Set up a .NET 10 solution with a WPF editor and a separate package library.
- Added package creation when a USB drive or selected folder is empty. The first write creates `nova/` and preserves other root files.
- Added ordered page loading, package validation, same-type media replacement, and image and video inspection. Changed packages update playlist metadata, manifest hashes, and the command fields.
- Added Plug and play and Copy and replace packaging, export-to-folder, staged drive writes with Windows volume verification, backups, and restore.
- Added the three-pane T10 editor with a portrait preview, media strip, page properties, and USB review.
- Added reference-backed checks for the five fixtures, replacement behavior, overwrite handling, backups, and restoration.
