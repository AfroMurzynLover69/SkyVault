public sealed record PendingRegistration(
    string Email,
    string AuthVerifier,
    string PasswordSalt,
    string WrappedMasterKey,
    string WrappedMasterKeyIv,
    string Code,
    DateTimeOffset ExpiresAt);
