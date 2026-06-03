public sealed record PendingPasswordReset(
    string Email,
    string Token,
    DateTimeOffset ExpiresAt);
