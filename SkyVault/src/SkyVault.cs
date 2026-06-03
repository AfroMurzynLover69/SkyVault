using System.Net;

const long quotaBytes = 5L * 1024 * 1024 * 1024;
int port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out int configuredPort)
    ? configuredPort
    : 8080;

Directory.CreateDirectory("data");

var userStore = new UserStore(Path.Combine("data", "users.json"), quotaBytes);
await userStore.LoadAsync();

var fileStorage = new FileStorage(Path.Combine("data", "files"), userStore);
var emailSender = EmailSender.FromEnvironment();
var server = new CloudWebServer(IPAddress.Any, port, userStore, fileStorage, emailSender);

Console.WriteLine("SkyVault is running.");
Console.WriteLine($"Open: http://127.0.0.1:{port}");
Console.WriteLine("User files are stored in: data/files/<email>/");

if (!emailSender.IsConfigured)
{
    Console.WriteLine("SMTP is not configured. Set SMTP_HOST, SMTP_PORT, SMTP_USER, SMTP_PASS and SMTP_FROM.");
}

Console.WriteLine("Press Ctrl+C to stop the server.");

await server.StartAsync();
