public sealed class UserAccount
{
    public required string Username { get; init; }
    public string? AuthVerifierHash { get; set; }
    public string? PasswordSalt { get; set; }
    public string? WrappedMasterKey { get; set; }
    public string? WrappedMasterKeyIv { get; set; }
    public long QuotaBytes { get; init; }
    public long UsedBytes { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}
