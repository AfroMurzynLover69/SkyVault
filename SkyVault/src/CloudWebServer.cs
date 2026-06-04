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
    private readonly ConcurrentDictionary<string, DeviceSessionInfo> deviceSessions = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> starredFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PendingRegistration> pendingRegistrations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PendingPasswordReset> pendingPasswordResets = new(StringComparer.Ordinal);

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

            string remoteAddress = GetRemoteAddress(client);
            HttpResponse response = await HandleRequestAsync(request, remoteAddress);
            await response.WriteAsync(stream);
        }
        catch (IOException)
        {
            AppLog.Warn("Client disconnected with an I/O error.");
        }
        catch (SocketException)
        {
            AppLog.Warn("Socket error while handling client.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Unhandled server error while handling client.", ex);
        }
        finally
        {
            client.Close();
        }
    }

    private async Task<HttpResponse> HandleRequestAsync(HttpRequest request, string remoteAddress)
    {
        string? sessionId = request.GetCookie("cloud_session");
        string? email = GetLoggedInEmail(request);

        if (email is not null && sessionId is not null)
        {
            TouchDeviceSession(sessionId, email, request, remoteAddress);
        }

        if ((request.Method == "GET" || request.Method == "HEAD") && request.Path == "/assets/logo.png")
        {
            return StaticFile(Path.Combine("SkyVault", "assets", "logo.png"), "image/png");
        }

        if ((request.Method == "GET" || request.Method == "HEAD") && request.Path.StartsWith("/assets/icons/", StringComparison.Ordinal))
        {
            string iconName = Path.GetFileName(request.Path);
            return StaticFile(Path.Combine("SkyVault", "assets", "icons", iconName), "image/svg+xml");
        }

        if ((request.Method == "GET" || request.Method == "HEAD") && request.Path == "/")
        {
            string mode = request.Query.GetValueOrDefault("mode", "login");
            string currentDirectory = NormalizeCloudPath(request.Query.GetValueOrDefault("path", ""));
            string view = NormalizeView(request.Query.GetValueOrDefault("view", "home"));
            return Html(PageRenderer.RenderHome(email, GetUser(email), fileStorage.GetFiles(email), mode, currentDirectory: currentDirectory, currentView: view, trashFiles: fileStorage.GetTrashFiles(email), deviceSessions: GetDeviceSessions(email), starredPaths: GetStarredPaths(email)));
        }

        if ((request.Method == "GET" || request.Method == "HEAD") && request.Path == "/storage")
        {
            UserAccount? account = GetUser(email);

            if (account is null)
            {
                return Redirect("/");
            }

            return Html(PageRenderer.RenderStorage(account, fileStorage.GetFiles(email)));
        }

        if ((request.Method == "GET" || request.Method == "HEAD") && request.Path == "/forgot-password")
        {
            return Html(PageRenderer.RenderForgotPassword());
        }

        if ((request.Method == "GET" || request.Method == "HEAD") && request.Path == "/files/open")
        {
            if (email is null)
            {
                return Redirect("/");
            }

            string filePath = request.Query.GetValueOrDefault("path", "");
            byte[]? content = await fileStorage.ReadFileAsync(email, filePath);

            if (content is null)
            {
                return Html(PageRenderer.RenderNotFound(), 404, "Not Found");
            }

            return Html(PageRenderer.RenderFilePreview(filePath, content));
        }

        if ((request.Method == "GET" || request.Method == "HEAD") && request.Path == "/files/raw")
        {
            if (email is null)
            {
                return Redirect("/");
            }

            string filePath = request.Query.GetValueOrDefault("path", "");
            byte[]? content = await fileStorage.ReadFileAsync(email, filePath);

            if (content is null)
            {
                return Html(PageRenderer.RenderNotFound(), 404, "Not Found");
            }

            var response = new HttpResponse(200, "OK", content, GetContentType(filePath));
            response.Headers["Content-Disposition"] = $"inline; filename=\"{EscapeHeaderFileName(Path.GetFileName(filePath))}\"";
            return response;
        }

        if ((request.Method == "GET" || request.Method == "HEAD") && request.Path == "/files/download")
        {
            if (email is null)
            {
                return Redirect("/");
            }

            string filePath = request.Query.GetValueOrDefault("path", "");
            byte[]? content = await fileStorage.ReadFileAsync(email, filePath);

            if (content is null)
            {
                return Html(PageRenderer.RenderNotFound(), 404, "Not Found");
            }

            var response = new HttpResponse(200, "OK", content, "application/octet-stream");
            response.Headers["Content-Disposition"] = $"attachment; filename=\"{EscapeHeaderFileName(Path.GetFileName(filePath))}\"";
            return response;
        }

        if (request.Method == "POST" && request.Path == "/files/download-zip")
        {
            if (email is null)
            {
                return Redirect("/");
            }

            IReadOnlyList<string> selectedFiles = ParseSelectedFiles(request.Form.GetValueOrDefault("paths", ""));
            byte[]? zip = await fileStorage.CreateZipAsync(email, selectedFiles);

            if (zip is null)
            {
                return Html(PageRenderer.RenderHome(email, GetUser(email), fileStorage.GetFiles(email), "login", "Select files first."));
            }

            var response = new HttpResponse(200, "OK", zip, "application/zip");
            response.Headers["Content-Disposition"] = "attachment; filename=\"skyvault-selection.zip\"";
            return response;
        }

        if (request.Method == "POST" && request.Path == "/files/move")
        {
            if (email is null)
            {
                return Redirect("/");
            }

            IReadOnlyList<string> selectedFiles = ParseSelectedFiles(request.Form.GetValueOrDefault("paths", ""));
            string destinationDirectory = NormalizeCloudPath(request.Form.GetValueOrDefault("destinationPath", ""));
            string currentDirectory = NormalizeCloudPath(request.Form.GetValueOrDefault("currentPath", ""));
            bool overwriteExisting = string.Equals(request.Form.GetValueOrDefault("overwriteExisting", ""), "true", StringComparison.OrdinalIgnoreCase);
            string newName = request.Form.GetValueOrDefault("newName", "");
            (int moved, int skippedExisting, int missing) = await fileStorage.MoveFilesAsync(email, selectedFiles, destinationDirectory, overwriteExisting, newName);
            TrackCopiedOrMoved(sessionId, moved);
            string message = BuildMoveMessage(moved, skippedExisting, missing);

            if (moved == 0 && skippedExisting > 0)
            {
                AppLog.Warn($"Move blocked by existing destination for {email}: {destinationDirectory}");
            }
            else if (moved > 0 && skippedExisting > 0)
            {
                AppLog.Warn($"Move completed with conflicts for {email}: {moved} moved, {skippedExisting} skipped, destination={destinationDirectory}");
            }
            else if (moved > 0)
            {
                AppLog.Info($"Move completed for {email}: {moved} item(s) to {destinationDirectory}");
            }

            if (missing > 0)
            {
                AppLog.Warn($"Move skipped missing items for {email}: {missing} item(s)");
            }

            HttpResponse response = Html(PageRenderer.RenderHome(email, GetUser(email), fileStorage.GetFiles(email), "login", message, currentDirectory, deviceSessions: GetDeviceSessions(email)));

            if (!overwriteExisting && skippedExisting > 0)
            {
                response.Headers["X-Conflict"] = "true";
            }

            return response;
        }

        if (request.Method == "POST" && request.Path == "/files/copy")
        {
            if (email is null)
            {
                return Redirect("/");
            }

            IReadOnlyList<string> selectedFiles = ParseSelectedFiles(request.Form.GetValueOrDefault("paths", ""));
            string destinationDirectory = NormalizeCloudPath(request.Form.GetValueOrDefault("destinationPath", ""));
            string currentDirectory = NormalizeCloudPath(request.Form.GetValueOrDefault("currentPath", ""));
            bool overwriteExisting = string.Equals(request.Form.GetValueOrDefault("overwriteExisting", ""), "true", StringComparison.OrdinalIgnoreCase);
            (int copied, int skippedExisting, int missing) = await fileStorage.CopyFilesAsync(email, selectedFiles, destinationDirectory, overwriteExisting);
            TrackCopiedOrMoved(sessionId, copied);
            string message = BuildCopyMessage(copied, skippedExisting, missing);

            if (copied == 0 && skippedExisting > 0)
            {
                AppLog.Warn($"Copy blocked by existing destination for {email}: {destinationDirectory}");
            }
            else if (copied > 0 && skippedExisting > 0)
            {
                AppLog.Warn($"Copy completed with conflicts for {email}: {copied} copied, {skippedExisting} skipped, destination={destinationDirectory}");
            }
            else if (copied > 0)
            {
                AppLog.Info($"Copy completed for {email}: {copied} item(s) to {destinationDirectory}");
            }

            if (missing > 0)
            {
                AppLog.Warn($"Copy skipped missing items for {email}: {missing} item(s)");
            }

            HttpResponse response = Html(PageRenderer.RenderHome(email, GetUser(email), fileStorage.GetFiles(email), "login", message, currentDirectory, deviceSessions: GetDeviceSessions(email)));

            if (!overwriteExisting && skippedExisting > 0)
            {
                response.Headers["X-Conflict"] = "true";
            }

            return response;
        }

        if (request.Method == "POST" && request.Path == "/files/trash")
        {
            if (email is null)
            {
                return Redirect("/");
            }

            IReadOnlyList<string> selectedFiles = ParseSelectedFiles(request.Form.GetValueOrDefault("paths", ""));
            string currentDirectory = NormalizeCloudPath(request.Form.GetValueOrDefault("currentPath", ""));
            int moved = await fileStorage.MoveToTrashAsync(email, selectedFiles);
            TrackDeleted(sessionId, moved);
            string message = moved == 0 ? "Select files first." : $"Moved {moved} file(s) to trash.";

            if (moved == 0)
            {
                AppLog.Warn($"Trash move requested without selected files for {email}");
            }
            else
            {
                AppLog.Info($"Moved {moved} item(s) to trash for {email}");
            }

            return Html(PageRenderer.RenderHome(email, GetUser(email), fileStorage.GetFiles(email), "login", message, currentDirectory, deviceSessions: GetDeviceSessions(email)));
        }

        if (request.Method == "POST" && request.Path == "/files/star")
        {
            if (email is null)
            {
                return Redirect("/");
            }

            IReadOnlyList<string> selectedFiles = ParseSelectedFiles(request.Form.GetValueOrDefault("paths", ""));
            string currentDirectory = NormalizeCloudPath(request.Form.GetValueOrDefault("currentPath", ""));
            int changed = ToggleStarredFiles(email, selectedFiles);
            string message = changed == 0 ? "Najpierw zaznacz pliki." : $"Zmieniono gwiazdkę dla {changed} element(y).";

            return Html(PageRenderer.RenderHome(email, GetUser(email), fileStorage.GetFiles(email), "login", message, currentDirectory, deviceSessions: GetDeviceSessions(email), starredPaths: GetStarredPaths(email)));
        }

        if (request.Method == "POST" && request.Path == "/files/trash/restore")
        {
            if (email is null)
            {
                return Redirect("/");
            }

            IReadOnlyList<string> selectedFiles = ParseSelectedFiles(request.Form.GetValueOrDefault("paths", ""));
            string currentDirectory = NormalizeCloudPath(request.Form.GetValueOrDefault("currentPath", ""));
            bool overwriteExisting = string.Equals(request.Form.GetValueOrDefault("overwriteExisting", ""), "true", StringComparison.OrdinalIgnoreCase);
            (int restored, int skippedExisting, int missing) = await fileStorage.RestoreFromTrashAsync(email, selectedFiles, overwriteExisting);
            TrackCopiedOrMoved(sessionId, restored);
            string message = BuildRestoreMessage(restored, skippedExisting, missing);

            if (restored == 0 && skippedExisting > 0)
            {
                AppLog.Warn($"Restore blocked by existing destination for {email}");
            }
            else if (restored > 0 && skippedExisting > 0)
            {
                AppLog.Warn($"Restore completed with conflicts for {email}: {restored} restored, {skippedExisting} skipped");
            }
            else if (restored > 0)
            {
                AppLog.Info($"Restored {restored} item(s) from trash for {email}");
            }

            if (missing > 0)
            {
                AppLog.Warn($"Restore skipped missing trash items for {email}: {missing} item(s)");
            }

            HttpResponse response = Html(PageRenderer.RenderHome(email, GetUser(email), fileStorage.GetFiles(email), "login", message, currentDirectory, "trash", fileStorage.GetTrashFiles(email), GetDeviceSessions(email)));

            if (!overwriteExisting && skippedExisting > 0)
            {
                response.Headers["X-Conflict"] = "true";
            }

            return response;
        }

        if (request.Method == "POST" && request.Path == "/files/trash/delete")
        {
            if (email is null)
            {
                return Redirect("/");
            }

            IReadOnlyList<string> selectedFiles = ParseSelectedFiles(request.Form.GetValueOrDefault("paths", ""));
            string currentDirectory = NormalizeCloudPath(request.Form.GetValueOrDefault("currentPath", ""));
            int deleted = await fileStorage.DeleteFromTrashAsync(email, selectedFiles);
            TrackDeleted(sessionId, deleted);
            string message = deleted == 0 ? "Select files first." : $"Deleted {deleted} file(s) forever.";

            if (deleted == 0)
            {
                AppLog.Warn($"Permanent delete requested without selected files for {email}");
            }
            else
            {
                AppLog.Info($"Permanently deleted {deleted} item(s) from trash for {email}");
            }

            return Html(PageRenderer.RenderHome(email, GetUser(email), fileStorage.GetFiles(email), "login", message, currentDirectory, "trash", fileStorage.GetTrashFiles(email), GetDeviceSessions(email)));
        }

        if (request.Method == "POST" && request.Path == "/files/trash/empty")
        {
            if (email is null)
            {
                return Redirect("/");
            }

            string currentDirectory = NormalizeCloudPath(request.Form.GetValueOrDefault("currentPath", ""));
            int emptied = await fileStorage.EmptyTrashAsync(email);
            TrackDeleted(sessionId, emptied);
            string message = emptied == 0 ? "Trash is already empty." : "Trash emptied.";

            if (emptied == 0)
            {
                AppLog.Warn($"Empty trash requested but trash was already empty for {email}");
            }
            else
            {
                AppLog.Info($"Emptied trash for {email}");
            }

            return Html(PageRenderer.RenderHome(email, GetUser(email), fileStorage.GetFiles(email), "login", message, currentDirectory, "trash", fileStorage.GetTrashFiles(email), GetDeviceSessions(email)));
        }

        if (request.Method == "POST" && request.Path == "/forgot-password")
        {
            string resetEmail = NormalizeEmail(request.Form.GetValueOrDefault("email", ""));
            UserAccount? account = userStore.GetUser(resetEmail);

            if (account is not null)
            {
                string token = GenerateResetToken();
                pendingPasswordResets[token] = new PendingPasswordReset(
                    resetEmail,
                    token,
                    DateTimeOffset.UtcNow.AddMinutes(30));

                string resetUrl = BuildResetUrl(request, token);
                EmailSendResult sendResult = await emailSender.SendPasswordResetAsync(resetEmail, resetUrl);

                if (sendResult != EmailSendResult.Sent)
                {
                    pendingPasswordResets.TryRemove(token, out _);
                    return Html(PageRenderer.RenderForgotPassword(EmailMessage(sendResult)));
                }
            }

            return Html(PageRenderer.RenderForgotPassword("If that email exists, a reset link has been sent."));
        }

        if ((request.Method == "GET" || request.Method == "HEAD") && request.Path == "/reset")
        {
            string token = request.Query.GetValueOrDefault("token", "");

            if (!IsValidResetToken(token))
            {
                return Html(PageRenderer.RenderForgotPassword("Reset link is invalid or expired."));
            }

            return Html(PageRenderer.RenderResetPassword(token));
        }

        if (request.Method == "POST" && request.Path == "/reset-password")
        {
            string token = request.Form.GetValueOrDefault("token", "");
            string newPassword = request.Form.GetValueOrDefault("password", "");
            string confirmPassword = request.Form.GetValueOrDefault("confirmPassword", "");

            if (!IsValidResetToken(token))
            {
                return Html(PageRenderer.RenderForgotPassword("Reset link is invalid or expired."));
            }

            if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
            {
                return Html(PageRenderer.RenderResetPassword(token, "Passwords do not match."));
            }

            PendingPasswordReset pending = pendingPasswordResets[token];
            RegisterResult result = await userStore.SetPasswordAsync(pending.Email, newPassword);

            if (result != RegisterResult.Created)
            {
                return Html(PageRenderer.RenderResetPassword(token, RegistrationMessage(result)));
            }

            pendingPasswordResets.TryRemove(token, out _);
            return Html(PageRenderer.RenderHome(null, null, [], "login", "Password changed. You can sign in now."));
        }

        if (request.Method == "POST" && request.Path == "/register")
        {
            string newEmail = NormalizeEmail(request.Form.GetValueOrDefault("email", ""));
            string password = request.Form.GetValueOrDefault("password", "");
            RegisterResult result = userStore.ValidateRegistration(newEmail, password);

            if (result != RegisterResult.Created)
            {
                AppLog.Warn($"Registration rejected for {newEmail}: {RegistrationMessage(result)}");
                return Html(PageRenderer.RenderHome(null, null, [], "register", RegistrationMessage(result)));
            }

            string code = GenerateVerificationCode();
            EmailSendResult sendResult = await emailSender.SendVerificationCodeAsync(newEmail, code);

            if (sendResult != EmailSendResult.Sent)
            {
                AppLog.Warn($"Verification email could not be sent to {newEmail}: {EmailMessage(sendResult)}");
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
                AppLog.Warn($"Verification attempted for missing pending registration: {verifyEmail}");
                return Html(PageRenderer.RenderHome(null, null, [], "register", "Register again to get a new code."));
            }

            if (pending.ExpiresAt < DateTimeOffset.UtcNow)
            {
                AppLog.Warn($"Verification code expired for {verifyEmail}");
                pendingRegistrations.TryRemove(verifyEmail, out _);
                return Html(PageRenderer.RenderHome(null, null, [], "register", "Verification code expired. Register again."));
            }

            if (!string.Equals(code, pending.Code, StringComparison.Ordinal))
            {
                AppLog.Warn($"Wrong verification code for {verifyEmail}");
                return Html(PageRenderer.RenderVerification(verifyEmail, "Wrong verification code."));
            }

            RegisterResult result = await userStore.RegisterAsync(pending.Email, pending.Password);

            if (result != RegisterResult.Created)
            {
                AppLog.Warn($"Final registration failed for {verifyEmail}: {RegistrationMessage(result)}");
                pendingRegistrations.TryRemove(verifyEmail, out _);
                return Html(PageRenderer.RenderHome(null, null, [], "register", RegistrationMessage(result)));
            }

            pendingRegistrations.TryRemove(verifyEmail, out _);
            fileStorage.CreateUserDirectory(pending.Email);
            string newSessionId = CreateSession(pending.Email, request, remoteAddress);
            return Redirect("/", newSessionId);
        }

        if (request.Method == "POST" && request.Path == "/login")
        {
            string login = NormalizeEmail(request.Form.GetValueOrDefault("email", ""));
            string password = request.Form.GetValueOrDefault("password", "");

            if (userStore.ValidateLogin(login, password))
            {
                string newSessionId = CreateSession(login, request, remoteAddress);
                return Redirect("/", newSessionId);
            }

            AppLog.Warn($"Failed login attempt for {login}");
            return Html(PageRenderer.RenderHome(null, null, [], "login", "Wrong email or password."));
        }

        if (request.Method == "POST" && request.Path == "/files/upload")
        {
            if (email is null)
            {
                return Redirect("/");
            }

            string currentDirectory = NormalizeCloudPath(
                request.GetMultipartField("currentPath")
                ?? request.Form.GetValueOrDefault("currentPath", ""));
            string uploadSource = request.GetMultipartField("uploadSource") ?? "unknown";
            int clientItemCount = ParseMultipartInt(request, "clientItemCount", -1);
            int clientFileCount = ParseMultipartInt(request, "clientFileCount", -1);
            int clientCollectedCount = ParseMultipartInt(request, "clientCollectedCount", -1);
            IReadOnlyList<MultipartFile> files = request.GetUploadedFiles("file");
            AppLog.Info($"Upload request in '{currentDirectory}' from {uploadSource} contains {files.Count} file part(s); client items={clientItemCount}, files={clientFileCount}, collected={clientCollectedCount}: {string.Join(", ", files.Select(file => file.FileName))}");

            if (files.Count == 0)
            {
                AppLog.Warn($"Upload attempted without a file in {currentDirectory}");
                return Html(PageRenderer.RenderHome(email, GetUser(email), fileStorage.GetFiles(email), "login", "Choose a file first.", currentDirectory));
            }

            int uploaded = 0;
            int invalid = 0;
            int quotaExceeded = 0;
            int failed = 0;

            foreach (MultipartFile file in files)
            {
                string uploadPath = CombineCloudPath(currentDirectory, file.FileName);
                FileCreateResult result = await fileStorage.SaveUploadedFileAsync(email, file with { FileName = uploadPath });

                switch (result)
                {
                    case FileCreateResult.Created:
                        uploaded += 1;
                        break;
                    case FileCreateResult.InvalidFileName:
                        invalid += 1;
                        AppLog.Warn($"Upload failed for {uploadPath}: invalid name");
                        break;
                    case FileCreateResult.QuotaExceeded:
                        quotaExceeded += 1;
                        AppLog.Warn($"Upload failed for {uploadPath}: quota exceeded");
                        break;
                    default:
                        failed += 1;
                        AppLog.Warn($"Upload failed for {uploadPath}: unknown error");
                        break;
                }
            }

            string message = BuildUploadSummary(
                uploaded,
                files.Count,
                invalid,
                quotaExceeded,
                failed,
                uploadSource,
                clientItemCount,
                clientFileCount,
                clientCollectedCount);

            TrackUploaded(sessionId, uploaded);
            return Html(PageRenderer.RenderHome(email, GetUser(email), fileStorage.GetFiles(email), "login", message, currentDirectory, deviceSessions: GetDeviceSessions(email)));
        }

        if (request.Method == "POST" && request.Path == "/files/create")
        {
            if (email is null)
            {
                return Redirect("/");
            }

            string currentDirectory = NormalizeCloudPath(request.Form.GetValueOrDefault("currentPath", ""));
            string fileName = CombineCloudPath(currentDirectory, request.Form.GetValueOrDefault("fileName", ""));
            FileCreateResult result = await fileStorage.CreateTextFileAsync(email, fileName, "");
            string message = FileActionMessage(result, "Empty file created.", "Could not create file.");

            if (result != FileCreateResult.Created)
            {
                AppLog.Warn($"Create file failed for {fileName}: {message}");
            }

            TrackUploaded(sessionId, result == FileCreateResult.Created ? 1 : 0);
            return Html(PageRenderer.RenderHome(email, GetUser(email), fileStorage.GetFiles(email), "login", message, currentDirectory, deviceSessions: GetDeviceSessions(email)));
        }

        if (request.Method == "POST" && request.Path == "/folders/create")
        {
            if (email is null)
            {
                return Redirect("/");
            }

            string currentDirectory = NormalizeCloudPath(request.Form.GetValueOrDefault("currentPath", ""));
            string folderName = CombineCloudPath(currentDirectory, request.Form.GetValueOrDefault("folderName", ""));
            FileCreateResult result = await fileStorage.CreateFolderAsync(email, folderName);
            string message = FileActionMessage(result, "Folder created.", "Could not create folder.");

            if (result != FileCreateResult.Created)
            {
                AppLog.Warn($"Create folder failed for {folderName}: {message}");
            }

            TrackUploaded(sessionId, result == FileCreateResult.Created ? 1 : 0);
            return Html(PageRenderer.RenderHome(email, GetUser(email), fileStorage.GetFiles(email), "login", message, currentDirectory, deviceSessions: GetDeviceSessions(email)));
        }

        if (request.Method == "POST" && request.Path == "/logout")
        {
            string? logoutSessionId = request.GetCookie("cloud_session");

            if (logoutSessionId is not null)
            {
                sessions.TryRemove(logoutSessionId, out _);
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

    private string CreateSession(string email, HttpRequest request, string remoteAddress)
    {
        string sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        sessions[sessionId] = email;
        TouchDeviceSession(sessionId, email, request, remoteAddress, isNew: true);
        return sessionId;
    }

    private void TouchDeviceSession(string sessionId, string email, HttpRequest request, string remoteAddress, bool isNew = false)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string userAgent = request.Headers.GetValueOrDefault("User-Agent", "");
        (string deviceName, string systemName) = DescribeDevice(userAgent);

        deviceSessions.AddOrUpdate(
            sessionId,
            _ => new DeviceSessionInfo
            {
                SessionId = sessionId,
                Email = email,
                IpAddress = remoteAddress,
                DeviceName = deviceName,
                SystemName = systemName,
                StartedAt = now,
                LastSeenAt = now
            },
            (_, existing) =>
            {
                existing.IpAddress = remoteAddress;
                existing.DeviceName = deviceName;
                existing.SystemName = systemName;
                existing.LastSeenAt = isNew ? existing.StartedAt : now;
                return existing;
            });
    }

    private IReadOnlyList<DeviceSessionInfo> GetDeviceSessions(string? email)
    {
        if (email is null)
        {
            return [];
        }

        return deviceSessions.Values
            .Where(session => string.Equals(session.Email, email, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(session => session.LastSeenAt)
            .ToList();
    }

    private IReadOnlySet<string> GetStarredPaths(string? email)
    {
        if (email is null || !starredFiles.TryGetValue(email, out ConcurrentDictionary<string, byte>? paths))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return paths.Keys.ToHashSet(StringComparer.Ordinal);
    }

    private int ToggleStarredFiles(string email, IReadOnlyList<string> paths)
    {
        ConcurrentDictionary<string, byte> userStars = starredFiles.GetOrAdd(email, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        int changed = 0;

        foreach (string rawPath in paths)
        {
            string path = NormalizeCloudPath(rawPath);

            if (path.Length == 0)
            {
                continue;
            }

            if (!userStars.TryRemove(path, out _))
            {
                userStars[path] = 1;
            }

            changed += 1;
        }

        return changed;
    }

    private void TrackUploaded(string? sessionId, int count)
    {
        if (count > 0 && sessionId is not null && deviceSessions.TryGetValue(sessionId, out DeviceSessionInfo? session))
        {
            session.UploadedFiles += count;
        }
    }

    private void TrackDeleted(string? sessionId, int count)
    {
        if (count > 0 && sessionId is not null && deviceSessions.TryGetValue(sessionId, out DeviceSessionInfo? session))
        {
            session.DeletedFiles += count;
        }
    }

    private void TrackCopiedOrMoved(string? sessionId, int count)
    {
        if (count > 0 && sessionId is not null && deviceSessions.TryGetValue(sessionId, out DeviceSessionInfo? session))
        {
            session.CopiedOrMovedFiles += count;
        }
    }

    private static string GetRemoteAddress(TcpClient client)
    {
        return client.Client.RemoteEndPoint is IPEndPoint endpoint
            ? endpoint.Address.ToString()
            : "unknown";
    }

    private static (string DeviceName, string SystemName) DescribeDevice(string userAgent)
    {
        string lower = userAgent.ToLowerInvariant();
        string system = lower switch
        {
            _ when lower.Contains("android", StringComparison.Ordinal) => "Android",
            _ when lower.Contains("iphone", StringComparison.Ordinal) || lower.Contains("ipad", StringComparison.Ordinal) => "iOS",
            _ when lower.Contains("linux", StringComparison.Ordinal) => "Linux",
            _ when lower.Contains("windows", StringComparison.Ordinal) => "Windows",
            _ when lower.Contains("mac os", StringComparison.Ordinal) => "macOS",
            _ => "Nieznany system"
        };
        string browser = lower switch
        {
            _ when lower.Contains("edg/", StringComparison.Ordinal) => "Edge",
            _ when lower.Contains("firefox/", StringComparison.Ordinal) => "Firefox",
            _ when lower.Contains("chrome/", StringComparison.Ordinal) => "Chrome",
            _ when lower.Contains("safari/", StringComparison.Ordinal) => "Safari",
            _ => "Przeglądarka"
        };
        string device = lower.Contains("mobile", StringComparison.Ordinal)
            || lower.Contains("android", StringComparison.Ordinal)
            || lower.Contains("iphone", StringComparison.Ordinal)
                ? "Telefon"
                : "Komputer";

        return ($"{device} {system}", $"{system} / {browser}");
    }

    private static string NormalizeEmail(string email)
    {
        return email.Trim().ToLowerInvariant();
    }

    private static string GenerateVerificationCode()
    {
        return RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    }

    private static string GenerateResetToken()
    {
        return WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    }

    private bool IsValidResetToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)
            || !pendingPasswordResets.TryGetValue(token, out PendingPasswordReset? pending))
        {
            return false;
        }

        if (pending.ExpiresAt >= DateTimeOffset.UtcNow)
        {
            return true;
        }

        pendingPasswordResets.TryRemove(token, out _);
        return false;
    }

    private static string BuildResetUrl(HttpRequest request, string token)
    {
        string host = request.Headers.GetValueOrDefault("Host", "127.0.0.1:8080");
        string scheme = request.Headers.TryGetValue("X-Forwarded-Proto", out string? forwardedProto)
            ? forwardedProto
            : "http";

        return $"{scheme}://{host}/reset?token={WebUtility.UrlEncode(token)}";
    }

    private static string RegistrationMessage(RegisterResult result)
    {
        string message = result switch
        {
            RegisterResult.InvalidEmail => "Enter a valid email address.",
            RegisterResult.InvalidPassword => "Password must be at least 4 characters.",
            RegisterResult.AlreadyExists => "Email already has an account.",
            _ => "Registration failed."
        };

        if (result != RegisterResult.Created)
        {
            AppLog.Warn($"Registration validation failed: {message}");
        }

        return message;
    }

    private static string EmailMessage(EmailSendResult result)
    {
        string message = result switch
        {
            EmailSendResult.NotConfigured => "Email sending is not configured. Set SMTP_HOST, SMTP_PORT, SMTP_USER, SMTP_PASS and SMTP_FROM.",
            EmailSendResult.AuthenticationFailed => "SMTP login failed. Check SMTP_USER, SMTP_PASS and enable mail client access in your mailbox settings.",
            EmailSendResult.Failed => "Could not send email. Check SMTP settings.",
            _ => "Could not send email."
        };

        if (result != EmailSendResult.Sent)
        {
            AppLog.Warn($"Email send failed: {message}");
        }

        return message;
    }

    private static string FileActionMessage(FileCreateResult result, string createdMessage, string failedMessage)
    {
        string message = result switch
        {
            FileCreateResult.Created => createdMessage,
            FileCreateResult.InvalidFileName => "Name is invalid. Avoid path separators and control characters.",
            FileCreateResult.QuotaExceeded => "File is too large for your remaining space.",
            _ => failedMessage
        };

        if (result != FileCreateResult.Created)
        {
            AppLog.Warn($"File action failed: {message}");
        }

        return message;
    }

    private static HttpResponse Html(string body, int statusCode = 200, string reason = "OK")
    {
        return new HttpResponse(statusCode, reason, body);
    }

    private static HttpResponse StaticFile(string path, string contentType)
    {
        if (!File.Exists(path))
        {
            return Html(PageRenderer.RenderNotFound(), 404, "Not Found");
        }

        var response = new HttpResponse(200, "OK", File.ReadAllBytes(path), contentType);
        response.Headers["Cache-Control"] = "public, max-age=86400";
        return response;
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

    private static string EscapeHeaderFileName(string fileName)
    {
        fileName = string.IsNullOrWhiteSpace(fileName) ? "download" : fileName;
        return fileName
            .Replace("\\", "_", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal)
            .Replace("\"", "'", StringComparison.Ordinal)
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "", StringComparison.Ordinal);
    }

    private static string GetContentType(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" or ".oga" => "audio/ogg",
            ".flac" => "audio/flac",
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",
            ".opus" => "audio/opus",
            ".mp4" or ".m4v" => "video/mp4",
            ".webm" => "video/webm",
            ".ogv" => "video/ogg",
            ".mov" => "video/quicktime",
            ".mkv" => "video/x-matroska",
            ".txt" or ".log" or ".md" or ".csv" or ".json" or ".xml" or ".html" or ".css" or ".js" or ".cs" or ".sh" => "text/plain; charset=utf-8",
            _ => "text/plain; charset=utf-8"
        };
    }

    private static IReadOnlyList<string> ParseSelectedFiles(string paths)
    {
        return paths
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static string NormalizeCloudPath(string path)
    {
        string[] parts = path.Trim()
            .Replace('\\', '/')
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part != "." && part != ".." && !part.Contains("..", StringComparison.Ordinal))
            .ToArray();

        return string.Join('/', parts);
    }

    private static string NormalizeView(string view)
    {
        return view.Trim().ToLowerInvariant() switch
        {
            "recent" or "videos" or "images" or "music" or "documents" or "trash" or "computers" or "shared" or "starred" => view.Trim().ToLowerInvariant(),
            _ => "home"
        };
    }

    private static string CombineCloudPath(string currentDirectory, string name)
    {
        string normalizedDirectory = NormalizeCloudPath(currentDirectory);
        string normalizedName = NormalizeCloudPath(name);

        if (normalizedDirectory.Length == 0)
        {
            return normalizedName;
        }

        if (normalizedName.Length == 0)
        {
            return normalizedDirectory;
        }

        return $"{normalizedDirectory}/{normalizedName}";
    }

    private static string BuildMoveMessage(int moved, int skippedExisting, int missing)
    {
        if (moved == 0 && skippedExisting > 0)
        {
            return "That destination already contains a file or folder with the same name.";
        }

        var parts = new List<string>();

        if (moved > 0)
        {
            parts.Add($"Moved {moved} file(s).");
        }

        if (skippedExisting > 0)
        {
            parts.Add($"{skippedExisting} item(s) were skipped because the target already exists.");
        }

        if (missing > 0)
        {
            parts.Add($"{missing} item(s) were missing.");
        }

        return parts.Count == 0 ? "Select files first." : string.Join(' ', parts);
    }

    private static string BuildCopyMessage(int copied, int skippedExisting, int missing)
    {
        if (copied == 0 && skippedExisting > 0)
        {
            return "That destination already contains a file or folder with the same name.";
        }

        var parts = new List<string>();

        if (copied > 0)
        {
            parts.Add($"Copied {copied} file(s).");
        }

        if (skippedExisting > 0)
        {
            parts.Add($"{skippedExisting} item(s) were skipped because the target already exists.");
        }

        if (missing > 0)
        {
            parts.Add($"{missing} item(s) were missing.");
        }

        return parts.Count == 0 ? "Select files first." : string.Join(' ', parts);
    }

    private static string BuildRestoreMessage(int restored, int skippedExisting, int missing)
    {
        if (restored == 0 && skippedExisting > 0)
        {
            return "That destination already contains a file or folder with the same name.";
        }

        var parts = new List<string>();

        if (restored > 0)
        {
            parts.Add($"Restored {restored} file(s).");
        }

        if (skippedExisting > 0)
        {
            parts.Add($"{skippedExisting} item(s) were skipped because the target already exists.");
        }

        if (missing > 0)
        {
            parts.Add($"{missing} item(s) were missing from trash.");
        }

        return parts.Count == 0 ? "Select files first." : string.Join(' ', parts);
    }

    private static int ParseMultipartInt(HttpRequest request, string fieldName, int fallback)
    {
        return int.TryParse(request.GetMultipartField(fieldName), out int value) ? value : fallback;
    }

    private static string BuildUploadSummary(
        int uploaded,
        int total,
        int invalid,
        int quotaExceeded,
        int failed,
        string uploadSource,
        int clientItemCount,
        int clientFileCount,
        int clientCollectedCount)
    {
        var parts = new List<string>();

        parts.Add($"Uploaded {uploaded} of {total} received file(s).");

        if (invalid > 0)
        {
            parts.Add($"{invalid} invalid name(s).");
        }

        if (quotaExceeded > 0)
        {
            parts.Add($"{quotaExceeded} over quota.");
        }

        if (failed > 0)
        {
            parts.Add($"{failed} failed.");
        }

        if (uploadSource == "drop")
        {
            parts.Add($"Drop debug: items={clientItemCount}, files={clientFileCount}, collected={clientCollectedCount}, server={total}.");
        }

        return parts.Count == 0 ? "Could not upload file." : string.Join(' ', parts);
    }
}

public static class WebEncoders
{
    public static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
