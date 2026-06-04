public sealed record ServerOptions
{
    public int MaxConnections { get; init; } = 128;
    public int MaxActiveRequests { get; init; } = 64;
    public int MaxUiRequests { get; init; } = 48;
    public int MaxUploads { get; init; } = 2;
    public int MaxDownloads { get; init; } = 4;
    public int MaxFileOperations { get; init; } = 8;
    public int MaxUploadsPerUser { get; init; } = 1;
    public int MaxDownloadsPerUser { get; init; } = 2;
    public int MaxFileOperationsPerUser { get; init; } = 3;
    public int RequestQueueTimeoutSeconds { get; init; } = 5;
    public int UiQueueTimeoutSeconds { get; init; } = 15;
    public int UploadQueueTimeoutSeconds { get; init; } = 3600;
    public int DownloadQueueTimeoutSeconds { get; init; } = 3600;
    public int FileOperationQueueTimeoutSeconds { get; init; } = 300;
    public int HeaderReadTimeoutSeconds { get; init; } = 10;
    public int ClientIdleTimeoutSeconds { get; init; } = 60;
    public int UploadTimeoutSeconds { get; init; } = 3600;
    public int DownloadTimeoutSeconds { get; init; } = 3600;
    public int MaxHeaderBytes { get; init; } = 64 * 1024;
    public int MaxMultipartFieldBytes { get; init; } = 64 * 1024;
    public int MaxChunkUploadBytes { get; init; } = 8 * 1024 * 1024;
    public int MaxUploadSessionsPerUser { get; init; } = 8;
    public int UploadSessionTtlHours { get; init; } = 24;
    public int GracefulShutdownSeconds { get; init; } = 30;

    public static ServerOptions FromEnvironment()
    {
        return new ServerOptions
        {
            MaxConnections = GetInt("SKYVAULT_MAX_CONNECTIONS", "MAX_CONNECTIONS", 128),
            MaxActiveRequests = GetInt("SKYVAULT_MAX_ACTIVE_REQUESTS", "MAX_ACTIVE_REQUESTS", 64),
            MaxUiRequests = GetInt("SKYVAULT_MAX_UI_REQUESTS", "MAX_UI_REQUESTS", 48),
            MaxUploads = GetInt("SKYVAULT_MAX_UPLOADS", "MAX_UPLOADS", 2),
            MaxDownloads = GetInt("SKYVAULT_MAX_DOWNLOADS", "MAX_DOWNLOADS", 4),
            MaxFileOperations = GetInt("SKYVAULT_MAX_FILE_OPERATIONS", "MAX_FILE_OPERATIONS", 8),
            MaxUploadsPerUser = GetInt("SKYVAULT_MAX_UPLOADS_PER_USER", "MAX_UPLOADS_PER_USER", 1),
            MaxDownloadsPerUser = GetInt("SKYVAULT_MAX_DOWNLOADS_PER_USER", "MAX_DOWNLOADS_PER_USER", 2),
            MaxFileOperationsPerUser = GetInt("SKYVAULT_MAX_FILE_OPERATIONS_PER_USER", "MAX_FILE_OPERATIONS_PER_USER", 3),
            RequestQueueTimeoutSeconds = GetInt("SKYVAULT_REQUEST_QUEUE_TIMEOUT_SECONDS", "REQUEST_QUEUE_TIMEOUT_SECONDS", 5),
            UiQueueTimeoutSeconds = GetInt("SKYVAULT_UI_QUEUE_TIMEOUT_SECONDS", "UI_QUEUE_TIMEOUT_SECONDS", GetInt("SKYVAULT_REQUEST_QUEUE_TIMEOUT_SECONDS", "REQUEST_QUEUE_TIMEOUT_SECONDS", 15)),
            UploadQueueTimeoutSeconds = GetInt("SKYVAULT_UPLOAD_QUEUE_TIMEOUT_SECONDS", "UPLOAD_QUEUE_TIMEOUT_SECONDS", GetInt("SKYVAULT_REQUEST_QUEUE_TIMEOUT_SECONDS", "REQUEST_QUEUE_TIMEOUT_SECONDS", 3600)),
            DownloadQueueTimeoutSeconds = GetInt("SKYVAULT_DOWNLOAD_QUEUE_TIMEOUT_SECONDS", "DOWNLOAD_QUEUE_TIMEOUT_SECONDS", GetInt("SKYVAULT_REQUEST_QUEUE_TIMEOUT_SECONDS", "REQUEST_QUEUE_TIMEOUT_SECONDS", 3600)),
            FileOperationQueueTimeoutSeconds = GetInt("SKYVAULT_FILE_OPERATION_QUEUE_TIMEOUT_SECONDS", "FILE_OPERATION_QUEUE_TIMEOUT_SECONDS", GetInt("SKYVAULT_REQUEST_QUEUE_TIMEOUT_SECONDS", "REQUEST_QUEUE_TIMEOUT_SECONDS", 300)),
            HeaderReadTimeoutSeconds = GetInt("SKYVAULT_HEADER_READ_TIMEOUT_SECONDS", "HEADER_READ_TIMEOUT_SECONDS", 10),
            ClientIdleTimeoutSeconds = GetInt("SKYVAULT_CLIENT_IDLE_TIMEOUT_SECONDS", "CLIENT_IDLE_TIMEOUT_SECONDS", 60),
            UploadTimeoutSeconds = GetInt("SKYVAULT_UPLOAD_TIMEOUT_SECONDS", "UPLOAD_TIMEOUT_SECONDS", 3600),
            DownloadTimeoutSeconds = GetInt("SKYVAULT_DOWNLOAD_TIMEOUT_SECONDS", "DOWNLOAD_TIMEOUT_SECONDS", 3600),
            MaxHeaderBytes = GetInt("SKYVAULT_MAX_HEADER_BYTES", "MAX_HEADER_BYTES", 64 * 1024),
            MaxMultipartFieldBytes = GetInt("SKYVAULT_MAX_MULTIPART_FIELD_BYTES", "MAX_MULTIPART_FIELD_BYTES", 64 * 1024),
            MaxChunkUploadBytes = GetInt("SKYVAULT_MAX_CHUNK_UPLOAD_BYTES", "MAX_CHUNK_UPLOAD_BYTES", 8 * 1024 * 1024),
            MaxUploadSessionsPerUser = GetInt("SKYVAULT_MAX_UPLOAD_SESSIONS_PER_USER", "MAX_UPLOAD_SESSIONS_PER_USER", 8),
            UploadSessionTtlHours = GetInt("SKYVAULT_UPLOAD_SESSION_TTL_HOURS", "UPLOAD_SESSION_TTL_HOURS", 24),
            GracefulShutdownSeconds = GetInt("SKYVAULT_GRACEFUL_SHUTDOWN_SECONDS", "GRACEFUL_SHUTDOWN_SECONDS", 30)
        };
    }

    private static int GetInt(string preferredName, string fallbackName, int fallback)
    {
        string? value = Environment.GetEnvironmentVariable(preferredName)
            ?? Environment.GetEnvironmentVariable(fallbackName);

        return int.TryParse(value, out int parsed) && parsed > 0 ? parsed : fallback;
    }
}
