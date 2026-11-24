# NovaStar T10 USB Editor

A local Windows editor for the NovaStar T10 installation with a 256 × 768 portrait display. Start with a blank USB or open an existing ViPlex package. Use the editor to build and edit preset sets, prepare a `nova/` folder, and write or restore a USB package. The [implementation plan](docs/superpowers/plans/2026-11-24-viplex-usb-wrapper.md) records how the supplied ViPlex exports established the package format.

## Build and run

Use Windows with the .NET 10 SDK.

```powershell
dotnet build ViPlexWrapper.slnx
dotnet run --project src/ViPlex.Windows
```

To export a set with video trims or fades, use a Windows FFmpeg build with `libx264`. The editor looks beside `ViPlex.Windows.exe`, in `%LOCALAPPDATA%\ViPlex T10\Tools`, and on `PATH`. [FFmpeg downloads](https://ffmpeg.org/download.html)

## Create or edit a package

1. Open a USB drive or folder. Its current program opens as **Set 1** with its pages in playback order. If the folder is blank, enter a program name, the T10 connection credentials and the media for Set 1.
2. Choose **New set…** to add another set with the same player settings. Use **Rename…** to label a preset in the editor. Its exported program name comes from the original program or its creation form.
3. Use **Add pages**, **Duplicate**, **Move up**, **Move down**, and **Remove** to change the selected set’s playlist. Image pages accept JPEG or PNG, and video pages accept supported H.264 MP4. Choose **Edit image** to move or resize a still image, or **Reset image** to restore its fitted placement.
4. Choose **Edit page…** to name a page and set its repeat count, date range, and playback weekdays. Set a hold time for an image, or trim a video and adjust its volume. You can apply a hold time to all images in the current set. The editor renders fades to or from black into H.264, including fades on still images.
5. Choose **Prepare set** to review the selected set and its pages. **Plug and play** plays from the connected USB. **Copy and replace** applies the ViPlex import and overwrite flags.
6. Choose **Export set** to get a copyable `nova/` folder, or **Write to USB** to write the same package to a removable drive. The app asks before replacing an existing destination package and saves a local backup.

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

The runner uses temporary directories for all writes. Reference checks cover package creation, page ordering and editing, playback metadata, export, overwrite, and backup restoration. See [MILESTONES.md](MILESTONES.md) for recorded build and desktop checks.

Hardware validation remains open for the first package made from scratch, the T10 firmware and USB filesystem, Copy and replace across two revisions, and the command size rule when multiple pages reuse one media file. Test an untouched official export first, then use a spare USB to test the wrapper's output.

## Change log

### 2026-11-24 · Initial implementation

- Set up a .NET 10 solution with a WPF editor and a separate package library.
- Added package creation when a USB drive or selected folder is empty. The first write creates `nova/` and preserves other root files.
- Added ordered page loading, package validation, same-type media replacement, and image and video inspection. Changed packages update playlist metadata, manifest hashes, and the command fields.
- Added Plug and play and Copy and replace packaging, export-to-folder, staged drive writes with Windows volume verification, backups, and restore.
- Added the three-pane T10 editor with a portrait preview, media strip, page properties, and USB review.
- Added reference-backed checks for the five fixtures, replacement behavior, overwrite handling, backups, and restoration.
