public sealed record PendingRegistration(
    string Email,
    string Password,
    string Code,
    DateTimeOffset ExpiresAt);
