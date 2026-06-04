using System.Net;
using System.Security.Cryptography;

LoadDotEnv(".env");

const long bytesPerGb = 1024L * 1024 * 1024;
long quotaGb = TryGetLongEnvironment("SKYVAULT_QUOTA_GB", out long configuredQuotaGb)
    ? Math.Max(configuredQuotaGb, 1)
    : 5;
long quotaBytes = quotaGb * bytesPerGb;
int port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out int configuredPort)
    ? configuredPort
    : 8080;
string dataDirectory = Environment.GetEnvironmentVariable("SKYVAULT_DATA_DIR") ?? "data";
string filesDirectory = Path.Combine(dataDirectory, "files");
string storageDirectories = Environment.GetEnvironmentVariable("SKYVAULT_STORAGE_DIRS") ?? filesDirectory;
string storageMode = Environment.GetEnvironmentVariable("SKYVAULT_STORAGE_MODE") ?? "single";
string[] fileStorageDirectories = storageDirectories
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

Directory.CreateDirectory(dataDirectory);
AppLog.Initialize("log.txt");
ServerRuntime.InstanceId = Guid.NewGuid().ToString("N");

var userStore = new UserStore(Path.Combine(dataDirectory, "users.json"), quotaBytes);
await userStore.LoadAsync();

var fileStorage = new FileStorage(fileStorageDirectories, storageMode, userStore);
var emailSender = EmailSender.FromEnvironment();
ServerOptions serverOptions = ServerOptions.FromEnvironment();
var server = new CloudWebServer(IPAddress.Any, port, userStore, fileStorage, emailSender, serverOptions);
using var shutdown = new CancellationTokenSource();

AppLog.Info("SkyVault is running.");
AppLog.Info($"Build: {BuildInfo.Version}");
AppLog.Info($"Server session: {ServerRuntime.InstanceId}");
AppLog.Info($"Open: http://127.0.0.1:{port}");
AppLog.Info($"Data directory: {dataDirectory}");
AppLog.Info($"Storage mode: {storageMode}");
AppLog.Info($"Storage locations: {string.Join(", ", fileStorageDirectories)}");
AppLog.Info("User files are stored in: <storage location>/<email>/");
AppLog.Info($"Default user quota: {quotaGb} GB");
AppLog.Info($"CPU cores visible to .NET: {Environment.ProcessorCount}");
AppLog.Info($"Server limits: connections={serverOptions.MaxConnections}, requests={serverOptions.MaxActiveRequests}, ui={serverOptions.MaxUiRequests}, uploads={serverOptions.MaxUploads}, downloads={serverOptions.MaxDownloads}, fileOps={serverOptions.MaxFileOperations}");
AppLog.Info($"Per-user limits: uploads={serverOptions.MaxUploadsPerUser}, downloads={serverOptions.MaxDownloadsPerUser}, fileOps={serverOptions.MaxFileOperationsPerUser}");
AppLog.Info($"Queue timeouts: ui={serverOptions.UiQueueTimeoutSeconds}s, upload={serverOptions.UploadQueueTimeoutSeconds}s, download={serverOptions.DownloadQueueTimeoutSeconds}s, fileOps={serverOptions.FileOperationQueueTimeoutSeconds}s");
AppLog.Info($"I/O timeouts: header={serverOptions.HeaderReadTimeoutSeconds}s, idle={serverOptions.ClientIdleTimeoutSeconds}s, upload={serverOptions.UploadTimeoutSeconds}s, download={serverOptions.DownloadTimeoutSeconds}s");

if (TryGetLongEnvironment("SKYVAULT_MAX_RAM_MB", out long configuredRamMb))
{
    AppLog.Info($"Configured RAM limit: {configuredRamMb} MB");
}

if (!emailSender.IsConfigured)
{
    AppLog.Warn("SMTP is not configured. Set SMTP_HOST, SMTP_PORT, SMTP_USER, SMTP_PASS and SMTP_FROM.");
}

AppLog.Info("Press Ctrl+C to stop the server.");

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    AppLog.Info("Shutdown requested. Stopping listener and waiting for active requests.");
    shutdown.Cancel();
};

await server.StartAsync(shutdown.Token);

static bool TryGetLongEnvironment(string name, out long value)
{
    return long.TryParse(Environment.GetEnvironmentVariable(name), out value);
}

static void LoadDotEnv(string path)
{
    if (!File.Exists(path))
    {
        return;
    }

    foreach (string rawLine in File.ReadAllLines(path))
    {
        string line = rawLine.Trim();

        if (line.Length == 0 || line.StartsWith('#') || !line.Contains('=', StringComparison.Ordinal))
        {
            continue;
        }

        int separator = line.IndexOf('=');
        string key = line[..separator].Trim();
        string value = line[(separator + 1)..].Trim();

        if (key.Length == 0 || Environment.GetEnvironmentVariable(key) is not null)
        {
            continue;
        }

        if ((value.StartsWith('\'') && value.EndsWith('\'')) || (value.StartsWith('"') && value.EndsWith('"')))
        {
            value = value[1..^1];
        }

        Environment.SetEnvironmentVariable(key, value);
    }
}
