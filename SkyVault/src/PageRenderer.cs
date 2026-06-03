using System.Globalization;
using System.Net;

public static class PageRenderer
{
    public static string RenderHome(
        string? email,
        UserAccount? account,
        IReadOnlyList<FileEntry> files,
        string mode = "login",
        string? message = null)
    {
        string content = account is null
            ? RenderAuthPanel(mode, message)
            : RenderDashboard(account, files, message);

        return Layout(content);
    }

    public static string RenderNotFound()
    {
        return Layout("""
        <section class="auth-page">
          <div class="auth-brand">
            <div class="brand-lockup">
              <img class="brand-logo" src="/assets/logo.png" alt="SkyVault">
            </div>
            <h1>Not found</h1>
            <p>The page you requested does not exist.</p>
          </div>
          <div class="auth-card">
            <a class="button" href="/">Back to SkyVault</a>
          </div>
        </section>
        """);
    }

    public static string RenderVerification(string email, string? message)
    {
        return Layout(RenderVerificationPanel(email, message));
    }

    public static string RenderForgotPassword(string? message = null)
    {
        return Layout(RenderForgotPasswordPanel(message));
    }

    public static string RenderResetPassword(string token, string? message = null)
    {
        return Layout(RenderResetPasswordPanel(token, message));
    }

    private static string RenderAuthPanel(string mode, string? message)
    {
        bool registerMode = mode.Equals("register", StringComparison.OrdinalIgnoreCase);
        string action = registerMode ? "/register" : "/login";
        string title = registerMode ? "Create account" : "Sign in";
        string button = registerMode ? "Create account" : "Sign in";
        string passwordAuto = registerMode ? "new-password" : "current-password";
        string switchText = registerMode ? "Already have an account?" : "No account yet?";
        string switchLink = registerMode ? "/" : "/?mode=register";
        string switchLabel = registerMode ? "Sign in" : "Create account";
        string alert = RenderAlert(message);

        return $$"""
        <section class="auth-page">
          <div class="auth-brand">
            <div class="brand-lockup">
              <img class="brand-logo" src="/assets/logo.png" alt="SkyVault">
            </div>
            <h1>Your private file space.</h1>
            <p>Email verification, local storage, and a focused browser workspace.</p>
          </div>

          <form class="auth-card" method="post" action="{{action}}">
            <h2>{{title}}</h2>
            {{alert}}
            <label for="email">Email</label>
            <input id="email" name="email" type="email" autocomplete="email" required maxlength="254">
            <label for="password">Password</label>
            <input id="password" name="password" type="password" autocomplete="{{passwordAuto}}" required minlength="4">
            <button type="submit">{{button}}</button>
            <p class="switch"><a href="/forgot-password">Forgot password?</a></p>
            <p class="switch">{{switchText}} <a href="{{switchLink}}">{{switchLabel}}</a></p>
          </form>
        </section>
        """;
    }

    private static string RenderForgotPasswordPanel(string? message)
    {
        string alert = RenderAlert(message);

        return $$"""
        <section class="auth-page">
          <div class="auth-brand">
            <div class="brand-lockup">
              <img class="brand-logo" src="/assets/logo.png" alt="SkyVault">
            </div>
            <h1>Reset your password.</h1>
            <p>Enter your account email and SkyVault will send a reset link.</p>
          </div>

          <form class="auth-card" method="post" action="/forgot-password">
            <h2>Password reset</h2>
            {{alert}}
            <label for="email">Email</label>
            <input id="email" name="email" type="email" autocomplete="email" required maxlength="254">
            <button type="submit">Send reset link</button>
            <p class="switch"><a href="/">Back to sign in</a></p>
          </form>
        </section>
        """;
    }

    private static string RenderResetPasswordPanel(string token, string? message)
    {
        string alert = RenderAlert(message);

        return $$"""
        <section class="auth-page">
          <div class="auth-brand">
            <div class="brand-lockup">
              <img class="brand-logo" src="/assets/logo.png" alt="SkyVault">
            </div>
            <h1>Choose a new password.</h1>
            <p>Set a new password for your SkyVault account.</p>
          </div>

          <form class="auth-card" method="post" action="/reset-password">
            <h2>New password</h2>
            {{alert}}
            <input name="token" type="hidden" value="{{Escape(token)}}">
            <label for="password">New password</label>
            <input id="password" name="password" type="password" autocomplete="new-password" required minlength="4">
            <label for="confirmPassword">Confirm password</label>
            <input id="confirmPassword" name="confirmPassword" type="password" autocomplete="new-password" required minlength="4">
            <button type="submit">Save password</button>
            <p class="switch"><a href="/">Back to sign in</a></p>
          </form>
        </section>
        """;
    }

    private static string RenderVerificationPanel(string email, string? message)
    {
        string alert = RenderAlert(message);

        return $$"""
        <section class="auth-page">
          <div class="auth-brand">
            <div class="brand-lockup">
              <img class="brand-logo" src="/assets/logo.png" alt="SkyVault">
            </div>
            <h1>Check your inbox.</h1>
            <p>Enter the verification code sent to {{Escape(email)}}.</p>
          </div>

          <form class="auth-card" method="post" action="/verify">
            <h2>Verify email</h2>
            {{alert}}
            <input name="email" type="hidden" value="{{Escape(email)}}">
            <label for="code">Code from email</label>
            <input id="code" name="code" class="code-input" inputmode="numeric" autocomplete="one-time-code" required minlength="6" maxlength="6" pattern="[0-9]{6}">
            <button type="submit">Verify</button>
            <p class="switch"><a href="/?mode=register">Register again</a></p>
          </form>
        </section>
        """;
    }

    private static string RenderDashboard(UserAccount account, IReadOnlyList<FileEntry> files, string? message)
    {
        double usedPercent = account.QuotaBytes == 0 ? 0 : account.UsedBytes * 100.0 / account.QuotaBytes;
        usedPercent = Math.Clamp(usedPercent, 0, 100);
        string usedPercentText = usedPercent.ToString("0.##", CultureInfo.InvariantCulture);
        string rows = files.Count == 0
            ? """<tr class="empty-row"><td colspan="3">No files yet.</td></tr>"""
            : string.Join("\n", files.Select(RenderFileRow));
        string alert = RenderAlert(message, success: true);
        string accountInitial = GetInitial(account.Username);

        return $$"""
        <section class="app-shell">
          <aside class="sidebar">
            <div class="brand-lockup">
              <img class="brand-logo" src="/assets/logo.png" alt="SkyVault">
            </div>

            <form class="upload-panel" id="uploadForm" method="post" action="/files/upload" enctype="multipart/form-data">
              <label class="file-picker" for="fileInput">
                <span class="file-picker-icon">+</span>
                <span>New upload</span>
              </label>
              <input name="file" id="fileInput" type="file" multiple required>
              <input name="folder" id="folderInput" type="file" webkitdirectory directory multiple>
              <button type="submit">Upload</button>
              <div class="upload-status">
                <div class="progress">
                  <span id="progressBar"></span>
                </div>
                <p id="uploadInfo">No upload running.</p>
              </div>
            </form>

            <nav class="nav-list" aria-label="SkyVault sections">
              <a class="nav-item active" href="/">
                <span class="nav-icon home-icon"></span>
                <span>Home</span>
              </a>
              <a class="nav-item" href="/">
                <span class="nav-icon folder-icon"></span>
                <span>My files</span>
              </a>
              <a class="nav-item" href="/">
                <span class="nav-icon clock-icon"></span>
                <span>Recent</span>
              </a>
            </nav>

            <div class="storage-summary">
              <div class="meter">
                <span style="width: {{usedPercentText}}%"></span>
              </div>
              <p>{{FormatBytes(account.UsedBytes)}} of {{FormatBytes(account.QuotaBytes)}} used</p>
            </div>
          </aside>

          <main class="workspace">
            <header class="workspace-top">
              <label class="search-box">
                <span class="search-icon"></span>
                <input id="fileSearch" type="search" placeholder="Search files" autocomplete="off">
              </label>
              <div class="account-menu">
                <span class="account-email">{{Escape(account.Username)}}</span>
                <span class="avatar">{{Escape(accountInitial)}}</span>
                <form method="post" action="/logout">
                  <button class="secondary" type="submit">Logout</button>
                </form>
              </div>
            </header>

            {{alert}}

            <section class="files-panel" id="filesPanel">
              <div class="section-head">
                <div>
                  <p class="eyebrow">Workspace</p>
                  <h1>My files</h1>
                </div>
                <span>{{files.Count}} item(s)</span>
              </div>

              <table>
                <thead>
                  <tr>
                    <th>Name</th>
                    <th>Size</th>
                    <th>Modified</th>
                  </tr>
                </thead>
                <tbody id="fileRows">
                  {{rows}}
                </tbody>
              </table>
              <p class="empty-filter" id="emptyFilter">No matching files.</p>
            </section>
          </main>
        </section>
        <div class="context-menu" id="contextMenu" hidden>
          <button type="button" data-action="upload-file">
            <span class="context-icon upload-file-icon"></span>
            <span>Upload file</span>
          </button>
          <button type="button" data-action="upload-folder">
            <span class="context-icon upload-folder-icon"></span>
            <span>Upload folder</span>
          </button>
          <button type="button" data-action="new-file">
            <span class="context-icon new-file-icon"></span>
            <span>New empty file</span>
          </button>
          <button type="button" data-action="new-folder">
            <span class="context-icon new-folder-icon"></span>
            <span>New folder</span>
          </button>
        </div>
        <script>
          const form = document.getElementById('uploadForm');
          const input = document.getElementById('fileInput');
          const folderInput = document.getElementById('folderInput');
          const bar = document.getElementById('progressBar');
          const info = document.getElementById('uploadInfo');
          const search = document.getElementById('fileSearch');
          const rows = Array.from(document.querySelectorAll('#fileRows tr[data-file-name]'));
          const emptyFilter = document.getElementById('emptyFilter');
          const filesPanel = document.getElementById('filesPanel');
          const contextMenu = document.getElementById('contextMenu');
          let autoUploadAfterPick = false;
          let dragDepth = 0;

          input.addEventListener('change', () => {
            info.textContent = describeFiles(input.files);

            if (autoUploadAfterPick && input.files.length) {
              autoUploadAfterPick = false;
              uploadFileItems(fileItemsFromList(input.files));
              return;
            }

            autoUploadAfterPick = false;
          });

          folderInput.addEventListener('change', () => {
            if (folderInput.files.length) {
              uploadFileItems(fileItemsFromList(folderInput.files));
            }
          });

          form.addEventListener('submit', async (event) => {
            event.preventDefault();

            if (!input.files.length) {
              info.textContent = 'Choose a file first.';
              return;
            }

            await uploadFileItems(fileItemsFromList(input.files));
          });

          search.addEventListener('input', () => {
            const query = search.value.trim().toLowerCase();
            let visible = 0;

            rows.forEach((row) => {
              const match = row.dataset.fileName.includes(query);
              row.hidden = !match;

              if (match) {
                visible += 1;
              }
            });

            emptyFilter.style.display = rows.length && !visible ? 'block' : 'none';
          });

          document.addEventListener('dragenter', (event) => {
            if (!hasDraggedFiles(event)) {
              return;
            }

            dragDepth += 1;
            event.preventDefault();
            filesPanel.classList.add('is-dragging');
            info.textContent = 'Drop files or folders to upload.';
          });

          document.addEventListener('dragover', (event) => {
            if (!hasDraggedFiles(event)) {
              return;
            }

            event.preventDefault();
            event.dataTransfer.dropEffect = 'copy';
          });

          document.addEventListener('dragleave', (event) => {
            if (!hasDraggedFiles(event)) {
              return;
            }

            dragDepth = Math.max(dragDepth - 1, 0);

            if (dragDepth === 0) {
              filesPanel.classList.remove('is-dragging');
            }
          });

          document.addEventListener('drop', async (event) => {
            if (!hasDraggedFiles(event)) {
              return;
            }

            event.preventDefault();
            dragDepth = 0;
            filesPanel.classList.remove('is-dragging');
            hideContextMenu();

            const droppedFiles = await getDroppedFileItems(event.dataTransfer);
            await uploadFileItems(droppedFiles);
          });

          filesPanel.addEventListener('contextmenu', (event) => {
            event.preventDefault();
            showContextMenu(event.clientX, event.clientY);
          });

          document.addEventListener('click', (event) => {
            if (!contextMenu.contains(event.target)) {
              hideContextMenu();
            }
          });

          document.addEventListener('keydown', (event) => {
            if (event.key === 'Escape') {
              hideContextMenu();
            }
          });

          contextMenu.addEventListener('click', async (event) => {
            const button = event.target.closest('button[data-action]');

            if (!button) {
              return;
            }

            const action = button.dataset.action;
            hideContextMenu();

            if (action === 'upload-file') {
              autoUploadAfterPick = true;
              input.click();
              return;
            }

            if (action === 'upload-folder') {
              folderInput.click();
              return;
            }

            if (action === 'new-file') {
              await createRemoteItem('/files/create', 'fileName', 'File name');
              return;
            }

            if (action === 'new-folder') {
              await createRemoteItem('/folders/create', 'folderName', 'Folder name');
            }
          });

          async function uploadFileItems(items) {
            const uploadItems = items.filter((item) => item.file);

            if (!uploadItems.length) {
              info.textContent = 'No files to upload.';
              return;
            }

            let lastResponse = '';

            try {
              for (let index = 0; index < uploadItems.length; index += 1) {
                const item = uploadItems[index];
                const data = new FormData();
                data.append('file', item.file, item.path || item.file.name);
                lastResponse = await uploadOne(data, item, index, uploadItems.length);
              }

              input.value = '';
              folderInput.value = '';
              document.open();
              document.write(lastResponse);
              document.close();
            } catch {
              info.textContent = 'Upload failed.';
            }
          }

          function uploadOne(data, item, index, total) {
            return new Promise((resolve, reject) => {
              const startedAt = Date.now();
              const request = new XMLHttpRequest();
              const prefix = total > 1 ? (index + 1) + '/' + total + ' - ' : '';

              request.upload.addEventListener('progress', (event) => {
                if (!event.lengthComputable) {
                  info.textContent = prefix + 'Uploading ' + item.path + '...';
                  return;
                }

                const percent = Math.round((event.loaded / event.total) * 100);
                const seconds = Math.max((Date.now() - startedAt) / 1000, 0.1);
                const speed = event.loaded / seconds;
                bar.style.width = percent + '%';
                info.textContent = prefix + percent + '% - ' + formatBytes(speed) + '/s';
              });

              request.addEventListener('load', () => {
                resolve(request.responseText);
              });

              request.addEventListener('error', reject);
              request.open('POST', '/files/upload');
              bar.style.width = '0%';
              info.textContent = prefix + 'Starting ' + item.path + '...';
              request.send(data);
            });
          }

          function fileItemsFromList(files) {
            return Array.from(files).map((file) => ({
              file,
              path: file.webkitRelativePath || file.name
            }));
          }

          async function getDroppedFileItems(dataTransfer) {
            const transferItems = Array.from(dataTransfer.items || []);
            const collected = [];

            if (transferItems.length && transferItems.some((item) => item.webkitGetAsEntry)) {
              for (const item of transferItems) {
                if (item.kind !== 'file') {
                  continue;
                }

                const entry = item.webkitGetAsEntry ? item.webkitGetAsEntry() : null;

                if (entry) {
                  await collectEntry(entry, '', collected);
                }
              }

              if (collected.length) {
                return collected;
              }
            }

            return fileItemsFromList(dataTransfer.files || []);
          }

          function collectEntry(entry, prefix, collected) {
            return new Promise((resolve) => {
              if (entry.isFile) {
                entry.file((file) => {
                  collected.push({ file, path: prefix + file.name });
                  resolve();
                }, resolve);
                return;
              }

              if (!entry.isDirectory) {
                resolve();
                return;
              }

              const reader = entry.createReader();

              const readBatch = () => {
                reader.readEntries(async (entries) => {
                  if (!entries.length) {
                    resolve();
                    return;
                  }

                  for (const child of entries) {
                    await collectEntry(child, prefix + entry.name + '/', collected);
                  }

                  readBatch();
                }, resolve);
              };

              readBatch();
            });
          }

          async function createRemoteItem(endpoint, fieldName, promptText) {
            const rawName = prompt(promptText);

            if (rawName === null) {
              return;
            }

            const name = rawName.trim();

            if (!name) {
              info.textContent = 'Name is required.';
              return;
            }

            const data = new URLSearchParams();
            data.set(fieldName, name);
            info.textContent = 'Creating...';

            try {
              const response = await fetch(endpoint, {
                method: 'POST',
                headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                body: data.toString()
              });
              const html = await response.text();
              document.open();
              document.write(html);
              document.close();
            } catch {
              info.textContent = 'Could not create item.';
            }
          }

          function hasDraggedFiles(event) {
            return Array.from(event.dataTransfer?.types || []).includes('Files');
          }

          function describeFiles(files) {
            if (!files.length) {
              return 'No upload running.';
            }

            if (files.length === 1) {
              return files[0].webkitRelativePath || files[0].name;
            }

            return files.length + ' files selected.';
          }

          function showContextMenu(x, y) {
            contextMenu.hidden = false;
            const rect = contextMenu.getBoundingClientRect();
            const left = Math.min(x, window.innerWidth - rect.width - 10);
            const top = Math.min(y, window.innerHeight - rect.height - 10);
            contextMenu.style.left = Math.max(10, left) + 'px';
            contextMenu.style.top = Math.max(10, top) + 'px';
          }

          function hideContextMenu() {
            contextMenu.hidden = true;
          }

          function formatBytes(bytes) {
            if (bytes >= 1024 * 1024) {
              return (bytes / 1024 / 1024).toFixed(2) + ' MB';
            }

            if (bytes >= 1024) {
              return (bytes / 1024).toFixed(2) + ' KB';
            }

            return Math.round(bytes) + ' B';
          }
        </script>
        """;
    }

    private static string RenderFileRow(FileEntry file)
    {
        string iconClass = file.IsFolder ? "folder-icon row-folder-icon" : "file-icon";
        string size = file.IsFolder ? "Folder" : FormatBytes(file.SizeBytes);

        return $$"""
        <tr data-file-name="{{Escape(file.Name.ToLowerInvariant())}}" data-entry-kind="{{(file.IsFolder ? "folder" : "file")}}">
          <td>
            <span class="file-name">
              <span class="{{iconClass}}"></span>
              <span>{{Escape(file.Name)}}</span>
            </span>
          </td>
          <td>{{size}}</td>
          <td>{{file.ModifiedAt.LocalDateTime:g}}</td>
        </tr>
        """;
    }

    private static string Layout(string content)
    {
        return $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>SkyVault</title>
          <style>
            :root {
              --bg: #f7f9fc;
              --surface: #ffffff;
              --surface-soft: #eef4ff;
              --line: #dfe5ef;
              --line-strong: #c8d2e1;
              --text: #1f2937;
              --muted: #64748b;
              --blue: #2563eb;
              --blue-soft: #dbeafe;
              --green: #10b981;
              --amber: #f59e0b;
              --shadow: 0 18px 38px rgba(31, 41, 55, 0.10);
            }

            * { box-sizing: border-box; }

            body {
              margin: 0;
              min-height: 100vh;
              font-family: Arial, sans-serif;
              background: var(--bg);
              color: var(--text);
            }

            body, input, button {
              font-size: 15px;
            }

            main {
              min-height: 100vh;
            }

            h1, h2, p {
              margin-top: 0;
            }

            h1 {
              margin-bottom: 6px;
              font-size: 30px;
              line-height: 1.15;
              font-weight: 700;
            }

            h2 {
              margin-bottom: 20px;
              font-size: 24px;
              line-height: 1.2;
            }

            p {
              color: var(--muted);
              line-height: 1.5;
            }

            a {
              color: var(--blue);
              font-weight: 700;
              text-decoration: none;
            }

            .brand-lockup {
              display: inline-flex;
              align-items: center;
            }

            .brand-logo {
              display: block;
              width: 174px;
              height: auto;
              object-fit: contain;
            }

            .auth-page {
              min-height: 100vh;
              display: grid;
              grid-template-columns: minmax(0, 1fr) 390px;
              gap: 48px;
              align-items: center;
              width: min(1080px, calc(100% - 40px));
              margin: 0 auto;
              padding: 44px 0;
            }

            .auth-brand h1 {
              max-width: 520px;
              margin-top: 42px;
              font-size: 48px;
            }

            .auth-brand p {
              max-width: 520px;
              font-size: 18px;
            }

            .auth-card {
              background: var(--surface);
              border: 1px solid var(--line);
              border-radius: 8px;
              padding: 30px;
              box-shadow: var(--shadow);
            }

            .alert {
              margin-bottom: 18px;
              padding: 13px 15px;
              border-radius: 8px;
              background: #fff7ed;
              border: 1px solid #fed7aa;
              color: #7c2d12;
            }

            .alert.success {
              background: #ecfdf5;
              border-color: #a7f3d0;
              color: #065f46;
            }

            label {
              display: block;
              margin: 15px 0 7px;
              color: #334155;
              font-weight: 700;
            }

            input {
              width: 100%;
              height: 44px;
              border: 1px solid var(--line-strong);
              border-radius: 8px;
              padding: 0 13px;
              font: inherit;
              color: var(--text);
              background: white;
            }

            input:focus {
              outline: 2px solid var(--blue-soft);
              border-color: var(--blue);
            }

            .code-input {
              text-align: center;
              font-size: 22px;
              font-weight: 700;
            }

            button, .button {
              display: inline-flex;
              align-items: center;
              justify-content: center;
              min-height: 42px;
              margin-top: 18px;
              padding: 0 18px;
              border: 0;
              border-radius: 8px;
              background: var(--blue);
              color: white;
              font: inherit;
              font-weight: 700;
              text-decoration: none;
              cursor: pointer;
            }

            .secondary {
              margin-top: 0;
              background: #eef2f7;
              color: var(--text);
            }

            .switch {
              margin: 16px 0 0;
              font-size: 14px;
            }

            .app-shell {
              min-height: 100vh;
              display: grid;
              grid-template-columns: 256px minmax(0, 1fr);
              background: var(--bg);
            }

            .sidebar {
              display: flex;
              flex-direction: column;
              gap: 18px;
              padding: 22px 16px;
              border-right: 1px solid var(--line);
              background: #f4f7fb;
            }

            .sidebar .brand-logo {
              width: 168px;
            }

            .upload-panel {
              display: grid;
              gap: 12px;
              margin-top: 10px;
            }

            .upload-panel input[type="file"] {
              position: absolute;
              width: 1px;
              height: 1px;
              overflow: hidden;
              clip: rect(0, 0, 0, 0);
            }

            .file-picker {
              display: flex;
              align-items: center;
              gap: 10px;
              min-height: 52px;
              margin: 0;
              padding: 0 16px;
              border: 1px solid var(--line);
              border-radius: 8px;
              background: var(--surface);
              box-shadow: 0 8px 18px rgba(31, 41, 55, 0.08);
              cursor: pointer;
            }

            .file-picker-icon {
              display: grid;
              place-items: center;
              width: 28px;
              height: 28px;
              border-radius: 8px;
              background: var(--blue);
              color: white;
              font-size: 22px;
              line-height: 1;
            }

            .upload-panel button {
              width: 100%;
              margin-top: 0;
            }

            .upload-status {
              display: grid;
              gap: 8px;
            }

            .upload-status p {
              margin: 0;
              font-size: 13px;
              word-break: break-word;
            }

            .progress,
            .meter {
              height: 8px;
              overflow: hidden;
              border-radius: 999px;
              background: #dce4ef;
            }

            .progress span,
            .meter span {
              display: block;
              width: 0;
              height: 100%;
              background: linear-gradient(90deg, var(--blue), var(--green));
            }

            .nav-list {
              display: grid;
              gap: 4px;
              margin-top: 4px;
            }

            .nav-item {
              display: flex;
              align-items: center;
              gap: 12px;
              min-height: 42px;
              padding: 0 12px;
              border-radius: 999px;
              color: #334155;
              font-weight: 700;
            }

            .nav-item.active {
              background: var(--blue-soft);
              color: #0f3b80;
            }

            .nav-icon,
            .file-icon,
            .search-icon {
              position: relative;
              display: inline-block;
              flex: 0 0 auto;
            }

            .home-icon {
              width: 16px;
              height: 16px;
              border-radius: 4px;
              background: #0f3b80;
              transform: rotate(45deg);
            }

            .home-icon::after {
              content: "";
              position: absolute;
              left: 4px;
              top: 4px;
              width: 8px;
              height: 8px;
              background: #0f3b80;
              transform: rotate(-45deg);
            }

            .folder-icon {
              width: 18px;
              height: 14px;
              border: 2px solid currentColor;
              border-radius: 3px;
            }

            .folder-icon::before {
              content: "";
              position: absolute;
              left: 1px;
              top: -5px;
              width: 8px;
              height: 5px;
              border: 2px solid currentColor;
              border-bottom: 0;
              border-radius: 3px 3px 0 0;
            }

            .clock-icon {
              width: 18px;
              height: 18px;
              border: 2px solid currentColor;
              border-radius: 50%;
            }

            .clock-icon::before {
              content: "";
              position: absolute;
              left: 7px;
              top: 3px;
              width: 2px;
              height: 6px;
              background: currentColor;
            }

            .clock-icon::after {
              content: "";
              position: absolute;
              left: 7px;
              top: 8px;
              width: 6px;
              height: 2px;
              background: currentColor;
            }

            .storage-summary {
              margin-top: auto;
              padding: 12px;
            }

            .storage-summary p {
              margin: 9px 0 0;
              font-size: 13px;
            }

            .workspace {
              display: grid;
              grid-template-rows: auto auto 1fr;
              gap: 18px;
              min-width: 0;
              padding: 18px 22px 28px;
            }

            .workspace-top {
              display: grid;
              grid-template-columns: minmax(260px, 760px) auto;
              gap: 18px;
              align-items: center;
            }

            .search-box {
              position: relative;
              margin: 0;
            }

            .search-box input {
              height: 50px;
              padding-left: 44px;
              border: 0;
              background: #e8eef7;
              border-radius: 999px;
            }

            .search-icon {
              position: absolute;
              left: 18px;
              top: 16px;
              width: 14px;
              height: 14px;
              border: 2px solid #475569;
              border-radius: 50%;
            }

            .search-icon::after {
              content: "";
              position: absolute;
              right: -6px;
              bottom: -5px;
              width: 8px;
              height: 2px;
              border-radius: 999px;
              background: #475569;
              transform: rotate(45deg);
            }

            .account-menu {
              display: flex;
              align-items: center;
              justify-content: flex-end;
              gap: 10px;
              min-width: 0;
            }

            .account-email {
              max-width: 250px;
              overflow: hidden;
              color: var(--muted);
              font-size: 14px;
              text-overflow: ellipsis;
              white-space: nowrap;
            }

            .avatar {
              display: grid;
              place-items: center;
              width: 36px;
              height: 36px;
              border-radius: 50%;
              background: #0f766e;
              color: white;
              font-weight: 800;
            }

            .files-panel {
              position: relative;
              min-width: 0;
              overflow: hidden;
              border: 1px solid var(--line);
              border-radius: 8px;
              background: var(--surface);
              box-shadow: 0 8px 24px rgba(31, 41, 55, 0.06);
            }

            .files-panel.is-dragging {
              border-color: var(--blue);
              box-shadow: 0 0 0 4px rgba(37, 99, 235, 0.12), 0 8px 24px rgba(31, 41, 55, 0.06);
            }

            .files-panel.is-dragging::after {
              content: "Drop files or folders to upload";
              position: absolute;
              inset: 12px;
              z-index: 4;
              display: grid;
              place-items: center;
              border: 2px dashed #60a5fa;
              border-radius: 8px;
              background: rgba(239, 246, 255, 0.92);
              color: #1d4ed8;
              font-weight: 800;
              pointer-events: none;
            }

            .section-head {
              display: flex;
              align-items: center;
              justify-content: space-between;
              gap: 18px;
              padding: 24px 26px 16px;
            }

            .section-head .eyebrow {
              margin-bottom: 4px;
              color: var(--blue);
              font-size: 12px;
              font-weight: 800;
              text-transform: uppercase;
            }

            .section-head span {
              color: var(--muted);
              font-size: 14px;
              white-space: nowrap;
            }

            table {
              width: 100%;
              border-collapse: collapse;
            }

            th, td {
              padding: 13px 26px;
              border-top: 1px solid #edf1f7;
              text-align: left;
              white-space: nowrap;
            }

            th {
              color: var(--muted);
              font-size: 12px;
              font-weight: 800;
              text-transform: uppercase;
            }

            tbody tr:hover {
              background: #f8fafc;
            }

            .file-name {
              display: inline-flex;
              align-items: center;
              gap: 11px;
              min-width: 0;
              font-weight: 700;
            }

            .file-icon {
              width: 22px;
              height: 26px;
              border: 2px solid #94a3b8;
              border-radius: 4px;
              background: #f8fafc;
            }

            .file-icon::after {
              content: "";
              position: absolute;
              right: -2px;
              top: -2px;
              width: 8px;
              height: 8px;
              border-left: 2px solid #94a3b8;
              border-bottom: 2px solid #94a3b8;
              background: #eef2f7;
              border-radius: 0 3px 0 3px;
            }

            .row-folder-icon {
              width: 24px;
              height: 18px;
              color: #2563eb;
              background: #dbeafe;
            }

            .context-menu {
              position: fixed;
              z-index: 20;
              display: grid;
              min-width: 190px;
              padding: 6px;
              border: 1px solid var(--line);
              border-radius: 8px;
              background: var(--surface);
              box-shadow: 0 18px 36px rgba(15, 23, 42, 0.18);
            }

            .context-menu[hidden] {
              display: none;
            }

            .context-menu button {
              justify-content: flex-start;
              gap: 10px;
              min-height: 38px;
              margin: 0;
              padding: 0 10px;
              border-radius: 6px;
              background: transparent;
              color: var(--text);
              font-weight: 700;
            }

            .context-menu button:hover {
              background: #eef4ff;
            }

            .context-icon {
              position: relative;
              display: inline-block;
              width: 18px;
              height: 18px;
              color: #2563eb;
            }

            .upload-file-icon,
            .new-file-icon {
              border: 2px solid currentColor;
              border-radius: 4px;
            }

            .upload-file-icon::after,
            .new-file-icon::after {
              content: "";
              position: absolute;
              right: -2px;
              top: -2px;
              width: 7px;
              height: 7px;
              border-left: 2px solid currentColor;
              border-bottom: 2px solid currentColor;
              background: white;
              border-radius: 0 3px 0 3px;
            }

            .upload-file-icon::before,
            .upload-folder-icon::after,
            .new-file-icon::before,
            .new-folder-icon::after {
              content: "";
              position: absolute;
              left: 6px;
              top: 4px;
              width: 2px;
              height: 10px;
              background: currentColor;
            }

            .upload-file-icon::before,
            .upload-folder-icon::after {
              transform: rotate(90deg);
            }

            .upload-folder-icon,
            .new-folder-icon {
              width: 20px;
              height: 15px;
              margin-top: 2px;
              border: 2px solid currentColor;
              border-radius: 3px;
            }

            .upload-folder-icon::before,
            .new-folder-icon::before {
              content: "";
              position: absolute;
              left: 1px;
              top: -6px;
              width: 8px;
              height: 5px;
              border: 2px solid currentColor;
              border-bottom: 0;
              border-radius: 3px 3px 0 0;
            }

            .new-file-icon::before,
            .new-folder-icon::after {
              box-shadow: 0 0 0 0 currentColor;
            }

            .new-file-icon::before {
              transform: none;
            }

            .new-file-icon {
              background:
                linear-gradient(currentColor, currentColor) center / 10px 2px no-repeat;
            }

            .new-folder-icon {
              background:
                linear-gradient(currentColor, currentColor) center / 10px 2px no-repeat;
            }

            .empty-row td,
            .empty-filter {
              color: var(--muted);
              text-align: center;
            }

            .empty-row td {
              padding: 72px 24px;
            }

            .empty-filter {
              display: none;
              margin: 0;
              padding: 52px 24px;
              border-top: 1px solid #edf1f7;
            }

            @media (max-width: 900px) {
              .app-shell {
                grid-template-columns: 1fr;
              }

              .sidebar {
                position: static;
                border-right: 0;
                border-bottom: 1px solid var(--line);
              }

              .nav-list {
                grid-template-columns: repeat(3, minmax(0, 1fr));
              }

              .nav-item {
                justify-content: center;
              }

              .storage-summary {
                margin-top: 0;
              }

              .workspace-top {
                grid-template-columns: 1fr;
              }

              .account-menu {
                justify-content: space-between;
              }
            }

            @media (max-width: 720px) {
              .auth-page {
                grid-template-columns: 1fr;
                width: min(100% - 28px, 520px);
                gap: 24px;
              }

              .auth-brand h1 {
                margin-top: 26px;
                font-size: 36px;
              }

              .workspace {
                padding: 14px;
              }

              .section-head {
                align-items: flex-start;
                flex-direction: column;
              }

              th:nth-child(3),
              td:nth-child(3) {
                display: none;
              }

              th, td {
                padding: 12px 16px;
              }
            }
          </style>
        </head>
        <body>
          <main>
            {{content}}
          </main>
        </body>
        </html>
        """;
    }

    private static string RenderAlert(string? message, bool success = false)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "";
        }

        string className = success ? "alert success" : "alert";
        return $"""<div class="{className}">{Escape(message)}</div>""";
    }

    private static string GetInitial(string email)
    {
        char first = email.Trim().FirstOrDefault(char.IsLetterOrDigit);
        return first == default ? "S" : char.ToUpperInvariant(first).ToString();
    }

    private static string Escape(string value)
    {
        return WebUtility.HtmlEncode(value);
    }

    private static string FormatBytes(long bytes)
    {
        const double kib = 1024.0;
        const double mib = kib * 1024.0;
        const double gib = mib * 1024.0;

        if (bytes >= gib)
        {
            return $"{bytes / gib:0.##} GB";
        }

        if (bytes >= mib)
        {
            return $"{bytes / mib:0.##} MB";
        }

        if (bytes >= kib)
        {
            return $"{bytes / kib:0.##} KB";
        }

        return $"{bytes} B";
    }
}
