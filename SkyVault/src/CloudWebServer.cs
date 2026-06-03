using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

public sealed class CloudWebServer
{
    private readonly IPAddress address;
    private readonly int port;
    private readonly UserStore userStore;
    private readonly FileStorage fileStorage;
    private readonly EmailSender emailSender;
    private readonly ConcurrentDictionary<string, string> sessions = new();
    private readonly ConcurrentDictionary<string, PendingRegistration> pendingRegistrations = new(StringComparer.OrdinalIgnoreCase);

    public CloudWebServer(IPAddress address, int port, UserStore userStore, FileStorage fileStorage, EmailSender emailSender)
    {
        this.address = address;
        this.port = port;
        this.userStore = userStore;
        this.fileStorage = fileStorage;
        this.emailSender = emailSender;
    }

    public async Task StartAsync()
    {
        TcpListener listener = new TcpListener(address, port);
        listener.Start();

        while (true)
        {
            TcpClient client = await listener.AcceptTcpClientAsync();
            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            await using NetworkStream stream = client.GetStream();
            HttpRequest? request = await HttpRequest.ReadAsync(stream);

            if (request is null)
            {
                return;
            }

            HttpResponse response = await HandleRequestAsync(request);
            await response.WriteAsync(stream);
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
        finally
        {
            client.Close();
        }
    }

    private async Task<HttpResponse> HandleRequestAsync(HttpRequest request)
    {
        string? email = GetLoggedInEmail(request);

        if ((request.Method == "GET" || request.Method == "HEAD") && request.Path == "/")
        {
            string mode = request.Query.GetValueOrDefault("mode", "login");
            return Html(PageRenderer.RenderHome(email, GetUser(email), fileStorage.GetFiles(email), mode));
        }

        if (request.Method == "POST" && request.Path == "/register")
        {
            string newEmail = NormalizeEmail(request.Form.GetValueOrDefault("email", ""));
            string password = request.Form.GetValueOrDefault("password", "");
            RegisterResult result = userStore.ValidateRegistration(newEmail, password);

            if (result != RegisterResult.Created)
            {
                return Html(PageRenderer.RenderHome(null, null, [], "register", RegistrationMessage(result)));
            }

            string code = GenerateVerificationCode();
            EmailSendResult sendResult = await emailSender.SendVerificationCodeAsync(newEmail, code);

            if (sendResult != EmailSendResult.Sent)
            {
                return Html(PageRenderer.RenderHome(null, null, [], "register", EmailMessage(sendResult)));
            }

            pendingRegistrations[newEmail] = new PendingRegistration(
                newEmail,
                password,
                code,
                DateTimeOffset.UtcNow.AddMinutes(10));

            return Html(PageRenderer.RenderVerification(newEmail, "Verification code sent. Check your mailbox."));
        }

        if (request.Method == "POST" && request.Path == "/verify")
        {
            string verifyEmail = NormalizeEmail(request.Form.GetValueOrDefault("email", ""));
            string code = request.Form.GetValueOrDefault("code", "").Trim();

            if (!pendingRegistrations.TryGetValue(verifyEmail, out PendingRegistration? pending))
            {
                return Html(PageRenderer.RenderHome(null, null, [], "register", "Register again to get a new code."));
            }

            if (pending.ExpiresAt < DateTimeOffset.UtcNow)
            {
                pendingRegistrations.TryRemove(verifyEmail, out _);
                return Html(PageRenderer.RenderHome(null, null, [], "register", "Verification code expired. Register again."));
            }

            if (!string.Equals(code, pending.Code, StringComparison.Ordinal))
            {
                return Html(PageRenderer.RenderVerification(verifyEmail, "Wrong verification code."));
            }

            RegisterResult result = await userStore.RegisterAsync(pending.Email, pending.Password);

            if (result != RegisterResult.Created)
            {
                pendingRegistrations.TryRemove(verifyEmail, out _);
                return Html(PageRenderer.RenderHome(null, null, [], "register", RegistrationMessage(result)));
            }

            pendingRegistrations.TryRemove(verifyEmail, out _);
            fileStorage.CreateUserDirectory(pending.Email);
            string sessionId = CreateSession(pending.Email);
            return Redirect("/", sessionId);
        }

        if (request.Method == "POST" && request.Path == "/login")
        {
            string login = NormalizeEmail(request.Form.GetValueOrDefault("email", ""));
            string password = request.Form.GetValueOrDefault("password", "");

            if (userStore.ValidateLogin(login, password))
            {
                string sessionId = CreateSession(login);
                return Redirect("/", sessionId);
            }

            return Html(PageRenderer.RenderHome(null, null, [], "login", "Wrong email or password."));
        }

        if (request.Method == "POST" && request.Path == "/files/upload")
        {
            if (email is null)
            {
                return Redirect("/");
            }

            MultipartFile? file = request.GetUploadedFile("file");

            if (file is null)
            {
                return Html(PageRenderer.RenderHome(email, GetUser(email), fileStorage.GetFiles(email), "login", "Choose a file first."));
            }

            FileCreateResult result = await fileStorage.SaveUploadedFileAsync(email, file);

            string message = result switch
            {
                FileCreateResult.Created => "File uploaded.",
                FileCreateResult.InvalidFileName => "File name is invalid.",
                FileCreateResult.QuotaExceeded => "File is too large for your remaining space.",
                _ => "Could not upload file."
            };

            return Html(PageRenderer.RenderHome(email, GetUser(email), fileStorage.GetFiles(email), "login", message));
        }

        if (request.Method == "POST" && request.Path == "/logout")
        {
            string? sessionId = request.GetCookie("cloud_session");

            if (sessionId is not null)
            {
                sessions.TryRemove(sessionId, out _);
            }

            HttpResponse response = Redirect("/");
            response.Headers["Set-Cookie"] = "cloud_session=; Path=/; Max-Age=0; SameSite=Lax";
            return response;
        }

        return Html(PageRenderer.RenderNotFound(), 404, "Not Found");
    }

    private UserAccount? GetUser(string? email)
    {
        return email is null ? null : userStore.GetUser(email);
    }

    private string? GetLoggedInEmail(HttpRequest request)
    {
        string? sessionId = request.GetCookie("cloud_session");
        return sessionId is not null && sessions.TryGetValue(sessionId, out string? email) ? email : null;
    }

    private string CreateSession(string email)
    {
        string sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        sessions[sessionId] = email;
        return sessionId;
    }

    private static string NormalizeEmail(string email)
    {
        return email.Trim().ToLowerInvariant();
    }

    private static string GenerateVerificationCode()
    {
        return RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    }

    private static string RegistrationMessage(RegisterResult result)
    {
        return result switch
        {
            RegisterResult.InvalidEmail => "Enter a valid email address.",
            RegisterResult.InvalidPassword => "Password must be at least 4 characters.",
            RegisterResult.AlreadyExists => "Email already has an account.",
            _ => "Registration failed."
        };
    }

    private static string EmailMessage(EmailSendResult result)
    {
        return result switch
        {
            EmailSendResult.NotConfigured => "Email sending is not configured. Set SMTP_HOST, SMTP_PORT, SMTP_USER, SMTP_PASS and SMTP_FROM.",
            EmailSendResult.AuthenticationFailed => "SMTP login failed. Check SMTP_USER, SMTP_PASS and enable mail client access in your mailbox settings.",
            EmailSendResult.Failed => "Could not send verification email. Check SMTP settings.",
            _ => "Could not send verification email."
        };
    }

    private static HttpResponse Html(string body, int statusCode = 200, string reason = "OK")
    {
        return new HttpResponse(statusCode, reason, body);
    }

    private static HttpResponse Redirect(string location, string? sessionId = null)
    {
        var response = new HttpResponse(303, "See Other", "");
        response.Headers["Location"] = location;

        if (sessionId is not null)
        {
            response.Headers["Set-Cookie"] = $"cloud_session={sessionId}; Path=/; HttpOnly; SameSite=Lax";
        }

        return response;
    }
}
