using System.IO.Compression;
using System.Text.Json;

namespace WebLaunch.Core;

/// <summary>Version metadata participates in the same recoverable transaction as replaced files.</summary>
/// <summary>Describes the version-marker transition that must commit or roll back with installed files.</summary>
/// <param name="PreviousVersion">Previously durable version value, or null when no value existed.</param>
/// <param name="Commit">Persists the target version after the replacement files have been written.</param>
/// <param name="Restore">Restores the previous durable version during rollback or interrupted-transaction recovery.</param>
public sealed record VersionMetadataTransaction(string? PreviousVersion, Action Commit, Action<string?> Restore);

/// <summary>Stages a ZIP, journals replacements, and recovers interrupted installs before another update.</summary>
public sealed class ArchiveInstaller
{
    private sealed record Change(string Relative, bool Existed);
    private sealed class Journal
    {
        public List<Change> Changes { get; set; } = [];
        public bool Committed { get; set; }
        public bool HasVersionMetadata { get; set; }
        public string? PreviousVersion { get; set; }
    }
    private const string TransactionName = ".weblaunch-transaction";
    /// <summary>Stages and validates an archive, journals file replacement, and rolls back if commit fails.</summary>
    /// <param name="archive">Path to a fully downloaded archive.</param>
    /// <param name="root">Installation root to which validated entries are contained.</param>
    /// <param name="token">Cancellation token checked during staging and replacement.</param>
    /// <param name="commitVersion">Optional legacy metadata callback. Callers needing durable rollback should use the transaction overload.</param>
    /// <returns>A task that completes only after the transaction has committed or rollback has completed.</returns>
    public Task InstallAsync(string archive, string root, CancellationToken token, Action? commitVersion = null) =>
        InstallCoreAsync(archive, root, token, commitVersion, versionMetadata: null);

    /// <summary>Installs an archive with metadata included in the recoverable transaction.</summary>
    /// <param name="archive">Path to a fully downloaded archive.</param>
    /// <param name="root">Installation root to which validated entries are contained.</param>
    /// <param name="token">Cancellation token checked during staging and replacement.</param>
    /// <param name="versionMetadata">Commit/restore callbacks and old version value persisted in the journal.</param>
    /// <returns>A task that completes only after files and version metadata have committed.</returns>
    public Task InstallAsync(string archive, string root, CancellationToken token, VersionMetadataTransaction versionMetadata) =>
        InstallCoreAsync(archive, root, token, commitVersion: null,
            versionMetadata ?? throw new ArgumentNullException(nameof(versionMetadata)));

    private async Task InstallCoreAsync(
        string archive,
        string root,
        CancellationToken token,
        Action? commitVersion,
        VersionMetadataTransaction? versionMetadata)
    {
        root = Path.GetFullPath(root);
        SafePath.RejectLinks(root);
        Directory.CreateDirectory(root);
        using var installLock = new FileStream(SafePath.Resolve(root, ".weblaunch-install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Recover(root, versionMetadata?.Restore);
        var transaction = SafePath.Resolve(root, TransactionName);
        Directory.CreateDirectory(transaction);
        var stage = Path.Combine(transaction, "stage");
        var backup = Path.Combine(transaction, "backup");
        Directory.CreateDirectory(stage); Directory.CreateDirectory(backup);
        var journal = new Journal
        {
            HasVersionMetadata = versionMetadata is not null,
            PreviousVersion = versionMetadata?.PreviousVersion
        };
        var commitMarkerPersisted = false;
        var metadataCommitStarted = false;
        var rollbackCompleted = false;
        try
        {
            Save(transaction, journal);
            using var zip = ZipFile.OpenRead(archive);
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            if (zip.Entries.Count > 100_000) throw new InvalidDataException("Archive contains too many entries.");
            // Validate every path before extracting or modifying any game file.
            foreach (var entry in zip.Entries)
            {
                var relative = entry.FullName.TrimEnd('/', '\\');
                if (relative.Length == 0 || relative.StartsWith(".weblaunch-", StringComparison.OrdinalIgnoreCase) ||
                    ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                    ((FileAttributes)entry.ExternalAttributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Unsupported archive entry.");
                SafePath.Resolve(root, relative);
                if (!targets.Add(relative.Replace('\\', '/'))) throw new InvalidDataException("Duplicate archive path.");
                total = checked(total + entry.Length);
                if (total > 250L * 1024 * 1024 * 1024) throw new InvalidDataException("Archive is too large.");
            }
            if (new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace < total * 2)
                throw new IOException("Insufficient disk space for staging and recovery.");
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                var relative = entry.FullName.TrimEnd('/', '\\');
                var staged = SafePath.Resolve(stage, relative);
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(staged); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                await using var input = entry.Open();
                await using var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, token);
                if (output.Length != entry.Length) throw new InvalidDataException("Archive entry length mismatch.");
            }
            foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\')))
            {
                token.ThrowIfCancellationRequested();
                var target = SafePath.Resolve(root, entry.FullName);
                var staged = SafePath.Resolve(stage, entry.FullName);
                var saved = SafePath.Resolve(backup, entry.FullName);
                var existed = File.Exists(target);
                journal.Changes.Add(new(entry.FullName, existed));
                Save(transaction, journal);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (existed) { Directory.CreateDirectory(Path.GetDirectoryName(saved)!); File.Move(target, saved); }
                File.Move(staged, target);
            }
            token.ThrowIfCancellationRequested();

            // The journal stays uncommitted until both file replacement and metadata persistence finish.
            // If metadata write fails after a partial write, the previous value remains recoverable.
            if (versionMetadata is not null)
            {
                metadataCommitStarted = true;
                versionMetadata.Commit();
            }
            else
            {
                commitVersion?.Invoke();
            }

            journal.Committed = true;
            Save(transaction, journal);
            commitMarkerPersisted = true;
        }
        catch
        {
            if (!commitMarkerPersisted)
            {
                try
                {
                    if (journal.HasVersionMetadata && metadataCommitStarted)
                    {
                        if (versionMetadata is null)
                            throw new IOException("Interrupted version commit requires updater recovery.");
                        versionMetadata.Restore(journal.PreviousVersion);
                    }

                    journal.Committed = false;
                    Rollback(root, transaction, journal);
                    rollbackCompleted = true;
                }
                catch
                {
                    // Retain the uncommitted journal and backups; never discard the only recovery data.
                    throw;
                }
            }
            throw;
        }
        finally
        {
            if (commitMarkerPersisted || rollbackCompleted ||
                (journal.Changes.Count == 0 && !metadataCommitStarted))
                Directory.Delete(transaction, true);
        }
    }
    private static void Save(string directory, Journal journal)
    {
        var file = Path.Combine(directory, "journal.json");
        using (var stream = new FileStream(file + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, journal); stream.Flush(true); }
        File.Move(file + ".tmp", file, true);
    }
    /// <summary>Recovers a prior interrupted install before callers decide whether offline launch is safe.</summary>
    /// <param name="root">Installation root that owns the transaction journal.</param>
    /// <param name="restoreVersion">Restores version metadata recorded by an interrupted transaction.</param>
    /// <returns>True when a pending transaction was recovered; false when no transaction existed.</returns>
    public bool RecoverPendingTransaction(string root, Action<string?>? restoreVersion = null)
    {
        root = Path.GetFullPath(root);
        SafePath.RejectLinks(root);
        Directory.CreateDirectory(root);
        using var installLock = new FileStream(
            SafePath.Resolve(root, ".weblaunch-install.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var transaction = SafePath.Resolve(root, TransactionName);
        if (!TransactionDirectoryExists(transaction)) return false;
        SafePath.RejectLinks(transaction);
        Recover(root, restoreVersion);
        return true;
    }

    private static bool TransactionDirectoryExists(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0)
                throw new IOException("Update transaction path is not a directory.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void Recover(string root, Action<string?>? restoreVersion)
    {
        var transaction = SafePath.Resolve(root, TransactionName);
        if (!TransactionDirectoryExists(transaction)) return;
        SafePath.RejectLinks(transaction);
        var file = SafePath.Resolve(transaction, "journal.json");
        if (!File.Exists(file)) throw new IOException("Incomplete update staging requires inspection before retrying.");
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(file)) ?? throw new InvalidDataException("Invalid recovery journal.");
        if (!journal.Committed)
        {
            if (journal.HasVersionMetadata)
            {
                if (restoreVersion is null)
                    throw new IOException("Interrupted version commit requires updater recovery.");
                restoreVersion(journal.PreviousVersion);
            }
            Rollback(root, transaction, journal);
        }
        Directory.Delete(transaction, true);
    }
    private static void Rollback(string root, string transaction, Journal journal)
    {
        foreach (var change in journal.Changes.AsEnumerable().Reverse())
        {
            var target = SafePath.Resolve(root, change.Relative);
            var backup = SafePath.Resolve(Path.Combine(transaction, "backup"), change.Relative);
            var staged = SafePath.Resolve(Path.Combine(transaction, "stage"), change.Relative);
            if (File.Exists(backup)) File.Move(backup, target, true);
            else if (!change.Existed && !File.Exists(staged)) File.Delete(target);
        }
        journal.Changes.Clear();
        Save(transaction, journal);
    }
}
