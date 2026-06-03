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

var userStore = new UserStore(Path.Combine(dataDirectory, "users.json"), quotaBytes);
await userStore.LoadAsync();

var fileStorage = new FileStorage(fileStorageDirectories, storageMode, userStore);
var emailSender = EmailSender.FromEnvironment();
var server = new CloudWebServer(IPAddress.Any, port, userStore, fileStorage, emailSender);

Console.WriteLine("SkyVault is running.");
Console.WriteLine($"Build: {BuildInfo.Version}");
Console.WriteLine($"Open: http://127.0.0.1:{port}");
Console.WriteLine($"Data directory: {dataDirectory}");
Console.WriteLine($"Storage mode: {storageMode}");
Console.WriteLine($"Storage locations: {string.Join(", ", fileStorageDirectories)}");
Console.WriteLine("User files are stored in: <storage location>/<email>/");
Console.WriteLine($"Default user quota: {quotaGb} GB");
Console.WriteLine($"CPU cores visible to .NET: {Environment.ProcessorCount}");

if (TryGetLongEnvironment("SKYVAULT_MAX_RAM_MB", out long configuredRamMb))
{
    Console.WriteLine($"Configured RAM limit: {configuredRamMb} MB");
}

if (!emailSender.IsConfigured)
{
    Console.WriteLine("SMTP is not configured. Set SMTP_HOST, SMTP_PORT, SMTP_USER, SMTP_PASS and SMTP_FROM.");
}

Console.WriteLine("Press Ctrl+C to stop the server.");

await server.StartAsync();

static bool TryGetLongEnvironment(string name, out long value)
{
    return long.TryParse(Environment.GetEnvironmentVariable(name), out value);
}
