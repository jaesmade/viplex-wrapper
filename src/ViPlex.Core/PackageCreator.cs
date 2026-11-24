using System.Text.Json.Nodes;

namespace ViPlex.Core;

public static class PackageCreator
{
    private const int ImageDurationMilliseconds = 10_000;
    private const string AuthenticationKey = "authenticUserInfos";

    public static void Create(
        string stagedNovaPath,
        string programName,
        PlaybackMode mode,
        string playerUsername,
        string playerPassword,
        IReadOnlyList<MediaReplacement> pages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerUsername);
        ArgumentException.ThrowIfNullOrWhiteSpace(playerPassword);
        var playerCommand = new JsonObject
        {
            [AuthenticationKey] = new JsonArray(new JsonObject
            {
                ["username"] = playerUsername,
                ["password"] = playerPassword
            })
        };
        Create(stagedNovaPath, programName, mode, playerCommand, pages);
    }

    public static void Create(
        string stagedNovaPath,
        string programName,
        PlaybackMode mode,
        NovaPackage playerSource,
        IReadOnlyList<MediaReplacement> pages)
    {
        ArgumentNullException.ThrowIfNull(playerSource);
        Create(stagedNovaPath, programName, mode, playerSource.Command, pages);
    }

    private static void Create(
        string stagedNovaPath,
        string programName,
        PlaybackMode mode,
        JsonObject playerCommand,
        IReadOnlyList<MediaReplacement> pages)
    {
        PackageFiles.SafeName(programName);
        ArgumentNullException.ThrowIfNull(pages);
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (pages.Count == 0)
            throw new ArgumentException("Add at least one media page to create a program.", nameof(pages));

        string destination = PackageFiles.NormalizePath(stagedNovaPath);
        PackageFiles.Stage(destination, temporary =>
        {
            WritePackage(temporary, programName, mode, playerCommand, pages);
            _ = NovaPackage.Open(temporary);
        });
    }

    private static void WritePackage(
        string root,
        string programName,
        PlaybackMode mode,
        JsonObject playerCommand,
        IReadOnlyList<MediaReplacement> pages)
    {
        string programDirectory = Directory.CreateDirectory(Path.Combine(root, "program", programName)).FullName;
        string mediaDirectory = Directory.CreateDirectory(Path.Combine(root, "media")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "viplex_terminal", "media"));

        using Stream template = typeof(PackageCreator).Assembly.GetManifestResourceStream("ViPlex.Core.NewPackageDefaults.json")
            ?? throw new InvalidOperationException("The new-package defaults could not be loaded.");
        JsonObject defaults = PackageJson.Object(JsonNode.Parse(template), "new-package defaults");
        JsonObject playlist = PackageJson.Object(defaults["playlist"], "playlist");
        JsonObject solution = PackageJson.Object(defaults["playSolution"], "playSolution");
        playlist["name"] = programName;
        playlist["uuid"] = Guid.NewGuid().ToString();
        solution["uuid"] = Guid.NewGuid().ToString();

        JsonArray scenes = PackageJson.Array(playlist, "sceneItems");
        JsonObject defaultScene = PackageJson.Single(scenes, "sceneItems");
        scenes.Clear();
        var resources = new Dictionary<string, ManifestAsset>(StringComparer.Ordinal);
        var thumbnails = new Dictionary<string, ManifestAsset>(StringComparer.Ordinal);
        for (int index = 0; index < pages.Count; index++)
        {
            MediaReplacement page = pages[index];
            ArgumentNullException.ThrowIfNull(page);
            string source = PackageFiles.NormalizePath(page.SourcePath);
            using FileStream sourceFile = File.OpenRead(source);
            MediaDetails media = MediaInspector.Inspect(source);
            string mediaName = Path.GetFileName(source);
            string mediaType = media.Kind == MediaKind.Video ? "VIDEO" : "PICTURE";
            int duration = media.Kind == MediaKind.Image
                ? ImageDurationMilliseconds
                : media.DurationMilliseconds ?? throw new InvalidDataException("A video page requires a readable duration.");

            ManifestAsset resource = AddResource(source, sourceFile.Length, media.Kind, mediaType, mediaDirectory, resources);
            ManifestAsset thumbnail = AddThumbnail(page.ThumbnailPath, programDirectory, thumbnails);
            scenes.Add(CreateScene(defaultScene, index, duration, mediaName, mediaType, resource, thumbnail));
        }

        JsonArray playlistEntry = WriteConfiguration(programDirectory, PackageManifest.PlaylistName, playlist);
        var manifest = new JsonObject
        {
            ["name"] = programName,
            ["source"] = solution["source"]?.DeepClone(),
            ["playRelations"] = WriteConfiguration(programDirectory, PackageManifest.RelationName,
                PackageJson.Object(defaults["playRelation"], "playRelation")),
            ["playSolutions"] = WriteConfiguration(programDirectory, PackageManifest.SolutionName, solution),
            ["playlists"] = playlistEntry,
            ["scheduleConstraints"] = WriteConfiguration(programDirectory, PackageManifest.ScheduleName,
                PackageJson.Object(defaults["scheduleConstraint"], "scheduleConstraint")),
            ["audiolists"] = new JsonArray(),
            ["medialists"] = new JsonArray(),
            ["resources"] = new JsonArray(resources.Values.Select(asset => (JsonNode)asset.Json).ToArray()),
            ["thumbnails"] = new JsonArray(thumbnails.Values.Select(asset => (JsonNode)asset.Json).ToArray())
        };
        PackageJson.Write(Path.Combine(programDirectory, PackageManifest.FileName), manifest);

        bool import = mode == PlaybackMode.CopyAndReplace;
        var command = new JsonObject
        {
            ["programInfos"] = new JsonArray(new JsonObject
            {
                ["name"] = programName,
                ["url"] = programName,
                ["isPlay"] = true,
                ["isImport"] = import,
                ["thumbnailName"] = scenes[0]!["thumbnail"]!.GetValue<string>(),
                ["identifier"] = playlistEntry[0]!["md5"]!.GetValue<string>(),
                ["version"] = "1.0",
                ["size"] = CommandSize.Calculate(resources.Values, pages.Count, CommandSizeBasis.Resource),
                ["isOverride"] = import
            })
        };
        if (playerCommand.TryGetPropertyValue(AuthenticationKey, out var authentication))
            command[AuthenticationKey] = authentication?.DeepClone();

        PackageJson.WriteCommand(Path.Combine(root, NovaPackage.CommandFileName), command);
    }

    private static JsonObject CreateScene(
        JsonObject template, int index, int duration, string mediaName, string mediaType,
        ManifestAsset resource, ManifestAsset thumbnail)
    {
        var scene = (JsonObject)template.DeepClone();
        scene["id"] = index;
        scene["uuid"] = Guid.NewGuid().ToString();
        scene["duration"] = duration;
        scene["thumbnail"] = thumbnail.Name;
        if (index > 0)
            scene["orderNumber"] = index;

        JsonObject page = PackageJson.Object(scene["page"], "page");
        page["id"] = index;
        page["name"] = $"Page{index + 1}";
        JsonObject container = PackageJson.Single(PackageJson.Array(page, "widgetContainers"), "widgetContainers");
        container["uuid"] = Guid.NewGuid().ToString();

        JsonObject contents = PackageJson.Object(container["contents"], "contents");
        JsonObject widget = PackageJson.Single(PackageJson.Array(contents, "widgets"), "widgets");
        widget["id"] = 100000 + index;
        widget["uuid"] = Guid.NewGuid().ToString();
        widget["name"] = mediaName;
        widget["duration"] = duration;
        widget["type"] = mediaType;
        widget["originalType"] = mediaType;
        widget["filesize"] = resource.Size;
        widget["dataSource"] = resource.Name;
        widget["originalDataSource"] = mediaName;
        if (resource.Kind == MediaKind.Video)
            widget["metadata"] = new JsonObject { ["volume"] = 100 };
        return scene;
    }

    private static ManifestAsset AddResource(
        string source, long size, MediaKind kind, string type, string directory, Dictionary<string, ManifestAsset> resources)
    {
        string digest = PackageFiles.HashFile(source);
        string name = digest + Path.GetExtension(source).ToLowerInvariant();
        string stored = PackageFiles.StoreAsset(source, directory, name);
        if (!resources.TryGetValue(name, out ManifestAsset? asset))
        {
            asset = new ManifestAsset(name, stored, digest, size, kind, new JsonObject
            {
                ["fileName"] = name,
                ["md5"] = digest,
                ["Md5Suffixes"] = name,
                ["url"] = Path.GetFileName(source),
                ["type"] = type,
                ["size"] = size
            });
            resources.Add(name, asset);
        }
        return asset;
    }

    private static ManifestAsset AddThumbnail(string? input, string directory, Dictionary<string, ManifestAsset> thumbnails)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new InvalidDataException("Every new page needs a JPEG thumbnail.");

        string source = PackageFiles.NormalizePath(input);
        using FileStream sourceFile = File.OpenRead(source);
        if (Path.GetExtension(source).ToLowerInvariant() is not (".jpg" or ".jpeg"))
            throw new InvalidDataException("A page thumbnail must be a JPEG image.");
        _ = MediaInspector.Inspect(source);

        string digest = PackageFiles.HashFile(source);
        string name = digest + ".jpg";
        string stored = PackageFiles.StoreAsset(source, directory, name);
        if (!thumbnails.TryGetValue(name, out ManifestAsset? asset))
        {
            asset = new ManifestAsset(name, stored, digest, 0, MediaKind.Image, new JsonObject
            {
                ["fileName"] = name,
                ["size"] = 0,
                ["md5"] = digest
            });
            thumbnails.Add(name, asset);
        }
        return asset;
    }

    private static JsonArray WriteConfiguration(string directory, string name, JsonObject contents)
    {
        string path = Path.Combine(directory, name);
        PackageJson.Write(path, contents);
        string digest = PackageFiles.HashFile(path);
        return new JsonArray(new JsonObject
        {
            ["fileName"] = name,
            ["md5"] = digest,
            ["Md5Suffixes"] = digest + ".json"
        });
    }
}
