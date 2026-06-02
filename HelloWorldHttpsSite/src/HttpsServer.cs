using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

public sealed class HttpsServer
{
    private readonly IPAddress address;
    private readonly int port;
    private readonly X509Certificate2 certificate;
    private readonly byte[] body;

    public HttpsServer(IPAddress address, int port, X509Certificate2 certificate, string html)
    {
        this.address = address;
        this.port = port;
        this.certificate = certificate;
        body = Encoding.UTF8.GetBytes(html);
    }

    public async Task StartAsync()
    {
        TcpListener listener = new TcpListener(address, port);
        listener.Start();

        while (true)
        {
            await HandleClientAsync(listener);
        }
    }

    private async Task HandleClientAsync(TcpListener listener)
    {
        try
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            await using NetworkStream stream = client.GetStream();
            await using SslStream sslStream = new SslStream(stream, false);

            await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            });

            await WriteResponseAsync(sslStream);
        }
        catch (AuthenticationException)
        {
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private async Task WriteResponseAsync(SslStream sslStream)
    {
        string header =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n";

        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        await sslStream.WriteAsync(headerBytes);
        await sslStream.WriteAsync(body);
    }
}
