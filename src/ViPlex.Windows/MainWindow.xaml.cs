using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using ViPlex.Core;

namespace ViPlex.Windows;

public partial class MainWindow : Window
{
    private static readonly string BackupRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ViPlex T10", "Backups");

    private readonly string scratchRoot = Path.Combine(Path.GetTempPath(), $"ViPlex-T10-{Guid.NewGuid():N}");
    private readonly ObservableCollection<ImportedMedia> mediaLibrary = [];
    private readonly ObservableCollection<PresetSet> sets = [];

    private PresetSet? selectedSet;
    private PageRow? selectedPage;
    private int nextSetNumber = 1;
    private bool busy;
    private bool playing;

    public MainWindow()
    {
        InitializeComponent();
        MediaStrip.ItemsSource = mediaLibrary;
        SetSelector.ItemsSource = sets;
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select a USB drive or folder" };
        if (dialog.ShowDialog(this) != true || !ConfirmDiscard())
        {
            return;
        }

        var folder = dialog.FolderName;
        SetBusy(true);
        ShowActivity("Opening export…", folder, inProgress: true);
        CloseVideo();
        try
        {
            var blank = await Task.Run(() => FindBlankDestination(folder));
            if (blank is null)
            {
                LoadWorkspace(await Task.Run(() => ReadSnapshot(folder)));
                Activity.Visibility = Visibility.Collapsed;
            }
            else
            {
                Activity.Visibility = Visibility.Collapsed;
                CreateProgram(blank);
            }
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            ShowActivity("Could not open export", $"{folder}\n{ex.Message}", error: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool HasPendingEdits => sets.Any(set => set.HasPendingEdits);

    private bool ConfirmDiscard() => !HasPendingEdits || MessageBox.Show(this,
        "Discard the unpublished changes in your preset sets?", "Pending edits", MessageBoxButton.YesNo, MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;

    private static SuggestedDestination? FindBlankDestination(string folder)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!Directory.Exists(path) || File.Exists(Path.Combine(path, "command.json")))
        {
            return null;
        }

        var insideNova = Path.GetFileName(path).Equals("nova", StringComparison.OrdinalIgnoreCase);
        var root = insideNova ? Path.GetDirectoryName(path)! : path;
        var nova = insideNova ? path : Path.Combine(path, "nova");
        if (File.Exists(nova) || (Directory.Exists(nova) && Directory.EnumerateFileSystemEntries(nova).Any()))
        {
            return null;
        }

        var identity = PrepareWindow.IdentifyRemovableVolume(root);
        return new SuggestedDestination(root, identity, identity is not null && SamePath(root, Path.GetPathRoot(root)!));
    }

    private void CreateProgram(SuggestedDestination destination)
    {
        var dialog = new NewPackageWindow(async draft =>
        {
            var created = await Task.Run(() => StageNewProgram(draft));
            LoadWorkspace(created.Snapshot, destination, created.Media);
        }) { Owner = this };
        dialog.ShowDialog();
    }

    private void NewSet_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSet is not { } current)
        {
            return;
        }

        var dialog = new NewPackageWindow(async draft =>
        {
            var created = await Task.Run(() => StageNewProgram(draft, current));
            AddSet(created.Snapshot, created.Media, current.Destination);
        }, NextSetName) { Owner = this };
        dialog.ShowDialog();
    }

    private (PackageSnapshot Snapshot, List<ImportedMedia> Media) StageNewProgram(NewPackageDraft draft, PresetSet? playerSet = null)
    {
        var work = Path.Combine(scratchRoot, Guid.NewGuid().ToString("N"));
        var staged = Path.Combine(work, "nova");
        Directory.CreateDirectory(work);
        try
        {
            var media = new List<ImportedMedia>();
            foreach (var path in draft.MediaPaths)
            {
                try
                {
                    media.Add(CacheMedia(path, requiredKind: null, cacheRoot: work));
                }
                catch (Exception ex) when (IsPreviewFailure(ex))
                {
                    var reason = ex is InvalidDataException ? ex.Message : "Windows could not read this media file.";
                    throw new InvalidDataException($"{Path.GetFileName(path)}\n{reason}", ex);
                }
            }

            var pages = media.Select(asset => asset.Replacement).ToArray();
            if (playerSet is null)
            {
                PackageCreator.Create(staged, draft.ProgramName, PlaybackMode.PlugAndPlay,
                    draft.PlayerUsername, draft.PlayerPassword, pages);
            }
            else
            {
                PackageCreator.Create(staged, draft.ProgramName, playerSet.Mode, playerSet.Snapshot.Package, pages);
            }
            return (ReadSnapshot(staged), media);
        }
        catch
        {
            DeleteScratch(work);
            throw;
        }
    }

    private static PackageSnapshot ReadSnapshot(string path)
    {
        var package = NovaPackage.Open(path);
        var previews = package.Pages.ToDictionary(page => page.Index, page =>
            TryLoadPreview(page.Kind == MediaKind.Image ? page.MediaPath : page.ThumbnailPath)
            ?? (page.Kind == MediaKind.Image ? TryLoadPreview(page.ThumbnailPath) : null));
        return new PackageSnapshot(package, previews);
    }

    private static BitmapSource? TryLoadPreview(string path)
    {
        try
        {
            return LoadPreview(path, width: 256, height: 0);
        }
        catch (Exception ex) when (IsPreviewFailure(ex))
        {
            return null;
        }
    }

    private static BitmapSource LoadPreview(string path, int width, int height)
    {
        using var input = File.OpenRead(path);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = width;
        image.DecodePixelHeight = height;
        image.StreamSource = input;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private string NextSetName => $"Set {nextSetNumber}";

    private void LoadWorkspace(PackageSnapshot loaded, SuggestedDestination? destination = null,
        IReadOnlyList<ImportedMedia>? media = null)
    {
        sets.Clear();
        mediaLibrary.Clear();
        nextSetNumber = 1;
        AddSet(loaded, media, destination);
    }

    private void AddSet(PackageSnapshot snapshot, IReadOnlyList<ImportedMedia>? media = null,
        SuggestedDestination? destination = null)
    {
        var set = new PresetSet(NextSetName, snapshot, destination, isNew: media is not null);
        nextSetNumber++;
        if (media is not null)
        {
            set.Pending.UnionWith(snapshot.Package.Pages.Select(page => page.Index));
            foreach (var asset in media)
            {
                mediaLibrary.Add(asset);
            }
        }

        sets.Add(set);
        SetSelector.SelectedItem = set;
    }

    private void SetSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        SetImageEditing(false);
        selectedSet = SetSelector.SelectedItem as PresetSet;
        RefreshSelectedSet();
    }

    private void RefreshSelectedSet()
    {
        ProgramName.Text = selectedSet?.Snapshot.Package.ProgramName ?? "No program loaded";
        ProgramName.ToolTip = selectedSet?.Snapshot.Package.ProgramName;
        SourceStatus.Text = selectedSet switch
        {
            null => "No export open",
            { IsNew: true } => "New program  ·  Prepared locally",
            { SourceCopied: true } set => $"Source copy  ·  {set.OriginalSourcePath}",
            { } set => $"Source  ·  {set.OriginalSourcePath}"
        };
        SourceStatus.ToolTip = selectedSet is { IsNew: false } source ? source.OriginalSourcePath : null;
        UpdateMediaStrip();
        RefreshPages(selectedSet?.SelectedPageIndex ?? 0);
        UpdateActions();
    }

    private void RefreshPages(int selectedIndex)
    {
        if (selectedSet is not { Snapshot: var snapshot } set)
        {
            PageList.ItemsSource = null;
            ProgramSummary.Text = "Open a folder to load or create a program.";
            EditStatus.Text = "";
            return;
        }

        var keepEditingImage = editingImage && selectedPage?.Index == selectedIndex;
        var rows = snapshot.Package.Pages.Select(page =>
        {
            set.Replacements.TryGetValue(page.Index, out var imported);
            return new PageRow(page, imported, imported is null ? snapshot.Previews[page.Index] : imported.Thumbnail,
                set.IsPagePending(page.Index));
        }).ToList();
        PageList.ItemsSource = rows;
        PageList.SelectedItem = rows.Find(row => row.Index == selectedIndex) ?? rows.FirstOrDefault();
        SetImageEditing(keepEditingImage);
        ProgramSummary.Text = $"{rows.Count} {(rows.Count == 1 ? "page" : "pages")}  ·  {FormatDuration(rows.Sum(row => (long)row.Duration))}";
        var pendingPages = rows.Count(row => row.Pending);
        EditStatus.Text = pendingPages == 0 ? "No pending edits" : $"{pendingPages} {(pendingPages == 1 ? "page" : "pages")} pending";
    }

    private void PageSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        CloseVideo();
        selectedPage = PageList.SelectedItem as PageRow;
        var hasPage = selectedPage is not null;
        ScreenView.Visibility = PropertyScroll.Visibility = hasPage ? Visibility.Visible : Visibility.Collapsed;
        EmptyPreview.Visibility = EmptyProperties.Visibility = hasPage ? Visibility.Collapsed : Visibility.Visible;
        PlayButton.Visibility = selectedPage?.Page.Kind == MediaKind.Video ? Visibility.Visible : Visibility.Collapsed;
        Poster.Source = selectedPage?.Page.Kind == MediaKind.Video ? selectedPage.Thumbnail : null;
        SelectPreviewImage();
        if (selectedPage is not { } page)
        {
            return;
        }

        if (selectedSet is { } set)
        {
            set.SelectedPageIndex = page.Index;
        }

        SelectedName.Text = page.Name;
        SelectedPosition.Text = $"Page {PageList.SelectedIndex + 1} of {PageList.Items.Count}";
        SelectedFile.Text = page.FileName;
        SelectedKind.Text = page.Page.Kind.ToString();
        SelectedDuration.Text = FormatDuration(page.Duration);
        SetReplaceFeedback(page.Pending ? "This page has not been published." : "");
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (selectedPage?.Page.Kind != MediaKind.Video)
        {
            return;
        }

        if (playing)
        {
            VideoPreview.Pause();
            SetPlaying(false);
            return;
        }

        SetReplaceFeedback(selectedPage.Pending ? "This page has not been published." : "");
        if (VideoPreview.Source is null)
        {
            VideoPreview.Source = new Uri(selectedPage.MediaPath, UriKind.Absolute);
        }

        VideoPreview.Play();
        SetPlaying(true);
    }

    private void PreviewMedia_Opened(object sender, RoutedEventArgs e)
    {
        if (VideoPreview.Source is not null)
        {
            Poster.Visibility = Visibility.Collapsed;
        }
    }

    private void PreviewMedia_Ended(object sender, RoutedEventArgs e) => CloseVideo();

    private void PreviewMedia_Failed(object sender, ExceptionRoutedEventArgs e)
    {
        if (VideoPreview.Source is null)
        {
            return;
        }

        CloseVideo();
        SetReplaceFeedback("Windows could not play this video in the preview.", error: true);
    }

    private void SetPlaying(bool active)
    {
        playing = active;
        PlayButton.Content = active ? "Pause video" : "Play video";
    }

    private void CloseVideo()
    {
        if (VideoPreview.Source is not null)
        {
            VideoPreview.Close();
            VideoPreview.Source = null;
        }

        Poster.Visibility = Visibility.Visible;
        SetPlaying(false);
    }

    private async void Replace_Click(object sender, RoutedEventArgs e)
    {
        if (selectedPage is { } page)
        {
            await ImportMediaAsync(page);
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e) => await ImportMediaAsync(page: null);

    private async Task ImportMediaAsync(PageRow? page)
    {
        var dialog = new OpenFileDialog
        {
            Title = page is null ? "Import media" : $"Choose a replacement for {page.Name}",
            Filter = page?.Page.Kind switch
            {
                MediaKind.Image => "JPEG and PNG images|*.jpg;*.jpeg;*.png",
                MediaKind.Video => "H.264 MP4 video|*.mp4",
                _ => "Supported media|*.jpg;*.jpeg;*.png;*.mp4"
            }
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var sourcePath = dialog.FileName;
        SetBusy(true);
        ShowActivity("Importing media…", dialog.SafeFileName, inProgress: true);
        try
        {
            var imported = await Task.Run(() => CacheMedia(sourcePath, page?.Page.Kind));
            mediaLibrary.Add(imported);
            UpdateMediaStrip();
            if (page is not null)
            {
                ApplyReplacement(page, imported);
            }

            Activity.Visibility = Visibility.Collapsed;
            MediaScroll.ScrollToRightEnd();
        }
        catch (Exception ex) when (IsPreviewFailure(ex))
        {
            var message = ex is InvalidDataException ? ex.Message : "Windows could not read this media file.";
            if (page is null)
            {
                ShowActivity("Could not import media", message, error: true);
            }
            else
            {
                Activity.Visibility = Visibility.Collapsed;
                SetReplaceFeedback(message, error: true);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private ImportedMedia CacheMedia(string fileName, MediaKind? requiredKind, string? cacheRoot = null, string? imageName = null)
    {
        var folder = Path.Combine(cacheRoot ?? scratchRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var name = imageName ?? Path.GetFileName(fileName);
            var cacheName = imageName is null ? name
                : ImageComposition.GetImageFileName(imageName, Path.GetExtension(fileName));
            var copy = Path.Combine(folder, cacheName);
            File.Copy(fileName, copy, overwrite: false);
            var details = MediaInspector.Inspect(copy);
            if (requiredKind is not null && details.Kind != requiredKind)
            {
                throw new InvalidDataException(requiredKind == MediaKind.Image
                    ? "Choose a replacement image for this page." : "Choose a replacement video for this page.");
            }

            // Limit one dimension while retaining the image's aspect ratio.
            var portrait = details.Width is { } width && details.Height is { } height && (long)width * 3 < height;
            var image = details.Kind == MediaKind.Video
                ? CreateVideoPoster(name, details.DurationMilliseconds.GetValueOrDefault())
                : LoadPreview(copy, portrait ? 0 : 256, portrait ? 768 : 0);
            var thumbnail = Path.Combine(folder, $"{Guid.NewGuid():N}.jpg");
            using var output = File.Create(thumbnail);
            var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(output);
            return new ImportedMedia(name, details, new MediaReplacement(copy, thumbnail), image);
        }
        catch
        {
            DeleteScratch(folder);
            throw;
        }
    }

    private static BitmapSource CreateVideoPoster(string name, int duration)
    {
        var visual = new DrawingVisual();
        var background = new SolidColorBrush(Color.FromRgb(21, 31, 35));
        var tile = new SolidColorBrush(Color.FromRgb(34, 60, 58));
        var accent = new SolidColorBrush(Color.FromRgb(105, 226, 205));
        var muted = new SolidColorBrush(Color.FromRgb(168, 184, 188));
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(background, null, new Rect(0, 0, 256, 768));
            drawing.DrawRoundedRectangle(tile, null, new Rect(93, 246, 70, 70), 16, 16);
            var play = new StreamGeometry();
            using (var shape = play.Open())
            {
                shape.BeginFigure(new Point(121, 267), isFilled: true, isClosed: true);
                shape.LineTo(new Point(143, 281), isStroked: false, isSmoothJoin: false);
                shape.LineTo(new Point(121, 295), isStroked: false, isSmoothJoin: false);
            }
            drawing.DrawGeometry(accent, null, play);
            DrawPosterText(drawing, "Video", 338, 18, accent, maxLines: 1);
            var fileNameHeight = DrawPosterText(drawing, name, 372, 20, Brushes.White, maxLines: 3);
            DrawPosterText(drawing, FormatDuration(duration), 385 + fileNameHeight, 16, muted, maxLines: 1);
        }

        var image = new RenderTargetBitmap(256, 768, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        image.Freeze();
        return image;
    }

    private static double DrawPosterText(DrawingContext drawing, string text, double y, double size, Brush brush, int maxLines)
    {
        var label = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, pixelsPerDip: 1)
        {
            MaxTextWidth = 216,
            MaxLineCount = maxLines,
            TextAlignment = TextAlignment.Center,
            Trimming = TextTrimming.CharacterEllipsis
        };
        drawing.DrawText(label, new Point(20, y));
        return label.Height;
    }

    private void ApplyMedia_Click(object sender, RoutedEventArgs e)
    {
        if (selectedPage is { } page && sender is Button { DataContext: ImportedMedia media })
        {
            ApplyReplacement(page, media);
        }
    }

    private void ApplyReplacement(PageRow page, ImportedMedia media)
    {
        if (selectedSet is not { } set)
        {
            return;
        }

        if (media.Details.Kind != page.Page.Kind)
        {
            SetReplaceFeedback(page.Page.Kind == MediaKind.Image
                ? "Select an imported image for this page." : "Select an imported video for this page.", error: true);
            return;
        }

        if (set.Replacements.TryGetValue(page.Index, out var current) && ReferenceEquals(current, media))
        {
            return;
        }

        set.Replacements[page.Index] = media;
        set.Pending.Add(page.Index);
        RefreshPages(page.Index);
    }

    private void UpdateMediaStrip()
    {
        EmptyStrip.Visibility = mediaLibrary.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MediaScroll.Visibility = mediaLibrary.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetReplaceFeedback(string text, bool error = false)
    {
        ReplaceFeedback.Text = text;
        ReplaceFeedback.Foreground = (Brush)FindResource(error ? "ErrorBrush" : "MutedBrush");
    }

    private async void Prepare_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSet is not { Snapshot.Package: var source } set)
        {
            return;
        }

        var destination = set.Destination;
        var dialog = new PrepareWindow(set.Name, source.ProgramName, ProgramSummary.Text, set.Mode,
            destination?.Root, destination?.VolumeIdentity, destination?.WriteToUsb == true) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.DestinationRoot is not { } root)
        {
            return;
        }

        var target = Path.Combine(root, "nova");
        if (OverlapsLoadedSource(target))
        {
            ShowActivity("Choose a different export destination", "The destination nova folder overlaps a loaded set’s source package.", error: true);
            return;
        }

        var overwrite = Directory.Exists(target);
        if (overwrite && !ConfirmOverwrite(target, restoring: false))
        {
            return;
        }

        await PublishSetAsync(set, root, dialog.SelectedMode, overwrite, dialog.DestinationVolumeIdentity, dialog.WriteToUsb);
    }

    private async Task PublishSetAsync(PresetSet set, string root, PlaybackMode mode, bool overwrite,
        string? volumeIdentity, bool writeToUsb)
    {
        var source = set.Snapshot.Package;
        var target = Path.Combine(root, "nova");
        var work = Path.Combine(scratchRoot, Guid.NewGuid().ToString("N"));
        var staged = Path.Combine(work, "nova");
        var changes = set.Replacements.ToDictionary(pair => pair.Key, pair => pair.Value.Replacement);
        var placements = new Dictionary<int, Rect>(set.ImagePlacements);

        SetBusy(true);
        CloseVideo();
        try
        {
            ShowActivity($"Preparing {set.Name}…", target, inProgress: true);
            await Task.Run(() =>
            {
                Directory.CreateDirectory(work);
                var export = CreateExportReplacements(source, work, changes, placements);
                PackageBuilder.Build(source, staged, mode, export);
            });
            ShowActivity(writeToUsb ? $"Writing {set.Name} to USB…" : $"Exporting {set.Name}…", target, inProgress: true);
            var result = await WritePreservingSourceAsync(target, setToReload: null,
                () => PackagePublisher.Publish(staged, root, overwrite, BackupRoot, volumeIdentity));
            set.Mode = mode;
            set.Pending.Clear();
            set.PublishedImagePlacements = placements;
            RefreshSelectedSet();
            ShowCompletion(writeToUsb ? $"{set.Name} written to USB" : $"{set.Name} exported", result, note: null);
        }
        catch (Exception ex) when (IsPreviewFailure(ex))
        {
            ShowActivity($"Could not publish {set.Name}", $"{target}\n{ex.Message}", error: true);
        }
        finally
        {
            await Task.Run(() => DeleteScratch(work));
            SetBusy(false);
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var folder = new OpenFolderDialog { Title = "Choose a previous USB backup" };
        if (Directory.Exists(BackupRoot))
        {
            folder.InitialDirectory = BackupRoot;
        }
        if (folder.ShowDialog(this) != true)
        {
            return;
        }

        var drive = new DriveWindow { Owner = this };
        if (drive.ShowDialog() != true || drive.SelectedRoot is not { } root || drive.SelectedIdentity is not { } identity)
        {
            return;
        }

        var target = Path.Combine(root, "nova");
        if (OverlapsLoadedSource(target))
        {
            ShowActivity("Choose a different USB drive", "The destination nova folder overlaps a loaded set’s source package.", error: true);
            return;
        }

        if (!ConfirmOverwrite(target, restoring: true))
        {
            return;
        }

        SetBusy(true);
        CloseVideo();
        ShowActivity("Restoring USB backup…", target, inProgress: true);
        try
        {
            var backupPath = folder.FolderName;
            var result = await WritePreservingSourceAsync(target, selectedSet,
                () => PackagePublisher.Restore(backupPath, root, BackupRoot, identity));
            var note = await ReloadRestoredSourceAsync(result.NovaPath);
            ShowCompletion("USB backup restored", result, note);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            ShowActivity("Could not restore USB backup", $"{target}\n{ex.Message}", error: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool ConfirmOverwrite(string target, bool restoring)
    {
        var description = restoring ? "Restore the selected backup to" : "Replace the existing nova folder at";
        var warning = restoring && selectedSet is { HasPendingEdits: true } set && SamePath(set.Snapshot.Package.NovaPath, target)
            ? $"\n\nRestoring this drive will discard the pending edits in {set.Name}." : "";
        return MessageBox.Show(this, $"{description}\n\n{target}\n\nAny existing nova folder will be backed up locally.{warning}",
            restoring ? "Confirm USB restore" : "Confirm replacement", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private async Task<PublishResult> WritePreservingSourceAsync(string target, PresetSet? setToReload, Func<PublishResult> write)
    {
        var set = sets.FirstOrDefault(candidate => candidate != setToReload && SamePath(candidate.Snapshot.Package.NovaPath, target));
        if (set is null)
        {
            return await Task.Run(write);
        }

        var original = set.Snapshot;
        var work = Path.Combine(scratchRoot, Guid.NewGuid().ToString("N"));
        var staged = Path.Combine(work, "nova");
        PackageSnapshot? copy = null;
        try
        {
            // Keep the original media available for further editing after its source is overwritten.
            copy = await Task.Run(() =>
            {
                Directory.CreateDirectory(work);
                PackageBuilder.Build(original.Package, staged, original.Package.Mode, new Dictionary<int, MediaReplacement>());
                return original with { Package = NovaPackage.Open(staged) };
            });
            return await Task.Run(write);
        }
        catch
        {
            // A failed write normally leaves the source intact. Keep the copy if the original is unavailable.
            if (copy is not null && await Task.Run(() => SourceCommandMatches(target, staged)))
            {
                copy = null;
            }

            throw;
        }
        finally
        {
            if (copy is null)
            {
                await Task.Run(() => DeleteScratch(work));
            }
            else
            {
                set.Snapshot = copy;
                set.SourceCopied = true;
                if (set == selectedSet)
                {
                    RefreshSelectedSet();
                }
            }
        }
    }

    private static bool SourceCommandMatches(string source, string copy)
    {
        try
        {
            return File.ReadAllBytes(Path.Combine(source, "command.json")).AsSpan()
                .SequenceEqual(File.ReadAllBytes(Path.Combine(copy, "command.json")));
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return false;
        }
    }

    private async Task<string?> ReloadRestoredSourceAsync(string output)
    {
        if (selectedSet is not { } set || !SamePath(set.Snapshot.Package.NovaPath, output))
        {
            return null;
        }

        try
        {
            set.Snapshot = await Task.Run(() => ReadSnapshot(output));
            set.Mode = set.Snapshot.Package.Mode;
            set.Replacements.Clear();
            set.Pending.Clear();
            set.ImagePlacements.Clear();
            set.PublishedImagePlacements.Clear();
            SetImageEditing(false);
            RefreshSelectedSet();
            return null;
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            sets.Remove(set);
            SetSelector.SelectedItem = sets.FirstOrDefault();
            return "Reopen the destination folder to continue editing it.";
        }
    }

    private bool OverlapsLoadedSource(string target) => sets.Any(set =>
    {
        var source = set.Snapshot.Package.NovaPath;
        return !SamePath(source, target) && (IsInside(source, target) || IsInside(target, source));
    });

    private static bool SamePath(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(string parent, string child)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var prefix = Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
        return Path.GetFullPath(child).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private void ShowCompletion(string title, PublishResult result, string? note)
    {
        var lines = new List<string> { result.NovaPath };
        if (result.BackupPath is { } backup)
        {
            lines.Add($"Previous package backed up to {backup}");
        }

        if (result.PreviousPath is { } previous)
        {
            lines.Add($"The drive also retains a previous copy at {previous}");
        }

        if (note is not null)
        {
            lines.Add(note);
        }

        ShowActivity(title, string.Join('\n', lines));
    }

    private void ShowActivity(string title, string detail, bool inProgress = false, bool error = false)
    {
        ActivityTitle.Text = title;
        ActivityTitle.Foreground = (Brush)FindResource(error ? "ErrorBrush" : "TextBrush");
        ActivityDetail.Text = detail;
        ActivityProgress.Visibility = inProgress ? Visibility.Visible : Visibility.Collapsed;
        DismissActivityButton.Visibility = inProgress ? Visibility.Collapsed : Visibility.Visible;
        Activity.Visibility = Visibility.Visible;
    }

    private void DismissActivity_Click(object sender, RoutedEventArgs e) => Activity.Visibility = Visibility.Collapsed;

    private void SetBusy(bool value)
    {
        busy = value;
        Editor.IsEnabled = OpenButton.IsEnabled = RestoreButton.IsEnabled = !value;
        UpdateActions();
    }

    private void UpdateActions()
    {
        SetSelector.IsEnabled = NewSetButton.IsEnabled = PrepareButton.IsEnabled = ImportButton.IsEnabled =
            !busy && selectedSet is not null;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel)
        {
            e.Cancel = busy || !ConfirmDiscard();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        CloseVideo();
        DeleteScratch(scratchRoot);
        base.OnClosed(e);
    }

    private static void DeleteScratch(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            // A media decoder may retain a handle briefly after playback closes.
        }
    }

    private static bool IsFileFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException;

    private static bool IsPreviewFailure(Exception ex) => IsFileFailure(ex) || ex is NotSupportedException or FormatException;

    private static string FormatDuration(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(milliseconds);
        if (time.TotalMinutes < 1)
        {
            return $"{time.TotalSeconds:0.###} s";
        }

        var clock = time.TotalHours < 1 ? $"{time:m\\:ss}" : $"{(long)time.TotalHours}:{time:mm\\:ss}";
        return time.Milliseconds == 0 ? clock : $"{clock}.{time.Milliseconds:000}".TrimEnd('0');
    }

    private sealed record PackageSnapshot(NovaPackage Package, IReadOnlyDictionary<int, BitmapSource?> Previews);

    private sealed record SuggestedDestination(string Root, string? VolumeIdentity, bool WriteToUsb);

    private sealed class PresetSet(string name, PackageSnapshot snapshot, SuggestedDestination? destination, bool isNew)
    {
        public string Name { get; } = name;
        public PackageSnapshot Snapshot { get; set; } = snapshot;
        public SuggestedDestination? Destination { get; } = destination;
        public PlaybackMode Mode { get; set; } = snapshot.Package.Mode;
        public int SelectedPageIndex { get; set; }
        public bool IsNew { get; } = isNew;
        public string OriginalSourcePath { get; } = snapshot.Package.NovaPath;
        public bool SourceCopied { get; set; }
        public Dictionary<int, ImportedMedia> Replacements { get; } = [];
        public HashSet<int> Pending { get; } = [];
        public Dictionary<int, Rect> ImagePlacements { get; } = [];
        public Dictionary<int, Rect> PublishedImagePlacements { get; set; } = [];

        public bool HasPendingEdits => Snapshot.Package.Pages.Any(page => IsPagePending(page.Index));

        public bool IsPagePending(int index) => Pending.Contains(index) ||
            ImagePlacements.GetValueOrDefault(index, Rect.Empty) != PublishedImagePlacements.GetValueOrDefault(index, Rect.Empty);
    }

    private sealed record ImportedMedia(string Name, MediaDetails Details, MediaReplacement Replacement, BitmapSource? Thumbnail)
    {
        public string ApplyLabel => $"Apply {Name} to the selected page";
    }

    private sealed record PageRow(NovaPage Page, ImportedMedia? Imported, BitmapSource? Thumbnail, bool Pending)
    {
        public int Index => Page.Index;
        public string Name => Page.Name;
        public string FileName => Imported?.Name ?? Page.MediaName;
        public string MediaPath => Imported?.Replacement.SourcePath ?? Page.MediaPath;
        public int Duration => Imported?.Details.DurationMilliseconds ?? Page.DurationMilliseconds;
        public string Summary => $"{Page.Kind}  ·  {FormatDuration(Duration)}";
    }
}
