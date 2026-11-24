using System.Text.Json.Nodes;

namespace ViPlex.Core;

public enum PlaybackMode { PlugAndPlay, CopyAndReplace }
public enum MediaKind { Image, Video }

public sealed record NovaPage(
    int Index,
    string Name,
    MediaKind Kind,
    int DurationMilliseconds,
    string MediaPath,
    string ThumbnailPath)
{
    public string MediaName { get; init; } = Name;
    public int RepeatCount { get; init; } = 1;
    public int? VideoVolume { get; init; }
    public PageSchedule? Schedule { get; init; }
    public bool CanEditSchedule { get; init; } = true;
}

public sealed record MediaReplacement(string SourcePath, string? ThumbnailPath = null);

public sealed class NovaPackage
{
    internal const string CommandFileName = "command.json";

    private NovaPackage(string novaPath)
    {
        NovaPath = novaPath;
        var files = new PackageFiles(novaPath);
        (Command, CommandFingerprint) = PackageJson.ReadCommand(files.RequireFile(CommandFileName));
        Program = PackageJson.Single(PackageJson.Array(Command, "programInfos"), "programInfos");
        ProgramName = PackageJson.RequiredText(Program, "name");
        ProgramFolder = "program/" + PackageFiles.SafeName(PackageJson.String(Program, "url"));

        bool play = PackageJson.Value<bool>(Program, "isPlay");
        bool import = PackageJson.Value<bool>(Program, "isImport");
        bool replace = PackageJson.Value<bool>(Program, "isOverride");
        if (!play || import != replace)
            throw new InvalidDataException("The selected command does not match a supported USB playback mode.");
        Mode = import ? PlaybackMode.CopyAndReplace : PlaybackMode.PlugAndPlay;

        Manifest = new PackageManifest(files, ProgramFolder);
        PackageJson.Match(Manifest.Json, "name", ProgramName);

        string playlistRelativePath = ProgramFolder + "/" + PackageManifest.PlaylistName;
        if (!PackageJson.String(Program, "identifier").Equals(files.Digest(playlistRelativePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected command identifier does not match the playlist MD5.");

        Playlist = PackageJson.Read(files.RequireFile(playlistRelativePath));
        if (PackageJson.Value<double>(Playlist, "width") != 256 || PackageJson.Value<double>(Playlist, "height") != 768)
            throw new InvalidDataException("The selected playlist must use the 256 × 768 canvas.");
        PackageJson.Match(Playlist, "pickPolicy", "ORDER");
        PackageJson.Match(Playlist, "name", ProgramName);
        if (!Guid.TryParse(PackageJson.String(Playlist, "uuid"), out Guid uuid) || uuid == Guid.Empty)
            throw new InvalidDataException("The selected playlist has an invalid UUID.");

        ValidateRelations(files);

        JsonArray scenes = PackageJson.Array(Playlist, "sceneItems");
        if (scenes.Count == 0)
            throw new InvalidDataException("The selected program has no pages.");

        PageBinding[] bindings = scenes.Select((scene, index) => ReadPage(scene, index)).ToArray();
        PageBindings = bindings;
        Pages = Array.AsReadOnly(bindings.Select(binding => binding.Page).ToArray());

        PackageJson.Match(Program, "thumbnailName", bindings[0].Thumbnail.Name);
        if (!Manifest.Resources.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(bindings.Select(binding => binding.Media.Name)) ||
            !Manifest.Thumbnails.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(bindings.Select(binding => binding.Thumbnail.Name)))
            throw new InvalidDataException("The selected manifest lists assets outside the supported single-media-per-page layout.");

        SizeBasis = CommandSize.Identify(PackageJson.Value<long>(Program, "size"), Manifest.Resources.Values, bindings.Length);
    }

    public string NovaPath { get; }
    public string ProgramName { get; }
    public PlaybackMode Mode { get; }
    public IReadOnlyList<NovaPage> Pages { get; }

    internal string ProgramFolder { get; }
    internal JsonObject Command { get; }
    internal string CommandFingerprint { get; }
    internal JsonObject Program { get; }
    internal JsonObject Playlist { get; }
    internal PackageManifest Manifest { get; }
    internal IReadOnlyList<PageBinding> PageBindings { get; }
    internal CommandSizeBasis SizeBasis { get; }

    public static NovaPackage Open(string rootOrNovaPath) => new(PackageFiles.FindNova(rootOrNovaPath));

    private PageBinding ReadPage(JsonNode? node, int index)
    {
        JsonObject scene = PackageJson.Object(node, "sceneItems");
        PackageJson.Match(scene, "type", "PAGE");
        RejectBackgroundMedia(scene);
        JsonObject page = PackageJson.Object(scene["page"], "page");
        if (PackageJson.Array(page, "widgets").Count != 0)
            throw new InvalidDataException("Every page must contain one media widget in one full-screen container.");

        JsonObject container = PackageJson.Single(PackageJson.Array(page, "widgetContainers"), "widgetContainers");
        RequireFullScreen(container);
        JsonObject contents = PackageJson.Object(container["contents"], "contents");
        if (PackageJson.Array(contents, "widgetContainer").Count != 0)
            throw new InvalidDataException("Nested widget containers are outside the supported playlist layout.");

        JsonObject widget = PackageJson.Single(PackageJson.Array(contents, "widgets"), "contents.widgets");
        RequireFullScreen(widget);
        RejectBackgroundMedia(widget);
        string type = PackageJson.String(widget, "type");
        PackageJson.Match(widget, "originalType", type);
        MediaKind kind = PackageManifest.ParseKind(type);
        int duration = PackageJson.Value<int>(scene, "duration");
        if (duration <= 0 || duration != PackageJson.Value<int>(widget, "duration"))
            throw new InvalidDataException("Page and widget durations must match and be positive.");

        ManifestAsset media = PackageManifest.FindAsset(Manifest.Resources, PackageJson.String(widget, "dataSource"));
        if (media.Kind != kind || media.Size != PackageJson.Value<long>(widget, "filesize"))
            throw new InvalidDataException("The widget media type or file size does not match the manifest.");

        ManifestAsset thumbnail = PackageManifest.FindAsset(Manifest.Thumbnails, PackageJson.String(scene, "thumbnail"));
        var schedule = PagePlaybackEdits.ReadSchedule(scene, out bool canEditSchedule);
        var entry = new NovaPage(index, PackageJson.RequiredText(page, "name"), kind, duration, media.Path, thumbnail.Path)
        {
            MediaName = PackageJson.RequiredText(widget, "name"),
            RepeatCount = PagePlaybackEdits.ReadRepeatCount(scene),
            VideoVolume = PagePlaybackEdits.ReadVideoVolume(widget, kind),
            Schedule = schedule,
            CanEditSchedule = canEditSchedule
        };
        return new PageBinding(entry, scene, widget, media, thumbnail);
    }

    private void ValidateRelations(PackageFiles files)
    {
        JsonObject relation = PackageJson.Read(files.RequireFile(ProgramFolder + "/" + PackageManifest.RelationName));
        JsonObject solution = PackageJson.Read(files.RequireFile(ProgramFolder + "/" + PackageManifest.SolutionName));
        JsonObject schedule = PackageJson.Read(files.RequireFile(ProgramFolder + "/" + PackageManifest.ScheduleName));
        PackageJson.Match(relation, "playSolutionSource", PackageManifest.SolutionName);

        JsonObject relationItem = PackageJson.Single(PackageJson.Array(relation, "relations"), "relations");
        JsonObject task = PackageJson.Single(PackageJson.Array(solution, "items"), "items");
        JsonObject constraintLink = PackageJson.Single(PackageJson.Array(relationItem, "scheduleConstraints"), "scheduleConstraints");
        JsonObject playlistLink = PackageJson.Single(PackageJson.Array(constraintLink, "playlists"), "playlists");
        PackageJson.Match(constraintLink, "constraintSource", PackageManifest.ScheduleName);
        PackageJson.Match(playlistLink, "playlistSource", PackageManifest.PlaylistName);

        if (PackageJson.Value<int>(relationItem, "taskId") != PackageJson.Value<int>(task, "id") ||
            PackageJson.Value<int>(constraintLink, "constraintId") != PackageJson.Value<int>(schedule, "id") ||
            PackageJson.Value<int>(playlistLink, "playlistId") != PackageJson.Value<int>(Playlist, "id"))
            throw new InvalidDataException("The selected program has inconsistent task, schedule or playlist references.");
    }

    private static void RequireFullScreen(JsonObject item)
    {
        JsonObject layout = PackageJson.Object(item["layout"], "layout");
        PackageJson.Match(layout, "x", "0%");
        PackageJson.Match(layout, "y", "0%");
        PackageJson.Match(layout, "width", "100%");
        PackageJson.Match(layout, "height", "100%");
        if (PackageJson.Value<double>(layout, "xNum") != 0 || PackageJson.Value<double>(layout, "yNum") != 0 ||
            PackageJson.Value<double>(layout, "widthNum") != 100 || PackageJson.Value<double>(layout, "heightNum") != 100)
            throw new InvalidDataException("The selected program contains a non-full-screen media layout.");
    }

    private static void RejectBackgroundMedia(JsonObject item)
    {
        foreach (string key in new[] { "backgroundDrawable", "backgroundMusic" })
        {
            if (item.ContainsKey(key) && PackageJson.String(item, key).Length != 0)
                throw new InvalidDataException("Background images or audio are outside the supported playlist layout.");
        }
    }
}

internal sealed record PageBinding(NovaPage Page, JsonObject Scene, JsonObject Widget, ManifestAsset Media, ManifestAsset Thumbnail);
