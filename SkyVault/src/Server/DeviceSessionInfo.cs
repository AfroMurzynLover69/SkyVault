public sealed class DeviceSessionInfo
{
    public required string SessionId { get; init; }
    public required string Email { get; init; }
    public required string IpAddress { get; set; }
    public required string DeviceName { get; set; }
    public required string SystemName { get; set; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset LastSeenAt { get; set; }
    public int UploadedFiles { get; set; }
    public int DeletedFiles { get; set; }
    public int CopiedOrMovedFiles { get; set; }

    public TimeSpan Duration => LastSeenAt - StartedAt;
}
