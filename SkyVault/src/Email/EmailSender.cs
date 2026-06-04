using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

public sealed class EmailSender
{
    private const string LogoContentId = "skyvault-logo";
    private static readonly string LogoPath = Path.Combine("SkyVault", "assets", "icons", "logo.png");
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
        return await SendMessageAsync(
            recipient,
            "SkyVault verification code",
            BuildVerificationMessage);

        string BuildVerificationMessage()
        {
            string safeRecipient = WebUtility.HtmlEncode(recipient);
            string safeCode = WebUtility.HtmlEncode(code);

            return BuildHtmlShell(
                "Verify your email",
                $"Use this code to finish creating the SkyVault account for {safeRecipient}.",
                $"""
                <div style="background:#eef4ff;border:1px solid #bfdbfe;border-radius:8px;padding:22px;text-align:center;">
                  <div style="font-size:34px;line-height:1;font-weight:700;color:#1d4ed8;">{safeCode}</div>
                </div>
                """,
                "This code expires in 10 minutes. If you did not request it, you can ignore this email.");
        }
    }

    private async Task<EmailSendResult> SendMessageAsync(string recipient, string subject, Func<string> buildHtml)
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
            await WriteLineAsync(stream, BuildMessage(recipient, subject, buildHtml()));
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

    private string BuildMessage(string recipient, string subject, string html)
    {
        string boundary = "skyvault-" + Guid.NewGuid().ToString("N");

        return string.Join("\r\n", [
            $"From: SkyVault <{from}>",
            $"To: {recipient}",
            $"Subject: {subject}",
            "MIME-Version: 1.0",
            $"Content-Type: multipart/related; boundary=\"{boundary}\"",
            "",
            $"--{boundary}",
            "Content-Type: text/html; charset=utf-8",
            "Content-Transfer-Encoding: 8bit",
            "",
            html,
            BuildLogoPart(boundary),
            $"--{boundary}--"
        ]);
    }

    private static string BuildHtmlShell(string title, string intro, string actionHtml, string footer)
    {
        string logoHtml = File.Exists(LogoPath)
            ? $"""<img src="cid:{LogoContentId}" alt="SkyVault" style="display:block;width:180px;max-width:100%;height:auto;border:0;">"""
            : """<div style="font-size:20px;font-weight:700;color:#111827;">SkyVault</div>""";

        return $$"""
        <!doctype html>
        <html>
        <body style="margin:0;padding:0;background:#f6f8fb;font-family:Arial,sans-serif;color:#1f2937;">
          <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background:#f6f8fb;padding:32px 16px;">
            <tr>
              <td align="center">
                <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="max-width:560px;background:#ffffff;border:1px solid #e2e8f0;border-radius:8px;overflow:hidden;">
                  <tr>
                    <td style="padding:28px 30px 18px;">
                      {{logoHtml}}
                    </td>
                  </tr>
                  <tr>
                    <td style="padding:0 30px 8px;">
                      <h1 style="margin:0 0 10px;font-size:28px;line-height:1.2;color:#111827;">{{title}}</h1>
                      <p style="margin:0;color:#64748b;font-size:15px;line-height:1.6;">{{intro}}</p>
                    </td>
                  </tr>
                  <tr>
                    <td style="padding:22px 30px;">
                      {{actionHtml}}
                    </td>
                  </tr>
                  <tr>
                    <td style="padding:0 30px 30px;">
                      <p style="margin:0;color:#64748b;font-size:14px;line-height:1.6;">{{footer}}</p>
                    </td>
                  </tr>
                </table>
              </td>
            </tr>
          </table>
        </body>
        </html>
        """;
    }

    private static string BuildLogoPart(string boundary)
    {
        if (!File.Exists(LogoPath))
        {
            return "";
        }

        string base64 = Convert.ToBase64String(File.ReadAllBytes(LogoPath));
        string encodedLines = string.Join("\r\n", Enumerable.Range(0, (base64.Length + 75) / 76)
            .Select(index =>
            {
                int start = index * 76;
                int length = Math.Min(76, base64.Length - start);
                return base64.Substring(start, length);
            }));

        return string.Join("\r\n", [
            $"--{boundary}",
            "Content-Type: image/png; name=\"logo.png\"",
            "Content-Transfer-Encoding: base64",
            $"Content-ID: <{LogoContentId}>",
            "Content-Disposition: inline; filename=\"logo.png\"",
            "",
            encodedLines
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
