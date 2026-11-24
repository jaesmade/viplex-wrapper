using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using ViPlex.Core;

namespace ViPlex.Windows;

public partial class MainWindow
{
    private const int DefaultImageDuration = 10_000;
    private const long MaxDisplayDuration = long.MaxValue / TimeSpan.TicksPerMillisecond - 1;
    private readonly DispatcherTimer previewClock = new() { Interval = TimeSpan.FromMilliseconds(40) };

    private void RenameSet_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSet is not { } set)
        {
            return;
        }

        var panel = new StackPanel { Margin = new Thickness(18) };
        var input = new TextBox
        {
            Text = set.Name, MaxLength = 120, MinHeight = 32,
            Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 12)
        };
        System.Windows.Automation.AutomationProperties.SetName(input, "Set name");
        panel.Children.Add(input);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var rename = new Button { Content = "Rename", IsDefault = true, Style = (Style)FindResource("PrimaryButton") };
        actions.Children.Add(cancel);
        actions.Children.Add(rename);
        panel.Children.Add(actions);
        var dialog = new Window
        {
            Title = "Rename set", Owner = this, Width = 350, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false, Background = Background, FontFamily = FontFamily, FontSize = FontSize, Content = panel
        };
        dialog.Loaded += (_, _) =>
        {
            Keyboard.Focus(input);
            input.SelectAll();
        };
        input.TextChanged += (_, _) => rename.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
        rename.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() == true)
        {
            set.Rename(input.Text.Trim());
        }
    }

    private async void AddPages_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSet is not { } set)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = $"Add pages to {set.Name}", Filter = "Supported media|*.jpg;*.jpeg;*.png;*.mp4", Multiselect = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var work = Path.Combine(scratchRoot, Guid.NewGuid().ToString("N"));
        SetBusy(true);
        ShowActivity("Adding pages…", set.Name, inProgress: true);
        try
        {
            var assets = await Task.Run(() =>
            {
                var imported = new List<ImportedMedia>();
                foreach (var path in dialog.FileNames)
                {
                    try
                    {
                        imported.Add(CacheMedia(path, requiredKind: null, cacheRoot: work));
                    }
                    catch (Exception ex) when (IsPreviewFailure(ex))
                    {
                        throw new InvalidDataException($"{Path.GetFileName(path)}\n{ex.Message}", ex);
                    }
                }

                return imported;
            });
            var first = set.NextPageKey;
            foreach (var asset in assets)
            {
                var key = set.NextPageKey++;
                set.Pages.Add(new EditablePage(key, null, asset, Guid.NewGuid()));
                set.Pending.Add(key);
                mediaLibrary.Add(asset);
            }

            set.StructurePending = true;
            UpdateMediaStrip();
            RefreshPages(first);
            PageList.ScrollIntoView(PageList.SelectedItem);
            Activity.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (IsPreviewFailure(ex))
        {
            await Task.Run(() => DeleteScratch(work));
            ShowActivity("Could not add pages", ex.Message, error: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void DuplicatePage_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSet is not { } set || selectedPage is not { } row)
        {
            return;
        }

        var key = set.NextPageKey++;
        var draft = row.Draft;
        set.Pages.Insert(PageList.SelectedIndex + 1, new EditablePage(key, draft.SourceIndex, draft.AddedMedia, Guid.NewGuid())
        {
            Options = draft.Options with { Name = $"{row.Name} copy" }
        });
        if (set.Replacements.TryGetValue(row.Index, out var replacement))
        {
            set.Replacements[key] = replacement;
        }
        if (set.ImagePlacements.TryGetValue(row.Index, out var placement))
        {
            set.ImagePlacements[key] = placement;
        }

        set.StructurePending = true;
        set.Pending.Add(key);
        RefreshPages(key);
        PageList.ScrollIntoView(PageList.SelectedItem);
    }

    private void RemovePage_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSet is not { } set || selectedPage is not { } row || set.Pages.Count <= 1)
        {
            return;
        }

        var position = PageList.SelectedIndex;
        set.Pages.RemoveAt(position);
        set.Replacements.Remove(row.Index);
        set.ImagePlacements.Remove(row.Index);
        set.Pending.Remove(row.Index);
        set.StructurePending = true;
        RefreshPages(set.Pages[Math.Min(position, set.Pages.Count - 1)].Key);
    }

    private void MovePageUp_Click(object sender, RoutedEventArgs e) => MovePage(-1);
    private void MovePageDown_Click(object sender, RoutedEventArgs e) => MovePage(1);

    private void MovePage(int distance)
    {
        if (selectedSet is not { } set || selectedPage is not { } row)
        {
            return;
        }

        var position = PageList.SelectedIndex;
        var target = position + distance;
        if (target < 0 || target >= set.Pages.Count)
        {
            return;
        }

        (set.Pages[position], set.Pages[target]) = (set.Pages[target], set.Pages[position]);
        set.StructurePending = true;
        RefreshPages(row.Index);
        PageList.ScrollIntoView(PageList.SelectedItem);
    }

    private void UpdatePageActions()
    {
        var position = PageList.SelectedIndex;
        var count = selectedSet?.Pages.Count ?? 0;
        DuplicatePageButton.IsEnabled = position >= 0;
        RemovePageButton.IsEnabled = position >= 0 && count > 1;
        MovePageUpButton.IsEnabled = position > 0;
        MovePageDownButton.IsEnabled = position >= 0 && position < count - 1;
    }

    private void EditPage_Click(object sender, RoutedEventArgs e)
    {
        if (selectedSet is not { } set || selectedPage is not { } row)
        {
            return;
        }

        CloseVideo();
        var isImage = row.Page.Kind == MediaKind.Image;
        var options = row.Options;
        var initial = new PagePlaybackValues(row.Name, isImage ? row.Duration : 0, row.RepeatCount, row.VideoVolume,
            row.Schedule, options.TrimStart, options.TrimEnd ?? row.SourceDuration, options.FadeIn, options.FadeOut);
        var dialog = new PagePlaybackWindow(row.Page.Kind, row.SourceDuration, initial, row.Page.CanEditSchedule) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Values is not { } edited)
        {
            return;
        }

        var bulkImages = dialog.ApplyToAllImages ? set.Pages.Select(page => CreatePageRow(set, page))
            .Where(page => page.Page.Kind == MediaKind.Image && page.Index != row.Index && page.Duration != edited.ImageDuration)
            .ToArray() : [];
        var incompatible = bulkImages.FirstOrDefault(page => (page.Options.FadeIn > 0 || page.Options.FadeOut > 0) &&
            (edited.ImageDuration < 100 || (long)page.Options.FadeIn + page.Options.FadeOut > edited.ImageDuration));
        if (incompatible is not null)
        {
            SetReplaceFeedback($"The new hold time is too short for the fades on {incompatible.Name}. Adjust those fades or choose a longer hold.", error: true);
            return;
        }

        if (edited != initial)
        {
            row.Draft.Options = options with
            {
                Name = edited.Name != initial.Name ? edited.Name : options.Name,
                ImageDuration = isImage && edited.ImageDuration != initial.ImageDuration ? edited.ImageDuration : options.ImageDuration,
                RepeatCount = edited.RepeatCount != initial.RepeatCount ? edited.RepeatCount : options.RepeatCount,
                VideoVolume = edited.VideoVolume != initial.VideoVolume ? edited.VideoVolume : options.VideoVolume,
                Schedule = edited.Schedule != initial.Schedule ? edited.Schedule : options.Schedule,
                TrimStart = edited.TrimStart,
                TrimEnd = edited.TrimEnd == row.SourceDuration ? null : edited.TrimEnd,
                FadeIn = edited.FadeIn,
                FadeOut = edited.FadeOut
            };
            set.Pending.Add(row.Index);
        }

        foreach (var image in bulkImages)
        {
            image.Draft.Options = image.Options with { ImageDuration = edited.ImageDuration };
            set.Pending.Add(image.Index);
        }

        RefreshPages(row.Index);
    }

    private static PageRow CreatePageRow(PresetSet set, EditablePage draft)
    {
        var package = set.Snapshot.Package;
        var initial = draft.AddedMedia;
        var page = draft.SourceIndex is { } sourceIndex ? package.Pages[sourceIndex]
            : new NovaPage(draft.Key, $"Page{draft.Key + 1}", initial!.Details.Kind,
                initial.Details.DurationMilliseconds ?? DefaultImageDuration, initial.Replacement.SourcePath,
                initial.Replacement.ThumbnailPath!) { MediaName = initial.Name };
        var imported = set.Replacements.GetValueOrDefault(draft.Key) ?? initial;
        return new PageRow(draft, page, imported, imported?.Thumbnail ?? set.Snapshot.Previews.GetValueOrDefault(page.Index),
            set.IsPagePending(draft.Key));
    }

    private static string PlaybackSummary(PageRow row)
    {
        var details = new List<string>();
        if (row.RepeatCount > 1)
        {
            details.Add($"Repeats {row.RepeatCount} times");
        }
        if (row.Schedule != new PageSchedule())
        {
            details.Add("Scheduled");
        }
        else if (!row.Page.CanEditSchedule)
        {
            details.Add("Existing schedule");
        }
        if (row.Page.Kind == MediaKind.Video && row.VideoVolume != 100)
        {
            details.Add($"Volume {row.VideoVolume}%");
        }
        if (row.Options.TrimStart > 0 || row.Options.TrimEnd.HasValue)
        {
            details.Add("Trimmed");
        }
        if (row.Options.FadeIn > 0 || row.Options.FadeOut > 0)
        {
            details.Add("Fade");
        }

        return string.Join("  ·  ", details);
    }

    private void PreviewClock_Tick(object? sender, EventArgs e)
    {
        if (selectedPage is not { } page || !playing || VideoPreview.Source is null || Poster.Visibility == Visibility.Visible)
        {
            return;
        }

        var elapsed = VideoPreview.Position.TotalMilliseconds - page.Options.TrimStart;
        if (elapsed >= page.Duration)
        {
            CloseVideo();
        }
        else
        {
            var fadeIn = page.Options.FadeIn == 0 ? 1 : Math.Clamp(elapsed / page.Options.FadeIn, 0, 1);
            var fadeOut = page.Options.FadeOut == 0 ? 1 : Math.Clamp((page.Duration - elapsed) / page.Options.FadeOut, 0, 1);
            VideoPreview.Opacity = Math.Min(fadeIn, fadeOut);
            VideoPreview.Volume = page.VideoVolume / 100d * VideoPreview.Opacity;
        }
    }

    private static async Task<PackagePageEdit[]> CreateExportPagesAsync(string work, IReadOnlyList<PageRow> pages,
        IReadOnlyDictionary<int, Rect> placements)
    {
        var export = new PackagePageEdit[pages.Count];
        for (var index = 0; index < pages.Count; index++)
        {
            var row = pages[index];
            var options = row.Options;
            var media = row.Imported?.Replacement;
            var directory = Path.Combine(work, "page-edits", row.Index.ToString(CultureInfo.InvariantCulture));
            if (placements.TryGetValue(row.Index, out var placement))
            {
                var photo = LoadPreview(row.MediaPath, width: 0, height: 0);
                media = ImageComposition.Save(photo, placement, directory, row.FileName);
            }

            var imageDuration = options.ImageDuration;
            if (options.TrimStart > 0 || options.TrimEnd.HasValue || options.FadeIn > 0 || options.FadeOut > 0)
            {
                Directory.CreateDirectory(directory);
                var output = Path.Combine(directory, ImageComposition.GetSafeMediaFileName(row.FileName, ".mp4"));
                var isImage = row.Page.Kind == MediaKind.Image;
                await VideoEditing.CreateAsync(media?.SourcePath ?? row.MediaPath, output, row.Page.Kind,
                    isImage ? row.Duration : row.SourceDuration, options.TrimStart, options.TrimEnd ?? (isImage ? row.Duration : row.SourceDuration),
                    options.FadeIn, options.FadeOut);
                media = new MediaReplacement(output, media?.ThumbnailPath ?? row.Page.ThumbnailPath);
                imageDuration = null;
            }

            export[index] = new PackagePageEdit(row.Draft.SourceIndex, media, options.Name, imageDuration,
                options.RepeatCount, options.VideoVolume, options.Schedule, row.Draft.Identity);
        }

        return export;
    }

    private sealed class EditablePage(int key, int? sourceIndex, ImportedMedia? addedMedia, Guid? identity = null)
    {
        public int Key { get; } = key;
        public int? SourceIndex { get; } = sourceIndex;
        public ImportedMedia? AddedMedia { get; } = addedMedia;
        public Guid? Identity { get; } = identity;
        public PageOptions Options { get; set; } = new();
    }

    private sealed record PageOptions(string? Name = null, int? ImageDuration = null, int? RepeatCount = null,
        int? VideoVolume = null, PageSchedule? Schedule = null, int TrimStart = 0, int? TrimEnd = null,
        int FadeIn = 0, int FadeOut = 0);
}
