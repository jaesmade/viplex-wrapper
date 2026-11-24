using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace ViPlex.Core;

public sealed record PackagePageEdit(
    int? SourceIndex,
    MediaReplacement? Media = null,
    string? Name = null,
    int? ImageDurationMilliseconds = null,
    int? RepeatCount = null,
    int? VideoVolume = null,
    PageSchedule? Schedule = null,
    Guid? DraftIdentity = null);

public static class PackageBuilder
{
    public static void Build(
        NovaPackage source,
        string stagedNovaPath,
        PlaybackMode mode,
        IReadOnlyDictionary<int, MediaReplacement> replacements)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(replacements);
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (replacements.Keys.Any(index => index < 0 || index >= source.Pages.Count))
            throw new ArgumentOutOfRangeException(nameof(replacements), "A replacement refers to a page outside the selected playlist.");

        BuildStaged(source, stagedNovaPath, mode, package => ReplacePages(package, replacements));
    }

    public static void BuildEdited(
        NovaPackage source,
        string stagedNovaPath,
        PlaybackMode mode,
        IReadOnlyList<PackagePageEdit> pages)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pages);
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (pages.Count == 0)
            throw new ArgumentException("Keep at least one page in the playlist.", nameof(pages));
        var draftIdentities = new HashSet<Guid>();
        foreach (PackagePageEdit edit in pages)
        {
            ArgumentNullException.ThrowIfNull(edit);
            if (edit.SourceIndex is int index && (index < 0 || index >= source.Pages.Count))
                throw new ArgumentOutOfRangeException(nameof(pages), "A page refers to a source outside the selected playlist.");
            if (edit.SourceIndex is null && edit.Media is null)
                throw new ArgumentException("A new page needs media and a thumbnail.", nameof(pages));
            if (edit.Name is not null && string.IsNullOrWhiteSpace(edit.Name))
                throw new ArgumentException("A page name cannot be blank.", nameof(pages));
            if (edit.ImageDurationMilliseconds is <= 0)
                throw new ArgumentOutOfRangeException(nameof(pages), "An image hold time must be positive.");
            if (edit.DraftIdentity is Guid draftIdentity && !draftIdentities.Add(draftIdentity))
                throw new ArgumentException("Each added or duplicated draft needs a unique identity.", nameof(pages));
        }

        BuildStaged(source, stagedNovaPath, mode, package => EditPlaylist(package, pages));
    }

    private static void BuildStaged(NovaPackage source, string stagedNovaPath, PlaybackMode mode, Func<NovaPackage, bool> editPlaylist)
    {
        string destination = PackageFiles.NormalizePath(stagedNovaPath);
        if (PackageFiles.Contains(source.NovaPath, destination))
            throw new IOException("The staging path must be outside the source nova folder.");

        var sourceFiles = new PackageFiles(source.NovaPath);
        PackageFiles.Stage(destination, temporary =>
        {
            sourceFiles.CopyTo(temporary);
            NovaPackage package = NovaPackage.Open(temporary);
            if (package.CommandFingerprint != source.CommandFingerprint)
                throw new IOException("The source package changed after opening. Open the package again before preparing it.");

            bool playlistChanged = editPlaylist(package);
            if (playlistChanged || mode != package.Mode)
            {
                bool import = mode == PlaybackMode.CopyAndReplace;
                package.Program["isImport"] = import;
                package.Program["isOverride"] = import;
                PackageJson.WriteCommand(Path.Combine(temporary, NovaPackage.CommandFileName), package.Command);
                _ = NovaPackage.Open(temporary);
            }
        });
    }

    private static bool ReplacePages(NovaPackage package, IReadOnlyDictionary<int, MediaReplacement> replacements)
    {
        if (replacements.Count == 0)
            return false;

        string programDirectory = Path.Combine(package.NovaPath, package.ProgramFolder);
        string mediaDirectory = Path.Combine(package.NovaPath, "media");
        PageBinding[] pages = package.PageBindings.ToArray();
        bool changed = false;
        for (int index = 0; index < pages.Length; index++)
        {
            if (!replacements.TryGetValue(index, out MediaReplacement? replacement))
                continue;
            ArgumentNullException.ThrowIfNull(replacement);

            PageBinding page = pages[index];
            ManifestAsset media = ReplaceMedia(page, replacement.SourcePath, mediaDirectory, package.Manifest.Resources);
            ManifestAsset thumbnail = replacement.ThumbnailPath is null
                ? page.Thumbnail
                : ReplaceThumbnail(page, replacement.ThumbnailPath, programDirectory, package.Manifest.Thumbnails);
            if (media != page.Media || thumbnail != page.Thumbnail)
            {
                pages[index] = page with { Media = media, Thumbnail = thumbnail };
                changed = true;
            }
        }

        if (!changed)
            return false;

        WriteChangedPlaylist(package, pages.Select(page => page.Media), pages.Select(page => page.Thumbnail).ToArray());
        return true;
    }

    private static bool EditPlaylist(NovaPackage package, IReadOnlyList<PackagePageEdit> edits)
    {
        string programDirectory = Path.Combine(package.NovaPath, package.ProgramFolder);
        string mediaDirectory = Path.Combine(package.NovaPath, "media");
        JsonArray originalScenes = PackageJson.Array(package.Playlist, "sceneItems");
        var editedScenes = new JsonArray();
        var resources = new List<ManifestAsset>(edits.Count);
        var thumbnails = new List<ManifestAsset>(edits.Count);
        var seen = new HashSet<int>();
        var identities = new PageIdentities(package.PageBindings);
        bool orderChanged = edits.Count != originalScenes.Count || edits.Where((edit, index) => edit.SourceIndex != index).Any();
        JsonObject? template = null;

        for (int index = 0; index < edits.Count; index++)
        {
            PackagePageEdit edit = edits[index];
            PageBinding? original = edit.SourceIndex is int sourceIndex ? package.PageBindings[sourceIndex] : null;
            string? path = edit.Media is null ? null : PackageFiles.NormalizePath(edit.Media.SourcePath);
            using FileStream? mediaFile = path is null ? null : File.OpenRead(path);
            MediaDetails? details = path is null ? null : MediaInspector.Inspect(path);
            JsonObject scene;
            PageNodes nodes;
            ManifestAsset media;
            ManifestAsset thumbnail;

            if (original is null || details is not null && details.Kind != original.Page.Kind)
            {
                thumbnail = edit.Media!.ThumbnailPath is { } replacementThumbnail
                    ? PackageCreator.AddThumbnail(replacementThumbnail, programDirectory, package.Manifest.Thumbnails)
                    : original?.Thumbnail ?? throw new InvalidDataException("A new page needs a JPEG thumbnail.");
                template ??= PackageCreator.ReadDefaultScene();
                (scene, media) = PackageCreator.CreateMediaPage(template, index, path!, mediaFile!.Length, details!, thumbnail,
                    mediaDirectory, package.Manifest.Resources);
                nodes = ReadPageNodes(scene);
                if (original is not null)
                    KeepPageSettings(original.Scene, scene, nodes);
            }
            else
            {
                scene = (JsonObject)original.Scene.DeepClone();
                nodes = ReadPageNodes(scene);
                var binding = original with { Scene = scene, Widget = nodes.Widget };
                media = path is null ? original.Media : ReplaceMedia(binding, path, mediaDirectory, package.Manifest.Resources, details);
                thumbnail = edit.Media?.ThumbnailPath is { } replacementThumbnail
                    ? ReplaceThumbnail(binding, replacementThumbnail, programDirectory, package.Manifest.Thumbnails)
                    : original.Thumbnail;
            }

            if (edit.DraftIdentity is not null || edit.SourceIndex is not int sourcePage || !seen.Add(sourcePage))
                identities.Assign(scene, nodes, edit.DraftIdentity);
            if (edit.Name is not null)
                nodes.Page["name"] = edit.Name;
            if (edit.ImageDurationMilliseconds is int hold)
            {
                if (media.Kind != MediaKind.Image)
                    throw new ArgumentException("A video uses its media duration. Image hold time applies only to still images.", nameof(edits));
                scene["duration"] = hold;
                nodes.Widget["duration"] = hold;
            }
            PagePlaybackEdits.Apply(scene, nodes.Widget, media.Kind, edit);
            if (orderChanged)
            {
                if (index == 0)
                    scene.Remove("orderNumber");
                else
                    scene["orderNumber"] = index;
            }

            editedScenes.Add(scene);
            resources.Add(media);
            thumbnails.Add(thumbnail);
        }

        if (JsonNode.DeepEquals(originalScenes, editedScenes))
            return false;

        package.Playlist["sceneItems"] = editedScenes;
        WriteChangedPlaylist(package, resources, thumbnails);
        return true;
    }

    private static void KeepPageSettings(JsonObject original, JsonObject scene, PageNodes target)
    {
        PageNodes source = ReadPageNodes(original);
        CopyProperties(original, scene, "id", "uuid", "name", "backgroundColor", "enable", "rules", "IsUserBkMusicDuration",
            "repeatCount", "constraints", "orderNumber");
        CopyProperties(source.Page, target.Page, "id", "uuid", "name", "inAnimation", "outAnimation", "border");
        CopyProperties(source.Container, target.Container, "id", "uuid");
        CopyProperties(source.Widget, target.Widget, "id", "uuid", "enable", "repeatCount", "constraints");
    }

    private static void CopyProperties(JsonObject source, JsonObject target, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (source.TryGetPropertyValue(key, out JsonNode? value))
                target[key] = value?.DeepClone();
            else
                target.Remove(key);
        }
    }

    private static PageNodes ReadPageNodes(JsonObject scene)
    {
        JsonObject page = PackageJson.Object(scene["page"], "page");
        JsonObject container = PackageJson.Single(PackageJson.Array(page, "widgetContainers"), "widgetContainers");
        JsonObject contents = PackageJson.Object(container["contents"], "contents");
        JsonObject widget = PackageJson.Single(PackageJson.Array(contents, "widgets"), "widgets");
        return new PageNodes(page, container, widget);
    }

    private static void WriteChangedPlaylist(NovaPackage package, IEnumerable<ManifestAsset> resources, IReadOnlyList<ManifestAsset> thumbnails)
    {
        string programDirectory = Path.Combine(package.NovaPath, package.ProgramFolder);
        string playlistPath = Path.Combine(programDirectory, PackageManifest.PlaylistName);
        PackageJson.Write(playlistPath, package.Playlist);
        string playlistDigest = PackageFiles.HashFile(playlistPath);
        JsonObject playlistEntry = PackageJson.Single(PackageJson.Array(package.Manifest.Json, "playlists"), "playlists");
        playlistEntry["md5"] = playlistDigest;
        playlistEntry["Md5Suffixes"] = playlistDigest + ".json";

        SynchronizeAssetEntries(package.Manifest.Json, "resources", resources);
        SynchronizeAssetEntries(package.Manifest.Json, "thumbnails", thumbnails);
        PackageJson.Write(Path.Combine(programDirectory, PackageManifest.FileName), package.Manifest.Json);

        package.Program["identifier"] = playlistDigest;
        package.Program["thumbnailName"] = thumbnails[0].Name;
        package.Program["size"] = CommandSize.Calculate(resources, thumbnails.Count, package.SizeBasis);
    }

    private static ManifestAsset ReplaceMedia(
        PageBinding page, string source, string directory, Dictionary<string, ManifestAsset> resources, MediaDetails? details = null)
    {
        string sourcePath = PackageFiles.NormalizePath(source);
        string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        using FileStream sourceFile = File.OpenRead(sourcePath);
        details ??= MediaInspector.Inspect(sourcePath);
        if (details.Kind != page.Page.Kind || !PackageManifest.ValidExtension(details.Kind, extension))
            throw new InvalidDataException("The replacement must use the same media type as its selected page.");

        string digest = PackageFiles.HashFile(sourcePath);
        if (digest == page.Media.Digest)
        {
            PackageFiles.RequireIdenticalContent(sourcePath, page.Media.Path);
            return page.Media;
        }

        long size = sourceFile.Length;
        string name = digest + extension;
        if (!resources.TryGetValue(name, out ManifestAsset? asset))
        {
            string path = PackageFiles.StoreAsset(sourcePath, directory, name);
            JsonObject entry = (JsonObject)page.Media.Json.DeepClone();
            entry["fileName"] = name;
            entry["md5"] = digest;
            entry["Md5Suffixes"] = name;
            entry["url"] = sourcePath;
            entry["size"] = size;
            asset = new ManifestAsset(name, path, digest, size, details.Kind, entry);
            resources.Add(name, asset);
        }
        else
        {
            PackageFiles.RequireIdenticalContent(sourcePath, asset.Path);
        }

        page.Widget["name"] = Path.GetFileName(sourcePath);
        page.Widget["dataSource"] = name;
        page.Widget["originalDataSource"] = sourcePath;
        page.Widget["filesize"] = size;
        if (page.Widget["ShowPic"] is JsonValue showPic && showPic.TryGetValue(out string? original) && !string.IsNullOrEmpty(original))
        {
            int separator = Math.Max(original.LastIndexOf('\\'), original.LastIndexOf('/'));
            page.Widget["ShowPic"] = original[..(separator + 1)] + digest + ".jpg";
        }

        if (details.Kind == MediaKind.Video)
        {
            if (details.DurationMilliseconds is not int duration || duration <= 0)
                throw new InvalidDataException("The replacement video must have a readable duration.");
            page.Scene["duration"] = duration;
            page.Widget["duration"] = duration;
        }
        return asset;
    }

    private static ManifestAsset ReplaceThumbnail(
        PageBinding page, string source, string directory, Dictionary<string, ManifestAsset> thumbnails)
    {
        string sourcePath = PackageFiles.NormalizePath(source);
        string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        using FileStream sourceFile = File.OpenRead(sourcePath);
        if (extension is not (".jpg" or ".jpeg") || MediaInspector.Inspect(sourcePath).Kind != MediaKind.Image)
            throw new InvalidDataException("A replacement thumbnail must be a JPEG image.");

        string digest = PackageFiles.HashFile(sourcePath);
        if (digest == page.Thumbnail.Digest)
        {
            PackageFiles.RequireIdenticalContent(sourcePath, page.Thumbnail.Path);
            return page.Thumbnail;
        }

        string name = digest + ".jpg";
        if (!thumbnails.TryGetValue(name, out ManifestAsset? asset))
        {
            string path = PackageFiles.StoreAsset(sourcePath, directory, name);
            JsonObject entry = (JsonObject)page.Thumbnail.Json.DeepClone();
            entry["fileName"] = name;
            entry["md5"] = digest;
            asset = new ManifestAsset(name, path, digest, 0, MediaKind.Image, entry);
            thumbnails.Add(name, asset);
        }
        else
        {
            PackageFiles.RequireIdenticalContent(sourcePath, asset.Path);
        }

        page.Scene["thumbnail"] = name;
        return asset;
    }

    private static void SynchronizeAssetEntries(JsonObject manifest, string key, IEnumerable<ManifestAsset> referencedAssets)
    {
        ManifestAsset[] assets = referencedAssets.DistinctBy(asset => asset.Name, StringComparer.Ordinal).ToArray();
        JsonArray entries = PackageJson.Array(manifest, key);
        var remaining = assets.Select(asset => asset.Name).ToHashSet(StringComparer.Ordinal);
        for (int index = 0; index < entries.Count;)
        {
            string name = PackageJson.String(PackageJson.Object(entries[index], key), "fileName");
            if (remaining.Remove(name))
                index++;
            else
                entries.RemoveAt(index);
        }
        foreach (ManifestAsset asset in assets)
        {
            if (remaining.Contains(asset.Name))
                entries.Add(asset.Json.DeepClone());
        }
    }

    private sealed record PageNodes(JsonObject Page, JsonObject Container, JsonObject Widget);

    private sealed class PageIdentities
    {
        private readonly HashSet<int> pageIds = [];
        private readonly HashSet<int> containerIds = [];
        private readonly HashSet<int> widgetIds = [];
        private int nextPageId;
        private int nextContainerId = 1_000;
        private int nextWidgetId = 100_000;

        public PageIdentities(IReadOnlyList<PageBinding> bindings)
        {
            foreach (PageBinding binding in bindings)
            {
                PageNodes nodes = ReadPageNodes(binding.Scene);
                Remember(pageIds, binding.Scene);
                Remember(pageIds, nodes.Page);
                Remember(containerIds, nodes.Container);
                Remember(widgetIds, nodes.Widget);
            }
        }

        public void Assign(JsonObject scene, PageNodes nodes, Guid? draftIdentity)
        {
            int pageId = Next(pageIds, ref nextPageId);
            SetIdentity(scene, pageId, draftIdentity, role: 0);
            nodes.Page["id"] = pageId;
            SetIdentity(nodes.Container, Next(containerIds, ref nextContainerId), draftIdentity, role: 1);
            SetIdentity(nodes.Widget, Next(widgetIds, ref nextWidgetId), draftIdentity, role: 2);
        }

        private static void Remember(HashSet<int> ids, JsonObject node)
        {
            if (node["id"] is JsonValue value && value.TryGetValue(out int id))
                ids.Add(id);
        }

        private static int Next(HashSet<int> ids, ref int id)
        {
            while (ids.Contains(id))
                id++;
            return id++;
        }

        private static void SetIdentity(JsonObject node, int id, Guid? draftIdentity, byte role)
        {
            node["id"] = id;
            node["uuid"] = (draftIdentity is Guid draft ? DeriveUuid(draft, role) : Guid.NewGuid()).ToString();
        }

        private static Guid DeriveUuid(Guid draft, byte role)
        {
            Span<byte> input = stackalloc byte[17];
            draft.TryWriteBytes(input, bigEndian: true, out _);
            input[16] = role;
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(input, digest);
            // Namespace each part of the draft with a distinct, deterministic version-8 UUID.
            digest[6] = (byte)((digest[6] & 0x0F) | 0x80);
            digest[8] = (byte)((digest[8] & 0x3F) | 0x80);
            return new Guid(digest[..16], bigEndian: true);
        }
    }
}
