using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ViPlex.Core;

public sealed record PublishResult(string NovaPath, string? BackupPath, string? PreviousPath = null);

public static class PackagePublisher
{
    private const string SnapshotFileName = "snapshot.json";

    public static PublishResult Publish(string stagedNovaPath, string destinationRoot, bool overwrite, string? backupRoot = null, string? expectedVolumeIdentity = null)
    {
        return Install(NovaPackage.Open(stagedNovaPath).NovaPath, destinationRoot, overwrite, backupRoot, backupSnapshot: null, expectedVolumeIdentity);
    }

    public static PublishResult Restore(string backupNovaPath, string destinationRoot, string backupRoot, string? expectedVolumeIdentity = null)
    {
        var selected = PackageFiles.NormalizePath(backupNovaPath);
        var nova = Path.GetFileName(selected).Equals("nova", StringComparison.OrdinalIgnoreCase)
            ? selected
            : Path.Combine(selected, "nova");
        var snapshotPath = Path.Combine(Path.GetDirectoryName(nova)!, SnapshotFileName);
        PackageFiles.CheckAncestors(snapshotPath);
        if (!File.Exists(snapshotPath))
            throw new InvalidDataException("Select a backup created by the editor. Its nova folder and snapshot.json must be together.");

        TreeEntry[] snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<TreeEntry[]>(File.ReadAllBytes(snapshotPath))
                ?? throw new InvalidDataException("The backup snapshot is missing its file list.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The backup snapshot is invalid.");
        }

        return Install(nova, destinationRoot, overwrite: true, backupRoot, snapshot, expectedVolumeIdentity);
    }

    private static PublishResult Install(string source, string destinationRoot, bool overwrite, string? backupRoot, TreeEntry[]? backupSnapshot, string? expectedVolumeIdentity)
    {
        var root = PackageFiles.NormalizePath(destinationRoot);
        RequirePlainDirectory(root);
        var target = Path.Combine(root, "nova");
        if (PackageFiles.Contains(source, target) || PackageFiles.Contains(target, source))
            throw new IOException("Choose a destination outside the prepared package.");

        var lockName = "ViPlexWrapper." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())));
        using var destinationLock = new Mutex(false, lockName);
        try
        {
            if (!destinationLock.WaitOne(0))
                throw new IOException("Another write to this destination is in progress.");
        }
        catch (AbandonedMutexException)
        {
            // The lock now belongs to this process. The incoming package is validated again.
        }

        try
        {
            return InstallWhileLocked(source, root, target, overwrite, backupRoot, backupSnapshot, expectedVolumeIdentity);
        }
        finally
        {
            destinationLock.ReleaseMutex();
        }
    }

    private static PublishResult InstallWhileLocked(string source, string root, string target, bool overwrite, string? backupRoot, TreeEntry[]? backupSnapshot, string? expectedVolumeIdentity)
    {
        VerifyVolume(root, expectedVolumeIdentity);
        var existing = Directory.Exists(target);
        if (!existing && File.Exists(target))
            throw new IOException("A file named nova already exists at the destination.");
        if (existing && !overwrite)
            throw new IOException("The destination already has a nova folder. Confirm replacement first.");
        if (existing)
            RequirePlainDirectory(target);

        var sourceTree = ReadTree(source);
        if (backupSnapshot is not null && !sourceTree.SequenceEqual(backupSnapshot))
            throw new InvalidDataException("The backup files do not match their saved snapshot.");
        var volume = new DriveInfo(Path.GetPathRoot(root)!);
        if (volume.AvailableFreeSpace < sourceTree.Where(entry => entry.Fingerprint is not null).Sum(entry => entry.Length))
            throw new IOException("The destination does not have enough free space for the prepared package.");

        var work = Path.Combine(root, $".viplex-write-{Guid.NewGuid():N}");
        var incoming = Path.Combine(work, "nova");
        var previous = Path.Combine(work, "previous-nova");
        string? backup = null;
        var movedPrevious = false;
        var installed = false;
        var retainWork = false;

        try
        {
            VerifyVolume(root, expectedVolumeIdentity);
            CopyTree(source, incoming, sourceTree);
            VerifyCopy(incoming, sourceTree);
            if (backupSnapshot is null)
                NovaPackage.Open(incoming);

            if (existing)
            {
                var actualBackupRoot = PackageFiles.NormalizePath(backupRoot ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ViPlex T10", "Backups"));
                if (PackageFiles.Contains(target, actualBackupRoot) || PackageFiles.Contains(work, actualBackupRoot))
                    throw new IOException("The backup location must be outside the existing package and staging folder.");
                if (expectedVolumeIdentity is not null && string.Equals(Path.GetPathRoot(root), Path.GetPathRoot(actualBackupRoot), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Choose a backup location off the USB drive.");
                VerifyVolume(root, expectedVolumeIdentity);
                var original = ReadTree(target);
                backup = SaveBackup(target, actualBackupRoot, original);
                VerifyCopy(target, original);
                VerifyVolume(root, expectedVolumeIdentity);
                Directory.Move(target, previous);
                movedPrevious = true;
            }

            VerifyVolume(root, expectedVolumeIdentity);
            Directory.Move(incoming, target);
            installed = true;
            VerifyCopy(target, sourceTree);
            if (backupSnapshot is null)
                NovaPackage.Open(target);
            VerifyVolume(root, expectedVolumeIdentity);

            if (movedPrevious && !TryDelete(previous))
            {
                retainWork = true;
                return new PublishResult(target, backup, previous);
            }

            return new PublishResult(target, backup);
        }
        catch (Exception failure)
        {
            try
            {
                if (installed || movedPrevious)
                    VerifyVolume(root, expectedVolumeIdentity);
                if (installed)
                    Directory.Move(target, Path.Combine(work, "failed-nova"));
                if (movedPrevious)
                    Directory.Move(previous, target);
            }
            catch (Exception rollbackFailure)
            {
                retainWork = true;
                throw new IOException($"The package write was interrupted and automatic rollback failed. Backup: {backup ?? "none"}. Recovery folder: {work}.",
                    new AggregateException(failure, rollbackFailure));
            }

            var recovery = backup is null ? "" : $" The prior package is also backed up at {backup}.";
            throw new IOException($"The package could not be written. {failure.Message}{recovery}", failure);
        }
        finally
        {
            if (!retainWork && SameVolume(root, expectedVolumeIdentity))
                TryDelete(work);
        }
    }

    private static string SaveBackup(string source, string backupRoot, TreeEntry[] original)
    {
        PackageFiles.CheckAncestors(backupRoot);
        Directory.CreateDirectory(backupRoot);
        RequirePlainDirectory(backupRoot);
        var backupFolder = Path.Combine(backupRoot, $"{DateTime.Now:yyyy-MM-dd_HHmmss}-{Guid.NewGuid():N}");
        var backupNova = Path.Combine(backupFolder, "nova");
        try
        {
            CopyTree(source, backupNova, original);
            VerifyCopy(backupNova, original);
            File.WriteAllBytes(Path.Combine(backupFolder, SnapshotFileName), JsonSerializer.SerializeToUtf8Bytes(original));
            return backupNova;
        }
        catch
        {
            TryDelete(backupFolder);
            throw;
        }
    }

    private static void CopyTree(string source, string destination, IEnumerable<TreeEntry> entries)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in entries)
        {
            var sourceEntry = Path.Combine(source, entry.RelativePath);
            var destinationEntry = Path.Combine(destination, entry.RelativePath);
            if (entry.Fingerprint is null)
            {
                RequirePlainDirectory(sourceEntry);
                Directory.CreateDirectory(destinationEntry);
            }
            else
            {
                if (File.GetAttributes(sourceEntry).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("The package uses a filesystem link. Open a local copy of the export.");
                File.Copy(sourceEntry, destinationEntry, overwrite: false);
            }
        }
    }

    private static void VerifyCopy(string path, TreeEntry[] expected)
    {
        if (!ReadTree(path).SequenceEqual(expected))
            throw new IOException("The copied files did not match the prepared package.");
    }

    private static TreeEntry[] ReadTree(string root)
    {
        RequirePlainDirectory(root);
        var entries = new List<TreeEntry>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("The package uses a filesystem link. Open a local copy of the export.");

                var relative = Path.GetRelativePath(root, path);
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    entries.Add(new TreeEntry(relative, 0, null));
                    pending.Push(path);
                }
                else
                {
                    using var input = File.OpenRead(path);
                    entries.Add(new TreeEntry(relative, input.Length, Convert.ToHexString(SHA256.HashData(input))));
                }
            }
        }

        return entries.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static void RequirePlainDirectory(string path)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Folder unavailable: {path}");

        PackageFiles.CheckAncestors(path);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception error) when (IsFileFailure(error))
        {
            return false;
        }
    }

    private static bool IsFileFailure(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException;

    private static void VerifyVolume(string root, string? expectedVolumeIdentity)
    {
        if (!SameVolume(root, expectedVolumeIdentity))
            throw new IOException("The selected USB drive changed or was removed. Select the drive again.");
    }

    private static bool SameVolume(string root, string? expectedVolumeIdentity)
    {
        try
        {
            return expectedVolumeIdentity is null || string.Equals(UsbVolume.Identity(root), expectedVolumeIdentity, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (IsFileFailure(error))
        {
            return false;
        }
    }

    private sealed record TreeEntry(string RelativePath, long Length, string? Fingerprint);
}
