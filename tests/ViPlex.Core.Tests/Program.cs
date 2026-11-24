using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ViPlex.Core;

if (args.Length != 1 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: dotnet run --project tests/ViPlex.Core.Tests -- <reference exports folder>");
    return 2;
}

var reference = Path.GetFullPath(args[0]);
var scratch = Directory.CreateTempSubdirectory("viplex-tests-").FullName;
var failures = 0;
try
{
    Check("Open all five exports", () =>
    {
        foreach (var (name, durations, mode) in new[]
        {
            ("picture", new[] { 10_000 }, PlaybackMode.CopyAndReplace),
            ("videoplayback", new[] { 100_000 }, PlaybackMode.CopyAndReplace),
            ("multipic", new[] { 10_000, 10_000 }, PlaybackMode.CopyAndReplace),
            ("multivid", new[] { 180_000, 100_000 }, PlaybackMode.CopyAndReplace),
            ("plugnply", new[] { 180_000, 100_000 }, PlaybackMode.PlugAndPlay),
        })
        {
            var package = Open(name);
            Require(package.Mode == mode && package.Pages.Select(page => page.DurationMilliseconds).SequenceEqual(durations), name);
        }
    });

    Check("Create and publish a new package on a blank USB folder", () =>
    {
        var image = Open("picture").Pages[0];
        var video = Open("multivid").Pages[0];
        var target = Directory.CreateDirectory(Path.Combine(scratch, "blank-usb")).FullName;
        File.WriteAllText(Path.Combine(target, "keep.txt"), "unrelated file");
        var staged = Path.Combine(scratch, "created", "nova");
        PackageCreator.Create(staged, "T10 Starter", PlaybackMode.PlugAndPlay, "admin", "fixture-password", new MediaReplacement[]
        {
            new(image.MediaPath, image.ThumbnailPath),
            new(video.MediaPath, video.ThumbnailPath),
        });

        var prepared = NovaPackage.Open(staged);
        Require(prepared.ProgramName == "T10 Starter" && prepared.Mode == PlaybackMode.PlugAndPlay, "New command has the wrong name or mode");
        Require(prepared.Pages.Select(page => page.Kind).SequenceEqual(new[] { MediaKind.Image, MediaKind.Video }), "New page order is incorrect");
        Require(prepared.Pages.Select(page => page.DurationMilliseconds).SequenceEqual(new[] { 10_000, 180_000 }), "New page durations are incorrect");
        Require(Directory.Exists(Path.Combine(staged, "viplex_terminal", "media")), "New package omits the empty terminal media folder");
        VerifyCommand(prepared);

        var decoded = JsonNode.Parse(Convert.FromBase64String(File.ReadAllText(Path.Combine(staged, "command.json"))))!;
        var account = decoded["authenticUserInfos"]![0]!;
        Require(account["username"]!.GetValue<string>() == "admin" && account["password"]!.GetValue<string>() == "fixture-password", "New command authentication does not match the supplied values");
        var result = PackagePublisher.Publish(staged, target, overwrite: false);
        Require(result.BackupPath is null && NovaPackage.Open(target).ProgramName == prepared.ProgramName, "Blank drive did not receive its first package");
        Require(Snapshot(result.NovaPath).SequenceEqual(Snapshot(staged)) && File.Exists(Path.Combine(target, "keep.txt")), "First write altered or omitted files");
    });

    Check("Missing media cannot be published as a new package", () =>
    {
        var staged = Path.Combine(scratch, "empty-program", "nova");
        RequireFailure(() => PackageCreator.Create(staged, "Empty", PlaybackMode.CopyAndReplace, "admin", "fixture-password", Array.Empty<MediaReplacement>()));
        Require(!Directory.Exists(staged), "An empty selection left a publishable package");
    });

    Check("No-edit export preserves the entire tree", () =>
    {
        var source = Open("multipic");
        var exported = Stage("no-edit", source, source.Mode);
        Require(Snapshot(exported).SequenceEqual(Snapshot(source.NovaPath)), "No-op changed package bytes or directories");
    });

    Check("Mode switch leaves every file except the command intact", () =>
    {
        var source = Open("multivid");
        var exported = Stage("mode", source, PlaybackMode.PlugAndPlay);
        Require(Snapshot(exported).Where(entry => entry.Path != "command.json")
            .SequenceEqual(Snapshot(source.NovaPath).Where(entry => entry.Path != "command.json")), "Program bytes changed with mode");
        var command = Command(exported);
        Require(command["isPlay"]!.GetValue<bool>() && !command["isImport"]!.GetValue<bool>() && !command["isOverride"]!.GetValue<bool>(), "Plug and play flags");
        Require(NovaPackage.Open(exported).Mode == PlaybackMode.PlugAndPlay, "Could not reopen switched package");
    });

    Check("Image replacement preserves a thumbnail shared with the first page", () =>
    {
        var source = Open("multipic");
        var replacement = Open("picture").Pages[0];
        var distinctImage = Directory.GetFiles(Path.Combine(source.NovaPath, "media"), "*.png").First();
        var newThumbnail = Open("videoplayback").Pages[0].ThumbnailPath;
        var exported = Stage("image-edit", source, source.Mode, new Dictionary<int, MediaReplacement>
        {
            [1] = new(distinctImage, newThumbnail),
        });
        var reopened = NovaPackage.Open(exported);
        Require(Path.GetFileName(reopened.Pages[0].MediaPath) == Path.GetFileName(replacement.MediaPath), "First page changed");
        Require(Path.GetFileName(reopened.Pages[0].ThumbnailPath) == Path.GetFileName(source.Pages[0].ThumbnailPath), "Shared first thumbnail changed");
        Require(Path.GetFileName(reopened.Pages[1].ThumbnailPath) == Path.GetFileName(newThumbnail), "Replacement thumbnail was not added");
        Require(reopened.Pages[1].DurationMilliseconds == 10_000, "Image duration changed");
        VerifyCommand(reopened);
    });

    Check("Video replacement updates duration, bytes and command identifier", () =>
    {
        var source = Open("multivid");
        var replacement = Directory.GetFiles(Path.Combine(source.NovaPath, "media"), "*.mp4")
            .Single(path => MediaInspector.Inspect(path).DurationMilliseconds == 60_000);
        var exported = Stage("video-edit", source, source.Mode, new Dictionary<int, MediaReplacement> { [1] = new(replacement) });
        var reopened = NovaPackage.Open(exported);
        Require(reopened.ProgramName == source.ProgramName && reopened.Pages[0].DurationMilliseconds == 180_000 && reopened.Pages[1].DurationMilliseconds == 60_000, "Video page timing or program changed unexpectedly");
        VerifyCommand(reopened);
    });

    Check("Single-page image and video packages export with updated media", () =>
    {
        var image = Open("picture");
        var video = Open("videoplayback");
        var anotherImage = Open("multipic").Pages[1].MediaPath;
        var anotherVideo = Open("multivid").Pages[0].MediaPath;
        var imageOutput = Stage("single-image", image, PlaybackMode.PlugAndPlay, new Dictionary<int, MediaReplacement> { [0] = new(anotherImage) });
        var videoOutput = Stage("single-video", video, PlaybackMode.PlugAndPlay, new Dictionary<int, MediaReplacement> { [0] = new(anotherVideo) });
        var editedImage = NovaPackage.Open(imageOutput);
        var editedVideo = NovaPackage.Open(videoOutput);
        Require(editedImage.Pages.Single().DurationMilliseconds == 10_000, "Image hold time changed");
        Require(editedVideo.Pages.Single().DurationMilliseconds == 180_000, "Video duration did not follow the replacement");
        Require(editedImage.Mode == PlaybackMode.PlugAndPlay && editedVideo.Mode == PlaybackMode.PlugAndPlay, "Edited packages have the wrong mode");
        VerifyCommand(editedImage);
        VerifyCommand(editedVideo);
    });

    Check("Wrong-type replacement is rejected", () =>
    {
        var image = Open("picture");
        RequireFailure(() => Stage("bad-type", image, image.Mode, new Dictionary<int, MediaReplacement>
        {
            [0] = new(Open("videoplayback").Pages[0].MediaPath),
        }));
    });

    Check("Invalid media bytes are rejected", () =>
    {
        var badImage = Path.Combine(scratch, "bad.png");
        File.WriteAllText(badImage, "not an image");
        RequireFailure(() => MediaInspector.Inspect(badImage));
    });

    Check("Corrupt media invalidates the package", () =>
    {
        var copy = Open("picture");
        var staged = Stage("broken", copy, copy.Mode);
        var media = NovaPackage.Open(staged).Pages[0].MediaPath;
        File.AppendAllText(media, "corrupt");
        RequireFailure(() => NovaPackage.Open(staged));
    });

    Check("A command cannot point outside the programs directory", () =>
    {
        var source = Open("picture");
        var edited = Stage("invalid-path", source, source.Mode);
        var path = Path.Combine(edited, "command.json");
        var document = JsonNode.Parse(Convert.FromBase64String(File.ReadAllText(path)))!;
        document["programInfos"]![0]!["url"] = "..\\outside";
        File.WriteAllText(path, Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(document.ToJsonString())));
        RequireFailure(() => NovaPackage.Open(edited));
    });

    Check("Publish checks overwrite and can restore a local backup", () =>
    {
        var first = Open("picture");
        var second = Open("multivid");
        var stagedFirst = Stage("publish-first", first, first.Mode);
        var stagedSecond = Stage("publish-second", second, PlaybackMode.PlugAndPlay);
        var target = Directory.CreateDirectory(Path.Combine(scratch, "usb-simulation")).FullName;
        var backupRoot = Path.Combine(scratch, "backups");
        File.WriteAllText(Path.Combine(target, "personal.txt"), "keep");
        var original = PackagePublisher.Publish(stagedFirst, target, false, backupRoot);
        Require(original.BackupPath is null, "An empty destination created an unexpected backup");
        RequireFailure(() => PackagePublisher.Publish(stagedSecond, target, false, backupRoot));
        var updated = PackagePublisher.Publish(stagedSecond, target, true, backupRoot);
        Require(Snapshot(updated.NovaPath).SequenceEqual(Snapshot(stagedSecond)), "USB output differs from the prepared folder");
        Require(updated.BackupPath is not null && Snapshot(updated.BackupPath).SequenceEqual(Snapshot(stagedFirst)), "Original backup is missing or differs");
        var restored = PackagePublisher.Restore(updated.BackupPath!, target, backupRoot);
        Require(Snapshot(restored.NovaPath).SequenceEqual(Snapshot(stagedFirst)), "Restore did not recover original files");
        Require(File.ReadAllText(Path.Combine(target, "personal.txt")) == "keep", "Publish modified unrelated files");
    });

    Check("Corrupt staged publish leaves the existing package as-is", () =>
    {
        var first = Open("picture");
        var invalid = Stage("invalid-publish", first, first.Mode);
        File.AppendAllText(NovaPackage.Open(invalid).Pages[0].MediaPath, "corrupt");
        var target = Directory.CreateDirectory(Path.Combine(scratch, "protected-destination")).FullName;
        PackagePublisher.Publish(first.NovaPath, target, false);
        RequireFailure(() => PackagePublisher.Publish(invalid, target, true, Path.Combine(scratch, "backups")));
        Require(Snapshot(Path.Combine(target, "nova")).SequenceEqual(Snapshot(first.NovaPath)), "A failed publish modified the destination");
    });

    Check("Restore can recover a prior package outside the editor profile", () =>
    {
        var target = Directory.CreateDirectory(Path.Combine(scratch, "legacy-destination")).FullName;
        var previous = Directory.CreateDirectory(Path.Combine(target, "nova", "legacy")).FullName;
        File.WriteAllText(Path.Combine(previous, "existing.dat"), "previous installation data");
        var backupRoot = Path.Combine(scratch, "backups");
        var published = PackagePublisher.Publish(Open("picture").NovaPath, target, true, backupRoot);
        Require(published.BackupPath is not null, "The prior directory was not backed up");
        var backupFile = Path.Combine(published.BackupPath!, "legacy", "existing.dat");
        File.WriteAllText(backupFile, "modified backup");
        RequireFailure(() => PackagePublisher.Restore(published.BackupPath!, target, backupRoot));
        Require(NovaPackage.Open(target).ProgramName == "Arta", "A tampered backup modified the destination");
        File.WriteAllText(backupFile, "previous installation data");
        var restored = PackagePublisher.Restore(published.BackupPath!, target, backupRoot);
        Require(File.ReadAllText(Path.Combine(restored.NovaPath, "legacy", "existing.dat")) == "previous installation data", "Restore altered the prior directory");
    });

    if (OperatingSystem.IsWindows())
    {
        Check("A changed USB volume identity prevents write before staging", () =>
        {
            var target = Directory.CreateDirectory(Path.Combine(scratch, "volume-destination")).FullName;
            Require(!string.IsNullOrWhiteSpace(UsbVolume.Identity(Path.GetPathRoot(target)!)), "Windows volume identity was empty");
            RequireFailure(() => PackagePublisher.Publish(Open("picture").NovaPath, target, false, expectedVolumeIdentity: "incorrect-volume"));
            Require(!Directory.EnumerateFileSystemEntries(target).Any(), "A volume mismatch changed the destination");
        });
    }
}
finally
{
    Directory.Delete(scratch, recursive: true);
}

return failures == 0 ? 0 : 1;

NovaPackage Open(string name) => NovaPackage.Open(Path.Combine(reference, name));

string Stage(string name, NovaPackage source, PlaybackMode mode, IReadOnlyDictionary<int, MediaReplacement>? replacements = null)
{
    var path = Path.Combine(scratch, name, "nova");
    PackageBuilder.Build(source, path, mode, replacements ?? new Dictionary<int, MediaReplacement>());
    return path;
}

void Check(string label, Action action)
{
    try
    {
        action();
        Console.WriteLine($"PASS {label}");
    }
    catch (Exception error)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {label}: {error.Message}");
    }
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void RequireFailure(Action action)
{
    try
    {
        action();
    }
    catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException or UnauthorizedAccessException or FormatException)
    {
        return;
    }

    throw new InvalidOperationException("Expected the operation to fail with an input or filesystem error.");
}

static JsonNode Command(string root) =>
    JsonNode.Parse(Convert.FromBase64String(File.ReadAllText(Path.Combine(root, "command.json"))))!["programInfos"]![0]!;

static void VerifyCommand(NovaPackage package)
{
    var program = Path.Combine(package.NovaPath, "program", package.ProgramName);
    var playlist = File.ReadAllBytes(Path.Combine(program, "playlist0.json"));
    var plan = JsonNode.Parse(File.ReadAllText(Path.Combine(program, "planlist.json")))!;
    var resources = plan["resources"]!.AsArray();
    var command = Command(package.NovaPath);
    Require(command["identifier"]!.GetValue<string>() == Convert.ToHexStringLower(MD5.HashData(playlist)), "Updated playlist identifier is inconsistent");
    var bytes = resources.Sum(entry => entry!["size"]!.GetValue<long>());
    Require(command["size"]!.GetValue<long>() == bytes + 1024 * (5 + resources.Count), "Updated command size is inconsistent");
}

static IEnumerable<(string Path, string Fingerprint)> Snapshot(string root)
{
    foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        var relative = Path.GetRelativePath(root, path);
        if (Directory.Exists(path))
        {
            yield return (relative, "directory");
            continue;
        }

        using var input = File.OpenRead(path);
        yield return (relative, Convert.ToHexString(SHA256.HashData(input)));
    }
}
