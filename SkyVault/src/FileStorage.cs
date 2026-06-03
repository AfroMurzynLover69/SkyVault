using System.Text;
using System.Text.RegularExpressions;
using System.IO.Compression;
using System.Globalization;

public sealed class FileStorage
{
    private static readonly Regex FileNameRegex = new("^[a-zA-Z0-9_. -]{1,80}$", RegexOptions.Compiled);
    private const int MaxRelativePathLength = 240;
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

    private IEnumerable<string> GetUserDirectories(string username)
    {
        return rootPaths.Select(rootPath => Path.Combine(rootPath, username));
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
        return FileNameRegex.IsMatch(fileName)
            && fileName != "."
            && fileName != ".."
            && !fileName.Contains("..", StringComparison.Ordinal);
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
