public sealed class UserAccount
{
    public required string Username { get; init; }
    public required string Password { get; set; }
    public long QuotaBytes { get; init; }
    public long UsedBytes { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}
