using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class FileStorage
{
    private const int MaxRelativePathLength = 1024;
    private const int MaxFileNameLength = 255;
    private const string MetadataDirectoryName = ".skyvault";
    private const string BlobDirectoryName = "blobs";
    private const string MetadataFileName = "metadata.json";
    private static readonly TimeSpan TrashRetention = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly IReadOnlyList<string> rootPaths;
    private readonly string storageMode;
    private readonly UserStore userStore;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> userFileLocks = new(StringComparer.OrdinalIgnoreCase);

    public FileStorage(string rootPath, UserStore userStore)
        : this([rootPath], "single", userStore)
    {
    }

    public FileStorage(IReadOnlyList<string> rootPaths, string storageMode, UserStore userStore)
    {
        this.rootPaths = rootPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .DefaultIfEmpty(Path.Combine("data", "files"))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        this.storageMode = NormalizeStorageMode(storageMode);
        this.userStore = userStore;

        foreach (string rootPath in this.rootPaths)
        {
            Directory.CreateDirectory(rootPath);
        }
    }

    public IReadOnlyList<FileEntry> GetFiles(string? username)
    {
        if (username is null)
        {
            return [];
        }

        username = NormalizeEmail(username);
        Dictionary<string, FileEntry> entries = new(StringComparer.Ordinal);

        foreach (StorageMetadataEntry entry in LoadMergedMetadata(username).Entries.Where(entry => !entry.InTrash))
        {
            string entryKey = GetEntryKey(entry);
            entries[entryKey] = ToFileEntry(entry);

            // Parent folders for encrypted entries are reconstructed client-side after decrypting paths.
        }

        return entries.Values
            .OrderByDescending(entry => entry.ModifiedAt)
            .ThenBy(entry => entry.Name)
            .ToList();
    }

    public IReadOnlyList<FileEntry> GetTrashFiles(string? username)
    {
        if (username is null)
        {
            return [];
        }

        username = NormalizeEmail(username);
        PurgeExpiredTrash(username);

        return LoadMergedMetadata(username).Entries
            .Where(entry => entry.InTrash)
            .OrderByDescending(entry => entry.TrashedAt ?? entry.ModifiedAt)
            .ThenBy(entry => entry.PathHash)
            .Select(ToFileEntry)
            .ToList();
    }

    public void CreateUserDirectory(string username)
    {
        foreach (string directory in GetUserDirectories(NormalizeEmail(username)))
        {
            EnsureVaultDirectories(directory);
        }
    }

    public async Task<FileCreateResult> CreateFolderAsync(
        string username,
        string folderName,
        string encryptedVirtualPath = "",
        string virtualPathIv = "",
        string pathHash = "")
    {
        username = NormalizeEmail(username);
        await using SemaphoreLease lease = await AcquireUserFileLockAsync(username);
        string relativePath = NormalizeRelativePath(folderName);
        bool hasEncryptedPath = !string.IsNullOrWhiteSpace(encryptedVirtualPath)
            && !string.IsNullOrWhiteSpace(virtualPathIv)
            && !string.IsNullOrWhiteSpace(pathHash);

        if (!hasEncryptedPath || !IsValidRelativePath(relativePath))
        {
            return FileCreateResult.InvalidFileName;
        }

        if (userStore.GetUser(username) is null)
        {
            return FileCreateResult.Failed;
        }

        if (FindEntriesByPathHash(username, pathHash.Trim()).Any(item => !item.Entry.InTrash))
        {
            return FileCreateResult.Failed;
        }

        foreach (string writeDirectory in GetWriteDirectories(username, relativePath))
        {
            StorageMetadata metadata = LoadMetadata(writeDirectory);
            metadata.Entries.Add(new StorageMetadataEntry
            {
                EncryptedVirtualPath = encryptedVirtualPath.Trim(),
                VirtualPathIv = virtualPathIv.Trim(),
                PathHash = pathHash.Trim(),
                BlobId = "",
                SizeBytes = 0,
                StoredSizeBytes = 0,
                ModifiedAt = DateTimeOffset.UtcNow,
                IsFolder = true
            });
            SaveMetadata(writeDirectory, metadata);
        }

        return FileCreateResult.Created;
    }

    public async Task<(FileCreateResult Result, string SavedPath)> SaveUploadedTempFileAsync(
        string username,
        string requestedPath,
        string tempPath,
        long length,
        string encryptedVirtualPath = "",
        string virtualPathIv = "",
        string pathHash = "")
    {
        username = NormalizeEmail(username);
        await using SemaphoreLease lease = await AcquireUserFileLockAsync(username);

        if (!IsSafeUploadPath(requestedPath))
        {
            return (FileCreateResult.InvalidFileName, "");
        }

        string relativePath = NormalizeRelativePath(requestedPath);

        if (!IsValidRelativePath(relativePath))
        {
            return (FileCreateResult.InvalidFileName, "");
        }

        UserAccount? user = userStore.GetUser(username);

        if (user is null || !File.Exists(tempPath))
        {
            return (FileCreateResult.Failed, "");
        }

        bool hasEncryptedPath = !string.IsNullOrWhiteSpace(encryptedVirtualPath)
            && !string.IsNullOrWhiteSpace(virtualPathIv)
            && !string.IsNullOrWhiteSpace(pathHash);

        if (!hasEncryptedPath)
        {
            return (FileCreateResult.InvalidFileName, "");
        }

        string targetRelativePath = pathHash.Trim();
        string targetPathHash = pathHash.Trim();
        long currentUsed = GetUsedBytes(username);
        long nextUsed = currentUsed + length;

        if (nextUsed > user.QuotaBytes)
        {
            return (FileCreateResult.QuotaExceeded, "");
        }

        IReadOnlyList<string> writeDirectories = GetWriteDirectories(username, targetRelativePath);
        string blobId = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string primaryBlobPath = GetBlobPath(writeDirectories[0], blobId);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(primaryBlobPath) ?? writeDirectories[0]);
            File.Move(tempPath, primaryBlobPath);

            foreach (string writeDirectory in writeDirectories.Skip(1))
            {
                string writeBlobPath = GetBlobPath(writeDirectory, blobId);
                Directory.CreateDirectory(Path.GetDirectoryName(writeBlobPath) ?? writeDirectory);
                File.Copy(primaryBlobPath, writeBlobPath, overwrite: false);
            }

            foreach (string writeDirectory in writeDirectories)
            {
                StorageMetadata metadata = LoadMetadata(writeDirectory);
                metadata.Entries.RemoveAll(entry => !entry.InTrash && string.Equals(entry.PathHash, targetPathHash, StringComparison.Ordinal));
                metadata.Entries.Add(new StorageMetadataEntry
                {
                    EncryptedVirtualPath = encryptedVirtualPath.Trim(),
                    VirtualPathIv = virtualPathIv.Trim(),
                    PathHash = targetPathHash,
                    BlobId = blobId,
                    SizeBytes = length,
                    StoredSizeBytes = length,
                    ModifiedAt = now,
                    IsFolder = false
                });
                SaveMetadata(writeDirectory, metadata);
            }

            await userStore.SetUsedBytesAsync(username, GetUsedBytes(username));
            return (FileCreateResult.Created, targetPathHash);
        }
        catch
        {
            DeleteFileQuietly(primaryBlobPath);

            foreach (string writeDirectory in writeDirectories.Skip(1))
            {
                DeleteFileQuietly(GetBlobPath(writeDirectory, blobId));
            }

            return (FileCreateResult.Failed, "");
        }
    }

    public long GetRemainingQuotaBytes(string username)
    {
        username = NormalizeEmail(username);
        UserAccount? user = userStore.GetUser(username);
        return user is null ? 0 : Math.Max(0, user.QuotaBytes - GetUsedBytes(username));
    }

    public async Task<byte[]?> ReadFileAsync(string username, string fileName)
    {
        FileDownload? download = await OpenReadStreamAsync(username, fileName);

        if (download is null)
        {
            return null;
        }

        await using Stream stream = download.Stream;
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }

    public Task<FileDownload?> OpenReadStreamAsync(string username, string fileName)
    {
        username = NormalizeEmail(username);
        string relativePath = NormalizeRelativePath(fileName);

        if (!IsValidRelativePath(relativePath))
        {
            return Task.FromResult<FileDownload?>(null);
        }

        foreach ((string directory, StorageMetadataEntry entry) in FindEntries(username, relativePath).Where(item => !item.Entry.IsFolder && !item.Entry.InTrash))
        {
            string blobPath = GetBlobPath(directory, entry.BlobId);

            if (!File.Exists(blobPath))
            {
                continue;
            }

            Stream stream = new FileStream(blobPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Task.FromResult<FileDownload?>(new FileDownload(relativePath, new FileInfo(blobPath).Length, entry.ModifiedAt.UtcDateTime, stream));
        }

        return Task.FromResult<FileDownload?>(null);
    }

    public Task<FileDownload?> OpenReadStreamByPathHashAsync(string username, string pathHash)
    {
        username = NormalizeEmail(username);
        pathHash = pathHash.Trim();

        if (string.IsNullOrWhiteSpace(pathHash))
        {
            return Task.FromResult<FileDownload?>(null);
        }

        foreach ((string directory, StorageMetadataEntry entry) in FindEntriesByPathHash(username, pathHash).Where(item => !item.Entry.IsFolder && !item.Entry.InTrash))
        {
            string blobPath = GetBlobPath(directory, entry.BlobId);

            if (!File.Exists(blobPath))
            {
                continue;
            }

            Stream stream = new FileStream(blobPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Task.FromResult<FileDownload?>(new FileDownload(pathHash, new FileInfo(blobPath).Length, entry.ModifiedAt.UtcDateTime, stream));
        }

        return Task.FromResult<FileDownload?>(null);
    }

    public async Task<byte[]?> CreateZipAsync(string username, IReadOnlyList<string> fileNames)
    {
        username = NormalizeEmail(username);
        List<string> validFileNames = fileNames
            .Select(NormalizeRelativePath)
            .Where(IsValidRelativePath)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (validFileNames.Count == 0)
        {
            return null;
        }

        await using var zipBytes = new MemoryStream();

        using (var archive = new ZipArchive(zipBytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string fileName in validFileNames)
            {
                byte[]? content = await ReadFileAsync(username, fileName);

                if (content is null)
                {
                    continue;
                }

                ZipArchiveEntry entry = archive.CreateEntry(fileName, CompressionLevel.Fastest);
                await using Stream entryStream = entry.Open();
                await entryStream.WriteAsync(content);
            }
        }

        return zipBytes.Length == 0 ? null : zipBytes.ToArray();
    }

    public long GetTotalSizeBytes(string username, IReadOnlyList<string> fileNames)
    {
        username = NormalizeEmail(username);
        return fileNames
            .Select(NormalizeRelativePath)
            .Where(IsValidRelativePath)
            .Distinct(StringComparer.Ordinal)
            .Select(path => LoadMergedMetadata(username).Entries.FirstOrDefault(entry => !entry.InTrash && !entry.IsFolder && entry.VirtualPath == path)?.StoredSizeBytes ?? 0)
            .Sum();
    }

    public async Task<int> MoveToTrashAsync(string username, IReadOnlyList<string> fileNames)
    {
        username = NormalizeEmail(username);
        await using SemaphoreLease lease = await AcquireUserFileLockAsync(username);
        PurgeExpiredTrash(username);
        int moved = 0;
        foreach (string selector in fileNames.Select(NormalizeRelativePath).Where(IsValidRelativePath).Distinct(StringComparer.Ordinal))
        {
            bool movedAnyReplica = false;

            foreach ((string directory, StorageMetadataEntry entry) in FindEntriesBySelector(username, selector).Where(item => !item.Entry.InTrash))
            {
                StorageMetadata metadata = LoadMetadata(directory);
                StorageMetadataEntry? writable = metadata.Entries.FirstOrDefault(item => item.Id == entry.Id);

                if (writable is null)
                {
                    continue;
                }

                writable.InTrash = true;
                writable.TrashedAt = DateTimeOffset.UtcNow;
                writable.TrashPath = null;
                SaveMetadata(directory, metadata);
                movedAnyReplica = true;
            }

            if (movedAnyReplica)
            {
                moved += 1;
            }
        }

        await userStore.SetUsedBytesAsync(username, GetUsedBytes(username));
        return moved;
    }

    public async Task<int> EmptyTrashAsync(string username)
    {
        username = NormalizeEmail(username);
        await using SemaphoreLease lease = await AcquireUserFileLockAsync(username);
        PurgeExpiredTrash(username);
        int deleted = 0;

        foreach (string directory in GetUserDirectories(username))
        {
            StorageMetadata metadata = LoadMetadata(directory);
            List<StorageMetadataEntry> trash = metadata.Entries.Where(entry => entry.InTrash).ToList();

            foreach (StorageMetadataEntry entry in trash)
            {
                if (!entry.IsFolder)
                {
                    DeleteFileQuietly(GetBlobPath(directory, entry.BlobId));
                }

                metadata.Entries.Remove(entry);
                deleted += 1;
            }

            SaveMetadata(directory, metadata);
        }

        await userStore.SetUsedBytesAsync(username, GetUsedBytes(username));
        return deleted;
    }

    public async Task<(int Moved, int SkippedExisting, int Missing)> RestoreFromTrashAsync(
        string username,
        IReadOnlyList<string> fileNames,
        bool overwriteExisting = false)
    {
        username = NormalizeEmail(username);
        await using SemaphoreLease lease = await AcquireUserFileLockAsync(username);
        PurgeExpiredTrash(username);
        int restored = 0;
        int skippedBecauseExists = 0;
        int missingFromTrash = 0;

        foreach (string selector in fileNames.Select(NormalizeRelativePath).Where(IsValidRelativePath).Distinct(StringComparer.Ordinal))
        {
            bool restoredAnyReplica = false;

            foreach (string directory in GetUserDirectories(username))
            {
                StorageMetadata metadata = LoadMetadata(directory);
                StorageMetadataEntry? entry = metadata.Entries.FirstOrDefault(item => item.InTrash && MatchesSelector(item, selector));

                if (entry is null)
                {
                    continue;
                }

                entry.InTrash = false;
                entry.TrashPath = null;
                entry.TrashedAt = null;
                entry.ModifiedAt = DateTimeOffset.UtcNow;
                SaveMetadata(directory, metadata);
                restoredAnyReplica = true;
            }

            if (restoredAnyReplica)
            {
                restored += 1;
            }
            else
            {
                missingFromTrash += 1;
            }
        }

        await userStore.SetUsedBytesAsync(username, GetUsedBytes(username));
        return (restored, skippedBecauseExists, missingFromTrash);
    }

    public async Task<int> DeleteFromTrashAsync(string username, IReadOnlyList<string> fileNames)
    {
        username = NormalizeEmail(username);
        await using SemaphoreLease lease = await AcquireUserFileLockAsync(username);
        PurgeExpiredTrash(username);
        int deleted = 0;

        foreach (string selector in fileNames.Select(NormalizeRelativePath).Where(IsValidRelativePath).Distinct(StringComparer.Ordinal))
        {
            bool deletedAnyReplica = false;

            foreach (string directory in GetUserDirectories(username))
            {
                StorageMetadata metadata = LoadMetadata(directory);
                StorageMetadataEntry? entry = metadata.Entries.FirstOrDefault(item => item.InTrash && MatchesSelector(item, selector));

                if (entry is null)
                {
                    continue;
                }

                if (!entry.IsFolder)
                {
                    DeleteFileQuietly(GetBlobPath(directory, entry.BlobId));
                }

                metadata.Entries.Remove(entry);
                SaveMetadata(directory, metadata);
                deletedAnyReplica = true;
            }

            if (deletedAnyReplica)
            {
                deleted += 1;
            }
        }

        await userStore.SetUsedBytesAsync(username, GetUsedBytes(username));
        return deleted;
    }

    public async Task<(int Updated, int Conflicts, int Missing)> UpdateEncryptedPathsAsync(string username, IReadOnlyList<EncryptedPathUpdate> updates)
    {
        username = NormalizeEmail(username);
        await using SemaphoreLease lease = await AcquireUserFileLockAsync(username);
        int updated = 0;
        int conflicts = 0;
        int missing = 0;

        foreach (EncryptedPathUpdate update in updates
            .Where(update => IsValidPathHash(update.SourcePathHash)
                && IsValidPathHash(update.NewPathHash)
                && !string.IsNullOrWhiteSpace(update.EncryptedVirtualPath)
                && !string.IsNullOrWhiteSpace(update.VirtualPathIv))
            .DistinctBy(update => update.SourcePathHash))
        {
            bool conflict = LoadMergedMetadata(username).Entries.Any(entry =>
                !entry.InTrash
                && !string.Equals(entry.PathHash, update.SourcePathHash, StringComparison.Ordinal)
                && string.Equals(entry.PathHash, update.NewPathHash, StringComparison.Ordinal));

            if (conflict)
            {
                conflicts += 1;
                continue;
            }

            bool updatedAnyReplica = false;

            foreach (string directory in GetUserDirectories(username))
            {
                StorageMetadata metadata = LoadMetadata(directory);
                StorageMetadataEntry? entry = metadata.Entries.FirstOrDefault(item =>
                    !item.InTrash && string.Equals(item.PathHash, update.SourcePathHash, StringComparison.Ordinal));

                if (entry is null)
                {
                    continue;
                }

                entry.EncryptedVirtualPath = update.EncryptedVirtualPath.Trim();
                entry.VirtualPathIv = update.VirtualPathIv.Trim();
                entry.PathHash = update.NewPathHash.Trim();
                entry.ModifiedAt = DateTimeOffset.UtcNow;
                SaveMetadata(directory, metadata);
                updatedAnyReplica = true;
            }

            if (updatedAnyReplica)
            {
                updated += 1;
            }
            else
            {
                missing += 1;
            }
        }

        return (updated, conflicts, missing);
    }

    public async Task<(int Copied, int Conflicts, int Missing)> CopyEncryptedEntriesAsync(string username, IReadOnlyList<EncryptedPathUpdate> updates)
    {
        username = NormalizeEmail(username);
        await using SemaphoreLease lease = await AcquireUserFileLockAsync(username);
        int copied = 0;
        int conflicts = 0;
        int missing = 0;

        foreach (EncryptedPathUpdate update in updates
            .Where(update => IsValidPathHash(update.SourcePathHash)
                && IsValidPathHash(update.NewPathHash)
                && !string.IsNullOrWhiteSpace(update.EncryptedVirtualPath)
                && !string.IsNullOrWhiteSpace(update.VirtualPathIv))
            .DistinctBy(update => update.NewPathHash))
        {
            bool conflict = LoadMergedMetadata(username).Entries.Any(entry =>
                !entry.InTrash && string.Equals(entry.PathHash, update.NewPathHash, StringComparison.Ordinal));

            if (conflict)
            {
                conflicts += 1;
                continue;
            }

            bool copiedAnyReplica = false;

            foreach (string directory in GetUserDirectories(username))
            {
                StorageMetadata metadata = LoadMetadata(directory);
                StorageMetadataEntry? source = metadata.Entries.FirstOrDefault(item =>
                    !item.InTrash && string.Equals(item.PathHash, update.SourcePathHash, StringComparison.Ordinal));

                if (source is null)
                {
                    continue;
                }

                string blobId = source.IsFolder ? "" : Guid.NewGuid().ToString("N");
                if (!source.IsFolder)
                {
                    string sourceBlobPath = GetBlobPath(directory, source.BlobId);
                    string destinationBlobPath = GetBlobPath(directory, blobId);
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationBlobPath) ?? directory);
                    File.Copy(sourceBlobPath, destinationBlobPath, overwrite: false);
                }

                metadata.Entries.Add(new StorageMetadataEntry
                {
                    EncryptedVirtualPath = update.EncryptedVirtualPath.Trim(),
                    VirtualPathIv = update.VirtualPathIv.Trim(),
                    PathHash = update.NewPathHash.Trim(),
                    BlobId = blobId,
                    SizeBytes = source.SizeBytes,
                    StoredSizeBytes = source.StoredSizeBytes,
                    ModifiedAt = DateTimeOffset.UtcNow,
                    IsFolder = source.IsFolder
                });
                SaveMetadata(directory, metadata);
                copiedAnyReplica = true;
            }

            if (copiedAnyReplica)
            {
                copied += 1;
            }
            else
            {
                missing += 1;
            }
        }

        await userStore.SetUsedBytesAsync(username, GetUsedBytes(username));
        return (copied, conflicts, missing);
    }

    public async Task<(int Moved, int SkippedExisting, int Missing)> MoveFilesAsync(
        string username,
        IReadOnlyList<string> fileNames,
        string destinationDirectory,
        bool overwriteExisting = false,
        string? newName = null)
    {
        username = NormalizeEmail(username);
        await using SemaphoreLease lease = await AcquireUserFileLockAsync(username);
        string normalizedDestination = NormalizeRelativePath(destinationDirectory);
        string normalizedNewName = NormalizeRelativePath(newName ?? "");

        if (normalizedDestination.Length > 0 && !IsValidRelativePath(normalizedDestination))
        {
            return (0, 0, 0);
        }

        if (normalizedNewName.Length > 0 && !IsValidRelativePath(normalizedNewName))
        {
            return (0, 0, 0);
        }

        int moved = 0;
        int skippedBecauseExists = 0;
        int missingSource = 0;

        foreach (string relativePath in fileNames.Select(NormalizeRelativePath).Where(IsValidRelativePath).Distinct(StringComparer.Ordinal))
        {
            string fileName = string.IsNullOrWhiteSpace(normalizedNewName) ? Path.GetFileName(relativePath) : normalizedNewName;
            string destinationRelativePath = string.IsNullOrEmpty(normalizedDestination) ? fileName : $"{normalizedDestination}/{fileName}";

            if (string.Equals(relativePath, destinationRelativePath, StringComparison.Ordinal)
                || destinationRelativePath.StartsWith(relativePath + "/", StringComparison.Ordinal))
            {
                continue;
            }

            if (!PathExists(username, relativePath))
            {
                missingSource += 1;
                continue;
            }

            if (PathExists(username, destinationRelativePath))
            {
                if (!overwriteExisting)
                {
                    skippedBecauseExists += 1;
                    continue;
                }

                DeletePathAcrossReplicas(username, destinationRelativePath);
            }

            bool movedAnyReplica = false;
            foreach (string directory in GetUserDirectories(username))
            {
                StorageMetadata metadata = LoadMetadata(directory);
                List<StorageMetadataEntry> affected = metadata.Entries
                    .Where(entry => !entry.InTrash && (entry.VirtualPath == relativePath || entry.VirtualPath.StartsWith(relativePath + "/", StringComparison.Ordinal)))
                    .ToList();

                foreach (StorageMetadataEntry entry in affected)
                {
                    entry.VirtualPath = entry.VirtualPath == relativePath
                        ? destinationRelativePath
                        : destinationRelativePath + entry.VirtualPath[relativePath.Length..];
                    entry.ModifiedAt = DateTimeOffset.UtcNow;
                    movedAnyReplica = true;
                }

                SaveMetadata(directory, metadata);
            }

            if (movedAnyReplica)
            {
                moved += 1;
            }
        }

        await userStore.SetUsedBytesAsync(username, GetUsedBytes(username));
        return (moved, skippedBecauseExists, missingSource);
    }

    public async Task<(int Copied, int SkippedExisting, int Missing)> CopyFilesAsync(
        string username,
        IReadOnlyList<string> fileNames,
        string destinationDirectory,
        bool overwriteExisting = false)
    {
        username = NormalizeEmail(username);
        await using SemaphoreLease lease = await AcquireUserFileLockAsync(username);
        string normalizedDestination = NormalizeRelativePath(destinationDirectory);

        if (normalizedDestination.Length > 0 && !IsValidRelativePath(normalizedDestination))
        {
            return (0, 0, 0);
        }

        int copied = 0;
        int skippedBecauseExists = 0;
        int missingSource = 0;

        foreach (string relativePath in fileNames.Select(NormalizeRelativePath).Where(IsValidRelativePath).Distinct(StringComparer.Ordinal))
        {
            string fileName = Path.GetFileName(relativePath);
            string destinationRelativePath = string.IsNullOrEmpty(normalizedDestination) ? fileName : $"{normalizedDestination}/{fileName}";

            if (string.Equals(relativePath, destinationRelativePath, StringComparison.Ordinal))
            {
                if (!overwriteExisting)
                {
                    skippedBecauseExists += 1;
                }

                continue;
            }

            if (!PathExists(username, relativePath))
            {
                missingSource += 1;
                continue;
            }

            if (PathExists(username, destinationRelativePath))
            {
                if (!overwriteExisting)
                {
                    skippedBecauseExists += 1;
                    continue;
                }

                DeletePathAcrossReplicas(username, destinationRelativePath);
            }

            bool copiedAnyReplica = false;

            foreach (string directory in GetUserDirectories(username))
            {
                StorageMetadata metadata = LoadMetadata(directory);
                List<StorageMetadataEntry> affected = metadata.Entries
                    .Where(entry => !entry.InTrash && (entry.VirtualPath == relativePath || entry.VirtualPath.StartsWith(relativePath + "/", StringComparison.Ordinal)))
                    .ToList();

                foreach (StorageMetadataEntry entry in affected)
                {
                    string newBlobId = entry.IsFolder ? "" : Guid.NewGuid().ToString("N");
                    string copiedPath = entry.VirtualPath == relativePath
                        ? destinationRelativePath
                        : destinationRelativePath + entry.VirtualPath[relativePath.Length..];

                    if (!entry.IsFolder)
                    {
                        string sourceBlobPath = GetBlobPath(directory, entry.BlobId);
                        string destinationBlobPath = GetBlobPath(directory, newBlobId);
                        Directory.CreateDirectory(Path.GetDirectoryName(destinationBlobPath) ?? directory);
                        File.Copy(sourceBlobPath, destinationBlobPath, overwrite: false);
                    }

                    metadata.Entries.Add(new StorageMetadataEntry
                    {
                        VirtualPath = copiedPath,
                        BlobId = newBlobId,
                        SizeBytes = entry.SizeBytes,
                        StoredSizeBytes = entry.StoredSizeBytes,
                        ModifiedAt = DateTimeOffset.UtcNow,
                        IsFolder = entry.IsFolder
                    });
                    copiedAnyReplica = true;
                }

                SaveMetadata(directory, metadata);
            }

            if (copiedAnyReplica)
            {
                copied += 1;
            }
        }

        await userStore.SetUsedBytesAsync(username, GetUsedBytes(username));
        return (copied, skippedBecauseExists, missingSource);
    }

    private IEnumerable<string> GetUserDirectories(string username)
    {
        return rootPaths.Select(rootPath => Path.Combine(rootPath, username));
    }

    private async Task<SemaphoreLease> AcquireUserFileLockAsync(string username)
    {
        SemaphoreSlim semaphore = userFileLocks.GetOrAdd(username, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();
        return new SemaphoreLease(semaphore);
    }

    private void PurgeExpiredTrash(string username)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.Subtract(TrashRetention);

        foreach (string directory in GetUserDirectories(username))
        {
            StorageMetadata metadata = LoadMetadata(directory);
            List<StorageMetadataEntry> expired = metadata.Entries
                .Where(entry => entry.InTrash && entry.TrashedAt is not null && entry.TrashedAt < cutoff)
                .ToList();

            foreach (StorageMetadataEntry entry in expired)
            {
                if (!entry.IsFolder)
                {
                    DeleteFileQuietly(GetBlobPath(directory, entry.BlobId));
                }

                metadata.Entries.Remove(entry);
            }

            if (expired.Count > 0)
            {
                SaveMetadata(directory, metadata);
            }
        }
    }

    private bool PathExists(string username, string relativePath)
    {
        return LoadMergedMetadata(username).Entries.Any(entry =>
            !entry.InTrash
            && (entry.VirtualPath == relativePath || entry.VirtualPath.StartsWith(relativePath + "/", StringComparison.Ordinal)));
    }

    private string GetAvailableUploadPath(string username, string relativePath)
    {
        if (!PathExists(username, relativePath))
        {
            return relativePath;
        }

        string directory = ToDisplayPath(Path.GetDirectoryName(relativePath) ?? "").Trim('/');
        string fileName = Path.GetFileNameWithoutExtension(relativePath);
        string extension = Path.GetExtension(relativePath);

        for (int index = 1; index < 10_000; index += 1)
        {
            string candidateName = $"{fileName} ({index}){extension}";
            string candidatePath = string.IsNullOrEmpty(directory) ? candidateName : $"{directory}/{candidateName}";

            if (!PathExists(username, candidatePath))
            {
                return candidatePath;
            }
        }

        return $"{directory}/{Guid.NewGuid():N}{extension}".Trim('/');
    }

    private void DeletePathAcrossReplicas(string username, string relativePath)
    {
        foreach (string directory in GetUserDirectories(username))
        {
            StorageMetadata metadata = LoadMetadata(directory);
            List<StorageMetadataEntry> entries = metadata.Entries
                .Where(entry => !entry.InTrash && (entry.VirtualPath == relativePath || entry.VirtualPath.StartsWith(relativePath + "/", StringComparison.Ordinal)))
                .ToList();

            foreach (StorageMetadataEntry entry in entries)
            {
                if (!entry.IsFolder)
                {
                    DeleteFileQuietly(GetBlobPath(directory, entry.BlobId));
                }

                metadata.Entries.Remove(entry);
            }

            SaveMetadata(directory, metadata);
        }
    }

    private string GetWriteDirectory(string username, string relativePath)
    {
        string? existingDirectory = FindEntries(username, relativePath)
            .Select(item => item.Directory)
            .FirstOrDefault();

        if (existingDirectory is not null)
        {
            return existingDirectory;
        }

        if (storageMode == "spread")
        {
            return GetUserDirectories(username)
                .OrderByDescending(GetAvailableBytes)
                .First();
        }

        return GetUserDirectories(username).First();
    }

    private IReadOnlyList<string> GetWriteDirectories(string username, string relativePath)
    {
        if (storageMode == "mirror")
        {
            return GetUserDirectories(username).ToList();
        }

        return [GetWriteDirectory(username, relativePath)];
    }

    private IEnumerable<(string Directory, StorageMetadataEntry Entry)> FindEntries(string username, string relativePath)
    {
        foreach (string directory in GetUserDirectories(username))
        {
            foreach (StorageMetadataEntry entry in LoadMetadata(directory).Entries.Where(entry => entry.VirtualPath == relativePath))
            {
                yield return (directory, entry);
            }
        }
    }

    private IEnumerable<(string Directory, StorageMetadataEntry Entry)> FindEntriesByPathHash(string username, string pathHash)
    {
        foreach (string directory in GetUserDirectories(username))
        {
            foreach (StorageMetadataEntry entry in LoadMetadata(directory).Entries.Where(entry => entry.PathHash == pathHash))
            {
                yield return (directory, entry);
            }
        }
    }

    private IEnumerable<(string Directory, StorageMetadataEntry Entry)> FindEntriesBySelector(string username, string selector)
    {
        foreach (string directory in GetUserDirectories(username))
        {
            foreach (StorageMetadataEntry entry in LoadMetadata(directory).Entries.Where(entry => MatchesSelector(entry, selector)))
            {
                yield return (directory, entry);
            }
        }
    }

    private static bool MatchesSelector(StorageMetadataEntry entry, string selector)
    {
        return string.Equals(entry.PathHash, selector, StringComparison.Ordinal)
            || string.Equals(entry.Id, selector, StringComparison.Ordinal);
    }

    private StorageMetadata LoadMergedMetadata(string username)
    {
        var merged = new StorageMetadata();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string directory in GetUserDirectories(username))
        {
            EnsureVaultDirectories(directory);

            foreach (StorageMetadataEntry entry in LoadMetadata(directory).Entries)
            {
                string key = $"{GetEntryKey(entry)}\0{entry.BlobId}\0{entry.InTrash}\0{entry.TrashPath}";
                if (seen.Add(key))
                {
                    merged.Entries.Add(entry);
                }
            }
        }

        return merged;
    }

    private static StorageMetadata LoadMetadata(string userDirectory)
    {
        EnsureVaultDirectories(userDirectory);
        string metadataPath = GetMetadataPath(userDirectory);

        if (!File.Exists(metadataPath))
        {
            return new StorageMetadata();
        }

        try
        {
            string json = File.ReadAllText(metadataPath);
            StorageMetadata metadata = JsonSerializer.Deserialize<StorageMetadata>(json) ?? new StorageMetadata();
            int removed = metadata.Entries.RemoveAll(entry =>
                string.IsNullOrWhiteSpace(entry.EncryptedVirtualPath)
                && string.IsNullOrWhiteSpace(entry.PathHash));

            foreach (StorageMetadataEntry entry in metadata.Entries)
            {
                entry.VirtualPath = "";
                entry.TrashPath = null;
            }

            if (removed > 0
                || json.Contains("\"VirtualPath\"", StringComparison.Ordinal)
                || json.Contains("\"TrashPath\"", StringComparison.Ordinal))
            {
                File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, JsonOptions));
            }

            return metadata;
        }
        catch (JsonException)
        {
            return new StorageMetadata();
        }
    }

    private static void SaveMetadata(string userDirectory, StorageMetadata metadata)
    {
        EnsureVaultDirectories(userDirectory);
        foreach (StorageMetadataEntry entry in metadata.Entries)
        {
            entry.VirtualPath = "";
            entry.TrashPath = null;
        }

        metadata.Entries.RemoveAll(entry =>
            string.IsNullOrWhiteSpace(entry.EncryptedVirtualPath)
            && string.IsNullOrWhiteSpace(entry.PathHash));
        File.WriteAllText(GetMetadataPath(userDirectory), JsonSerializer.Serialize(metadata, JsonOptions));
    }

    private static FileEntry ToFileEntry(StorageMetadataEntry entry)
    {
        return new FileEntry(
            entry.PathHash,
            entry.IsFolder ? 0 : entry.SizeBytes,
            entry.ModifiedAt,
            entry.IsFolder,
            entry.EncryptedVirtualPath,
            entry.VirtualPathIv,
            entry.PathHash);
    }

    private static string GetEntryKey(StorageMetadataEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.PathHash))
        {
            return entry.PathHash;
        }

        return entry.Id;
    }

    private static void EnsureVaultDirectories(string userDirectory)
    {
        Directory.CreateDirectory(GetBlobDirectory(userDirectory));
    }

    private static string GetVaultDirectory(string userDirectory)
    {
        return Path.Combine(userDirectory, MetadataDirectoryName);
    }

    private static string GetBlobDirectory(string userDirectory)
    {
        return Path.Combine(GetVaultDirectory(userDirectory), BlobDirectoryName);
    }

    private static string GetMetadataPath(string userDirectory)
    {
        return Path.Combine(GetVaultDirectory(userDirectory), MetadataFileName);
    }

    private static string GetBlobPath(string userDirectory, string blobId)
    {
        return Path.Combine(GetBlobDirectory(userDirectory), blobId);
    }

    private static string NormalizeEmail(string email)
    {
        return email.Trim().ToLowerInvariant();
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Trim().Replace('\\', '/').Trim('/');
    }

    private static string ToDisplayPath(string path)
    {
        return path.Replace('\\', '/');
    }

    private static string GetParentPath(string path)
    {
        int index = path.LastIndexOf('/');
        return index < 0 ? "" : path[..index];
    }

    private static bool IsValidRelativePath(string path)
    {
        if (path.Length == 0 || path.Length > MaxRelativePathLength)
        {
            return false;
        }

        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && parts.All(IsValidFileName);
    }

    private static bool IsValidFileName(string fileName)
    {
        return fileName.Length > 0
            && fileName.Length <= MaxFileNameLength
            && fileName != "."
            && fileName != ".."
            && !fileName.Contains("..", StringComparison.Ordinal)
            && fileName.All(ch => !char.IsControl(ch) && ch != '/' && ch != '\\' && ch != '\0');
    }

    private static bool IsValidPathHash(string pathHash)
    {
        return pathHash.Length is >= 16 and <= 256
            && pathHash.All(ch => char.IsAsciiHexDigit(ch) || ch is '-' or '_');
    }

    private static bool IsSafeUploadPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || Path.IsPathRooted(path)
            || path.Contains('\\', StringComparison.Ordinal)
            || path.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        string normalized = path.Replace('\\', '/');
        string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length > 0
            && parts.All(part => part != "." && part != ".." && !part.Contains("..", StringComparison.Ordinal));
    }

    private long GetUsedBytes(string username)
    {
        return LoadMergedMetadata(username).Entries
            .Where(entry => !entry.InTrash && !entry.IsFolder)
            .Select(entry => entry.StoredSizeBytes)
            .Sum();
    }

    private static long GetAvailableBytes(string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullPath);
        DriveInfo drive = new(Path.GetPathRoot(fullPath) ?? fullPath);
        return drive.AvailableFreeSpace;
    }

    private static string NormalizeStorageMode(string mode)
    {
        return mode.Trim().ToLowerInvariant() switch
        {
            "mirror" => "mirror",
            "spread" => "spread",
            "raid0" => "spread",
            _ => "single"
        };
    }

    private static bool TryParseTrashRelativePath(string path, out string sourceRelativePath, out string destinationRelativePath)
    {
        sourceRelativePath = "";
        destinationRelativePath = "";

        if (!IsValidRelativePath(path))
        {
            return false;
        }

        int slashIndex = path.IndexOf('/');

        if (slashIndex <= 0 || slashIndex >= path.Length - 1)
        {
            return false;
        }

        sourceRelativePath = path;
        destinationRelativePath = path[(slashIndex + 1)..];
        return IsValidRelativePath(destinationRelativePath);
    }

    private static void DeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class StorageMetadata
    {
        public List<StorageMetadataEntry> Entries { get; set; } = [];
    }

    private sealed class StorageMetadataEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [JsonIgnore]
        public string VirtualPath { get; set; } = "";
        public string EncryptedVirtualPath { get; set; } = "";
        public string VirtualPathIv { get; set; } = "";
        public string PathHash { get; set; } = "";
        public string BlobId { get; set; } = "";
        public long SizeBytes { get; set; }
        public long StoredSizeBytes { get; set; }
        public DateTimeOffset ModifiedAt { get; set; }
        public bool IsFolder { get; set; }
        public bool InTrash { get; set; }
        [JsonIgnore]
        public string? TrashPath { get; set; }
        public DateTimeOffset? TrashedAt { get; set; }
    }

    private sealed class SemaphoreLease : IAsyncDisposable
    {
        private readonly SemaphoreSlim semaphore;
        private bool disposed;

        public SemaphoreLease(SemaphoreSlim semaphore)
        {
            this.semaphore = semaphore;
        }

        public ValueTask DisposeAsync()
        {
            if (!disposed)
            {
                semaphore.Release();
                disposed = true;
            }

            return ValueTask.CompletedTask;
        }
    }
}

public sealed record FileDownload(string RelativePath, long Length, DateTime ModifiedAtUtc, Stream Stream);

public sealed record EncryptedPathUpdate(
    string SourcePathHash,
    string EncryptedVirtualPath,
    string VirtualPathIv,
    string NewPathHash);
