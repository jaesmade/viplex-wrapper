using System.Text.Json.Nodes;

namespace ViPlex.Core;

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

            bool playlistChanged = ReplacePages(package, replacements);
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

        string playlistPath = Path.Combine(programDirectory, PackageManifest.PlaylistName);
        PackageJson.Write(playlistPath, package.Playlist);
        string playlistDigest = PackageFiles.HashFile(playlistPath);
        JsonObject playlistEntry = PackageJson.Single(PackageJson.Array(package.Manifest.Json, "playlists"), "playlists");
        playlistEntry["md5"] = playlistDigest;
        playlistEntry["Md5Suffixes"] = playlistDigest + ".json";

        ManifestAsset[] resources = pages.Select(page => page.Media).ToArray();
        SynchronizeAssetEntries(package.Manifest.Json, "resources", resources);
        SynchronizeAssetEntries(package.Manifest.Json, "thumbnails", pages.Select(page => page.Thumbnail));
        PackageJson.Write(Path.Combine(programDirectory, PackageManifest.FileName), package.Manifest.Json);

        package.Program["identifier"] = playlistDigest;
        package.Program["thumbnailName"] = pages[0].Thumbnail.Name;
        package.Program["size"] = CommandSize.Calculate(resources, pages.Length, package.SizeBasis);
        return true;
    }

    private static ManifestAsset ReplaceMedia(
        PageBinding page, string source, string directory, Dictionary<string, ManifestAsset> resources)
    {
        string sourcePath = PackageFiles.NormalizePath(source);
        string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        using FileStream sourceFile = File.OpenRead(sourcePath);
        MediaDetails details = MediaInspector.Inspect(sourcePath);
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
}
