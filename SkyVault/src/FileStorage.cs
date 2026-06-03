using System.Text;
using System.Text.RegularExpressions;

public sealed class FileStorage
{
    private static readonly Regex FileNameRegex = new("^[a-zA-Z0-9_. -]{1,80}$", RegexOptions.Compiled);
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

        return Directory
            .EnumerateFiles(directory)
            .Select(path =>
            {
                var info = new FileInfo(path);
                return new FileEntry(info.Name, info.Length, info.LastWriteTimeUtc);
            })
            .OrderBy(entry => entry.Name)
            .ToList();
    }

    public void CreateUserDirectory(string username)
    {
        Directory.CreateDirectory(GetUserDirectory(NormalizeEmail(username)));
    }

    public async Task<FileCreateResult> CreateTextFileAsync(string username, string fileName, string content)
    {
        username = NormalizeEmail(username);

        if (!IsValidFileName(fileName))
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

        string path = Path.Combine(directory, fileName);
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

    public async Task<FileCreateResult> SaveUploadedFileAsync(string username, MultipartFile file)
    {
        username = NormalizeEmail(username);
        string fileName = Path.GetFileName(file.FileName);

        if (!IsValidFileName(fileName))
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

        string path = Path.Combine(directory, fileName);
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

    private static bool IsValidFileName(string fileName)
    {
        return FileNameRegex.IsMatch(fileName)
            && fileName != "."
            && fileName != ".."
            && !fileName.Contains("..", StringComparison.Ordinal);
    }

    private static long GetUsedBytes(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        return Directory.EnumerateFiles(directory).Sum(path => new FileInfo(path).Length);
    }
}
