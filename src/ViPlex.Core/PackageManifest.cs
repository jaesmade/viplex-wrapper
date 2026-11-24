using System.Text.Json.Nodes;

namespace ViPlex.Core;

internal sealed class PackageManifest
{
    internal const string FileName = "planlist.json";
    internal const string PlaylistName = "playlist0.json";
    internal const string RelationName = "playSolutionRelation.json";
    internal const string SolutionName = "play_solution.json";
    internal const string ScheduleName = "schedule_constraint.json";
    internal const int ConfigurationFileCount = 5;

    private static readonly (string Category, string File)[] Configurations =
    [
        ("playRelations", RelationName),
        ("playSolutions", SolutionName),
        ("playlists", PlaylistName),
        ("scheduleConstraints", ScheduleName)
    ];

    internal PackageManifest(PackageFiles files, string folder)
    {
        Json = PackageJson.Read(files.RequireFile(folder + "/" + FileName));
        if (PackageJson.Array(Json, "audiolists").Count != 0 || PackageJson.Array(Json, "medialists").Count != 0)
            throw new InvalidDataException("Separate audio and media lists are outside the supported playlist layout.");
        foreach ((string category, string selectedFile) in Configurations)
        {
            JsonObject entry = PackageJson.Single(PackageJson.Array(Json, category), category);
            string name = EntryName(entry);
            if (name != selectedFile)
                throw new InvalidDataException($"The selected program has an unexpected '{category}' reference.");
            string digest = ValidateDigest(files, folder + "/" + name, entry);
            if (!PackageJson.String(entry, "Md5Suffixes").Equals(digest + ".json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The manifest suffix for '{name}' does not match its MD5.");
        }

        foreach (JsonNode? node in PackageJson.Array(Json, "resources"))
        {
            JsonObject entry = PackageJson.Object(node, "resources");
            string name = EntryName(entry);
            string relativePath = "media/" + name;
            string digest = ValidateDigest(files, relativePath, entry);
            string path = files.RequireFile(relativePath);
            MediaKind kind = ParseKind(PackageJson.String(entry, "type"));
            string extension = Path.GetExtension(name);
            if (!ValidExtension(kind, extension) || !name.Equals(digest + extension, StringComparison.OrdinalIgnoreCase) ||
                PackageJson.String(entry, "Md5Suffixes") != name)
                throw new InvalidDataException($"Resource '{name}' does not use a supported MD5 filename and media type.");

            long size = new FileInfo(path).Length;
            if (size == 0 || PackageJson.Value<long>(entry, "size") != size)
                throw new InvalidDataException($"The manifest size of '{name}' does not match the resource.");
            if (MediaInspector.Inspect(path).Kind != kind)
                throw new InvalidDataException($"The content of '{name}' does not match its manifest media type.");
            AddEntry(Resources, new ManifestAsset(name, path, digest, size, kind, entry));
        }

        foreach (JsonNode? node in PackageJson.Array(Json, "thumbnails"))
        {
            JsonObject entry = PackageJson.Object(node, "thumbnails");
            string name = EntryName(entry);
            string relativePath = folder + "/" + name;
            string path = files.RequireFile(relativePath);
            string digest = ValidateDigest(files, relativePath, entry);
            if (!name.Equals(digest + ".jpg", StringComparison.OrdinalIgnoreCase) ||
                PackageJson.Value<long>(entry, "size") != 0 || new FileInfo(path).Length == 0)
                throw new InvalidDataException($"Thumbnail '{name}' does not use the supported JPEG manifest layout.");
            _ = MediaInspector.Inspect(path);
            AddEntry(Thumbnails, new ManifestAsset(name, path, digest, 0, MediaKind.Image, entry));
        }
    }

    internal JsonObject Json { get; }
    internal Dictionary<string, ManifestAsset> Resources { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, ManifestAsset> Thumbnails { get; } = new(StringComparer.Ordinal);

    internal static MediaKind ParseKind(string type) => type switch
    {
        "PICTURE" => MediaKind.Image,
        "VIDEO" => MediaKind.Video,
        _ => throw new InvalidDataException("The selected program contains an unsupported media type.")
    };

    internal static bool ValidExtension(MediaKind kind, string extension)
    {
        string lower = extension.ToLowerInvariant();
        return kind == MediaKind.Image ? lower is ".jpg" or ".jpeg" or ".png" : lower == ".mp4";
    }

    internal static ManifestAsset FindAsset(Dictionary<string, ManifestAsset> entries, string name)
    {
        PackageFiles.SafeName(name);
        return entries.TryGetValue(name, out ManifestAsset? entry)
            ? entry
            : throw new InvalidDataException($"'{name}' is referenced by the playlist but missing from the manifest.");
    }

    private static void AddEntry(Dictionary<string, ManifestAsset> entries, ManifestAsset asset)
    {
        if (!entries.TryAdd(asset.Name, asset))
            throw new InvalidDataException($"The manifest lists '{asset.Name}' more than once.");
    }

    private static string EntryName(JsonObject entry) => PackageFiles.SafeName(PackageJson.String(entry, "fileName"));

    private static string ValidateDigest(PackageFiles files, string relativePath, JsonObject entry)
    {
        string digest = files.Digest(relativePath);
        if (!PackageJson.String(entry, "md5").Equals(digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The MD5 of '{relativePath}' does not match the manifest.");
        return digest;
    }
}

internal sealed record ManifestAsset(string Name, string Path, string Digest, long Size, MediaKind Kind, JsonObject Json);

internal enum CommandSizeBasis { Resource, Page }

internal static class CommandSize
{
    private const int EntryOverhead = 1024;

    internal static long Calculate(IEnumerable<ManifestAsset> resources, int pageCount, CommandSizeBasis basis)
    {
        ManifestAsset[] entries = resources.DistinctBy(asset => asset.Name, StringComparer.Ordinal).ToArray();
        int count = basis == CommandSizeBasis.Page ? pageCount : entries.Length;
        return checked(entries.Sum(asset => asset.Size) + EntryOverhead * (PackageManifest.ConfigurationFileCount + count));
    }

    internal static CommandSizeBasis Identify(long actual, IEnumerable<ManifestAsset> resources, int pageCount)
    {
        // Official fixtures contain one distinct resource per page. For repeats, prefer
        // the resource-based formula, but preserve a page-based overhead when the source proves it.
        if (actual == Calculate(resources, pageCount, CommandSizeBasis.Resource))
            return CommandSizeBasis.Resource;
        if (actual == Calculate(resources, pageCount, CommandSizeBasis.Page))
            return CommandSizeBasis.Page;
        throw new InvalidDataException("The selected program size does not match the supported USB package layout.");
    }
}
