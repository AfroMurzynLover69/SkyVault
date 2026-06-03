using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

public sealed class EmailSender
{
    private readonly string host;
    private readonly int port;
    private readonly string username;
    private readonly string password;
    private readonly string from;
    private readonly bool enableSsl;

    private EmailSender(string host, int port, string username, string password, string from, bool enableSsl)
    {
        this.host = host;
        this.port = port;
        this.username = username;
        this.password = password;
        this.from = from;
        this.enableSsl = enableSsl;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(from);

    public static EmailSender FromEnvironment()
    {
        string host = Environment.GetEnvironmentVariable("SMTP_HOST") ?? "";
        string username = Environment.GetEnvironmentVariable("SMTP_USER") ?? "";
        string password = Environment.GetEnvironmentVariable("SMTP_PASS") ?? "";
        string from = Environment.GetEnvironmentVariable("SMTP_FROM") ?? username;
        bool enableSsl = !string.Equals(Environment.GetEnvironmentVariable("SMTP_SSL"), "false", StringComparison.OrdinalIgnoreCase);

        int port = int.TryParse(Environment.GetEnvironmentVariable("SMTP_PORT"), out int configuredPort)
            ? configuredPort
            : 587;

        return new EmailSender(host, port, username, password, from, enableSsl);
    }

    public async Task<EmailSendResult> SendVerificationCodeAsync(string recipient, string code)
    {
        if (!IsConfigured)
        {
            return EmailSendResult.NotConfigured;
        }

        try
        {
            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(host, port);

            Stream stream = tcpClient.GetStream();

            if (enableSsl && port == 465)
            {
                stream = await CreateTlsStreamAsync(stream);
            }

            await ReadExpectedAsync(stream, 220);
            await SendCommandAsync(stream, "EHLO localhost", 250);

            if (enableSsl && port != 465)
            {
                await SendCommandAsync(stream, "STARTTLS", 220);
                stream = await CreateTlsStreamAsync(stream);
                await SendCommandAsync(stream, "EHLO localhost", 250);
            }

            if (!string.IsNullOrWhiteSpace(username))
            {
                await SendCommandAsync(stream, "AUTH LOGIN", 334);
                await SendCommandAsync(stream, Convert.ToBase64String(Encoding.UTF8.GetBytes(username)), 334);
                await SendCommandAsync(stream, Convert.ToBase64String(Encoding.UTF8.GetBytes(password)), 235);
            }

            await SendCommandAsync(stream, $"MAIL FROM:<{from}>", 250);
            await SendCommandAsync(stream, $"RCPT TO:<{recipient}>", 250, 251);
            await SendCommandAsync(stream, "DATA", 354);
            await WriteLineAsync(stream, BuildMessage(recipient, code));
            await SendCommandAsync(stream, ".", 250);
            await SendCommandAsync(stream, "QUIT", 221);

            return EmailSendResult.Sent;
        }
        catch (SmtpCommandException exception) when (exception.ResponseCode == 535)
        {
            Console.WriteLine($"Could not send verification email to {recipient}: {exception.Message}");
            return EmailSendResult.AuthenticationFailed;
        }
        catch (Exception exception) when (exception is IOException or SocketException or InvalidOperationException or FormatException or AuthenticationException)
        {
            Console.WriteLine($"Could not send verification email to {recipient}: {exception.Message}");
            return EmailSendResult.Failed;
        }
    }

    private async Task<SslStream> CreateTlsStreamAsync(Stream stream)
    {
        var tlsStream = new SslStream(stream, false);
        await tlsStream.AuthenticateAsClientAsync(host);
        return tlsStream;
    }

    private string BuildMessage(string recipient, string code)
    {
        return string.Join("\r\n", [
            $"From: SkyVault <{from}>",
            $"To: {recipient}",
            "Subject: SkyVault verification code",
            "MIME-Version: 1.0",
            "Content-Type: text/plain; charset=utf-8",
            "",
            $"Your SkyVault verification code is: {code}",
            "",
            "This code expires in 10 minutes."
        ]);
    }

    private static async Task SendCommandAsync(Stream stream, string command, params int[] expectedCodes)
    {
        await WriteLineAsync(stream, command);
        await ReadExpectedAsync(stream, expectedCodes);
    }

    private static async Task ReadExpectedAsync(Stream stream, params int[] expectedCodes)
    {
        SmtpResponse response = await ReadResponseAsync(stream);

        if (!expectedCodes.Contains(response.Code))
        {
            string expected = string.Join(" or ", expectedCodes);
            throw new SmtpCommandException(response.Code, $"SMTP command failed. Expected {expected}, got {response.Code}: {response.Message}");
        }
    }

    private static async Task<SmtpResponse> ReadResponseAsync(Stream stream)
    {
        int code = 0;
        var lines = new List<string>();

        while (true)
        {
            string line = await ReadLineAsync(stream);

            if (line.Length < 3 || !int.TryParse(line[..3], out code))
            {
                throw new InvalidOperationException($"Invalid SMTP response: {line}");
            }

            lines.Add(line.Length > 4 ? line[4..] : "");

            if (line.Length < 4 || line[3] != '-')
            {
                return new SmtpResponse(code, string.Join(" | ", lines));
            }
        }
    }

    private static async Task<string> ReadLineAsync(Stream stream)
    {
        var bytes = new List<byte>();
        byte[] buffer = new byte[1];

        while (true)
        {
            int read = await stream.ReadAsync(buffer);

            if (read == 0)
            {
                throw new IOException("SMTP server closed the connection.");
            }

            if (buffer[0] == '\n')
            {
                if (bytes.Count > 0 && bytes[^1] == '\r')
                {
                    bytes.RemoveAt(bytes.Count - 1);
                }

                return Encoding.ASCII.GetString(bytes.ToArray());
            }

            bytes.Add(buffer[0]);
        }
    }

    private static async Task WriteLineAsync(Stream stream, string line)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\r\n");
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private sealed record SmtpResponse(int Code, string Message);

    private sealed class SmtpCommandException : InvalidOperationException
    {
        public SmtpCommandException(int responseCode, string message) : base(message)
        {
            ResponseCode = responseCode;
        }

        public int ResponseCode { get; }
    }
}
