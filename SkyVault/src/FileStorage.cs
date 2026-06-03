using System.Text;
using System.Text.RegularExpressions;
using System.IO.Compression;
using System.Globalization;

public sealed class FileStorage
{
    private const int MaxRelativePathLength = 1024;
    private const int MaxFileNameLength = 255;
    private static readonly TimeSpan TrashRetention = TimeSpan.FromDays(30);
    private readonly IReadOnlyList<string> rootPaths;
    private readonly string storageMode;
    private readonly UserStore userStore;

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

        foreach (string directory in GetUserDirectories(username))
        {
            Directory.CreateDirectory(directory);
        }

        Dictionary<string, FileEntry> entries = new(StringComparer.Ordinal);

        foreach (string directory in GetUserDirectories(username))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories)
                .Where(path => !IsTrashPath(directory, path)))
            {
                var info = new DirectoryInfo(path);
                string relativePath = ToDisplayPath(Path.GetRelativePath(directory, path));
                entries[relativePath] = new FileEntry(relativePath, 0, info.LastWriteTimeUtc, true);
            }

            foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(path => !IsTrashPath(directory, path)))
            {
                var info = new FileInfo(path);
                string relativePath = ToDisplayPath(Path.GetRelativePath(directory, path));

                if (!entries.TryGetValue(relativePath, out FileEntry? existing)
                    || existing.ModifiedAt < info.LastWriteTimeUtc)
                {
                    entries[relativePath] = new FileEntry(relativePath, info.Length, info.LastWriteTimeUtc);
                }
            }
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
        Dictionary<string, FileEntry> entries = new(StringComparer.Ordinal);

        foreach (string directory in GetUserDirectories(username))
        {
            string trashDirectory = Path.Combine(directory, ".trash");

            if (!Directory.Exists(trashDirectory))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(trashDirectory, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(path);
                string relativePath = ToDisplayPath(Path.GetRelativePath(trashDirectory, path));

                if (!entries.TryGetValue(relativePath, out FileEntry? existing)
                    || existing.ModifiedAt < info.LastWriteTimeUtc)
                {
                    entries[relativePath] = new FileEntry(relativePath, info.Length, info.LastWriteTimeUtc);
                }
            }
        }

        return entries.Values
            .OrderByDescending(entry => entry.ModifiedAt)
            .ThenBy(entry => entry.Name)
            .ToList();
    }

    public void CreateUserDirectory(string username)
    {
        foreach (string directory in GetUserDirectories(NormalizeEmail(username)))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public async Task<FileCreateResult> CreateTextFileAsync(string username, string fileName, string content)
    {
        username = NormalizeEmail(username);
        string relativePath = NormalizeRelativePath(fileName);

        if (!IsValidRelativePath(relativePath))
        {
            return FileCreateResult.InvalidFileName;
        }

        UserAccount? user = userStore.GetUser(username);

        if (user is null)
        {
            return FileCreateResult.Failed;
        }

        string targetDirectory = GetWriteDirectory(username, relativePath);
        Directory.CreateDirectory(targetDirectory);

        string path = GetSafeUserPath(targetDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? targetDirectory);
        long currentUsed = GetUsedBytes(username);
        long oldSize = GetExistingSize(username, relativePath);
        long newSize = Encoding.UTF8.GetByteCount(content);
        long nextUsed = currentUsed - oldSize + newSize;

        if (nextUsed > user.QuotaBytes)
        {
            return FileCreateResult.QuotaExceeded;
        }

        foreach (string writeDirectory in GetWriteDirectories(username, relativePath))
        {
            string writePath = GetSafeUserPath(writeDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(writePath) ?? writeDirectory);
            await File.WriteAllTextAsync(writePath, content);
        }

        await userStore.SetUsedBytesAsync(username, GetUsedBytes(username));
        return FileCreateResult.Created;
    }

    public Task<FileCreateResult> CreateFolderAsync(string username, string folderName)
    {
        username = NormalizeEmail(username);
        string relativePath = NormalizeRelativePath(folderName);

        if (!IsValidRelativePath(relativePath))
        {
            return Task.FromResult(FileCreateResult.InvalidFileName);
        }

        UserAccount? user = userStore.GetUser(username);

        if (user is null)
        {
            return Task.FromResult(FileCreateResult.Failed);
        }

        foreach (string directory in GetUserDirectories(username))
        {
            Directory.CreateDirectory(GetSafeUserPath(directory, relativePath));
        }

        return Task.FromResult(FileCreateResult.Created);
    }

    public async Task<FileCreateResult> SaveUploadedFileAsync(string username, MultipartFile file)
    {
        username = NormalizeEmail(username);
        string relativePath = NormalizeRelativePath(file.FileName);

        if (!IsValidRelativePath(relativePath))
        {
            return FileCreateResult.InvalidFileName;
        }

        UserAccount? user = userStore.GetUser(username);

        if (user is null)
        {
            return FileCreateResult.Failed;
        }

        string targetDirectory = GetWriteDirectory(username, relativePath);
        Directory.CreateDirectory(targetDirectory);

        string path = GetSafeUserPath(targetDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? targetDirectory);
        long currentUsed = GetUsedBytes(username);
        long oldSize = GetExistingSize(username, relativePath);
        long nextUsed = currentUsed - oldSize + file.Content.LongLength;

        if (nextUsed > user.QuotaBytes)
        {
            return FileCreateResult.QuotaExceeded;
        }

        foreach (string writeDirectory in GetWriteDirectories(username, relativePath))
        {
            string writePath = GetSafeUserPath(writeDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(writePath) ?? writeDirectory);
            await File.WriteAllBytesAsync(writePath, file.Content);
        }

        await userStore.SetUsedBytesAsync(username, GetUsedBytes(username));
        return FileCreateResult.Created;
    }

    public async Task<byte[]?> ReadFileAsync(string username, string fileName)
    {
        username = NormalizeEmail(username);
        string relativePath = NormalizeRelativePath(fileName);

        if (!IsValidRelativePath(relativePath))
        {
            return null;
        }

        foreach (string directory in GetUserDirectories(username))
        {
            string path = GetSafeUserPath(directory, relativePath);

            if (File.Exists(path))
            {
                return await File.ReadAllBytesAsync(path);
            }
        }

        return null;
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

    public async Task<int> MoveToTrashAsync(string username, IReadOnlyList<string> fileNames)
    {
        username = NormalizeEmail(username);
        PurgeExpiredTrash(username);
        int moved = 0;
        string trashBatch = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        foreach (string rawFileName in fileNames)
        {
            string relativePath = NormalizeRelativePath(rawFileName);
            bool movedAnyReplica = false;

            if (!IsValidRelativePath(relativePath))
            {
                continue;
            }

            foreach (string directory in GetUserDirectories(username))
            {
                string path = GetSafeUserPath(directory, relativePath);

                if (!File.Exists(path))
                {
                    continue;
                }

                string trashPath = GetSafeUserPath(Path.Combine(directory, ".trash", trashBatch), relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(trashPath) ?? directory);

                if (File.Exists(trashPath))
                {
                    File.Delete(trashPath);
                }

                File.Move(path, trashPath);
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
        PurgeExpiredTrash(username);
        int deleted = 0;

        foreach (string directory in GetUserDirectories(username))
        {
            string trashDirectory = Path.Combine(directory, ".trash");

            if (!Directory.Exists(trashDirectory))
            {
                continue;
            }

            foreach (string batchDirectory in Directory.EnumerateDirectories(trashDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                Directory.Delete(batchDirectory, true);
                deleted += 1;
            }
        }

        if (deleted > 0)
        {
            await userStore.SetUsedBytesAsync(username, GetUsedBytes(username));
        }

        return deleted;
    }

    public async Task<(int Moved, int SkippedExisting, int Missing)> RestoreFromTrashAsync(
        string username,
        IReadOnlyList<string> fileNames,
        bool overwriteExisting = false)
    {
        username = NormalizeEmail(username);
        PurgeExpiredTrash(username);
        int restored = 0;
        int skippedBecauseExists = 0;
        int missingFromTrash = 0;
        List<string> directories = GetUserDirectories(username).ToList();

        foreach (string rawFileName in fileNames)
        {
            string relativePath = NormalizeRelativePath(rawFileName);

            if (!TryParseTrashRelativePath(relativePath, out string sourceRelativePath, out string destinationRelativePath))
            {
                continue;
            }

            var sources = new List<(string Directory, string SourcePath)>();

            foreach (string directory in directories)
            {
                string trashRoot = Path.Combine(directory, ".trash");
                string sourcePath = GetSafeUserPath(trashRoot, sourceRelativePath);

                if (File.Exists(sourcePath))
                {
                    sources.Add((directory, sourcePath));
                }
            }

            if (sources.Count == 0)
            {
                missingFromTrash += 1;
                continue;
            }

            if (PathExists(username, destinationRelativePath))
            {
                if (!overwriteExisting)
                {
                    skippedBecauseExists += 1;
                    continue;
                }

                foreach ((string directory, _) in sources)
                {
                    DeletePathInDirectory(directory, destinationRelativePath);
                }
            }

            bool restoredAnyReplica = false;
            DateTime sourceModifiedAt = File.GetLastWriteTimeUtc(sources[0].SourcePath);

            foreach ((string directory, string sourcePath) in sources)
            {
                string destinationPath = GetSafeUserPath(directory, destinationRelativePath);

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? directory);

                File.Move(sourcePath, destinationPath);
                File.SetLastWriteTimeUtc(destinationPath, sourceModifiedAt);
                restoredAnyReplica = true;
            }

            if (restoredAnyReplica)
            {
                restored += 1;
            }
        }

        if (restored > 0)
        {
            await userStore.SetUsedBytesAsync(username, GetUsedBytes(username));
        }

        return (restored, skippedBecauseExists, missingFromTrash);
    }

    public async Task<int> DeleteFromTrashAsync(string username, IReadOnlyList<string> fileNames)
    {
        username = NormalizeEmail(username);
        PurgeExpiredTrash(username);
        int deleted = 0;

        foreach (string rawFileName in fileNames)
        {
            string relativePath = NormalizeRelativePath(rawFileName);
            bool deletedAnyReplica = false;

            if (!IsValidRelativePath(relativePath))
            {
                continue;
            }

            foreach (string directory in GetUserDirectories(username))
            {
                string trashRoot = Path.Combine(directory, ".trash");
                string path = GetSafeUserPath(trashRoot, relativePath);

                if (!File.Exists(path))
                {
                    continue;
                }

                File.Delete(path);
                deletedAnyReplica = true;
            }

            if (deletedAnyReplica)
            {
                deleted += 1;
            }
        }

        if (deleted > 0)
        {
            await userStore.SetUsedBytesAsync(username, GetUsedBytes(username));
        }

        return deleted;
    }

    public Task<(int Moved, int SkippedExisting, int Missing)> MoveFilesAsync(
        string username,
        IReadOnlyList<string> fileNames,
        string destinationDirectory,
        bool overwriteExisting = false,
        string? newName = null)
    {
        username = NormalizeEmail(username);
        string normalizedDestination = NormalizeRelativePath(destinationDirectory);
        string normalizedNewName = NormalizeRelativePath(newName ?? "");

        if (normalizedDestination.Length > 0 && !IsValidRelativePath(normalizedDestination))
        {
            return Task.FromResult((0, 0, 0));
        }

        if (normalizedNewName.Length > 0 && !IsValidRelativePath(normalizedNewName))
        {
            return Task.FromResult((0, 0, 0));
        }

        return MoveFilesInternalAsync(username, fileNames, normalizedDestination, overwriteExisting, normalizedNewName);
    }

    public Task<(int Copied, int SkippedExisting, int Missing)> CopyFilesAsync(
        string username,
        IReadOnlyList<string> fileNames,
        string destinationDirectory,
        bool overwriteExisting = false)
    {
        username = NormalizeEmail(username);
        string normalizedDestination = NormalizeRelativePath(destinationDirectory);

        if (normalizedDestination.Length > 0 && !IsValidRelativePath(normalizedDestination))
        {
            return Task.FromResult((0, 0, 0));
        }

        return CopyFilesInternalAsync(username, fileNames, normalizedDestination, overwriteExisting);
    }

    private IEnumerable<string> GetUserDirectories(string username)
    {
        return rootPaths.Select(rootPath => Path.Combine(rootPath, username));
    }

    private void PurgeExpiredTrash(string username)
    {
        DateTime cutoff = DateTime.UtcNow.Subtract(TrashRetention);

        foreach (string directory in GetUserDirectories(username))
        {
            string trashDirectory = Path.Combine(directory, ".trash");

            if (!Directory.Exists(trashDirectory))
            {
                continue;
            }

            foreach (string batchDirectory in Directory.EnumerateDirectories(trashDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                string batchName = Path.GetFileName(batchDirectory);

                if (!TryParseTrashBatchTime(batchName, out DateTime batchTimeUtc))
                {
                    continue;
                }

                if (batchTimeUtc < cutoff)
                {
                    Directory.Delete(batchDirectory, true);
                }
            }
        }
    }

    private async Task<(int Moved, int SkippedExisting, int Missing)> MoveFilesInternalAsync(
        string username,
        IReadOnlyList<string> fileNames,
        string destinationDirectory,
        bool overwriteExisting,
        string normalizedNewName)
    {
        int moved = 0;
        int skippedBecauseExists = 0;
        int missingSource = 0;

        foreach (string rawFileName in fileNames)
        {
            string relativePath = NormalizeRelativePath(rawFileName);

            if (!IsValidRelativePath(relativePath))
            {
                continue;
            }

            string fileName = string.IsNullOrWhiteSpace(normalizedNewName)
                ? Path.GetFileName(relativePath)
                : normalizedNewName;
            string destinationRelativePath = string.IsNullOrEmpty(destinationDirectory)
                ? fileName
                : $"{destinationDirectory}/{fileName}";

            if (string.Equals(relativePath, destinationRelativePath, StringComparison.Ordinal))
            {
                continue;
            }

            if (destinationRelativePath.StartsWith(relativePath + "/", StringComparison.Ordinal))
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
            DateTime sourceModifiedAt = DateTime.UtcNow;
            bool sourceModifiedCaptured = false;

            foreach (string directory in GetUserDirectories(username))
            {
                string sourcePath = GetSafeUserPath(directory, relativePath);
                string destinationPath = GetSafeUserPath(directory, destinationRelativePath);

                if (!File.Exists(sourcePath))
                {
                    if (!Directory.Exists(sourcePath))
                    {
                        continue;
                    }

                    if (!sourceModifiedCaptured)
                    {
                        sourceModifiedAt = Directory.GetLastWriteTimeUtc(sourcePath);
                        sourceModifiedCaptured = true;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? directory);

                    Directory.Move(sourcePath, destinationPath);
                    Directory.SetLastWriteTimeUtc(destinationPath, sourceModifiedAt);
                    movedAnyReplica = true;
                    continue;
                }

                if (!sourceModifiedCaptured)
                {
                    sourceModifiedAt = File.GetLastWriteTimeUtc(sourcePath);
                    sourceModifiedCaptured = true;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? directory);

                File.Move(sourcePath, destinationPath);
                File.SetLastWriteTimeUtc(destinationPath, sourceModifiedAt);
                movedAnyReplica = true;
            }

            if (movedAnyReplica)
            {
                moved += 1;
            }
        }

        if (moved > 0)
        {
            await userStore.SetUsedBytesAsync(username, GetUsedBytes(username));
        }

        return (moved, skippedBecauseExists, missingSource);
    }

    private async Task<(int Copied, int SkippedExisting, int Missing)> CopyFilesInternalAsync(
        string username,
        IReadOnlyList<string> fileNames,
        string destinationDirectory,
        bool overwriteExisting)
    {
        int copied = 0;
        int skippedBecauseExists = 0;
        int missingSource = 0;

        foreach (string rawFileName in fileNames)
        {
            string relativePath = NormalizeRelativePath(rawFileName);

            if (!IsValidRelativePath(relativePath))
            {
                continue;
            }

            string fileName = Path.GetFileName(relativePath);
            string destinationRelativePath = string.IsNullOrEmpty(destinationDirectory)
                ? fileName
                : $"{destinationDirectory}/{fileName}";

            if (string.Equals(relativePath, destinationRelativePath, StringComparison.Ordinal))
            {
                if (!overwriteExisting)
                {
                    skippedBecauseExists += 1;
                }

                continue;
            }

            if (destinationRelativePath.StartsWith(relativePath + "/", StringComparison.Ordinal))
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

            bool copiedAnyReplica = false;
            DateTime sourceModifiedAt = DateTime.UtcNow;
            bool sourceModifiedCaptured = false;

            foreach (string directory in GetUserDirectories(username))
            {
                string sourcePath = GetSafeUserPath(directory, relativePath);
                string destinationPath = GetSafeUserPath(directory, destinationRelativePath);

                if (!File.Exists(sourcePath))
                {
                    if (!Directory.Exists(sourcePath))
                    {
                        continue;
                    }

                    if (!sourceModifiedCaptured)
                    {
                        sourceModifiedAt = Directory.GetLastWriteTimeUtc(sourcePath);
                        sourceModifiedCaptured = true;
                    }

                    CopyDirectory(sourcePath, destinationPath);
                    Directory.SetLastWriteTimeUtc(destinationPath, sourceModifiedAt);
                    copiedAnyReplica = true;
                    continue;
                }

                if (!sourceModifiedCaptured)
                {
                    sourceModifiedAt = File.GetLastWriteTimeUtc(sourcePath);
                    sourceModifiedCaptured = true;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? directory);
                File.Copy(sourcePath, destinationPath, overwrite: true);
                File.SetLastWriteTimeUtc(destinationPath, sourceModifiedAt);
                copiedAnyReplica = true;
            }

            if (copiedAnyReplica)
            {
                copied += 1;
            }
        }

        if (copied > 0)
        {
            await userStore.SetUsedBytesAsync(username, GetUsedBytes(username));
        }

        return (copied, skippedBecauseExists, missingSource);
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (string sourceFile in Directory.EnumerateFiles(sourceDirectory))
        {
            string destinationFile = Path.Combine(destinationDirectory, Path.GetFileName(sourceFile));
            File.Copy(sourceFile, destinationFile, overwrite: true);
            File.SetLastWriteTimeUtc(destinationFile, File.GetLastWriteTimeUtc(sourceFile));
        }

        foreach (string sourceChildDirectory in Directory.EnumerateDirectories(sourceDirectory))
        {
            string destinationChildDirectory = Path.Combine(destinationDirectory, Path.GetFileName(sourceChildDirectory));
            CopyDirectory(sourceChildDirectory, destinationChildDirectory);
            Directory.SetLastWriteTimeUtc(destinationChildDirectory, Directory.GetLastWriteTimeUtc(sourceChildDirectory));
        }
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

    private static bool TryParseTrashBatchTime(string batchName, out DateTime batchTimeUtc)
    {
        return DateTime.TryParseExact(
            batchName,
            "yyyyMMdd-HHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out batchTimeUtc);
    }

    private bool PathExists(string username, string relativePath)
    {
        foreach (string directory in GetUserDirectories(username))
        {
            string path = GetSafeUserPath(directory, relativePath);

            if (File.Exists(path) || Directory.Exists(path))
            {
                return true;
            }
        }

        return false;
    }

    private void DeletePathAcrossReplicas(string username, string relativePath)
    {
        foreach (string directory in GetUserDirectories(username))
        {
            string path = GetSafeUserPath(directory, relativePath);

            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    private static void DeletePathInDirectory(string directory, string relativePath)
    {
        string path = GetSafeUserPath(directory, relativePath);

        if (File.Exists(path))
        {
            File.Delete(path);
        }
        else if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

    private string GetWriteDirectory(string username, string relativePath)
    {
        string? existingDirectory = GetUserDirectories(username)
            .FirstOrDefault(directory => File.Exists(GetSafeUserPath(directory, relativePath)));

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

    private static string GetSafeUserPath(string directory, string relativePath)
    {
        string[] parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return Path.Combine([directory, .. parts]);
    }

    private long GetUsedBytes(string username)
    {
        Dictionary<string, long> files = new(StringComparer.Ordinal);

        foreach (string directory in GetUserDirectories(username))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(path => !IsTrashPath(directory, path)))
            {
                string relativePath = ToDisplayPath(Path.GetRelativePath(directory, path));
                files[relativePath] = new FileInfo(path).Length;
            }
        }

        return files.Values.Sum();
    }

    private long GetExistingSize(string username, string relativePath)
    {
        foreach (string directory in GetUserDirectories(username))
        {
            string path = GetSafeUserPath(directory, relativePath);

            if (File.Exists(path))
            {
                return new FileInfo(path).Length;
            }
        }

        return 0;
    }

    private static long GetAvailableBytes(string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullPath);
        DriveInfo drive = new DriveInfo(Path.GetPathRoot(fullPath) ?? fullPath);
        return drive.AvailableFreeSpace;
    }

    private static bool IsTrashPath(string rootDirectory, string path)
    {
        string relativePath = ToDisplayPath(Path.GetRelativePath(rootDirectory, path));
        return relativePath == ".trash" || relativePath.StartsWith(".trash/", StringComparison.Ordinal);
    }

    private static string NormalizeStorageMode(string mode)
    {
        mode = mode.Trim().ToLowerInvariant();

        return mode switch
        {
            "single" or "spread" or "mirror" => mode,
            "raid1" => "mirror",
            "raid0" => "spread",
            _ => "single"
        };
    }
}
