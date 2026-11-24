using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ViPlex.Core;

internal sealed class PackageFiles
{
    private static readonly char[] InvalidNameCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    private readonly Dictionary<string, string> files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> directories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> digests = new(StringComparer.Ordinal);

    internal PackageFiles(string root)
    {
        CheckAncestors(root);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("The selected folder does not contain a nova package.");

        var pending = new Stack<(string FullPath, string RelativePath)>();
        pending.Push((root, ""));
        while (pending.TryPop(out var directory))
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory.FullPath))
            {
                string name = SafeName(Path.GetFileName(path));
                if (!names.Add(name))
                    throw new InvalidDataException("A package folder contains names that differ only by case.");

                string relativePath = directory.RelativePath.Length == 0 ? name : directory.RelativePath + "/" + name;
                FileAttributes attributes = File.GetAttributes(path);
                CheckLink(attributes);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Add(relativePath, path);
                    pending.Push((path, relativePath));
                }
                else
                {
                    files.Add(relativePath, path);
                }
            }
        }
    }

    internal string RequireFile(string relativePath) => files.TryGetValue(relativePath, out string? path)
        ? path
        : throw new InvalidDataException($"The package is missing '{relativePath}'.");

    internal string Digest(string relativePath)
    {
        if (!digests.TryGetValue(relativePath, out string? digest))
        {
            digest = HashFile(RequireFile(relativePath));
            digests.Add(relativePath, digest);
        }
        return digest;
    }

    internal void CopyTo(string destination)
    {
        foreach (string relativePath in directories.Keys)
            Directory.CreateDirectory(Path.Combine(destination, relativePath));

        foreach ((string relativePath, string path) in files)
        {
            CheckLink(File.GetAttributes(path));
            CopyFile(path, Path.Combine(destination, relativePath));
        }
    }

    internal static void CopyFile(string source, string destination)
    {
        File.Copy(source, destination);
        FileAttributes attributes = File.GetAttributes(destination);
        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(destination, attributes & ~FileAttributes.ReadOnly);
    }

    internal static void Stage(string destination, Action<string> write)
    {
        CheckAncestors(destination);
        if (Path.Exists(destination))
            throw new IOException("The staging path must be new.");

        string parent = Path.GetDirectoryName(destination) ?? throw new IOException("The staging path must have a parent folder.");
        Directory.CreateDirectory(parent);
        string temporary = Path.Combine(parent, ".viplex-build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            write(temporary);
            Directory.Move(temporary, destination);
        }
        finally
        {
            if (Directory.Exists(temporary))
                Directory.Delete(temporary, recursive: true);
        }
    }

    internal static string FindNova(string rootOrNovaPath)
    {
        string path = NormalizePath(rootOrNovaPath);
        CheckAncestors(path);
        if (File.Exists(Path.Combine(path, "command.json")))
            return path;

        string child = Path.Combine(path, "nova");
        if (Directory.Exists(child) || !Path.GetFileName(path).Equals("nova", StringComparison.OrdinalIgnoreCase))
            return child;
        return path;
    }

    internal static string NormalizePath(string path)
    {
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (OperatingSystem.IsWindows())
        {
            if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal) || normalized.StartsWith(@"\\.\", StringComparison.Ordinal))
                throw new InvalidDataException("Device paths are outside the supported package locations.");

            string root = Path.GetPathRoot(normalized)!;
            foreach (string segment in normalized[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                SafeName(segment);
        }
        return normalized;
    }

    internal static void CheckAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                CheckLink(File.GetAttributes(current));
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    internal static bool Contains(string directory, string path)
    {
        if (path.Equals(directory, StringComparison.OrdinalIgnoreCase))
            return true;

        string prefix = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    internal static string SafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.EndsWith('.') || name.EndsWith(' ') ||
            name.IndexOfAny(InvalidNameCharacters) >= 0 || name.Any(char.IsControl) || IsDeviceName(name))
            throw new InvalidDataException("A package file or folder name is invalid or escapes its expected location.");
        return name;
    }

    internal static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(MD5.HashData(stream));
    }

    internal static string StoreAsset(string source, string directory, string name)
    {
        string destination = Path.Combine(directory, name);
        if (Path.Exists(destination))
            RequireIdenticalContent(source, destination);
        else
            CopyFile(source, destination);
        return destination;
    }

    internal static void RequireIdenticalContent(string source, string destination)
    {
        using FileStream first = File.OpenRead(source);
        using FileStream second = File.OpenRead(destination);
        if (first.Length != second.Length)
            throw new InvalidDataException("Two different files share the same package asset filename.");

        Span<byte> firstBuffer = stackalloc byte[4096];
        Span<byte> secondBuffer = stackalloc byte[4096];
        while (first.Position < first.Length)
        {
            int count = (int)Math.Min(firstBuffer.Length, first.Length - first.Position);
            first.ReadExactly(firstBuffer[..count]);
            second.ReadExactly(secondBuffer[..count]);
            if (!firstBuffer[..count].SequenceEqual(secondBuffer[..count]))
                throw new InvalidDataException("Two different files share the same package asset filename.");
        }
    }

    private static void CheckLink(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Nova packages and staging paths cannot contain symbolic links or junctions.");
    }

    private static bool IsDeviceName(string name)
    {
        string stem = name.Split('.', 2)[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                                 stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3]);
    }
}

internal static class PackageJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions OutputFormat = new()
    {
        WriteIndented = true,
        NewLine = "\r\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal static JsonObject Read(string path) => Parse(File.ReadAllBytes(path), Path.GetFileName(path));

    internal static (JsonObject Body, string Fingerprint) ReadCommand(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(StrictUtf8.GetString(WithoutBom(raw).Span));
        }
        catch (Exception error) when (error is FormatException or DecoderFallbackException)
        {
            throw new InvalidDataException("command.json must contain Base64-wrapped JSON.");
        }
        return (Parse(bytes, "command.json"), Convert.ToHexString(SHA256.HashData(raw)));
    }

    internal static void Write(string path, JsonObject data) => File.WriteAllText(path, data.ToJsonString(OutputFormat));

    internal static void WriteCommand(string path, JsonObject data)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(data, OutputFormat);
        File.WriteAllText(path, Convert.ToBase64String(bytes));
    }

    internal static JsonObject Object(JsonNode? value, string location) => value as JsonObject ??
        throw new InvalidDataException($"Expected an object at '{location}'.");

    internal static JsonArray Array(JsonObject data, string key) => data[key] as JsonArray ??
        throw new InvalidDataException($"Expected an array at '{key}'.");

    internal static JsonObject Single(JsonArray array, string location) => array.Count == 1
        ? Object(array[0], location)
        : throw new InvalidDataException($"Expected one entry at '{location}'.");

    internal static string String(JsonObject data, string key) => Value<string>(data, key);

    internal static string RequiredText(JsonObject data, string key)
    {
        string value = String(data, key);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"Missing text at '{key}'.");
        return value;
    }

    internal static T Value<T>(JsonObject data, string key) => data[key] is JsonValue value && value.TryGetValue(out T? result)
        ? result
        : throw new InvalidDataException($"Invalid value at '{key}'.");

    internal static void Match(JsonObject data, string key, string expected)
    {
        if (!String(data, key).Equals(expected, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported or inconsistent value at '{key}'.");
    }

    private static JsonObject Parse(byte[] bytes, string file)
    {
        try
        {
            ReadOnlyMemory<byte> contents = WithoutBom(bytes);
            _ = StrictUtf8.GetCharCount(contents.Span);
            using JsonDocument document = JsonDocument.Parse(contents);
            RejectDuplicateProperties(document.RootElement);
            return Object(JsonNode.Parse(contents.Span), file);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        {
            throw new InvalidDataException($"'{file}' contains malformed JSON.");
        }
    }

    private static ReadOnlyMemory<byte> WithoutBom(ReadOnlyMemory<byte> bytes) =>
        bytes.Span.StartsWith("\uFEFF"u8) ? bytes[3..] : bytes;

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException("A package JSON object contains a duplicate property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
                RejectDuplicateProperties(item);
        }
    }
}
