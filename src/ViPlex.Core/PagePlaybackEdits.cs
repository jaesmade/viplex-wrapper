using System.Globalization;
using System.Text.Json.Nodes;

namespace ViPlex.Core;

[Flags]
public enum PlaybackDays
{
    None = 0,
    Sunday = 1,
    Monday = 2,
    Tuesday = 4,
    Wednesday = 8,
    Thursday = 16,
    Friday = 32,
    Saturday = 64,
    EveryDay = Sunday | Monday | Tuesday | Wednesday | Thursday | Friday | Saturday
}

public sealed record PageSchedule(
    DateOnly? StartDate = null,
    DateOnly? EndDate = null,
    PlaybackDays Days = PlaybackDays.EveryDay);

internal static class PagePlaybackEdits
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string StartTime = "T00:00:00";
    private const string EndTime = "T23:59:59";
    private const string VendorOffset = "Z+08:00";
    private const string WeekdaysCronPrefix = "0 0 0 ? * ";
    private static readonly DateOnly DefaultStartDate = new(1970, 1, 1);
    private static readonly DateOnly DefaultEndDate = new(4012, 1, 1);
    private static readonly PageSchedule DefaultSchedule = new();

    internal static int ReadRepeatCount(JsonObject scene)
    {
        int count = ReadInteger(scene["repeatCount"]).GetValueOrDefault();
        return count > 0 ? count : 1;
    }

    internal static int? ReadVideoVolume(JsonObject widget, MediaKind kind)
    {
        if (kind != MediaKind.Video)
            return null;

        int? volume = ReadInteger((widget["metadata"] as JsonObject)?["volume"]);
        return volume is >= 0 and <= 100 ? volume : 100;
    }

    internal static PageSchedule? ReadSchedule(JsonObject scene, out bool canEdit)
    {
        canEdit = TryReadSchedule(scene["constraints"], out var schedule);
        return canEdit && schedule != DefaultSchedule ? schedule : null;
    }

    internal static bool Apply(JsonObject scene, JsonObject widget, MediaKind kind, PackagePageEdit edit)
    {
        bool changed = false;
        if (edit.RepeatCount is { } count)
        {
            if (count < 1)
                throw new ArgumentOutOfRangeException(nameof(edit), "Page play count must be at least 1.");
            if (ReadInteger(scene["repeatCount"]) != count)
            {
                scene["repeatCount"] = count;
                changed = true;
            }
        }

        if (edit.VideoVolume is { } volume)
            changed |= ApplyVolume(widget, kind, volume);
        if (edit.Schedule is { } schedule)
            changed |= ApplySchedule(scene, schedule);
        return changed;
    }

    private static bool ApplyVolume(JsonObject widget, MediaKind kind, int volume)
    {
        if (kind != MediaKind.Video || volume < 0 || volume > 100)
            throw new ArgumentOutOfRangeException(nameof(volume), "Volume must be between 0 and 100 on a video page.");

        var metadata = widget["metadata"] switch
        {
            JsonObject existing => existing,
            null => new JsonObject(),
            _ => throw new InvalidDataException("The video uses metadata that the editor cannot change.")
        };
        if (ReadInteger(metadata["volume"]) == volume)
            return false;

        metadata["volume"] = volume;
        if (metadata.Parent is null)
            widget["metadata"] = metadata;
        return true;
    }

    private static bool ApplySchedule(JsonObject scene, PageSchedule schedule)
    {
        var start = schedule.StartDate ?? DefaultStartDate;
        var end = schedule.EndDate ?? DefaultEndDate;
        if (start > end)
            throw new ArgumentException("The validity start date must be on or before the end date.", nameof(schedule));
        if (schedule.Days == PlaybackDays.None || (schedule.Days & ~PlaybackDays.EveryDay) != 0)
            throw new ArgumentException("Select at least one valid day of the week.", nameof(schedule));

        if (!TryReadSchedule(scene["constraints"], out var current))
            throw new InvalidDataException("The page uses a playback schedule that the editor cannot change.");
        if (current == schedule)
            return false;

        var constraint = (scene["constraints"] as JsonArray)?.FirstOrDefault() as JsonObject ?? new JsonObject();
        // The supplied exports use Quartz-style weekday numbers, with Sunday represented by 1.
        var days = Enumerable.Range(1, 7)
            .Where(day => (schedule.Days & (PlaybackDays)(1 << (day - 1))) != PlaybackDays.None);
        constraint["startTime"] = start.ToString(DateFormat, CultureInfo.InvariantCulture) + StartTime + VendorOffset;
        constraint["endTime"] = end.ToString(DateFormat, CultureInfo.InvariantCulture) + EndTime + VendorOffset;
        constraint["cron"] = new JsonArray(WeekdaysCronPrefix + string.Join(",", days));
        if (constraint.Parent is null)
            scene["constraints"] = new JsonArray(constraint);
        return true;
    }

    private static bool TryReadSchedule(JsonNode? constraints, out PageSchedule schedule)
    {
        schedule = DefaultSchedule;
        if (constraints is null or JsonArray { Count: 0 })
            return true;
        if (constraints is not JsonArray { Count: 1 } array || array[0] is not JsonObject item ||
            !TryReadDate(item["startTime"], StartTime, out var start) ||
            !TryReadDate(item["endTime"], EndTime, out var end) || start > end ||
            item["cron"] is not JsonArray { Count: 1 } cron || !TryReadDays(cron[0], out var days))
            return false;

        schedule = new PageSchedule(start == DefaultStartDate ? null : start, end == DefaultEndDate ? null : end, days);
        return true;
    }

    private static bool TryReadDate(JsonNode? node, string time, out DateOnly date)
    {
        date = default;
        var value = ReadText(node);
        if (value is null || value.Length <= DateFormat.Length)
            return false;
        var suffix = value[DateFormat.Length..];
        return (suffix == time + VendorOffset || suffix == time + "Z+8:00") &&
            DateOnly.TryParseExact(value.AsSpan(0, DateFormat.Length), DateFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out date);
    }

    private static bool TryReadDays(JsonNode? node, out PlaybackDays days)
    {
        days = PlaybackDays.None;
        var value = ReadText(node);
        if (value is null || !value.StartsWith(WeekdaysCronPrefix, StringComparison.Ordinal))
            return false;
        foreach (string part in value[WeekdaysCronPrefix.Length..].Split(','))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var day) || day < 1 || day > 7)
                return false;
            days |= (PlaybackDays)(1 << (day - 1));
        }
        return true;
    }

    private static int? ReadInteger(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out int number) ? number : null;

    private static string? ReadText(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;
}
