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
        <section class="panel narrow">
          <h1>Not found</h1>
          <p>This page does not exist.</p>
          <a class="button" href="/">Back</a>
        </section>
        """);
    }

    public static string RenderVerification(string email, string? message)
    {
        return Layout(RenderVerificationPanel(email, message));
    }

    private static string RenderAuthPanel(string mode, string? message)
    {
        bool registerMode = mode.Equals("register", StringComparison.OrdinalIgnoreCase);
        string action = registerMode ? "/register" : "/login";
        string title = registerMode ? "Create account" : "Sign in";
        string button = registerMode ? "Register" : "Login";
        string passwordAuto = registerMode ? "new-password" : "current-password";
        string switchText = registerMode ? "Already have an account?" : "No account yet?";
        string switchLink = registerMode ? "/" : "/?mode=register";
        string switchLabel = registerMode ? "Login" : "Create account";
        string alert = string.IsNullOrWhiteSpace(message)
            ? ""
            : $"""<div class="alert">{Escape(message)}</div>""";

        return $$"""
        <section class="auth-shell">
          <div class="brand">
            <p class="eyebrow">Private storage</p>
            <h1>SkyVault</h1>
            <p>Simple browser cloud with accounts, 5 GB quota per user, and a basic file explorer.</p>
          </div>

          <form class="panel auth-panel" method="post" action="{{action}}">
            <h2>{{title}}</h2>
            {{alert}}
            <label>Email</label>
            <input name="email" type="email" autocomplete="email" required maxlength="254">
            <label>Password</label>
            <input name="password" type="password" autocomplete="{{passwordAuto}}" required minlength="4">
            <button type="submit">{{button}}</button>
            <p class="switch">{{switchText}} <a href="{{switchLink}}">{{switchLabel}}</a></p>
          </form>
        </section>
        """;
    }

    private static string RenderVerificationPanel(string email, string? message)
    {
        string alert = string.IsNullOrWhiteSpace(message)
            ? ""
            : $"""<div class="alert">{Escape(message)}</div>""";

        return $$"""
        <section class="auth-shell">
          <div class="brand">
            <p class="eyebrow">Email verification</p>
            <h1>SkyVault</h1>
            <p>Enter the code sent to {{Escape(email)}} to finish creating the account.</p>
          </div>

          <form class="panel auth-panel" method="post" action="/verify">
            <h2>Verify email</h2>
            {{alert}}
            <input name="email" type="hidden" value="{{Escape(email)}}">
            <label>Code from email</label>
            <input name="code" inputmode="numeric" autocomplete="one-time-code" required minlength="6" maxlength="6" pattern="[0-9]{6}">
            <button type="submit">Verify</button>
            <p class="switch"><a href="/?mode=register">Register again</a></p>
          </form>
        </section>
        """;
    }

    private static string RenderDashboard(UserAccount account, IReadOnlyList<FileEntry> files, string? message)
    {
        double usedPercent = account.QuotaBytes == 0 ? 0 : account.UsedBytes * 100.0 / account.QuotaBytes;
        string rows = files.Count == 0
            ? """<tr><td colspan="3" class="empty">No files yet.</td></tr>"""
            : string.Join("\n", files.Select(RenderFileRow));
        string alert = string.IsNullOrWhiteSpace(message)
            ? ""
            : $"""<div class="alert success">{Escape(message)}</div>""";

        return $$"""
        <header class="topbar">
          <div>
            <p class="eyebrow">SkyVault</p>
            <h1>Explorer</h1>
          </div>
          <form method="post" action="/logout">
            <button class="secondary" type="submit">Logout</button>
          </form>
        </header>

        {{alert}}

        <section class="dashboard">
          <aside class="panel account-card">
            <h2>{{Escape(account.Username)}}</h2>
            <div class="meter">
              <span style="width: {{usedPercent:0.##}}%"></span>
            </div>
            <p>{{FormatBytes(account.UsedBytes)}} used of {{FormatBytes(account.QuotaBytes)}}.</p>
          </aside>

          <section class="panel explorer">
            <div class="section-head">
              <h2>Files</h2>
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
              <tbody>
                {{rows}}
              </tbody>
            </table>
          </section>

          <form class="panel upload-file" id="uploadForm" method="post" action="/files/upload" enctype="multipart/form-data">
            <h2>Upload file</h2>
            <label>Choose file</label>
            <input name="file" id="fileInput" type="file" required>
            <div class="upload-status">
              <div class="progress">
                <span id="progressBar"></span>
              </div>
              <p id="uploadInfo">No upload running.</p>
            </div>
            <button type="submit">Upload</button>
          </form>
        </section>
        <script>
          const form = document.getElementById('uploadForm');
          const input = document.getElementById('fileInput');
          const bar = document.getElementById('progressBar');
          const info = document.getElementById('uploadInfo');

          form.addEventListener('submit', (event) => {
            event.preventDefault();

            if (!input.files.length) {
              info.textContent = 'Choose a file first.';
              return;
            }

            const startedAt = Date.now();
            const data = new FormData(form);
            const request = new XMLHttpRequest();

            request.upload.addEventListener('progress', (event) => {
              if (!event.lengthComputable) {
                info.textContent = 'Uploading...';
                return;
              }

              const percent = Math.round((event.loaded / event.total) * 100);
              const seconds = Math.max((Date.now() - startedAt) / 1000, 0.1);
              const speed = event.loaded / seconds;
              bar.style.width = percent + '%';
              info.textContent = percent + '% - ' + formatBytes(speed) + '/s';
            });

            request.addEventListener('load', () => {
              document.open();
              document.write(request.responseText);
              document.close();
            });

            request.addEventListener('error', () => {
              info.textContent = 'Upload failed.';
            });

            bar.style.width = '0%';
            info.textContent = 'Starting upload...';
            request.open('POST', '/files/upload');
            request.send(data);
          });

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
        return $$"""
        <tr>
          <td>{{Escape(file.Name)}}</td>
          <td>{{FormatBytes(file.SizeBytes)}}</td>
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
            * { box-sizing: border-box; }
            body {
              margin: 0;
              min-height: 100vh;
              font-family: Arial, sans-serif;
              background: #eef2f7;
              color: #172033;
            }
            main {
              width: min(1120px, calc(100% - 32px));
              margin: 0 auto;
              padding: 48px 0;
            }
            h1, h2, p { margin-top: 0; }
            h1 { margin-bottom: 10px; font-size: 44px; line-height: 1.1; }
            h2 { margin-bottom: 18px; font-size: 23px; }
            p { color: #536071; font-size: 16px; line-height: 1.5; }
            a { color: #1769e0; font-weight: 700; text-decoration: none; }
            .eyebrow {
              margin-bottom: 8px;
              color: #1769e0;
              font-size: 13px;
              font-weight: 800;
              letter-spacing: 0;
              text-transform: uppercase;
            }
            .auth-shell {
              min-height: calc(100vh - 96px);
              display: grid;
              grid-template-columns: 1fr 390px;
              gap: 44px;
              align-items: center;
            }
            .brand p:last-child { max-width: 560px; font-size: 19px; }
            .panel {
              background: #ffffff;
              border: 1px solid #d7dee8;
              border-radius: 8px;
              padding: 24px;
              box-shadow: 0 10px 24px rgba(20, 30, 50, 0.08);
            }
            .narrow { max-width: 520px; margin: 0 auto; }
            .alert {
              margin-bottom: 18px;
              padding: 13px 15px;
              border-radius: 8px;
              background: #fff4d6;
              border: 1px solid #f2d27a;
              color: #604500;
            }
            .alert.success {
              background: #eaf8ef;
              border-color: #9bd5ae;
              color: #155724;
            }
            label {
              display: block;
              margin: 14px 0 6px;
              color: #263244;
              font-weight: 700;
            }
            input, textarea {
              width: 100%;
              border: 1px solid #b8c2d1;
              border-radius: 6px;
              padding: 0 12px;
              font: inherit;
              color: #172033;
              background: white;
            }
            input { height: 42px; }
            textarea { min-height: 150px; padding-top: 10px; resize: vertical; }
            input[type="file"] {
              height: auto;
              padding: 10px;
            }
            button, .button {
              display: inline-flex;
              align-items: center;
              justify-content: center;
              min-height: 42px;
              margin-top: 18px;
              padding: 0 18px;
              border: 0;
              border-radius: 6px;
              background: #1769e0;
              color: white;
              font-size: 16px;
              font-weight: 700;
              text-decoration: none;
              cursor: pointer;
            }
            .secondary {
              margin-top: 0;
              background: #e8eef7;
              color: #172033;
            }
            .switch { margin: 16px 0 0; font-size: 15px; }
            .topbar {
              display: flex;
              align-items: center;
              justify-content: space-between;
              gap: 18px;
              margin-bottom: 22px;
            }
            .dashboard {
              display: grid;
              grid-template-columns: 280px 1fr;
              gap: 18px;
              align-items: start;
            }
            .account-card { position: sticky; top: 18px; }
            .meter {
              height: 16px;
              overflow: hidden;
              border-radius: 999px;
              background: #dbe3ef;
            }
            .meter span {
              display: block;
              height: 100%;
              background: #1769e0;
            }
            .explorer { overflow-x: auto; }
            .section-head {
              display: flex;
              justify-content: space-between;
              gap: 16px;
              align-items: baseline;
            }
            .section-head span { color: #6b7280; font-size: 14px; }
            table {
              width: 100%;
              border-collapse: collapse;
            }
            th, td {
              padding: 12px 10px;
              border-bottom: 1px solid #e2e8f0;
              text-align: left;
              white-space: nowrap;
            }
            th { color: #536071; font-size: 13px; text-transform: uppercase; }
            .empty { color: #6b7280; text-align: center; }
            .upload-file { grid-column: 2; }
            .upload-status { margin-top: 16px; }
            .progress {
              height: 14px;
              overflow: hidden;
              border-radius: 999px;
              background: #dbe3ef;
            }
            .progress span {
              display: block;
              width: 0;
              height: 100%;
              background: #1769e0;
              transition: width 0.12s linear;
            }
            #uploadInfo { margin: 8px 0 0; font-size: 14px; }
            @media (max-width: 820px) {
              main { padding: 28px 0; }
              h1 { font-size: 34px; }
              .auth-shell, .dashboard { grid-template-columns: 1fr; }
              .upload-file { grid-column: auto; }
              .account-card { position: static; }
              .topbar { align-items: flex-start; }
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
