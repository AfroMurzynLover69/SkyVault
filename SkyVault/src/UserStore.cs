using System.Text.Json;
using System.Text.RegularExpressions;

public sealed class UserStore
{
    private static readonly Regex EmailRegex = new("^[^\\s@/\\\\]+@[^\\s@/\\\\]+\\.[^\\s@/\\\\]+$", RegexOptions.Compiled);
    private readonly string filePath;
    private readonly long defaultQuotaBytes;
    private readonly Lock gate = new();
    private readonly Dictionary<string, UserAccount> users = new(StringComparer.OrdinalIgnoreCase);

    public UserStore(string filePath, long defaultQuotaBytes)
    {
        this.filePath = filePath;
        this.defaultQuotaBytes = defaultQuotaBytes;
    }

    public async Task LoadAsync()
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        string json = await File.ReadAllTextAsync(filePath);
        List<UserAccount>? loadedUsers = JsonSerializer.Deserialize<List<UserAccount>>(json);

        if (loadedUsers is null)
        {
            return;
        }

        lock (gate)
        {
            users.Clear();

            foreach (UserAccount user in loadedUsers)
            {
                users[NormalizeEmail(user.Username)] = user;
            }
        }
    }

    public async Task<RegisterResult> RegisterAsync(string email, string password)
    {
        string normalizedEmail = NormalizeEmail(email);
        RegisterResult validation = ValidateRegistration(normalizedEmail, password);

        if (validation != RegisterResult.Created)
        {
            return validation;
        }

        lock (gate)
        {
            if (users.ContainsKey(normalizedEmail))
            {
                return RegisterResult.AlreadyExists;
            }

            users[normalizedEmail] = new UserAccount
            {
                Username = normalizedEmail,
                Password = password,
                QuotaBytes = defaultQuotaBytes,
                UsedBytes = 0,
                CreatedAt = DateTimeOffset.UtcNow
            };
        }

        await SaveAsync();
        return RegisterResult.Created;
    }

    public RegisterResult ValidateRegistration(string email, string password)
    {
        string normalizedEmail = NormalizeEmail(email);

        if (!IsValidEmail(normalizedEmail))
        {
            return RegisterResult.InvalidEmail;
        }

        if (password.Length < 4)
        {
            return RegisterResult.InvalidPassword;
        }

        lock (gate)
        {
            return users.ContainsKey(normalizedEmail)
                ? RegisterResult.AlreadyExists
                : RegisterResult.Created;
        }
    }

    public bool ValidateLogin(string email, string password)
    {
        string normalizedEmail = NormalizeEmail(email);

        lock (gate)
        {
            return users.TryGetValue(normalizedEmail, out UserAccount? user) && user.Password == password;
        }
    }

    public async Task<RegisterResult> SetPasswordAsync(string email, string password)
    {
        string normalizedEmail = NormalizeEmail(email);

        if (password.Length < 4)
        {
            return RegisterResult.InvalidPassword;
        }

        lock (gate)
        {
            if (!users.TryGetValue(normalizedEmail, out UserAccount? user))
            {
                return RegisterResult.InvalidEmail;
            }

            user.Password = password;
        }

        await SaveAsync();
        return RegisterResult.Created;
    }

    public UserAccount? GetUser(string email)
    {
        string normalizedEmail = NormalizeEmail(email);

        lock (gate)
        {
            return users.TryGetValue(normalizedEmail, out UserAccount? user) ? user : null;
        }
    }

    public async Task SetUsedBytesAsync(string email, long usedBytes)
    {
        string normalizedEmail = NormalizeEmail(email);

        lock (gate)
        {
            if (users.TryGetValue(normalizedEmail, out UserAccount? user))
            {
                user.UsedBytes = usedBytes;
            }
        }

        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        List<UserAccount> snapshot;

        lock (gate)
        {
            snapshot = users.Values.OrderBy(user => user.Username).ToList();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? ".");

        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        string json = JsonSerializer.Serialize(snapshot, options);
        await File.WriteAllTextAsync(filePath, json);
    }

    private static string NormalizeEmail(string email)
    {
        return email.Trim().ToLowerInvariant();
    }

    private static bool IsValidEmail(string email)
    {
        return email.Length is >= 3 and <= 254 && EmailRegex.IsMatch(email);
    }
}
