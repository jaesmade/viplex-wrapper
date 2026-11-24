using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ViPlex.Core;

namespace ViPlex.Windows;

internal sealed record PagePlaybackValues(string Name, int ImageDuration, int RepeatCount, int VideoVolume,
    PageSchedule Schedule, int TrimStart, int TrimEnd, int FadeIn, int FadeOut);

internal partial class PagePlaybackWindow : Window
{
    private readonly MediaKind kind;
    private readonly int sourceDuration;
    private readonly (CheckBox Box, PlaybackDays Day)[] weekdays;

    public PagePlaybackWindow(MediaKind kind, int sourceDuration, PagePlaybackValues initial, bool canEditSchedule = true)
    {
        this.kind = kind;
        this.sourceDuration = sourceDuration;
        InitializeComponent();
        weekdays =
        [
            (Monday, PlaybackDays.Monday), (Tuesday, PlaybackDays.Tuesday), (Wednesday, PlaybackDays.Wednesday),
            (Thursday, PlaybackDays.Thursday), (Friday, PlaybackDays.Friday), (Saturday, PlaybackDays.Saturday),
            (Sunday, PlaybackDays.Sunday)
        ];
        NameInput.Text = initial.Name;
        HoldInput.Text = Seconds(initial.ImageDuration);
        RepeatInput.Text = initial.RepeatCount.ToString(CultureInfo.CurrentCulture);
        VolumeInput.Text = initial.VideoVolume.ToString(CultureInfo.CurrentCulture);
        TrimStartInput.Text = Seconds(initial.TrimStart);
        TrimEndInput.Text = Seconds(initial.TrimEnd);
        FadeInInput.Text = Seconds(initial.FadeIn);
        FadeOutInput.Text = Seconds(initial.FadeOut);
        StartDateInput.SelectedDate = initial.Schedule.StartDate?.ToDateTime(TimeOnly.MinValue);
        EndDateInput.SelectedDate = initial.Schedule.EndDate?.ToDateTime(TimeOnly.MinValue);
        foreach (var (box, day) in weekdays)
        {
            box.IsChecked = initial.Schedule.Days.HasFlag(day);
        }

        ImageTiming.Visibility = kind == MediaKind.Image ? Visibility.Visible : Visibility.Collapsed;
        TrimOptions.Visibility = VideoAudio.Visibility = kind == MediaKind.Video ? Visibility.Visible : Visibility.Collapsed;
        ScheduleFields.Visibility = canEditSchedule ? Visibility.Visible : Visibility.Collapsed;
        ExistingSchedule.Visibility = canEditSchedule ? Visibility.Collapsed : Visibility.Visible;
        SourceDurationLabel.Text = $"Source length · {Seconds(sourceDuration)} s";
    }

    public PagePlaybackValues? Values { get; private set; }
    public bool ApplyToAllImages => kind == MediaKind.Image && ApplyAllImages.IsChecked == true;

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var name = NameInput.Text.Trim();
        if (name.Length == 0 || !int.TryParse(RepeatInput.Text, out var repeat) || repeat < 1)
        {
            Feedback.Text = "Enter a page name and a repeat count of at least 1.";
            return;
        }

        int hold = 0, trimStart = 0, trimEnd = sourceDuration, volume = 100;
        if (kind == MediaKind.Image && (!TryMilliseconds(HoldInput.Text, out hold) || hold == 0))
        {
            Feedback.Text = "Enter a hold time greater than zero.";
            return;
        }

        if (kind == MediaKind.Video && (!TryMilliseconds(TrimStartInput.Text, out trimStart) ||
            !TryMilliseconds(TrimEndInput.Text, out trimEnd) || trimEnd <= trimStart || trimEnd > sourceDuration ||
            !int.TryParse(VolumeInput.Text, out volume) || volume is < 0 or > 100))
        {
            Feedback.Text = "Set a trim range within the video and a volume from 0 to 100.";
            return;
        }

        var duration = kind == MediaKind.Image ? hold : trimEnd - trimStart;
        if (!TryMilliseconds(FadeInInput.Text, out var fadeIn) || !TryMilliseconds(FadeOutInput.Text, out var fadeOut) ||
            (long)fadeIn + fadeOut > duration)
        {
            Feedback.Text = "The fade times must fit inside the page’s duration.";
            return;
        }

        if (duration < 100 && (fadeIn > 0 || fadeOut > 0 || trimStart > 0 || trimEnd < sourceDuration))
        {
            Feedback.Text = "Use at least 0.1 seconds for a page with trimming or fades.";
            return;
        }

        if (!TryDate(StartDateInput, out var start) || !TryDate(EndDateInput, out var end) || start > end)
        {
            Feedback.Text = "Choose valid dates with the end date on or after the start date.";
            return;
        }

        var days = weekdays.Aggregate((PlaybackDays)0, (selected, day) =>
            day.Box.IsChecked == true ? selected | day.Day : selected);
        if (days == 0)
        {
            Feedback.Text = "Select at least one playback day.";
            return;
        }

        Values = new PagePlaybackValues(name, hold, repeat, volume, new PageSchedule(start, end, days),
            trimStart, trimEnd, fadeIn, fadeOut);
        DialogResult = true;
    }

    private static bool TryDate(DatePicker picker, out DateOnly? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(picker.Text))
        {
            return true;
        }

        if (!DateOnly.TryParse(picker.Text, CultureInfo.CurrentCulture, out var parsed))
        {
            return false;
        }

        date = parsed;
        return true;
    }

    private static bool TryMilliseconds(string input, out int milliseconds)
    {
        milliseconds = 0;
        if (!decimal.TryParse(input, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite |
            NumberStyles.AllowTrailingWhite, CultureInfo.CurrentCulture, out var seconds) ||
            seconds > int.MaxValue / 1000m || decimal.Truncate(seconds * 1000) != seconds * 1000)
        {
            return false;
        }

        milliseconds = (int)(seconds * 1000);
        return true;
    }

    private static string Seconds(int milliseconds) => (milliseconds / 1000m).ToString("0.###", CultureInfo.CurrentCulture);
}
