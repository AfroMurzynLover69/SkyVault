using System.Net;

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

var userStore = new UserStore(Path.Combine(dataDirectory, "users.json"), quotaBytes);
await userStore.LoadAsync();

var fileStorage = new FileStorage(fileStorageDirectories, storageMode, userStore);
var emailSender = EmailSender.FromEnvironment();
var server = new CloudWebServer(IPAddress.Any, port, userStore, fileStorage, emailSender);

AppLog.Info("SkyVault is running.");
AppLog.Info($"Build: {BuildInfo.Version}");
AppLog.Info($"Open: http://127.0.0.1:{port}");
AppLog.Info($"Data directory: {dataDirectory}");
AppLog.Info($"Storage mode: {storageMode}");
AppLog.Info($"Storage locations: {string.Join(", ", fileStorageDirectories)}");
AppLog.Info("User files are stored in: <storage location>/<email>/");
AppLog.Info($"Default user quota: {quotaGb} GB");
AppLog.Info($"CPU cores visible to .NET: {Environment.ProcessorCount}");

if (TryGetLongEnvironment("SKYVAULT_MAX_RAM_MB", out long configuredRamMb))
{
    AppLog.Info($"Configured RAM limit: {configuredRamMb} MB");
}

if (!emailSender.IsConfigured)
{
    AppLog.Warn("SMTP is not configured. Set SMTP_HOST, SMTP_PORT, SMTP_USER, SMTP_PASS and SMTP_FROM.");
}

AppLog.Info("Press Ctrl+C to stop the server.");

await server.StartAsync();

static bool TryGetLongEnvironment(string name, out long value)
{
    return long.TryParse(Environment.GetEnvironmentVariable(name), out value);
}
