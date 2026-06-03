using System.Text;
using System.Text.RegularExpressions;

public sealed class FileStorage
{
    private static readonly Regex FileNameRegex = new("^[a-zA-Z0-9_. -]{1,80}$", RegexOptions.Compiled);
    private const int MaxRelativePathLength = 240;
    private readonly string rootPath;
    private readonly UserStore userStore;

    public FileStorage(string rootPath, UserStore userStore)
    {
        this.rootPath = rootPath;
        this.userStore = userStore;
        Directory.CreateDirectory(rootPath);
    }

    public IReadOnlyList<FileEntry> GetFiles(string? username)
    {
        if (username is null)
        {
            return [];
        }

        string directory = GetUserDirectory(NormalizeEmail(username));
        Directory.CreateDirectory(directory);

        var folders = Directory
            .EnumerateDirectories(directory, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                var info = new DirectoryInfo(path);
                return new FileEntry(ToDisplayPath(Path.GetRelativePath(directory, path)), 0, info.LastWriteTimeUtc, true);
            });
        var files = Directory
            .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                var info = new FileInfo(path);
                return new FileEntry(ToDisplayPath(Path.GetRelativePath(directory, path)), info.Length, info.LastWriteTimeUtc);
            });

        return folders
            .Concat(files)
            .OrderBy(entry => entry.IsFolder ? 0 : 1)
            .ThenBy(entry => entry.Name)
            .ToList();
    }

    public void CreateUserDirectory(string username)
    {
        Directory.CreateDirectory(GetUserDirectory(NormalizeEmail(username)));
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

        string directory = GetUserDirectory(username);
        Directory.CreateDirectory(directory);

        string path = GetSafeUserPath(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? directory);
        long currentUsed = GetUsedBytes(directory);
        long oldSize = File.Exists(path) ? new FileInfo(path).Length : 0;
        long newSize = Encoding.UTF8.GetByteCount(content);
        long nextUsed = currentUsed - oldSize + newSize;

        if (nextUsed > user.QuotaBytes)
        {
            return FileCreateResult.QuotaExceeded;
        }

        await File.WriteAllTextAsync(path, content);
        await userStore.SetUsedBytesAsync(username, nextUsed);
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

        string directory = GetUserDirectory(username);
        Directory.CreateDirectory(GetSafeUserPath(directory, relativePath));
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

        string directory = GetUserDirectory(username);
        Directory.CreateDirectory(directory);

        string path = GetSafeUserPath(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? directory);
        long currentUsed = GetUsedBytes(directory);
        long oldSize = File.Exists(path) ? new FileInfo(path).Length : 0;
        long nextUsed = currentUsed - oldSize + file.Content.LongLength;

        if (nextUsed > user.QuotaBytes)
        {
            return FileCreateResult.QuotaExceeded;
        }

        await File.WriteAllBytesAsync(path, file.Content);
        await userStore.SetUsedBytesAsync(username, nextUsed);
        return FileCreateResult.Created;
    }

    private string GetUserDirectory(string username)
    {
        return Path.Combine(rootPath, username);
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

    private static long GetUsedBytes(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
    }
}
