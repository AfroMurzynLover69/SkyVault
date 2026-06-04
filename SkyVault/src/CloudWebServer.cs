using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed class CloudWebServer
{
    private const long MaxPreviewBytes = 20L * 1024 * 1024;
    private const long MaxZipBytes = 512L * 1024 * 1024;
    private readonly IPAddress address;
    private readonly int port;
    private readonly UserStore userStore;
    private readonly FileStorage fileStorage;
    private readonly EmailSender emailSender;
    private readonly ServerOptions options;
    private readonly RequestScheduler scheduler;
    private readonly SemaphoreSlim connectionSlots;
    private readonly ConcurrentDictionary<long, Task> activeClientTasks = new();
    private readonly PeriodicTimer metricsTimer = new(TimeSpan.FromSeconds(30));
    private readonly ConcurrentDictionary<string, string> sessions = new();
    private readonly ConcurrentDictionary<string, DeviceSessionInfo> deviceSessions = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> starredFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, UploadSession> uploadSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingRegistration> pendingRegistrations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PendingPasswordReset> pendingPasswordResets = new(StringComparer.Ordinal);
    private long nextClientTaskId;

    public CloudWebServer(IPAddress address, int port, UserStore userStore, FileStorage fileStorage, EmailSender emailSender, ServerOptions? options = null)
    {
        this.address = address;
        this.port = port;
        this.userStore = userStore;
        this.fileStorage = fileStorage;
        this.emailSender = emailSender;
        this.options = options ?? new ServerOptions();
        scheduler = new RequestScheduler(this.options);
        connectionSlots = new SemaphoreSlim(this.options.MaxConnections, this.options.MaxConnections);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        TcpListener listener = new TcpListener(address, port);
        listener.Start();
        _ = LogMetricsAsync(cancellationToken);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient? client = null;

                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken);

                    if (!await connectionSlots.WaitAsync(TimeSpan.FromSeconds(options.UiQueueTimeoutSeconds), cancellationToken))
                    {
                        scheduler.MarkRejected();
                        AppLog.Warn($"Connection rejected: max active connections reached ({options.MaxConnections}).");
                        await WriteBusyAndCloseAsync(client, 503, "Server Busy", "Server is busy. Try again later.", cancellationToken);
                        continue;
                    }

                    long taskId = Interlocked.Increment(ref nextClientTaskId);
                    Task task = HandleClientAsync(client, cancellationToken);
                    activeClientTasks[taskId] = task;
                    _ = task.ContinueWith(_ =>
                    {
                        activeClientTasks.TryRemove(taskId, out Task? _);
                        connectionSlots.Release();
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    AppLog.Warn($"Accept loop recovered from error: {ex.Message}");
                    client?.Close();
                }
                catch (OutOfMemoryException ex)
                {
                    AppLog.Error("Critical memory pressure in accept loop.", ex);
                    client?.Close();
                }
            }
        }
        finally
        {
            listener.Stop();

            Task[] remaining = activeClientTasks.Values.ToArray();

            if (remaining.Length > 0)
            {
                AppLog.Info($"Waiting for {remaining.Length} active client(s) to finish.");
                await Task.WhenAny(Task.WhenAll(remaining), Task.Delay(TimeSpan.FromSeconds(options.GracefulShutdownSeconds), CancellationToken.None));
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverCancellationToken)
    {
        try
        {
            client.ReceiveTimeout = options.ClientIdleTimeoutSeconds * 1000;
            client.SendTimeout = options.ClientIdleTimeoutSeconds * 1000;
            await using NetworkStream stream = client.GetStream();
            HttpRequest? request = await HttpRequest.ReadAsync(stream, options, serverCancellationToken);

            if (request is null)
            {
                return;
            }

            string remoteAddress = GetRemoteAddress(client);
            await DispatchRequestAsync(request, remoteAddress, stream, serverCancellationToken);
        }
        catch (IOException)
        {
            AppLog.Warn("Client disconnected with an I/O error.");
        }
        catch (SocketException)
        {
            AppLog.Warn("Socket error while handling client.");
        }
        catch (OperationCanceledException)
        {
            AppLog.Warn("Client request timed out or server is shutting down.");
        }
        catch (TimeoutException ex)
        {
            AppLog.Warn($"Client request timed out: {ex.Message}");
        }
        catch (InvalidDataException ex)
        {
            AppLog.Warn($"Client sent invalid request data: {ex.Message}");
        }
        catch (OutOfMemoryException ex)
        {
            AppLog.Error("Critical memory pressure while handling client.", ex);
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

    private async Task DispatchRequestAsync(HttpRequest request, string remoteAddress, Stream stream, CancellationToken cancellationToken)
    {
        string? email = GetLoggedInEmail(request);
        RequestWorkload workload = ClassifyWorkload(request);
        await using RequestScheduler.RequestLease? lease = await scheduler.TryAcquireAsync(workload, email, cancellationToken);

        if (lease is null)
        {
            scheduler.MarkRejected();
            AppLog.Warn($"Request rejected by scheduler: workload={workload}, user={email ?? "anonymous"}, path={request.Path}");
            await Html("<h1>Server busy</h1><p>Too many requests are active. Try again later.</p>", 503, "Server Busy").WriteAsync(stream, cancellationToken);
            return;
        }

        try
        {
            using CancellationTokenSource workloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            if (workload == RequestWorkload.Upload)
            {
                workloadTimeout.CancelAfter(TimeSpan.FromSeconds(options.UploadTimeoutSeconds));
            }
            else if (workload == RequestWorkload.Download)
            {
                workloadTimeout.CancelAfter(TimeSpan.FromSeconds(options.DownloadTimeoutSeconds));
            }

            HttpResponse response = await HandleRequestAsync(request, remoteAddress, workloadTimeout.Token);
            await response.WriteAsync(stream, workloadTimeout.Token);
        }
        catch (UploadQuotaExceededException)
        {
            AppLog.Warn($"Request failed: upload quota exceeded for {email ?? "anonymous"}");
            await Html("<h1>Quota exceeded</h1><p>File is too large for your remaining space.</p>", 429, "Too Many Requests").WriteAsync(stream, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            AppLog.Warn($"Request cancelled or timed out: workload={workload}, user={email ?? "anonymous"}, path={request.Path}");
            await Html("<h1>Request timeout</h1><p>The request timed out.</p>", 503, "Request Timeout").WriteAsync(stream, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException or TimeoutException)
        {
            AppLog.Warn($"Request failed without stopping server: {ex.GetType().Name}: {ex.Message}");
            await Html("<h1>Request failed</h1><p>The request could not be processed.</p>", 400, "Bad Request").WriteAsync(stream, cancellationToken);
        }
        catch (OutOfMemoryException ex)
        {
            AppLog.Error("Critical memory pressure while dispatching request.", ex);
            await Html("<h1>Server error</h1><p>The server is under memory pressure.</p>", 503, "Server Error").WriteAsync(stream, CancellationToken.None);
        }
    }

    private RequestWorkload ClassifyWorkload(HttpRequest request)
    {
        if ((request.Method == "POST" && request.Path == "/files/upload")
            || (request.Method == "POST" && request.Path == "/api/uploads/start")
            || (request.Method == "PUT" && IsUploadChunkPath(request.Path)))
        {
            return RequestWorkload.Upload;
        }

        if ((request.Method == "GET" || request.Method == "HEAD")
            && (request.Path == "/files/raw" || request.Path == "/files/download"))
        {
            return RequestWorkload.Download;
        }

        if (request.Method == "POST"
            && (request.Path == "/files/download-zip"
                || request.Path == "/files/move"
                || request.Path == "/files/copy"
                || request.Path == "/files/trash"
                || request.Path == "/files/trash/restore"
                || request.Path == "/files/trash/delete"
                || request.Path == "/files/trash/empty"
                || request.Path == "/files/create"
                || request.Path == "/folders/create"
                || IsUploadCompletePath(request.Path)
                || IsUploadCancelPath(request.Path)))
        {
            return RequestWorkload.FileOperation;
        }

        return RequestWorkload.Ui;
    }

    private async Task WriteBusyAndCloseAsync(TcpClient client, int statusCode, string reason, string message, CancellationToken cancellationToken)
    {
        try
        {
            await using NetworkStream stream = client.GetStream();
            await Html($"<h1>{WebUtility.HtmlEncode(reason)}</h1><p>{WebUtility.HtmlEncode(message)}</p>", statusCode, reason).WriteAsync(stream, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            AppLog.Warn($"Could not write busy response: {ex.Message}");
        }
        finally
        {
            client.Close();
        }
    }

    private async Task LogMetricsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await metricsTimer.WaitForNextTickAsync(cancellationToken))
            {
                ThreadPool.GetAvailableThreads(out int workerAvailable, out int completionAvailable);
                ThreadPool.GetMaxThreads(out int workerMax, out int completionMax);
                int activeConnections = options.MaxConnections - connectionSlots.CurrentCount;
                AppLog.Info(
                    $"Server metrics: connections={activeConnections}, requests={scheduler.ActiveRequests}, uploads={scheduler.ActiveUploads}, downloads={scheduler.ActiveDownloads}, rejected={scheduler.RejectedByLimit}, queueTimeouts={scheduler.QueueTimeouts}, threadPoolWorkers={workerAvailable}/{workerMax}, ioThreads={completionAvailable}/{completionMax}");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<HttpResponse> HandleRequestAsync(HttpRequest request, string remoteAddress, CancellationToken cancellationToken)
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

        if (request.Method == "POST" && request.Path == "/api/uploads/start")
        {
            return await HandleChunkUploadStartAsync(request, email, cancellationToken);
        }

        if (request.Method == "PUT" && TryParseUploadChunkPath(request.Path, out string chunkUploadId, out int chunkIndex))
        {
            return await HandleChunkUploadChunkAsync(request, email, chunkUploadId, chunkIndex, cancellationToken);
        }

        if ((request.Method == "GET" || request.Method == "HEAD") && TryParseUploadStatusPath(request.Path, out string statusUploadId))
        {
            return HandleChunkUploadStatus(email, statusUploadId);
        }

        if (request.Method == "POST" && TryParseUploadCompletePath(request.Path, out string completeUploadId))
        {
            return await HandleChunkUploadCompleteAsync(email, sessionId, completeUploadId, cancellationToken);
        }

        if (request.Method == "POST" && TryParseUploadCancelPath(request.Path, out string cancelUploadId))
        {
            return HandleChunkUploadCancel(email, cancelUploadId);
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
            FileDownload? previewDownload = await fileStorage.OpenReadStreamAsync(email, filePath);

            if (previewDownload is null)
            {
                return Html(PageRenderer.RenderNotFound(), 404, "Not Found");
            }

            await using (previewDownload.Stream)
            {
                if (previewDownload.Length > MaxPreviewBytes)
                {
                    return Html(PageRenderer.RenderLargeFilePreview(filePath, previewDownload.Length));
                }
            }

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
            FileDownload? download = await fileStorage.OpenReadStreamAsync(email, filePath);

            if (download is null)
            {
                return Html(PageRenderer.RenderNotFound(), 404, "Not Found");
            }

            return BuildDownloadResponse(request, download, GetContentType(filePath), inline: true);
        }

        if ((request.Method == "GET" || request.Method == "HEAD") && request.Path == "/files/download")
        {
            if (email is null)
            {
                return Redirect("/");
            }

            string filePath = request.Query.GetValueOrDefault("path", "");
            FileDownload? download = await fileStorage.OpenReadStreamAsync(email, filePath);

            if (download is null)
            {
                return Html(PageRenderer.RenderNotFound(), 404, "Not Found");
            }

            return BuildDownloadResponse(request, download, "application/octet-stream", inline: false);
        }

        if (request.Method == "POST" && request.Path == "/files/download-zip")
        {
            if (email is null)
            {
                return Redirect("/");
            }

            IReadOnlyList<string> selectedFiles = ParseSelectedFiles(request.Form.GetValueOrDefault("paths", ""));
            long selectedBytes = fileStorage.GetTotalSizeBytes(email, selectedFiles);

            if (selectedBytes > MaxZipBytes)
            {
                return Html(PageRenderer.RenderHome(email, GetUser(email), fileStorage.GetFiles(email), "login", $"ZIP is limited to {FormatBytes(MaxZipBytes)}. Select fewer files."));
            }

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

            string uploadTempRoot = Path.Combine(Path.GetTempPath(), "SkyVault", "uploads", Guid.NewGuid().ToString("N"));
            MultipartReadResult multipart;

            try
            {
                AppLog.Info($"Upload request started for {email}; content-length={request.ContentLength}");
                multipart = await request.ReadMultipartToTempFilesAsync(uploadTempRoot, fileStorage.GetRemainingQuotaBytes(email), cancellationToken);
            }
            catch (UploadQuotaExceededException)
            {
                AppLog.Warn($"Upload stopped for {email}: quota exceeded while streaming request body.");
                DeleteDirectoryQuietly(uploadTempRoot);
                return Html(PageRenderer.RenderHome(email, GetUser(email), fileStorage.GetFiles(email), "login", "File is too large for your remaining space."));
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException or InvalidOperationException)
            {
                AppLog.Warn($"Upload failed while reading multipart body for {email}: {ex.Message}");
                DeleteDirectoryQuietly(uploadTempRoot);
                return Html(PageRenderer.RenderHome(email, GetUser(email), fileStorage.GetFiles(email), "login", "Upload failed."));
            }

            string rawCurrentDirectory = multipart.Fields.GetValueOrDefault("currentPath", "");

            if (!IsSafeCloudPath(rawCurrentDirectory, allowEmpty: true))
            {
                AppLog.Warn($"Upload rejected for {email}: invalid currentPath");
                DeleteDirectoryQuietly(uploadTempRoot);
                return Html(PageRenderer.RenderHome(email, GetUser(email), fileStorage.GetFiles(email), "login", "Upload path is invalid."));
            }

            string currentDirectory = NormalizeCloudPath(rawCurrentDirectory);
            string uploadSource = multipart.Fields.GetValueOrDefault("uploadSource", "unknown");
            int clientItemCount = ParseMultipartInt(multipart.Fields, "clientItemCount", -1);
            int clientFileCount = ParseMultipartInt(multipart.Fields, "clientFileCount", -1);
            int clientCollectedCount = ParseMultipartInt(multipart.Fields, "clientCollectedCount", -1);
            IReadOnlyList<UploadedTempFile> files = multipart.Files.Where(file => string.Equals(file.FieldName, "file", StringComparison.OrdinalIgnoreCase)).ToList();
            AppLog.Info($"Upload request in '{currentDirectory}' from {uploadSource} contains {files.Count} file part(s); client items={clientItemCount}, files={clientFileCount}, collected={clientCollectedCount}: {string.Join(", ", files.Select(file => $"{file.FileName} ({file.Length} bytes)"))}");

            if (files.Count == 0)
            {
                AppLog.Warn($"Upload attempted without a file in {currentDirectory}");
                DeleteDirectoryQuietly(uploadTempRoot);
                return Html(PageRenderer.RenderHome(email, GetUser(email), fileStorage.GetFiles(email), "login", "Choose a file first.", currentDirectory));
            }

            int uploaded = 0;
            int invalid = 0;
            int quotaExceeded = 0;
            int failed = 0;

            foreach (UploadedTempFile file in files)
            {
                string uploadPath = string.IsNullOrEmpty(currentDirectory)
                    ? file.FileName
                    : $"{currentDirectory}/{file.FileName}";
                AppLog.Info($"Saving uploaded file for {email}: {uploadPath}, size={file.Length} bytes");
                (FileCreateResult result, string savedPath) = await fileStorage.SaveUploadedTempFileAsync(email, uploadPath, file.TempPath, file.Length);

                switch (result)
                {
                    case FileCreateResult.Created:
                        uploaded += 1;
                        AppLog.Info($"Upload saved for {email}: {savedPath}, size={file.Length} bytes");
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

                DeleteFileQuietly(file.TempPath);
            }

            DeleteDirectoryQuietly(uploadTempRoot);

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

    private async Task<HttpResponse> HandleChunkUploadStartAsync(HttpRequest request, string? email, CancellationToken cancellationToken)
    {
        if (email is null)
        {
            return Json(new { error = "not_authenticated" }, 401, "Unauthorized");
        }

        CleanupExpiredUploadSessions();

        string requestedUploadId = request.Form.GetValueOrDefault("uploadId", "");
        if (TryGetOwnedUploadSession(email, requestedUploadId, out UploadSession? existingById))
        {
            if (existingById is null)
            {
                throw new InvalidOperationException("Upload session lookup returned null.");
            }

            existingById.LastActivityAt = DateTimeOffset.UtcNow;
            return UploadSessionJson(existingById);
        }

        string fileName = request.Form.GetValueOrDefault("filename", "").Trim();
        string relativePath = request.Form.GetValueOrDefault("relativePath", "").Trim();
        string currentDirectory = request.Form.GetValueOrDefault("currentPath", "").Trim();
        string lastModified = request.Form.GetValueOrDefault("lastModified", "").Trim();

        if (!long.TryParse(request.Form.GetValueOrDefault("fileSize", ""), out long fileSize)
            || fileSize <= 0
            || !int.TryParse(request.Form.GetValueOrDefault("chunkSize", ""), out int chunkSize)
            || chunkSize <= 0
            || chunkSize > options.MaxChunkUploadBytes)
        {
            return Json(new { error = "invalid_size" }, 400, "Bad Request");
        }

        if (!IsSafeUploadClientPath(fileName, allowEmpty: false)
            || !IsSafeCloudPath(currentDirectory, allowEmpty: true)
            || (!string.IsNullOrWhiteSpace(relativePath) && !IsSafeUploadClientPath(relativePath, allowEmpty: false)))
        {
            return Json(new { error = "invalid_path" }, 400, "Bad Request");
        }

        if (fileName.Length > 255 || relativePath.Length > 1024)
        {
            return Json(new { error = "name_too_long" }, 400, "Bad Request");
        }

        string uploadRelativePath = string.IsNullOrWhiteSpace(relativePath) ? fileName : relativePath;
        string requestedPath = CombineCloudPath(NormalizeCloudPath(currentDirectory), uploadRelativePath);

        if (!IsSafeUploadClientPath(requestedPath, allowEmpty: false))
        {
            return Json(new { error = "invalid_path" }, 400, "Bad Request");
        }

        if (fileSize > fileStorage.GetRemainingQuotaBytes(email))
        {
            return Json(new { error = "quota_exceeded" }, 429, "Too Many Requests");
        }

        UploadSession? existing = uploadSessions.Values.FirstOrDefault(session =>
            string.Equals(session.OwnerEmail, email, StringComparison.OrdinalIgnoreCase)
            && session.Status is UploadSessionStatus.Started or UploadSessionStatus.Uploading
            && session.FileSize == fileSize
            && session.ChunkSize == chunkSize
            && string.Equals(session.RequestedPath, requestedPath, StringComparison.Ordinal)
            && string.Equals(session.LastModified, lastModified, StringComparison.Ordinal));

        if (existing is not null)
        {
            existing.LastActivityAt = DateTimeOffset.UtcNow;
            return UploadSessionJson(existing);
        }

        int activeForUser = uploadSessions.Values.Count(session =>
            string.Equals(session.OwnerEmail, email, StringComparison.OrdinalIgnoreCase)
            && session.Status is UploadSessionStatus.Started or UploadSessionStatus.Uploading);

        if (activeForUser >= options.MaxUploadSessionsPerUser)
        {
            return Json(new { error = "too_many_upload_sessions" }, 429, "Too Many Requests");
        }

        string uploadId = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(18));
        string tempDirectory = Path.Combine(Path.GetTempPath(), "SkyVault", "chunk-uploads", uploadId);
        Directory.CreateDirectory(tempDirectory);

        var session = new UploadSession(
            uploadId,
            email,
            fileName,
            requestedPath,
            NormalizeCloudPath(currentDirectory),
            uploadRelativePath,
            lastModified,
            fileSize,
            chunkSize,
            tempDirectory,
            DateTimeOffset.UtcNow);

        uploadSessions[uploadId] = session;
        await Task.CompletedTask.WaitAsync(cancellationToken);
        return UploadSessionJson(session);
    }

    private async Task<HttpResponse> HandleChunkUploadChunkAsync(HttpRequest request, string? email, string uploadId, int chunkIndex, CancellationToken cancellationToken)
    {
        if (email is null)
        {
            return Json(new { error = "not_authenticated" }, 401, "Unauthorized");
        }

        if (!TryGetOwnedUploadSession(email, uploadId, out UploadSession? session))
        {
            return Json(new { error = "upload_not_found" }, 404, "Not Found");
        }

        if (session is null)
        {
            throw new InvalidOperationException("Upload session lookup returned null.");
        }

        if (session.Status is UploadSessionStatus.Completed or UploadSessionStatus.Cancelled)
        {
            return Json(new { error = "upload_closed" }, 409, "Conflict");
        }

        int totalChunks = session.TotalChunks;
        if (chunkIndex < 0 || chunkIndex >= totalChunks)
        {
            return Json(new { error = "invalid_chunk_index" }, 400, "Bad Request");
        }

        long expectedSize = GetExpectedChunkSize(session, chunkIndex);
        if (request.ContentLength != expectedSize
            || request.Body.LongLength != expectedSize
            || request.ContentLength > session.ChunkSize
            || request.ContentLength > options.MaxChunkUploadBytes)
        {
            return Json(new { error = "invalid_chunk_size", expectedSize }, 400, "Bad Request");
        }

        Directory.CreateDirectory(session.TempDirectory);
        string chunkPath = GetChunkPath(session, chunkIndex);

        if (File.Exists(chunkPath) && new FileInfo(chunkPath).Length == expectedSize)
        {
            session.MarkUploaded(chunkIndex);
            session.LastActivityAt = DateTimeOffset.UtcNow;
            return UploadSessionJson(session);
        }

        string tempChunkPath = chunkPath + ".tmp";
        await File.WriteAllBytesAsync(tempChunkPath, request.Body, cancellationToken);

        if (File.Exists(chunkPath))
        {
            File.Delete(chunkPath);
        }

        File.Move(tempChunkPath, chunkPath);
        session.MarkUploaded(chunkIndex);
        session.Status = UploadSessionStatus.Uploading;
        session.LastActivityAt = DateTimeOffset.UtcNow;
        return UploadSessionJson(session);
    }

    private HttpResponse HandleChunkUploadStatus(string? email, string uploadId)
    {
        if (email is null)
        {
            return Json(new { error = "not_authenticated" }, 401, "Unauthorized");
        }

        if (!TryGetOwnedUploadSession(email, uploadId, out UploadSession? session))
        {
            return Json(new { error = "upload_not_found" }, 404, "Not Found");
        }

        if (session is null)
        {
            throw new InvalidOperationException("Upload session lookup returned null.");
        }

        session.LastActivityAt = DateTimeOffset.UtcNow;
        return UploadSessionJson(session);
    }

    private async Task<HttpResponse> HandleChunkUploadCompleteAsync(string? email, string? sessionId, string uploadId, CancellationToken cancellationToken)
    {
        if (email is null)
        {
            return Json(new { error = "not_authenticated" }, 401, "Unauthorized");
        }

        if (!TryGetOwnedUploadSession(email, uploadId, out UploadSession? session))
        {
            return Json(new { error = "upload_not_found" }, 404, "Not Found");
        }

        if (session is null)
        {
            throw new InvalidOperationException("Upload session lookup returned null.");
        }

        if (session.UploadedChunks.Count < session.TotalChunks)
        {
            return Json(new { error = "missing_chunks", uploadedChunks = session.UploadedChunks.Order().ToArray() }, 409, "Conflict");
        }

        if (session.FileSize > fileStorage.GetRemainingQuotaBytes(email))
        {
            return Json(new { error = "quota_exceeded" }, 429, "Too Many Requests");
        }

        string assembledPath = Path.Combine(session.TempDirectory, "assembled.upload");

        try
        {
            await using (var output = new FileStream(assembledPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                for (int index = 0; index < session.TotalChunks; index += 1)
                {
                    string chunkPath = GetChunkPath(session, index);
                    long expectedSize = GetExpectedChunkSize(session, index);

                    if (!File.Exists(chunkPath) || new FileInfo(chunkPath).Length != expectedSize)
                    {
                        return Json(new { error = "missing_chunk", chunkIndex = index }, 409, "Conflict");
                    }

                    await using var input = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await input.CopyToAsync(output, 64 * 1024, cancellationToken);
                }
            }

            if (new FileInfo(assembledPath).Length != session.FileSize)
            {
                DeleteFileQuietly(assembledPath);
                return Json(new { error = "assembled_size_mismatch" }, 400, "Bad Request");
            }

            (FileCreateResult result, string savedPath) = await fileStorage.SaveUploadedTempFileAsync(email, session.RequestedPath, assembledPath, session.FileSize);

            if (result != FileCreateResult.Created)
            {
                DeleteFileQuietly(assembledPath);
                return Json(new { error = result.ToString() }, result == FileCreateResult.QuotaExceeded ? 429 : 400, result == FileCreateResult.QuotaExceeded ? "Too Many Requests" : "Bad Request");
            }

            session.Status = UploadSessionStatus.Completed;
            session.SavedPath = savedPath;
            session.LastActivityAt = DateTimeOffset.UtcNow;
            TrackUploaded(sessionId, 1);
            DeleteDirectoryQuietly(session.TempDirectory);
            uploadSessions.TryRemove(session.UploadId, out _);
            return Json(new { uploadId = session.UploadId, status = "done", savedPath });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DeleteFileQuietly(assembledPath);
            AppLog.Warn($"Chunk upload complete failed for {email}: {ex.Message}");
            return Json(new { error = "complete_failed" }, 500, "Server Error");
        }
    }

    private HttpResponse HandleChunkUploadCancel(string? email, string uploadId)
    {
        if (email is null)
        {
            return Json(new { error = "not_authenticated" }, 401, "Unauthorized");
        }

        if (!TryGetOwnedUploadSession(email, uploadId, out UploadSession? session))
        {
            return Json(new { error = "upload_not_found" }, 404, "Not Found");
        }

        if (session is null)
        {
            throw new InvalidOperationException("Upload session lookup returned null.");
        }

        session.Status = UploadSessionStatus.Cancelled;
        DeleteDirectoryQuietly(session.TempDirectory);
        uploadSessions.TryRemove(session.UploadId, out _);
        return Json(new { uploadId, status = "cancelled" });
    }

    private HttpResponse UploadSessionJson(UploadSession session)
    {
        return Json(new
        {
            uploadId = session.UploadId,
            filename = session.FileName,
            fileSize = session.FileSize,
            currentPath = session.CurrentPath,
            relativePath = session.RelativePath,
            chunkSize = session.ChunkSize,
            totalChunks = session.TotalChunks,
            uploadedChunks = session.UploadedChunks.Order().ToArray(),
            status = session.Status.ToString().ToLowerInvariant(),
            savedPath = session.SavedPath
        });
    }

    private bool TryGetOwnedUploadSession(string email, string uploadId, out UploadSession? session)
    {
        session = null;

        if (!IsValidUploadId(uploadId) || !uploadSessions.TryGetValue(uploadId, out UploadSession? found))
        {
            return false;
        }

        if (!string.Equals(found.OwnerEmail, email, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (found.CreatedAt.AddHours(options.UploadSessionTtlHours) < DateTimeOffset.UtcNow)
        {
            DeleteDirectoryQuietly(found.TempDirectory);
            uploadSessions.TryRemove(uploadId, out _);
            return false;
        }

        session = found;
        return true;
    }

    private void CleanupExpiredUploadSessions()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddHours(-options.UploadSessionTtlHours);

        foreach (UploadSession session in uploadSessions.Values)
        {
            if (session.CreatedAt >= cutoff && session.LastActivityAt >= cutoff)
            {
                continue;
            }

            DeleteDirectoryQuietly(session.TempDirectory);
            uploadSessions.TryRemove(session.UploadId, out _);
        }
    }

    private static string GetChunkPath(UploadSession session, int chunkIndex)
    {
        return Path.Combine(session.TempDirectory, $"{chunkIndex:D10}.chunk");
    }

    private static long GetExpectedChunkSize(UploadSession session, int chunkIndex)
    {
        long offset = (long)chunkIndex * session.ChunkSize;
        return Math.Min(session.ChunkSize, session.FileSize - offset);
    }

    private static bool IsUploadChunkPath(string path)
    {
        return TryParseUploadChunkPath(path, out _, out _);
    }

    private static bool IsUploadCompletePath(string path)
    {
        return TryParseUploadCompletePath(path, out _);
    }

    private static bool IsUploadCancelPath(string path)
    {
        return TryParseUploadCancelPath(path, out _);
    }

    private static bool TryParseUploadChunkPath(string path, out string uploadId, out int chunkIndex)
    {
        uploadId = "";
        chunkIndex = -1;
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 5
            && parts[0] == "api"
            && parts[1] == "uploads"
            && IsValidUploadId(parts[2])
            && parts[3] == "chunks"
            && int.TryParse(parts[4], out chunkIndex)
            && chunkIndex >= 0
            && (uploadId = parts[2]).Length > 0;
    }

    private static bool TryParseUploadStatusPath(string path, out string uploadId)
    {
        return TryParseUploadActionPath(path, "status", out uploadId);
    }

    private static bool TryParseUploadCompletePath(string path, out string uploadId)
    {
        return TryParseUploadActionPath(path, "complete", out uploadId);
    }

    private static bool TryParseUploadCancelPath(string path, out string uploadId)
    {
        return TryParseUploadActionPath(path, "cancel", out uploadId);
    }

    private static bool TryParseUploadActionPath(string path, string action, out string uploadId)
    {
        uploadId = "";
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 4
            && parts[0] == "api"
            && parts[1] == "uploads"
            && IsValidUploadId(parts[2])
            && parts[3] == action)
        {
            uploadId = parts[2];
            return true;
        }

        return false;
    }

    private static bool IsValidUploadId(string uploadId)
    {
        return uploadId.Length is >= 16 and <= 80
            && Regex.IsMatch(uploadId, "^[A-Za-z0-9_-]+$");
    }

    private static bool IsSafeUploadClientPath(string path, bool allowEmpty)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return allowEmpty;
        }

        if (path.Length > 1024
            || path.Contains('\0', StringComparison.Ordinal)
            || path.Contains('\\', StringComparison.Ordinal)
            || Path.IsPathRooted(path))
        {
            return false;
        }

        string[] parts = path.Replace('\\', '/')
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts.Length > 0
            && parts.All(part =>
                part.Length is > 0 and <= 255
                && part != "."
                && part != ".."
                && !part.Contains("..", StringComparison.Ordinal)
                && part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
    }

    private static HttpResponse BuildDownloadResponse(HttpRequest request, FileDownload download, string contentType, bool inline)
    {
        long start = 0;
        long end = download.Length - 1;
        int statusCode = 200;
        string reason = "OK";

        if (TryParseRange(request.Headers.GetValueOrDefault("Range", ""), download.Length, out long rangeStart, out long rangeEnd))
        {
            start = rangeStart;
            end = rangeEnd;
            statusCode = 206;
            reason = "Partial Content";
        }

        long contentLength = end >= start ? end - start + 1 : 0;

        if (download.Stream.CanSeek)
        {
            download.Stream.Seek(start, SeekOrigin.Begin);
        }

        var response = new HttpResponse(statusCode, reason, contentType, contentLength, _ => Task.FromResult<Stream?>(download.Stream));
        response.Headers["Content-Disposition"] = $"{(inline ? "inline" : "attachment")}; filename=\"{EscapeHeaderFileName(Path.GetFileName(download.RelativePath))}\"";
        response.Headers["Last-Modified"] = download.ModifiedAtUtc.ToString("R");
        response.Headers["Accept-Ranges"] = "bytes";

        if (statusCode == 206)
        {
            response.Headers["Content-Range"] = $"bytes {start}-{end}/{download.Length}";
        }

        return response;
    }

    private static bool TryParseRange(string rangeHeader, long length, out long start, out long end)
    {
        start = 0;
        end = Math.Max(0, length - 1);

        if (length <= 0 || string.IsNullOrWhiteSpace(rangeHeader) || !rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string range = rangeHeader["bytes=".Length..].Split(',', 2)[0].Trim();
        string[] parts = range.Split('-', 2);

        if (parts.Length != 2)
        {
            return false;
        }

        if (parts[0].Length == 0)
        {
            if (!long.TryParse(parts[1], out long suffixLength) || suffixLength <= 0)
            {
                return false;
            }

            start = Math.Max(0, length - suffixLength);
            end = length - 1;
            return true;
        }

        if (!long.TryParse(parts[0], out start) || start < 0 || start >= length)
        {
            return false;
        }

        if (parts[1].Length == 0)
        {
            end = length - 1;
            return true;
        }

        if (!long.TryParse(parts[1], out end) || end < start)
        {
            return false;
        }

        end = Math.Min(end, length - 1);
        return true;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return (bytes / 1024d / 1024 / 1024).ToString("0.##", CultureInfo.InvariantCulture) + " GB";
        }

        if (bytes >= 1024L * 1024)
        {
            return (bytes / 1024d / 1024).ToString("0.##", CultureInfo.InvariantCulture) + " MB";
        }

        if (bytes >= 1024)
        {
            return (bytes / 1024d).ToString("0.##", CultureInfo.InvariantCulture) + " KB";
        }

        return bytes + " B";
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

    private static HttpResponse Json(object payload, int statusCode = 200, string reason = "OK")
    {
        var response = new HttpResponse(statusCode, reason, JsonSerializer.Serialize(payload), "application/json; charset=utf-8");
        response.Headers["Cache-Control"] = "no-store";
        return response;
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

    private static bool IsSafeCloudPath(string path, bool allowEmpty)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return allowEmpty;
        }

        if (Path.IsPathRooted(path)
            || path.Contains('\\', StringComparison.Ordinal)
            || path.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = path.Trim()
            .Replace('\\', '/')
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts.Length > 0
            && parts.All(part => part != "." && part != ".." && !part.Contains("..", StringComparison.Ordinal));
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

    private static int ParseMultipartInt(Dictionary<string, string> fields, string fieldName, int fallback)
    {
        return int.TryParse(fields.GetValueOrDefault(fieldName), out int value) ? value : fallback;
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

    private static void DeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static void DeleteDirectoryQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}

public enum UploadSessionStatus
{
    Started,
    Uploading,
    Completed,
    Cancelled
}

public sealed class UploadSession
{
    private readonly ConcurrentDictionary<int, byte> uploadedChunks = new();

    public UploadSession(
        string uploadId,
        string ownerEmail,
        string fileName,
        string requestedPath,
        string currentPath,
        string relativePath,
        string lastModified,
        long fileSize,
        int chunkSize,
        string tempDirectory,
        DateTimeOffset createdAt)
    {
        UploadId = uploadId;
        OwnerEmail = ownerEmail;
        FileName = fileName;
        RequestedPath = requestedPath;
        CurrentPath = currentPath;
        RelativePath = relativePath;
        LastModified = lastModified;
        FileSize = fileSize;
        ChunkSize = chunkSize;
        TempDirectory = tempDirectory;
        CreatedAt = createdAt;
        LastActivityAt = createdAt;
    }

    public string UploadId { get; }
    public string OwnerEmail { get; }
    public string FileName { get; }
    public string RequestedPath { get; }
    public string CurrentPath { get; }
    public string RelativePath { get; }
    public string LastModified { get; }
    public long FileSize { get; }
    public int ChunkSize { get; }
    public string TempDirectory { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastActivityAt { get; set; }
    public UploadSessionStatus Status { get; set; } = UploadSessionStatus.Started;
    public string SavedPath { get; set; } = "";
    public int TotalChunks => (int)Math.Ceiling(FileSize / (double)ChunkSize);
    public IReadOnlyCollection<int> UploadedChunks => uploadedChunks.Keys.ToArray();

    public void MarkUploaded(int chunkIndex)
    {
        uploadedChunks[chunkIndex] = 1;
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
