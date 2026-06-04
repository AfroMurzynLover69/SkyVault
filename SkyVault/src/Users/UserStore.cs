using System.Security.Cryptography;
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

    public async Task<RegisterResult> RegisterAsync(string email, string authVerifier, string passwordSalt, string wrappedMasterKey, string wrappedMasterKeyIv)
    {
        string normalizedEmail = NormalizeEmail(email);
        RegisterResult validation = ValidateRegistration(normalizedEmail, authVerifier, passwordSalt, wrappedMasterKey, wrappedMasterKeyIv);

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
                AuthVerifierHash = HashAuthVerifier(authVerifier),
                PasswordSalt = passwordSalt,
                WrappedMasterKey = wrappedMasterKey,
                WrappedMasterKeyIv = wrappedMasterKeyIv,
                QuotaBytes = defaultQuotaBytes,
                UsedBytes = 0,
                CreatedAt = DateTimeOffset.UtcNow
            };
        }

        await SaveAsync();
        return RegisterResult.Created;
    }

    public RegisterResult ValidateRegistration(string email, string authVerifier, string passwordSalt, string wrappedMasterKey, string wrappedMasterKeyIv)
    {
        string normalizedEmail = NormalizeEmail(email);

        if (!IsValidEmail(normalizedEmail))
        {
            return RegisterResult.InvalidEmail;
        }

        if (!IsValidCryptoPackage(authVerifier, passwordSalt, wrappedMasterKey, wrappedMasterKeyIv))
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

    public bool ValidateLogin(string email, string authVerifier)
    {
        string normalizedEmail = NormalizeEmail(email);

        if (!TryHashAuthVerifier(authVerifier, out string verifierHash))
        {
            return false;
        }

        lock (gate)
        {
            if (!users.TryGetValue(normalizedEmail, out UserAccount? user)
                || string.IsNullOrWhiteSpace(user.AuthVerifierHash))
            {
                return false;
            }

            byte[] expected = Convert.FromBase64String(user.AuthVerifierHash);
            byte[] actual = Convert.FromBase64String(verifierHash);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
    }

    public async Task<RegisterResult> SetPasswordAsync(string email, string authVerifier, string passwordSalt, string wrappedMasterKey, string wrappedMasterKeyIv)
    {
        string normalizedEmail = NormalizeEmail(email);

        if (!IsValidCryptoPackage(authVerifier, passwordSalt, wrappedMasterKey, wrappedMasterKeyIv))
        {
            return RegisterResult.InvalidPassword;
        }

        lock (gate)
        {
            if (!users.TryGetValue(normalizedEmail, out UserAccount? user))
            {
                return RegisterResult.InvalidEmail;
            }

            user.AuthVerifierHash = HashAuthVerifier(authVerifier);
            user.PasswordSalt = passwordSalt;
            user.WrappedMasterKey = wrappedMasterKey;
            user.WrappedMasterKeyIv = wrappedMasterKeyIv;
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

    public UserCryptoMaterial? GetCryptoMaterial(string email)
    {
        string normalizedEmail = NormalizeEmail(email);

        lock (gate)
        {
            if (!users.TryGetValue(normalizedEmail, out UserAccount? user)
                || string.IsNullOrWhiteSpace(user.PasswordSalt)
                || string.IsNullOrWhiteSpace(user.WrappedMasterKey)
                || string.IsNullOrWhiteSpace(user.WrappedMasterKeyIv))
            {
                return null;
            }

            return new UserCryptoMaterial(user.PasswordSalt, user.WrappedMasterKey, user.WrappedMasterKeyIv);
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

    private static bool IsValidCryptoPackage(string authVerifier, string passwordSalt, string wrappedMasterKey, string wrappedMasterKeyIv)
    {
        return HasBase64ByteLength(authVerifier, 32)
            && HasBase64ByteLength(passwordSalt, 16)
            && HasMinimumBase64ByteLength(wrappedMasterKey, 48)
            && HasBase64ByteLength(wrappedMasterKeyIv, 12);
    }

    private static string HashAuthVerifier(string authVerifier)
    {
        if (!TryHashAuthVerifier(authVerifier, out string hash))
        {
            throw new ArgumentException("Invalid auth verifier.", nameof(authVerifier));
        }

        return hash;
    }

    private static bool TryHashAuthVerifier(string authVerifier, out string hash)
    {
        hash = "";

        try
        {
            byte[] verifier = Convert.FromBase64String(authVerifier);

            if (verifier.Length != 32)
            {
                return false;
            }

            hash = Convert.ToBase64String(SHA256.HashData(verifier));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool HasBase64ByteLength(string value, int expectedLength)
    {
        try
        {
            return Convert.FromBase64String(value).Length == expectedLength;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool HasMinimumBase64ByteLength(string value, int minimumLength)
    {
        try
        {
            return Convert.FromBase64String(value).Length >= minimumLength;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed record UserCryptoMaterial(string PasswordSalt, string WrappedMasterKey, string WrappedMasterKeyIv);
