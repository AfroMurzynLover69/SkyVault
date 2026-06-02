using System.Net;

string html = PageContent.GetHtml();
using var certificate = CertificateFactory.CreateLocalhostCertificate();

var server = new HttpsServer(IPAddress.Loopback, 5001, certificate, html);

Console.WriteLine("HTTPS site is running at:");
Console.WriteLine("https://127.0.0.1:5001");
Console.WriteLine("Press Ctrl+C to stop the server.");

await server.StartAsync();
