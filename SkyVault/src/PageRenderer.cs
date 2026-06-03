using System.Globalization;
using System.Net;
using System.Text;

public static class PageRenderer
{
    public static string RenderHome(
        string? email,
        UserAccount? account,
        IReadOnlyList<FileEntry> files,
        string mode = "login",
        string? message = null,
        string currentDirectory = "",
        string currentView = "home",
        IReadOnlyList<FileEntry>? trashFiles = null)
    {
        string content = account is null
            ? RenderAuthPanel(mode, message)
            : RenderDashboard(account, files, currentDirectory, currentView, trashFiles ?? [], message);

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

    public static string RenderFilePreview(string filePath, byte[] content)
    {
        filePath = NormalizeCloudPath(filePath);
        string fileName = GetDisplayName(filePath);
        string parentDirectory = GetParentDirectory(filePath);
        string backHref = parentDirectory.Length == 0 ? "/" : $"/?path={WebUtility.UrlEncode(parentDirectory)}";
        string rawHref = $"/files/raw?path={WebUtility.UrlEncode(filePath)}";
        string downloadHref = $"/files/download?path={WebUtility.UrlEncode(filePath)}";
        string viewer = RenderFileViewer(filePath, rawHref, content);

        return Layout($$"""
        <section class="preview-shell">
          <header class="preview-top">
            <a class="button secondary" href="{{backHref}}">Back</a>
            <div class="preview-title">
              <p class="eyebrow">/home/{{Escape(parentDirectory)}}</p>
              <h1>{{Escape(fileName)}}</h1>
            </div>
            <a class="button secondary" href="{{downloadHref}}">Download</a>
          </header>
          <section class="preview-panel">
            {{viewer}}
          </section>
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

    private static string RenderDashboard(UserAccount account, IReadOnlyList<FileEntry> files, string currentDirectory, string currentView, IReadOnlyList<FileEntry> trashFiles, string? message)
    {
        currentDirectory = NormalizeCloudPath(currentDirectory);
        currentView = NormalizeView(currentView);
      if (currentView == "trash" && currentDirectory.Length == 0)
      {
        currentDirectory = ".trash";
      }

      if (string.Equals(currentDirectory, ".trash", StringComparison.OrdinalIgnoreCase))
      {
        currentView = "trash";
      }
        bool trashMode = currentView == "trash";
        IReadOnlyList<FileEntry> visibleFiles = GetVisibleFiles(files, trashFiles, currentDirectory, currentView);
        double usedPercent = account.QuotaBytes == 0 ? 0 : account.UsedBytes * 100.0 / account.QuotaBytes;
        usedPercent = Math.Clamp(usedPercent, 0, 100);
        string usedPercentText = usedPercent.ToString("0.##", CultureInfo.InvariantCulture);
        string rows = visibleFiles.Count == 0
            ? """<tr class="empty-row"><td colspan="4">No files yet.</td></tr>"""
            : string.Join("\n", visibleFiles.Select(file => RenderFileRow(file, currentView != "trash")));
        string cards = visibleFiles.Count == 0
            ? """<p class="empty-grid">No files yet.</p>"""
            : string.Join("\n", visibleFiles.Select(file => RenderFileCard(file, currentView != "trash")));
        string alert = RenderAlert(message, success: true);
        string accountInitial = GetInitial(account.Username);
        string uploadInfo = message is null ? "No upload running." : Escape(message);
        string largestFiles = RenderLargestFiles(files);
        string parentDirectory = GetParentDirectory(currentDirectory);
        string parentHref = currentDirectory.Length == 0 ? "/" : $"/?path={WebUtility.UrlEncode(parentDirectory)}";
        string pathBreadcrumbs = RenderPathBreadcrumbs(currentDirectory);
        string trashActions = trashMode
            ? "<button class=\"trash-empty-button\" type=\"button\" id=\"emptyTrash\"><span class=\"context-icon delete-forever-icon\"></span><span>Empty trash</span></button>"
            : "";
        string contextMenuItems = trashMode
            ? "<button type=\"button\" data-action=\"file-info\"><span class=\"context-icon info-icon\"></span><span>Informacje</span></button><button type=\"button\" data-action=\"restore-trash\"><span class=\"context-icon restore-icon\"></span><span>Restore</span></button><button type=\"button\" data-action=\"delete-forever\"><span class=\"context-icon delete-forever-icon\"></span><span>Delete forever</span></button>"
            : "<button type=\"button\" data-action=\"file-info\"><span class=\"context-icon info-icon\"></span><span>Informacje</span></button><button type=\"button\" data-action=\"upload-file\"><span class=\"context-icon upload-file-icon\"></span><span>Upload file</span></button><button type=\"button\" data-action=\"upload-folder\"><span class=\"context-icon upload-folder-icon\"></span><span>Upload folder</span></button><button type=\"button\" data-action=\"new-file\"><span class=\"context-icon new-file-icon\"></span><span>New empty file</span></button><button type=\"button\" data-action=\"new-folder\"><span class=\"context-icon new-folder-icon\"></span><span>New folder</span></button><button type=\"button\" data-action=\"download-zip\"><span class=\"context-icon download-zip-icon\"></span><span>Download ZIP</span></button><button type=\"button\" data-action=\"move-selected-here\"><span class=\"context-icon move-here-icon\"></span><span>Move selected here</span></button><button type=\"button\" data-action=\"move-trash\"><span class=\"context-icon trash-icon\"></span><span>Move to trash</span></button>";

        return $$"""
        <section class="app-shell">
          <aside class="sidebar">
            <div class="brand-lockup">
              <img class="brand-logo" src="/assets/logo.png" alt="SkyVault">
            </div>

            <form class="upload-panel compact-upload" id="uploadForm" method="post" action="/files/upload" enctype="multipart/form-data">
              <input name="file" id="fileInput" type="file" multiple required>
              <input name="folder" id="folderInput" type="file" webkitdirectory directory multiple>
              <input name="currentPath" id="currentPath" type="hidden" value="{{Escape(currentDirectory)}}">
              <div class="upload-status">
                <div class="progress">
                  <span id="progressBar"></span>
                </div>
              </div>
            </form>

            <nav class="nav-list" aria-label="SkyVault sections">
              <p class="places-heading">Miejsca</p>
              <a class="nav-item {{HomeActiveClass(currentView, currentDirectory)}}" href="/">
                <img class="nav-icon-img" src="/assets/icons/user-home.svg" alt="">
                <span>Katalog domowy</span>
              </a>
              <a class="nav-item {{ActiveClass(currentView, "documents")}}" href="/?view=documents">
                <img class="nav-icon-img" src="/assets/icons/folder-documents.svg" alt="">
                <span>Dokumenty</span>
              </a>
              <a class="nav-item {{ActiveClass(currentView, "images")}}" href="/?view=images">
                <img class="nav-icon-img" src="/assets/icons/folder-pictures.svg" alt="">
                <span>Obrazy</span>
              </a>
              <a class="nav-item {{ActiveClass(currentView, "videos")}}" href="/?view=videos">
                <img class="nav-icon-img" src="/assets/icons/folder-videos.svg" alt="">
                <span>Filmy</span>
              </a>
              <a class="nav-item {{ActiveClass(currentView, "music")}}" href="/?view=music">
                <img class="nav-icon-img" src="/assets/icons/folder-music.svg" alt="">
                <span>Muzyka</span>
              </a>
              <a class="nav-item {{ActiveClass(currentView, "recent")}}" href="/?view=recent">
                <img class="nav-icon-img" src="/assets/icons/document-open-recent.svg" alt="">
                <span>Ostatnie pliki</span>
              </a>
              <a class="nav-item {{ActiveClass(currentView, "trash")}}" href="/?view=trash">
                <img class="nav-icon-img" src="/assets/icons/user-trash.svg" alt="">
                <span>Kosz</span>
              </a>

            </nav>

            <div class="storage-summary">
              <div class="largest-files" id="largestFiles" hidden>
                <p class="largest-title">Największe pliki</p>
                {{largestFiles}}
              </div>
              <div class="drive-row">
                <img class="nav-icon-img" src="/assets/icons/media-flash-sd-mmc.svg" alt="">
                <span>Skydysk</span>
                <button class="drive-toggle" id="driveToggle" type="button" aria-label="Pokaż największe pliki" aria-expanded="false">
                  <img class="drive-eject-icon" src="/assets/icons/media-eject.svg" alt="">
                </button>
              </div>
              <div class="meter">
                <span style="width: {{usedPercentText}}%"></span>
              </div>
              <p>{{FormatBytes(account.UsedBytes)}} z 5 GB</p>
              <p id="uploadInfo">{{uploadInfo}}</p>
            </div>
          </aside>

          <main class="workspace">
            <header class="workspace-top">
              <div></div>
              <div class="account-menu">
                <span class="account-email">{{Escape(account.Username)}}</span>
                <span class="avatar">{{Escape(accountInitial)}}</span>
                <form method="post" action="/logout">
                  <button class="secondary" type="submit">Logout</button>
                </form>
              </div>
            </header>

            <div class="location-row">
              <div class="path-bar" aria-label="Current cloud path">
                <a class="path-up" href="{{parentHref}}" aria-label="Parent folder">..</a>
                <img class="path-folder-icon" src="/assets/icons/folder.svg" alt="" aria-hidden="true">
                <div class="path-breadcrumbs" id="pathBreadcrumbs">
                  {{pathBreadcrumbs}}
                </div>
                <form class="path-editor" id="pathEditor" hidden action="/">
                  <input id="pathEditorInput" name="path" value="{{Escape(currentDirectory.Length == 0 ? "/home/" : ToVirtualPath(currentDirectory, true))}}" autocomplete="off">
                  <button class="path-editor-button" type="button" data-editor-action="clear" aria-label="Clear path">
                    <svg viewBox="0 0 22 22" focusable="false">
                      <path fill="currentColor" d="M14 5l5 6-5 6H3V5zm-2 3h-1c-.28 0-.53.11-.71.29L9 9.59 7.71 8.29A1 1 0 0 0 7 8H6v1c0 .28.11.53.29.71L7.59 11l-1.3 1.29A1 1 0 0 0 6 13v1h1c.28 0 .53-.11.71-.29L9 12.41l1.29 1.3c.18.18.43.29.71.29h1v-1c0-.28-.11-.53-.29-.71L10.41 11l1.3-1.29c.18-.18.29-.43.29-.71z"/>
                    </svg>
                  </button>
                  <button class="path-editor-button" type="button" data-editor-action="menu" aria-label="Show path menu">
                    <svg viewBox="0 0 16 16" focusable="false">
                      <path fill="currentColor" d="M7 2v8L3.5 6.5 2 8l6 6 6-6-1.5-1.5L9 10V2z"/>
                    </svg>
                  </button>
                  <button class="path-editor-button" type="submit" aria-label="Open path">
                    <svg viewBox="0 0 22 22" focusable="false">
                      <path fill="currentColor" d="M16.5 8c0 0 .965-.965.215-1.715S15 6.5 15 6.5l-6 7-2-2s-.965-.965-1.715-.215S5.5 13 5.5 13L9 16.5z"/>
                    </svg>
                  </button>
                </form>
              </div>
            </div>

            <div class="alert-slot">{{alert}}</div>

            <section class="files-panel" id="filesPanel" data-view="{{currentView}}">
              <div class="section-head">
                <div class="file-actions">
                  <span id="fileCount">{{visibleFiles.Count}} element(y)</span>
                  <div class="file-actions-right">
                    {{trashActions}}
                    <div class="file-search" id="fileSearchBox" hidden>
                      <input id="fileSearchInput" type="search" autocomplete="off" placeholder="Szukaj">
                    </div>
                    <button class="tool-button search-toggle" id="fileSearchToggle" type="button" aria-label="Szukaj" aria-expanded="false">
                      <span class="search-icon"></span>
                    </button>
                    <select class="sort-select" id="sortSelect" aria-label="Sortuj pliki">
                      <option value="modified-desc">Data: najnowsze</option>
                      <option value="modified-asc">Data: najstarsze</option>
                      <option value="name-asc">Nazwa: A-Z</option>
                      <option value="name-desc">Nazwa: Z-A</option>
                      <option value="size-desc">Rozmiar: największe</option>
                      <option value="size-asc">Rozmiar: najmniejsze</option>
                    </select>
                    <div class="view-switch" aria-label="File view">
                      <button class="view-button active" type="button" data-view-mode="list" aria-label="List view">
                        <span class="view-list-icon"></span>
                      </button>
                      <button class="view-button" type="button" data-view-mode="grid" aria-label="Icon view">
                        <span class="view-grid-icon"></span>
                      </button>
                    </div>
                  </div>
                </div>
              </div>

              <table class="files-table" id="filesTable">
                <thead>
                  <tr>
                    <th>Nazwa</th>
                    <th>Rozmiar</th>
                    <th>Data</th>
                  </tr>
                </thead>
                <tbody id="fileRows">
                  {{rows}}
                </tbody>
              </table>
              <div class="files-grid" id="filesGrid">
                {{cards}}
              </div>
            </section>
          </main>
        </section>
        <div class="context-menu" id="contextMenu" hidden>
          {{contextMenuItems}}
        </div>
        <div class="ui-confirm" id="uiConfirm" hidden>
          <div class="ui-confirm-panel" role="dialog" aria-modal="true" aria-labelledby="uiConfirmText">
            <p id="uiConfirmText"></p>
            <div class="ui-confirm-actions">
              <button type="button" id="uiConfirmCancel">Cancel</button>
              <button type="button" class="primary" id="uiConfirmOk">OK</button>
            </div>
          </div>
        </div>
        <div class="ui-confirm" id="uiPrompt" hidden>
          <div class="ui-confirm-panel" role="dialog" aria-modal="true" aria-labelledby="uiPromptText">
            <p id="uiPromptText"></p>
            <input id="uiPromptInput" type="text" autocomplete="off">
            <div class="ui-confirm-actions">
              <button type="button" id="uiPromptCancel">Cancel</button>
              <button type="button" class="primary" id="uiPromptOk">OK</button>
            </div>
          </div>
        </div>
        <div class="ui-confirm" id="uiInfo" hidden>
          <div class="ui-confirm-panel file-info-panel" role="dialog" aria-modal="true" aria-labelledby="uiInfoTitle">
            <p id="uiInfoTitle">Informacje o pliku</p>
            <dl class="file-info-list" id="uiInfoList"></dl>
            <div class="ui-confirm-actions">
              <button type="button" class="primary" id="uiInfoOk">OK</button>
            </div>
          </div>
        </div>
        <script>
          const form = document.getElementById('uploadForm');
          const input = document.getElementById('fileInput');
          const folderInput = document.getElementById('folderInput');
          const currentPath = document.getElementById('currentPath');
          const bar = document.getElementById('progressBar');
          const info = document.getElementById('uploadInfo');
          const driveToggle = document.getElementById('driveToggle');
          const largestFiles = document.getElementById('largestFiles');
          const pathBar = document.querySelector('.path-bar');
          const pathEditor = document.getElementById('pathEditor');
          const pathEditorInput = document.getElementById('pathEditorInput');
          const pathBreadcrumbs = document.getElementById('pathBreadcrumbs');
          const pathCrumbs = Array.from(document.querySelectorAll('.path-crumb'));
          const workspace = document.querySelector('.workspace');
          const rows = Array.from(document.querySelectorAll('#fileRows tr[data-file-name]'));
          const filesPanel = document.getElementById('filesPanel');
          const trashMode = filesPanel.dataset.view === 'trash';
          const contextMenu = document.getElementById('contextMenu');
          const viewButtons = Array.from(document.querySelectorAll('.view-button'));
          const fileCount = document.getElementById('fileCount');
          const fileSearchToggle = document.getElementById('fileSearchToggle');
          const fileSearchBox = document.getElementById('fileSearchBox');
          const fileSearchInput = document.getElementById('fileSearchInput');
          const sortSelect = document.getElementById('sortSelect');
          const emptyTrashButton = document.getElementById('emptyTrash');
          const uiConfirm = document.getElementById('uiConfirm');
          const uiConfirmText = document.getElementById('uiConfirmText');
          const uiConfirmCancel = document.getElementById('uiConfirmCancel');
          const uiConfirmOk = document.getElementById('uiConfirmOk');
          const uiPrompt = document.getElementById('uiPrompt');
          const uiPromptText = document.getElementById('uiPromptText');
          const uiPromptInput = document.getElementById('uiPromptInput');
          const uiPromptCancel = document.getElementById('uiPromptCancel');
          const uiPromptOk = document.getElementById('uiPromptOk');
          const uiInfo = document.getElementById('uiInfo');
          const uiInfoList = document.getElementById('uiInfoList');
          const uiInfoOk = document.getElementById('uiInfoOk');
          const fileRows = document.getElementById('fileRows');
          const filesTable = document.getElementById('filesTable');
          const filesGrid = document.getElementById('filesGrid');
          const cards = Array.from(document.querySelectorAll('.file-card'));
          const rowsByPath = new Map(rows.map((row) => [row.dataset.filePath || '', row]));
          const cardsByPath = new Map(cards.map((card) => [card.dataset.filePath || '', card]));
          const contextMenuInfo = contextMenu.querySelector('[data-action="file-info"]');
          const contextMenuMoveHere = contextMenu.querySelector('[data-action="move-selected-here"]');
          const moveDragType = 'application/x-skyvault-move-paths';
          let currentSort = 'modified';
          let sortDirection = 'desc';
          let autoUploadAfterPick = false;
          let dragDepth = 0;
          let selectionAnchor = null;
          let boxSelecting = false;
          let boxStartX = 0;
          let boxStartY = 0;
          let boxBaseSelection = new Set();
          const selectionBox = document.createElement('div');
          let fileViewMode = localStorage.getItem('skyvaultFileView') || 'list';
          let confirmResolver = null;
          let promptResolver = null;
          let contextMenuTargetPath = '';
          let contextMenuTargetKind = '';
          let searchQuery = '';
          const selectedPaths = new Set();

          sortRows();
          applyFileFilter();
          setFileView(fileViewMode);
          selectionBox.className = 'selection-box';
          selectionBox.hidden = true;
          document.body.appendChild(selectionBox);
          hidePathEditor();

          driveToggle.addEventListener('click', () => {
            const expanded = driveToggle.getAttribute('aria-expanded') === 'true';
            driveToggle.setAttribute('aria-expanded', String(!expanded));
            largestFiles.hidden = expanded;
          });

          pathBar.addEventListener('click', (event) => {
            if (event.target.closest('a, button, input')) {
              return;
            }

            showPathEditor();
          });

          pathBar.addEventListener('dragover', (event) => {
            if (!hasMoveDrag(event.dataTransfer)) {
              return;
            }

            event.preventDefault();
            event.dataTransfer.dropEffect = 'move';
            pathBar.classList.add('is-drop-target');
          });

          pathBar.addEventListener('dragleave', (event) => {
            if (!hasMoveDrag(event.dataTransfer)) {
              return;
            }

            pathBar.classList.remove('is-drop-target');
          });

          pathBar.addEventListener('drop', async (event) => {
            if (!hasMoveDrag(event.dataTransfer)) {
              return;
            }

            event.preventDefault();
            pathBar.classList.remove('is-drop-target');
            await moveDraggedFiles(getDraggedMovePaths(event.dataTransfer), currentPath.value);
          });

          pathCrumbs.forEach((crumb) => {
            crumb.addEventListener('dragover', (event) => {
              if (!hasMoveDrag(event.dataTransfer)) {
                return;
              }

              event.preventDefault();
              event.dataTransfer.dropEffect = 'move';
              crumb.classList.add('is-drop-target');
            });

            crumb.addEventListener('dragleave', (event) => {
              if (!hasMoveDrag(event.dataTransfer)) {
                return;
              }

              crumb.classList.remove('is-drop-target');
            });

            crumb.addEventListener('drop', async (event) => {
              if (!hasMoveDrag(event.dataTransfer)) {
                return;
              }

              event.preventDefault();
              crumb.classList.remove('is-drop-target');
              await moveDraggedFiles(getDraggedMovePaths(event.dataTransfer), crumb.dataset.targetPath || '');
            });
          });

          pathEditor.addEventListener('submit', (event) => {
            event.preventDefault();
            window.location.href = pathToUrl(pathEditorInput.value);
          });

          pathEditor.querySelector('[data-editor-action="clear"]').addEventListener('click', () => {
            pathEditorInput.value = '';
            pathEditorInput.focus();
          });

          pathEditor.querySelector('[data-editor-action="menu"]').addEventListener('click', () => {
            pathEditorInput.focus();
          });

          pathEditorInput.addEventListener('keydown', (event) => {
            if (event.key === 'Escape') {
              event.preventDefault();
              hidePathEditor();
            }
          });

          input.addEventListener('change', () => {
            info.textContent = describeFiles(input.files);

            if (autoUploadAfterPick && input.files.length) {
              autoUploadAfterPick = false;
              uploadFileItems(fileItemsFromList(input.files), {
                source: 'picker',
                items: input.files.length,
                files: input.files.length,
                collected: input.files.length
              });
              return;
            }

            autoUploadAfterPick = false;
          });

          folderInput.addEventListener('change', () => {
            if (folderInput.files.length) {
              uploadFileItems(fileItemsFromList(folderInput.files), {
                source: 'folder-picker',
                items: folderInput.files.length,
                files: folderInput.files.length,
                collected: folderInput.files.length
              });
            }
          });

          form.addEventListener('submit', async (event) => {
            event.preventDefault();

            if (!input.files.length) {
              info.textContent = 'Choose a file first.';
              return;
            }

            await uploadFileItems(fileItemsFromList(input.files), {
              source: 'picker',
              items: input.files.length,
              files: input.files.length,
              collected: input.files.length
            });
          });

          sortSelect.addEventListener('change', () => {
            const [sort, direction] = sortSelect.value.split('-');
            currentSort = sort;
            sortDirection = direction;
            sortRows();
          });

          fileSearchToggle.addEventListener('click', () => {
            const open = fileSearchBox.hidden;
            fileSearchBox.hidden = !open;
            fileSearchToggle.setAttribute('aria-expanded', String(open));

            if (open) {
              fileSearchInput.focus();
              fileSearchInput.select();
              return;
            }

            fileSearchInput.value = '';
            searchQuery = '';
            applyFileFilter();
          });

          fileSearchInput.addEventListener('input', () => {
            searchQuery = fileSearchInput.value.trim().toLowerCase();
            applyFileFilter();
          });

          fileSearchInput.addEventListener('keydown', (event) => {
            if (event.key === 'Escape') {
              event.preventDefault();
              fileSearchInput.value = '';
              searchQuery = '';
              applyFileFilter();
              fileSearchBox.hidden = true;
              fileSearchToggle.setAttribute('aria-expanded', 'false');
              fileSearchToggle.focus();
            }
          });

          viewButtons.forEach((button) => {
            button.addEventListener('click', () => {
              setFileView(button.dataset.viewMode);
            });
          });

          if (emptyTrashButton) {
            emptyTrashButton.addEventListener('click', async () => {
              await emptyTrash();
            });
          }

          uiConfirmCancel.addEventListener('click', () => {
            resolveConfirm(false);
          });

          uiConfirmOk.addEventListener('click', () => {
            resolveConfirm(true);
          });

          uiConfirm.addEventListener('click', (event) => {
            if (event.target === uiConfirm) {
              resolveConfirm(false);
            }
          });

          uiPromptCancel.addEventListener('click', () => {
            resolvePrompt(null);
          });

          uiPromptOk.addEventListener('click', () => {
            resolvePrompt(uiPromptInput.value);
          });

          uiPrompt.addEventListener('click', (event) => {
            if (event.target === uiPrompt) {
              resolvePrompt(null);
            }
          });

          uiPromptInput.addEventListener('keydown', (event) => {
            if (event.key === 'Enter') {
              event.preventDefault();
              resolvePrompt(uiPromptInput.value);
            }

            if (event.key === 'Escape') {
              event.preventDefault();
              resolvePrompt(null);
            }
          });

          uiInfoOk.addEventListener('click', () => {
            uiInfo.hidden = true;
          });

          uiInfo.addEventListener('click', (event) => {
            if (event.target === uiInfo) {
              uiInfo.hidden = true;
            }
          });

          rows.forEach((row) => {
            row.addEventListener('click', (event) => {
              if (event.button !== 0 || event.target.closest('a, input, button')) {
                return;
              }

              applyRowSelection(row, event);
            });

            row.addEventListener('dragstart', (event) => {
              beginMoveDrag(event, row.dataset.filePath || '', row.dataset.entryKind || 'file');
            });

            row.addEventListener('dragend', () => {
              clearDropTargets();
            });

            setupFolderDropTarget(row, row.dataset.filePath || '', row.dataset.entryKind || '');
          });

          cards.forEach((card) => {
            const row = getRowForCard(card);

            card.addEventListener('dragstart', (event) => {
              beginMoveDrag(event, card.dataset.filePath || '', card.dataset.entryKind || 'file');
            });

            card.addEventListener('dragend', () => {
              clearDropTargets();
            });

            card.addEventListener('click', (event) => {
              if (event.button !== 0 || event.target.closest('a, input, button')) {
                return;
              }

              if (!row) {
                return;
              }

              applyRowSelection(row, event);
            });

            setupFolderDropTarget(card, card.dataset.filePath || '', card.dataset.entryKind || '');
          });

          workspace.addEventListener('mousedown', (event) => {
            if (!canStartBoxSelection(event)) {
              return;
            }

            event.preventDefault();
            hideContextMenu();
            boxSelecting = true;
            boxStartX = event.clientX;
            boxStartY = event.clientY;
            boxBaseSelection = event.ctrlKey || event.metaKey ? new Set(selectedPaths) : new Set();

            if (!event.ctrlKey && !event.metaKey) {
              clearSelection();
            }

            updateSelectionBox(event.clientX, event.clientY);
          });

          document.addEventListener('mousemove', (event) => {
            if (!boxSelecting) {
              return;
            }

            event.preventDefault();
            updateSelectionBox(event.clientX, event.clientY);
            applyBoxSelection(getSelectionBoxRect());
          });

          document.addEventListener('mouseup', () => {
            if (!boxSelecting) {
              return;
            }

            boxSelecting = false;
            selectionBox.hidden = true;
            updateSelectedFiles();
          });

          rows.forEach((row) => {
            row.addEventListener('contextmenu', (event) => {
              event.preventDefault();

              if (event.ctrlKey || event.metaKey || event.shiftKey) {
                applyRowSelection(row, event);
              } else if (!selectedPaths.has(row.dataset.filePath || '')) {
                clearSelection();
                setRowSelected(row, true);
                selectionAnchor = row;
                updateSelectedFiles();
              }

              showContextMenu(event.clientX, event.clientY, row.dataset.filePath || '', row.dataset.entryKind || '');
            });
          });

          cards.forEach((card) => {
            card.addEventListener('contextmenu', (event) => {
              event.preventDefault();
              const row = getRowForCard(card);

              if (row && (event.ctrlKey || event.metaKey || event.shiftKey)) {
                applyRowSelection(row, event);
              } else if (row && !selectedPaths.has(row.dataset.filePath || '')) {
                clearSelection();
                setRowSelected(row, true);
                selectionAnchor = row;
                updateSelectedFiles();
              }

              showContextMenu(event.clientX, event.clientY, card.dataset.filePath || '', card.dataset.entryKind || '');
            });
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

            const transferItems = Array.from(event.dataTransfer.items || []);
            const transferFiles = Array.from(event.dataTransfer.files || []);
            const droppedFiles = await getDroppedFileItems(event.dataTransfer);
            info.textContent = 'Drop debug: items=' + transferItems.length + ', files=' + transferFiles.length + ', collected=' + droppedFiles.length + '.';
            await uploadFileItems(droppedFiles, {
              source: 'drop',
              items: transferItems.length,
              files: transferFiles.length,
              collected: droppedFiles.length
            });
          });

          filesPanel.addEventListener('contextmenu', (event) => {
            if (event.target.closest('tr[data-file-name], .file-card')) {
              return;
            }

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
              uiInfo.hidden = true;
            }
          });

          contextMenu.addEventListener('click', async (event) => {
            const button = event.target.closest('button[data-action]');

            if (!button) {
              return;
            }

            const action = button.dataset.action;
            const targetPath = contextMenuTargetPath;
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
              return;
            }

            if (action === 'download-zip') {
              downloadSelectedZip();
              return;
            }

            if (action === 'file-info') {
              showFileInfo(targetPath);
              return;
            }

            if (action === 'move-selected-here') {
              await moveSelectedToFolder(targetPath);
              return;
            }

            if (action === 'move-trash') {
              await moveSelectedToTrash();
              return;
            }

            if (action === 'restore-trash') {
              await restoreSelectedFromTrash();
              return;
            }

            if (action === 'delete-forever') {
              await deleteSelectedForever();
            }
          });

          async function uploadFileItems(items, diagnostics = {}) {
            const uploadItems = items.filter((item) => item.file);

            if (!uploadItems.length) {
              info.textContent = 'No files to upload.';
              return;
            }

            const data = new FormData();
            data.append('currentPath', currentPath.value);
            data.append('uploadSource', diagnostics.source || 'unknown');
            data.append('clientItemCount', String(diagnostics.items ?? uploadItems.length));
            data.append('clientFileCount', String(diagnostics.files ?? uploadItems.length));
            data.append('clientCollectedCount', String(diagnostics.collected ?? uploadItems.length));

            for (const item of uploadItems) {
              data.append('file', item.file, item.path || item.file.name);
            }

            try {
              const response = await uploadFiles(data, uploadItems);

              input.value = '';
              folderInput.value = '';
              document.open();
              document.write(response);
              document.close();
            } catch {
              info.textContent = 'Upload failed.';
            }
          }

          function uploadFiles(data, items) {
            return new Promise((resolve, reject) => {
              const startedAt = Date.now();
              const request = new XMLHttpRequest();
              const label = items.length === 1 ? items[0].path : items.length + ' files';

              request.upload.addEventListener('progress', (event) => {
                if (!event.lengthComputable) {
                  info.textContent = 'Uploading ' + label + '...';
                  return;
                }

                const percent = Math.round((event.loaded / event.total) * 100);
                const seconds = Math.max((Date.now() - startedAt) / 1000, 0.1);
                const speed = event.loaded / seconds;
                bar.style.width = percent + '%';
                info.textContent = percent + '% - ' + formatBytes(speed) + '/s';
              });

              request.addEventListener('load', () => {
                if (request.status >= 200 && request.status < 300) {
                  resolve(request.responseText);
                  return;
                }

                reject();
              });

              request.addEventListener('error', reject);
              request.open('POST', '/files/upload');
              bar.style.width = '0%';
              info.textContent = 'Starting ' + label + '...';
              request.send(data);
            });
          }

          function showPathEditor() {
            pathEditor.hidden = false;
            pathBreadcrumbs.hidden = true;
            pathEditorInput.focus();
            pathEditorInput.select();
          }

          function hidePathEditor() {
            pathEditor.hidden = true;
            pathBreadcrumbs.hidden = false;
          }

          function pathToUrl(value) {
            const cleaned = value.trim()
              .replaceAll('\\\\', '/')
              .replace(/^\/?home\/?/, '')
              .replace(/^\/+|\/+$/g, '');

            return cleaned ? '/?path=' + encodeURIComponent(cleaned) : '/';
          }

          function fileItemsFromList(files) {
            return Array.from(files).map((file) => ({
              file,
              path: file.webkitRelativePath || file.name
            }));
          }

          async function getDroppedFileItems(dataTransfer) {
            const transferItems = Array.from(dataTransfer.items || []);
            const transferFiles = Array.from(dataTransfer.files || []);
            const collected = [];
            const seen = new Set();

            const addCollected = (file, path) => {
              if (!file) {
                return;
              }

              const normalizedPath = (path || file.webkitRelativePath || file.name || '').trim();
              const key = normalizedPath + '|' + file.size + '|' + file.lastModified;

              if (seen.has(key)) {
                return;
              }

              seen.add(key);
              collected.push({ file, path: normalizedPath || file.name });
            };

            if (transferItems.length && transferItems.some((item) => item.webkitGetAsEntry)) {
              for (const item of transferItems) {
                if (item.kind !== 'file') {
                  continue;
                }

                const entry = item.webkitGetAsEntry ? item.webkitGetAsEntry() : null;

                if (entry) {
                  await collectEntry(entry, '', addCollected);
                }
              }
            }

            for (const file of transferFiles) {
              addCollected(file, file.webkitRelativePath || file.name);
            }

            return collected.length ? collected : fileItemsFromList(transferFiles);
          }

          function collectEntry(entry, prefix, addCollected) {
            return new Promise((resolve) => {
              if (entry.isFile) {
                entry.file((file) => {
                  addCollected(file, prefix + file.name);
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
                    await collectEntry(child, prefix + entry.name + '/', addCollected);
                  }

                  readBatch();
                }, resolve);
              };

              readBatch();
            });
          }

          async function createRemoteItem(endpoint, fieldName, promptText) {
            const rawName = await askText(promptText);

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
            data.set('currentPath', currentPath.value);
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

          function showContextMenu(x, y, targetPath = '', targetKind = '') {
            contextMenuTargetPath = targetPath;
            contextMenuTargetKind = targetKind;
            if (contextMenuInfo) {
              contextMenuInfo.hidden = !targetPath;
            }
            if (contextMenuMoveHere) {
              contextMenuMoveHere.hidden = targetKind !== 'folder' || !targetPath || selectedPaths.size === 0;
            }

            contextMenu.hidden = false;
            const rect = contextMenu.getBoundingClientRect();
            const left = Math.min(x, window.innerWidth - rect.width - 10);
            const top = Math.min(y, window.innerHeight - rect.height - 10);
            contextMenu.style.left = Math.max(10, left) + 'px';
            contextMenu.style.top = Math.max(10, top) + 'px';
          }

          function hideContextMenu() {
            contextMenu.hidden = true;
            contextMenuTargetPath = '';
            contextMenuTargetKind = '';
            if (contextMenuInfo) {
              contextMenuInfo.hidden = true;
            }
            if (contextMenuMoveHere) {
              contextMenuMoveHere.hidden = true;
            }
          }

          function showFileInfo(path) {
            const row = rowsByPath.get(path);

            if (!row) {
              return;
            }

            const kind = row.dataset.entryKind === 'folder' ? 'Folder' : 'Plik';
            const size = formatBytes(Number(row.dataset.sortSize || '0'));
            const modified = new Date(Number(row.dataset.sortModified || '0') * 1000).toLocaleString();
            const values = [
              ['Nazwa', path.split('/').pop() || path],
              ['Ścieżka', path],
              ['Typ', kind],
              ['Rozmiar', kind === 'Folder' ? 'Folder' : size],
              ['Modyfikacja', modified]
            ];

            uiInfoList.replaceChildren();
            values.forEach(([label, value]) => {
              const term = document.createElement('dt');
              term.textContent = label;
              const description = document.createElement('dd');
              description.textContent = value;
              description.title = value;
              uiInfoList.append(term, description);
            });

            uiInfo.hidden = false;
            uiInfoOk.focus();
          }

          function updateSelectedFiles() {
            if (selectedPaths.size > 0) {
              info.textContent = selectedPaths.size + ' item(s) selected.';
            }

            return selectedPaths.size;
          }

          function updateSelectionBox(currentX, currentY) {
            const left = Math.min(boxStartX, currentX);
            const top = Math.min(boxStartY, currentY);
            const width = Math.abs(currentX - boxStartX);
            const height = Math.abs(currentY - boxStartY);

            selectionBox.hidden = false;
            selectionBox.style.left = left + 'px';
            selectionBox.style.top = top + 'px';
            selectionBox.style.width = width + 'px';
            selectionBox.style.height = height + 'px';
          }

          function getSelectionBoxRect() {
            return selectionBox.getBoundingClientRect();
          }

          function canStartBoxSelection(event) {
            if (event.button !== 0) {
              return false;
            }

            if (event.target.closest('tr[data-file-name], .file-card, a, input, button, .workspace-top, .location-row, .alert-slot, .context-menu, .ui-confirm')) {
              return false;
            }

            const workspaceRect = workspace.getBoundingClientRect();

            return event.clientX >= workspaceRect.left
              && event.clientX <= workspaceRect.right
              && event.clientY >= workspaceRect.top
              && event.clientY <= workspaceRect.bottom;
          }

          function applyBoxSelection(selectionRect) {
            const targets = fileViewMode === 'grid'
              ? cards.map((card) => ({ row: getRowForCard(card), element: card }))
              : rows.map((row) => ({ row, element: row }));

            targets.forEach(({ row, element }) => {
              if (!row || row.hidden || !row.dataset.filePath) {
                return;
              }

              const path = row.dataset.filePath || '';
              const selected = boxBaseSelection.has(path) || rectanglesIntersect(selectionRect, element.getBoundingClientRect());
              setRowSelected(row, selected);
            });
          }

          function rectanglesIntersect(left, right) {
            return left.left < right.right
              && left.right > right.left
              && left.top < right.bottom
              && left.bottom > right.top;
          }

          function getRowForCard(card) {
            if (!card) {
              return null;
            }

            return rowsByPath.get(card.dataset.filePath || '') || null;
          }

          function getCardForRow(row) {
            if (!row) {
              return null;
            }

            return cardsByPath.get(row.dataset.filePath || '') || null;
          }

          function applyRowSelection(row, event) {
            const path = row.dataset.filePath || '';

            if (!path) {
              return;
            }

            if (event.shiftKey && selectionAnchor) {
              selectRange(selectionAnchor, row);
              updateSelectedFiles();
              return;
            }

            if (!event.ctrlKey && !event.metaKey) {
              clearSelection();
            }

            setRowSelected(row, event.ctrlKey || event.metaKey ? !selectedPaths.has(path) : true);
            selectionAnchor = row;
            updateSelectedFiles();
          }

          function selectRange(fromRow, toRow) {
            const visibleRows = rows.filter((row) => !row.hidden && row.dataset.filePath);
            const from = visibleRows.indexOf(fromRow);
            const to = visibleRows.indexOf(toRow);

            if (from < 0 || to < 0) {
              return;
            }

            clearSelection();
            const start = Math.min(from, to);
            const end = Math.max(from, to);

            for (let index = start; index <= end; index += 1) {
              setRowSelected(visibleRows[index], true);
            }
          }

          function clearSelection() {
            [...selectedPaths].forEach((path) => setRowSelected(rowsByPath.get(path) || null, false));
          }

          function updateSelectionStyles() {
            rows.forEach((row) => {
              const selected = selectedPaths.has(row.dataset.filePath || '');
              row.classList.toggle('is-selected', selected);

              const card = getCardForRow(row);
              if (card) {
                card.classList.toggle('is-selected', selected);
              }
            });
          }

          function setRowSelected(row, selected) {
            if (!row) {
              return;
            }

            const path = row.dataset.filePath || '';

            if (!path) {
              return;
            }

            if (selected) {
              selectedPaths.add(path);
            } else {
              selectedPaths.delete(path);
            }

            row.classList.toggle('is-selected', selected);

            const card = getCardForRow(row);

            if (card) {
              card.classList.toggle('is-selected', selected);
            }
          }

          function downloadSelectedZip() {
            const paths = getSelectedPaths();

            if (!paths.length) {
              info.textContent = 'Select files first.';
              return;
            }

            const zipForm = document.createElement('form');
            zipForm.method = 'post';
            zipForm.action = '/files/download-zip';
            const field = document.createElement('input');
            field.type = 'hidden';
            field.name = 'paths';
            field.value = paths.join('\n');
            zipForm.appendChild(field);
            document.body.appendChild(zipForm);
            zipForm.submit();
            zipForm.remove();
          }

          async function moveSelectedToTrash() {
            const paths = getSelectedPaths();

            if (!paths.length) {
              info.textContent = 'Select files first.';
              return;
            }

            if (!await askConfirm('Move selected file(s) to trash?')) {
              return;
            }

            const data = new URLSearchParams();
            data.set('paths', paths.join('\n'));
            data.set('currentPath', currentPath.value);

            try {
              const response = await fetch('/files/trash', {
                method: 'POST',
                headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                body: data.toString()
              });
              const html = await response.text();
              document.open();
              document.write(html);
              document.close();
            } catch {
              info.textContent = 'Could not move files to trash.';
            }
          }

          async function moveSelectedToFolder(destinationPath) {
            const paths = getSelectedPaths();

            if (!paths.length) {
              info.textContent = 'Select files first.';
              return;
            }

            if (!destinationPath) {
              info.textContent = 'Choose a destination folder.';
              return;
            }

            if (!await askConfirm('Move selected file(s) to this folder?')) {
              return;
            }

            await moveDraggedFiles(paths, destinationPath);
          }

          async function emptyTrash() {
            if (!await askConfirm('Empty the entire trash? This cannot be undone.')) {
              return;
            }

            try {
              const data = new URLSearchParams();
              data.set('currentPath', currentPath.value);

              const response = await fetch('/files/trash/empty', {
                method: 'POST',
                headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                body: data.toString()
              });
              const html = await response.text();
              document.open();
              document.write(html);
              document.close();
            } catch {
              info.textContent = 'Could not empty trash.';
            }
          }

          async function restoreSelectedFromTrash() {
            const paths = getSelectedPaths();

            if (!paths.length) {
              info.textContent = 'Select files first.';
              return;
            }

            const uniquePaths = [...new Set(paths)];

            if (!await askConfirm('Restore selected file(s) from trash?')) {
              return;
            }

            try {
              const result = await submitTrashActionRequest('/files/trash/restore', uniquePaths);

              if (result.conflict) {
                if (!await askConfirm('That destination already exists. Replace selected item(s)?')) {
                  document.open();
                  document.write(result.html);
                  document.close();
                  return;
                }

                const replaced = await submitTrashActionRequest('/files/trash/restore', uniquePaths, { overwriteExisting: 'true' });
                document.open();
                document.write(replaced.html);
                document.close();
                return;
              }

              document.open();
              document.write(result.html);
              document.close();
            } catch {
              info.textContent = 'Could not restore files from trash.';
            }
          }

          async function deleteSelectedForever() {
            const paths = getSelectedPaths();

            if (!paths.length) {
              info.textContent = 'Select files first.';
              return;
            }

            const uniquePaths = [...new Set(paths)];

            if (!await askConfirm('Delete selected file(s) forever? This cannot be undone.')) {
              return;
            }

            try {
              const result = await submitTrashActionRequest('/files/trash/delete', uniquePaths);
              document.open();
              document.write(result.html);
              document.close();
            } catch {
              info.textContent = 'Could not delete files forever.';
            }
          }

          function getSelectedPaths() {
            return [...selectedPaths];
          }

          function hasMoveDrag(dataTransfer) {
            return Boolean(dataTransfer && Array.from(dataTransfer.types || []).includes(moveDragType));
          }

          function getDraggedMovePaths(dataTransfer) {
            const payload = dataTransfer?.getData(moveDragType) || dataTransfer?.getData('text/plain') || '';

            if (payload.trim()) {
              return payload.split('\n').map((path) => path.trim()).filter(Boolean);
            }

            return [];
          }

          function clearDropTargets() {
            pathBar.classList.remove('is-drop-target');
            pathCrumbs.forEach((crumb) => crumb.classList.remove('is-drop-target'));
            document.querySelectorAll('.is-folder-drop-target').forEach((target) => {
              target.classList.remove('is-folder-drop-target');
            });
          }

          function setupFolderDropTarget(element, targetPath, targetKind) {
            if (targetKind !== 'folder' || !targetPath) {
              return;
            }

            element.addEventListener('dragover', (event) => {
              if (!hasMoveDrag(event.dataTransfer)) {
                return;
              }

              const draggedPaths = getDraggedMovePaths(event.dataTransfer);

              if (draggedPaths.includes(targetPath)) {
                return;
              }

              event.preventDefault();
              event.stopPropagation();
              event.dataTransfer.dropEffect = 'move';
              clearDropTargets();
              element.classList.add('is-folder-drop-target');
            });

            element.addEventListener('dragleave', () => {
              element.classList.remove('is-folder-drop-target');
            });

            element.addEventListener('drop', async (event) => {
              if (!hasMoveDrag(event.dataTransfer)) {
                return;
              }

              const draggedPaths = getDraggedMovePaths(event.dataTransfer);

              if (!draggedPaths.length || draggedPaths.includes(targetPath)) {
                return;
              }

              event.preventDefault();
              event.stopPropagation();
              clearDropTargets();
              await moveDraggedFiles(draggedPaths, targetPath);
            });
          }

          function beginMoveDrag(event, sourcePath, sourceKind) {
            if (!event.dataTransfer || !sourcePath) {
              return;
            }

            const selectedPaths = getSelectedPaths();

            if (!selectedPaths.includes(sourcePath)) {
              event.preventDefault();
              return;
            }

            const draggedPaths = selectedPaths.length > 1 && selectedPaths.includes(sourcePath)
              ? selectedPaths
              : [sourcePath];
            const preview = createMoveDragPreview(draggedPaths, sourceKind);

            event.dataTransfer.effectAllowed = 'move';
            event.dataTransfer.setData(moveDragType, draggedPaths.join('\n'));
            event.dataTransfer.setData('text/plain', draggedPaths.join('\n'));
            event.dataTransfer.setDragImage(preview, 18, 18);
            window.setTimeout(() => preview.remove(), 0);
          }

          function createMoveDragPreview(paths, sourceKind) {
            const preview = document.createElement('div');
            preview.className = 'move-drag-preview';

            const stack = document.createElement('div');
            stack.className = 'move-drag-stack';

            paths.slice(0, 3).forEach((path, index) => {
              const card = document.createElement('div');
              card.className = 'move-drag-card';
              card.style.zIndex = String(10 - index);

              const icon = document.createElement(sourceKind === 'folder' ? 'img' : 'span');
              icon.className = sourceKind === 'folder' ? 'folder-icon-img move-drag-folder-icon' : 'file-icon';
              if (sourceKind === 'folder') {
                icon.src = '/assets/icons/folder.svg';
                icon.alt = '';
              }
              card.appendChild(icon);
              stack.appendChild(card);
            });

            preview.appendChild(stack);

            if (paths.length > 3) {
              const count = document.createElement('span');
              count.className = 'move-drag-count';
              count.textContent = '+' + (paths.length - 3);
              preview.appendChild(count);
            }

            document.body.appendChild(preview);
            return preview;
          }

          async function moveDraggedFiles(paths, destinationPath) {
            const uniquePaths = [...new Set(paths.filter(Boolean))];

            if (!uniquePaths.length) {
              info.textContent = 'Select files first.';
              return;
            }

            try {
              const result = await submitMoveRequest(uniquePaths, destinationPath || '');

              if (result.conflict) {
                if (!await askConfirm('That destination already exists. Replace selected item(s)?')) {
                  document.open();
                  document.write(result.html);
                  document.close();
                  return;
                }

                const replaced = await submitMoveRequest(uniquePaths, destinationPath || '', { overwriteExisting: 'true' });
                document.open();
                document.write(replaced.html);
                document.close();
                return;
              }

              document.open();
              document.write(result.html);
              document.close();
            } catch {
              info.textContent = 'Could not move files.';
            }
          }

          async function submitMoveRequest(paths, destinationPath, extraFields = {}) {
            const data = new URLSearchParams();
            data.set('paths', paths.join('\n'));
            data.set('destinationPath', destinationPath);
            data.set('currentPath', currentPath.value);

            Object.entries(extraFields).forEach(([key, value]) => {
              data.set(key, value);
            });

            info.textContent = 'Moving...';

            const response = await fetch('/files/move', {
              method: 'POST',
              headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
              body: data.toString()
            });

            return {
              html: await response.text(),
              conflict: response.headers.get('X-Conflict') === 'true'
            };
          }

          async function submitTrashActionRequest(endpoint, paths, extraFields = {}) {
            const data = new URLSearchParams();
            data.set('paths', paths.join('\n'));
            data.set('currentPath', currentPath.value);

            Object.entries(extraFields).forEach(([key, value]) => {
              data.set(key, value);
            });

            info.textContent = 'Processing...';

            const response = await fetch(endpoint, {
              method: 'POST',
              headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
              body: data.toString()
            });

            return {
              html: await response.text(),
              conflict: response.headers.get('X-Conflict') === 'true'
            };
          }

          function askConfirm(message) {
            uiConfirmText.textContent = message;
            uiConfirm.hidden = false;
            uiConfirmOk.focus();

            return new Promise((resolve) => {
              confirmResolver = resolve;
            });
          }

          function resolveConfirm(value) {
            if (!confirmResolver) {
              uiConfirm.hidden = true;
              return;
            }

            const resolve = confirmResolver;
            confirmResolver = null;
            uiConfirm.hidden = true;
            resolve(value);
          }

          function askText(message, defaultValue = '') {
            uiPromptText.textContent = message;
            uiPromptInput.value = defaultValue;
            uiPrompt.hidden = false;
            window.setTimeout(() => {
              uiPromptInput.focus();
              uiPromptInput.select();
            }, 0);

            return new Promise((resolve) => {
              promptResolver = resolve;
            });
          }

          function resolvePrompt(value) {
            if (!promptResolver) {
              uiPrompt.hidden = true;
              return;
            }

            const resolve = promptResolver;
            promptResolver = null;
            uiPrompt.hidden = true;
            resolve(value);
          }

          function sortRows() {
            const compareEntries = (left, right) => {
              const leftKind = left.dataset.entryKind === 'folder' ? 0 : 1;
              const rightKind = right.dataset.entryKind === 'folder' ? 0 : 1;

              if (leftKind !== rightKind) {
                return leftKind - rightKind;
              }

              let result = 0;

              if (currentSort === 'name') {
                result = left.dataset.sortName.localeCompare(right.dataset.sortName);
              } else if (currentSort === 'size') {
                result = Number(left.dataset.sortSize) - Number(right.dataset.sortSize);
              } else {
                result = Number(left.dataset.sortModified) - Number(right.dataset.sortModified);
              }

              return sortDirection === 'asc' ? result : -result;
            };

            const sorted = [...rows].sort(compareEntries);
            const sortedCards = [...cards].sort(compareEntries);

            sorted.forEach((row) => fileRows.appendChild(row));
            sortedCards.forEach((card) => filesGrid.appendChild(card));
            syncSortSelect();
          }

          function syncSortSelect() {
            sortSelect.value = currentSort + '-' + sortDirection;
          }

          function applyFileFilter() {
            let visibleCount = 0;

            rows.forEach((row) => {
              const path = row.dataset.filePath || '';
              const name = row.dataset.fileName || '';
              const visible = !searchQuery
                || name.includes(searchQuery)
                || path.toLowerCase().includes(searchQuery);
              row.hidden = !visible;

              const card = cardsByPath.get(path);
              if (card) {
                card.hidden = !visible;
              }

              if (visible) {
                visibleCount += 1;
              } else {
                selectedPaths.delete(path);
              }
            });

            fileCount.textContent = visibleCount + ' element(y)';
            updateSelectionStyles();
          }

          function setFileView(mode) {
            fileViewMode = mode === 'grid' ? 'grid' : 'list';
            filesPanel.classList.toggle('is-grid-view', fileViewMode === 'grid');
            filesTable.style.display = fileViewMode === 'grid' ? 'none' : '';
            filesGrid.style.display = fileViewMode === 'grid' ? 'grid' : 'none';
            localStorage.setItem('skyvaultFileView', fileViewMode);
            viewButtons.forEach((button) => {
              button.classList.toggle('active', button.dataset.viewMode === fileViewMode);
              button.setAttribute('aria-pressed', String(button.dataset.viewMode === fileViewMode));
            });
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

    private static string RenderFileRow(FileEntry file, bool allowOpen = true)
    {
        string displayName = GetDisplayName(file.Name);
        string iconMarkup = file.IsFolder
            ? """<img class="folder-icon-img row-folder-icon" src="/assets/icons/folder.svg" alt="">"""
            : $"""<img class="file-icon-img row-file-icon" src="/assets/icons/{GetFileIconName(file)}" alt="">""";
        string size = file.IsFolder ? "Folder" : FormatBytes(file.SizeBytes);
        string encodedPath = WebUtility.UrlEncode(file.Name);
        string draggableAttr = " draggable=\"true\"";
        string nameContent = allowOpen
            ? file.IsFolder
                ? $"<a href=\"/?path={encodedPath}\" title=\"{Escape(displayName)}\">{Escape(displayName)}</a>"
                : $"<a href=\"/files/open?path={encodedPath}\" title=\"{Escape(displayName)}\">{Escape(displayName)}</a>"
            : $"""<span title="{Escape(displayName)}">{Escape(displayName)}</span>""";
        string uploadedClass = !file.IsFolder && DateTimeOffset.UtcNow - file.ModifiedAt < TimeSpan.FromMinutes(5)
            ? " class=\"fresh-upload\""
            : "";
        long modifiedUnix = file.ModifiedAt.ToUnixTimeSeconds();

        return $$"""
        <tr{{uploadedClass}}{{draggableAttr}} data-file-path="{{Escape(file.Name)}}" data-file-name="{{Escape(file.Name.ToLowerInvariant())}}" data-entry-kind="{{(file.IsFolder ? "folder" : "file")}}" data-sort-name="{{Escape(file.Name.ToLowerInvariant())}}" data-sort-size="{{file.SizeBytes}}" data-sort-modified="{{modifiedUnix}}">
          <td>
            <span class="file-name">
              {{iconMarkup}}
              <span>{{nameContent}}</span>
            </span>
          </td>
          <td>{{size}}</td>
          <td>{{file.ModifiedAt.LocalDateTime:g}}</td>
        </tr>
        """;
    }

    private static string RenderFileCard(FileEntry file, bool allowOpen = true)
    {
        string displayName = GetDisplayName(file.Name);
        string iconMarkup = file.IsFolder
            ? """<img class="folder-icon-img grid-folder-icon" src="/assets/icons/folder.svg" alt="">"""
            : $"""<img class="file-icon-img grid-file-icon" src="/assets/icons/{GetFileIconName(file)}" alt="">""";
        string encodedPath = WebUtility.UrlEncode(file.Name);
        string href = file.IsFolder ? $"/?path={encodedPath}" : $"/files/open?path={encodedPath}";
        string size = file.IsFolder ? "Folder" : FormatBytes(file.SizeBytes);
        string draggableAttr = " draggable=\"true\"";
        string content = allowOpen
            ? $"""<a class="file-card-link" href="{href}" title="{Escape(displayName)}">{Escape(displayName)}</a>"""
            : $"""<span class="file-card-link" title="{Escape(displayName)}">{Escape(displayName)}</span>""";
        long modifiedUnix = file.ModifiedAt.ToUnixTimeSeconds();

        return $$"""
        <div class="file-card"{{draggableAttr}} data-file-path="{{Escape(file.Name)}}" data-file-name="{{Escape(file.Name.ToLowerInvariant())}}" data-entry-kind="{{(file.IsFolder ? "folder" : "file")}}" data-sort-name="{{Escape(file.Name.ToLowerInvariant())}}" data-sort-size="{{file.SizeBytes}}" data-sort-modified="{{modifiedUnix}}">
          <div class="file-card-preview">
            {{iconMarkup}}
          </div>
          <div class="file-card-body">
            {{content}}
            <span class="file-card-meta">{{size}} · {{file.ModifiedAt.LocalDateTime:g}}</span>
          </div>
        </div>
        """;
    }

    private static string RenderFileViewer(string filePath, string rawHref, byte[] content)
    {
        string extension = Path.GetExtension(filePath).ToLowerInvariant();

        if (IsImageExtension(extension))
        {
            return $"""<img class="preview-media preview-image" src="{Escape(rawHref)}" alt="{Escape(GetDisplayName(filePath))}">""";
        }

        if (IsAudioExtension(extension))
        {
            return $"""<audio class="preview-media" src="{Escape(rawHref)}" controls autoplay></audio>""";
        }

        if (IsVideoExtension(extension))
        {
            return $"""<video class="preview-media preview-video" src="{Escape(rawHref)}" controls autoplay></video>""";
        }

        string text = Encoding.UTF8.GetString(content);
        return $"""<pre class="text-preview">{Escape(text)}</pre>""";
    }

    private static string GetFileIconName(FileEntry file)
    {
        if (file.SizeBytes == 0)
        {
            return "application-x-zerosize.svg";
        }

        return Path.GetExtension(file.Name).ToLowerInvariant() switch
        {
            ".txt" or ".log" => "text-plain.svg",
            ".md" or ".markdown" => "text-x-markdown.svg",
            ".json" => "application-json.svg",
            ".xml" => "text-xml.svg",
            ".html" or ".htm" => "text-html.svg",
            ".css" => "text-css.svg",
            ".js" or ".mjs" or ".cjs" => "application-x-javascript.svg",
            ".cs" => "text-x-csharp.svg",
            ".py" => "text-x-python.svg",
            ".sh" or ".bash" or ".zsh" => "application-x-executable.svg",
            ".pdf" => "application-pdf.svg",
            ".zip" => "application-zip.svg",
            ".tar" => "application-x-tar.svg",
            ".gz" or ".tgz" => "application-x-gzip.svg",
            ".rar" => "application-x-rar.svg",
            ".7z" => "application-x-7z-compressed.svg",
            ".jpg" or ".jpeg" => "image-jpeg.svg",
            ".png" => "image-png.svg",
            ".gif" => "image-gif.svg",
            ".bmp" => "image-bmp.svg",
            ".svg" => "image-svg+xml.svg",
            ".webp" => "image-x-generic.svg",
            ".mp3" => "audio-x-mpeg.svg",
            ".wav" => "audio-x-wav.svg",
            ".flac" => "audio-x-flac.svg",
            ".ogg" or ".oga" or ".opus" or ".m4a" or ".aac" => "audio-x-generic.svg",
            ".mp4" or ".m4v" or ".mov" => "video-mp4.svg",
            ".mkv" => "video-x-matroska.svg",
            ".webm" => "video-webm.svg",
            ".ogv" => "video-x-theora+ogg.svg",
            _ => "application-octet-stream.svg",
        };
    }

    private static string RenderLargestFiles(IReadOnlyList<FileEntry> files)
    {
        const long minBytes = 2L * 1024 * 1024;
        List<FileEntry> largestFiles = files
            .Where(file => !file.IsFolder && file.SizeBytes >= minBytes)
            .OrderByDescending(file => file.SizeBytes)
            .ThenBy(file => file.Name)
            .Take(100)
            .ToList();

        if (largestFiles.Count == 0)
        {
            return """<p class="largest-empty">Brak plików od 2 MB.</p>""";
        }

        string items = string.Join("\n", largestFiles.Select(file =>
        {
            string encodedPath = WebUtility.UrlEncode(file.Name);
            return $$"""
            <a class="largest-file" href="/files/open?path={{encodedPath}}">
              <span>{{Escape(GetDisplayName(file.Name))}}</span>
              <strong>{{FormatBytes(file.SizeBytes)}}</strong>
            </a>
            """;
        }));

        return $"""<div class="largest-list">{items}</div>""";
    }

    private static bool IsImageExtension(string extension)
    {
        return extension is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".svg";
    }

    private static bool IsAudioExtension(string extension)
    {
        return extension is ".mp3" or ".wav" or ".ogg" or ".oga" or ".flac" or ".m4a" or ".aac" or ".opus";
    }

    private static bool IsVideoExtension(string extension)
    {
        return extension is ".mp4" or ".webm" or ".ogv" or ".mov" or ".m4v" or ".mkv";
    }

    private static IReadOnlyList<FileEntry> GetDirectoryEntries(IReadOnlyList<FileEntry> files, string currentDirectory)
    {
        currentDirectory = NormalizeCloudPath(currentDirectory);
        string prefix = currentDirectory.Length == 0 ? "" : $"{currentDirectory}/";

        return files
            .Where(file =>
            {
                if (prefix.Length > 0 && !file.Name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return false;
                }

                string remaining = prefix.Length == 0 ? file.Name : file.Name[prefix.Length..];
                return remaining.Length > 0 && !remaining.Contains('/', StringComparison.Ordinal);
            })
            .OrderBy(file => file.IsFolder ? 0 : 1)
            .ThenBy(file => GetDisplayName(file.Name), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

        private static IReadOnlyList<FileEntry> AddTrashFolderIfNeeded(
          IReadOnlyList<FileEntry> entries,
          IReadOnlyList<FileEntry> trashFiles,
          string currentDirectory)
        {
          if (currentDirectory.Length != 0)
          {
            return entries;
          }

          DateTimeOffset modifiedAt = trashFiles.Count > 0
            ? trashFiles.Max(entry => entry.ModifiedAt)
            : DateTimeOffset.UtcNow;

          var list = entries.ToList();
          list.Insert(0, new FileEntry(".trash", 0, modifiedAt, true));
          return list;
        }

    private static IReadOnlyList<FileEntry> GetVisibleFiles(IReadOnlyList<FileEntry> files, IReadOnlyList<FileEntry> trashFiles, string currentDirectory, string currentView)
    {
        currentView = NormalizeView(currentView);

        return currentView switch
        {
            "recent" => files
                .Where(file => !file.IsFolder)
                .OrderByDescending(file => file.ModifiedAt)
                .ThenBy(file => file.Name)
                .Take(20)
                .ToList(),
            "videos" => FilterFiles(files, IsVideoExtension),
            "images" => FilterFiles(files, IsImageExtension),
            "music" => FilterFiles(files, IsAudioExtension),
            "documents" => files
                .Where(file => !file.IsFolder && IsDocumentFile(file.Name))
                .OrderByDescending(file => file.ModifiedAt)
                .ThenBy(file => file.Name)
                .ToList(),
            "trash" => trashFiles
                .OrderByDescending(file => file.ModifiedAt)
                .ThenBy(file => file.Name)
                .ToList(),
            _ => AddTrashFolderIfNeeded(GetDirectoryEntries(files, currentDirectory), trashFiles, currentDirectory)
        };
    }

    private static IReadOnlyList<FileEntry> FilterFiles(IReadOnlyList<FileEntry> files, Func<string, bool> extensionMatcher)
    {
        return files
            .Where(file => !file.IsFolder && extensionMatcher(Path.GetExtension(file.Name).ToLowerInvariant()))
            .OrderByDescending(file => file.ModifiedAt)
            .ThenBy(file => file.Name)
            .ToList();
    }

    private static bool IsDocumentFile(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return !IsImageExtension(extension) && !IsAudioExtension(extension) && !IsVideoExtension(extension);
    }

    private static string GetDisplayName(string path)
    {
        string cleaned = NormalizeCloudPath(path);
        int slashIndex = cleaned.LastIndexOf('/');
        return slashIndex < 0 ? cleaned : cleaned[(slashIndex + 1)..];
    }

    private static string ToVirtualPath(string relativePath, bool isFolder)
    {
        string cleaned = NormalizeCloudPath(relativePath);
        string path = string.IsNullOrWhiteSpace(cleaned) ? "/home" : $"/home/{cleaned}";
        return isFolder && path != "/home" ? $"{path}/" : path;
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
            "recent" or "videos" or "images" or "music" or "documents" or "trash" => view.Trim().ToLowerInvariant(),
            _ => "home"
        };
    }

    private static string ActiveClass(string currentView, string expectedView)
    {
        return NormalizeView(currentView) == expectedView ? "active" : "";
    }

    private static string HomeActiveClass(string currentView, string currentDirectory)
    {
        return NormalizeView(currentView) == "home" && NormalizeCloudPath(currentDirectory).Length == 0 ? "active" : "";
    }

    private static string PathActiveClass(string currentView, string currentDirectory, string expectedPath)
    {
        return NormalizeView(currentView) == "home"
            && string.Equals(NormalizeCloudPath(currentDirectory), NormalizeCloudPath(expectedPath), StringComparison.OrdinalIgnoreCase)
                ? "active"
                : "";
    }

    private static string GetParentDirectory(string currentDirectory)
    {
        currentDirectory = NormalizeCloudPath(currentDirectory);
        int slashIndex = currentDirectory.LastIndexOf('/');
        return slashIndex < 0 ? "" : currentDirectory[..slashIndex];
    }

    private static string RenderPathBreadcrumbs(string currentDirectory)
    {
        string normalized = NormalizeCloudPath(currentDirectory);
        List<string> crumbs = new()
        {
            """<a class="path-crumb" href="/" data-target-path="">home</a>"""
        };

        if (normalized.Length == 0)
        {
            crumbs[0] = """<a class="path-crumb is-current" href="/" data-target-path="" aria-current="page">home</a>""";
            return string.Join("", crumbs);
        }

        string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string path = "";

        for (int index = 0; index < parts.Length; index++)
        {
            string part = parts[index];
            path = path.Length == 0 ? part : $"{path}/{part}";
            string href = $"/?path={WebUtility.UrlEncode(path)}";
            string currentClass = index == parts.Length - 1 ? " is-current" : "";
            string ariaCurrent = index == parts.Length - 1 ? " aria-current=\"page\"" : "";
            crumbs.Add($"""<span class="path-separator" aria-hidden="true">/</span><a class="path-crumb{currentClass}" href="{href}" data-target-path="{Escape(path)}"{ariaCurrent}>{Escape(part)}</a>""");
        }

        return string.Join("", crumbs);
    }

    private static string Layout(string content)
    {
        string buildVersion = Escape(BuildInfo.Version);

        return $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>SkyVault</title>
          <style>
            :root {
              --bg: #000000;
              --surface: #101512;
              --surface-soft: #17221b;
              --line: #25342b;
              --line-strong: #3c5d49;
              --text: #e4eee8;
              --muted: #8fa398;
              --blue: #4b9f68;
              --blue-soft: #16271d;
              --green: #4b9f68;
              --amber: #fbbf24;
              --shadow: 0 18px 38px rgba(0, 0, 0, 0.34);
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

            .build-badge {
              position: fixed;
              left: 10px;
              bottom: 8px;
              z-index: 20;
              padding: 4px 7px;
              border: 1px solid rgba(148, 163, 184, 0.55);
              border-radius: 6px;
              background: rgba(255, 255, 255, 0.82);
              color: #64748b;
              font-size: 11px;
              font-weight: 800;
              letter-spacing: 0;
              backdrop-filter: blur(6px);
              pointer-events: none;
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
              gap: 16px;
              padding: 22px 16px;
              border-right: 1px solid var(--line);
              background: #f8fafc;
            }

            .sidebar .brand-logo {
              width: 168px;
            }

            .upload-panel {
              display: grid;
              gap: 12px;
              margin-top: 10px;
            }

            .compact-upload {
              gap: 8px;
              margin-top: 8px;
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
              gap: 3px;
              margin-top: 4px;
            }

            .places-heading {
              margin: 0 0 4px;
              padding: 0 8px;
              color: #94a3b8;
              font-size: 15px;
              line-height: 1.2;
            }

            .nav-item {
              display: flex;
              align-items: center;
              gap: 10px;
              min-height: 34px;
              padding: 0 8px;
              border-radius: 6px;
              color: #334155;
              font-size: 16px;
              font-weight: 500;
            }

            .nav-item.active {
              background: #e8eef7;
              color: #111827;
            }

            .nav-icon-img {
              display: block;
              width: 22px;
              height: 22px;
              flex: 0 0 auto;
              object-fit: contain;
            }

            .nav-icon,
            .file-icon {
              position: relative;
              display: inline-block;
              flex: 0 0 auto;
            }

            .file-icon-img {
              display: block;
              flex: 0 0 auto;
              object-fit: contain;
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

            .folder-icon-img {
              display: block;
              flex: 0 0 auto;
              object-fit: contain;
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

            .largest-files {
              max-height: 220px;
              margin-bottom: 10px;
              overflow-y: auto;
              border: 1px solid var(--line);
              border-radius: 8px;
              background: #ffffff;
            }

            .largest-title {
              position: sticky;
              top: 0;
              margin: 0;
              padding: 8px 10px;
              border-bottom: 1px solid #edf1f7;
              background: #ffffff;
              color: #64748b;
              font-size: 12px;
              font-weight: 800;
              text-transform: uppercase;
            }

            .largest-list {
              display: grid;
            }

            .largest-file {
              display: grid;
              grid-template-columns: minmax(0, 1fr) auto;
              gap: 8px;
              padding: 8px 10px;
              border-bottom: 1px solid #edf1f7;
              color: #334155;
              font-size: 13px;
              font-weight: 600;
            }

            .largest-file:last-child {
              border-bottom: 0;
            }

            .largest-file span {
              overflow: hidden;
              text-overflow: ellipsis;
              white-space: nowrap;
            }

            .largest-file strong {
              color: #0f766e;
              font-size: 12px;
              white-space: nowrap;
            }

            .largest-empty {
              margin: 0;
              padding: 10px;
              color: #64748b;
              font-size: 13px;
            }

            .storage-summary p {
              margin: 9px 0 0;
              font-size: 13px;
            }

            .storage-summary #uploadInfo {
              margin: 10px 0 0;
              color: #047857;
              font-weight: 700;
              word-break: break-word;
            }

            .drive-row {
              display: grid;
              grid-template-columns: auto minmax(0, 1fr) auto;
              gap: 10px;
              align-items: center;
              color: #334155;
              font-size: 16px;
              font-weight: 500;
            }

            .drive-eject-icon {
              display: block;
              width: 18px;
              height: 18px;
              opacity: 0.85;
            }

            .drive-toggle {
              display: grid;
              place-items: center;
              width: 28px;
              min-height: 28px;
              margin: 0;
              padding: 0;
              border-radius: 6px;
              background: transparent;
            }

            .drive-toggle[aria-expanded="true"] .drive-eject-icon {
              transform: rotate(180deg);
            }

            .workspace {
              display: grid;
              grid-template-rows: auto auto auto minmax(0, 1fr);
              gap: 8px;
              min-width: 0;
              height: 100vh;
              padding: 14px 18px 18px;
            }

            .workspace-top {
              display: grid;
              grid-template-columns: minmax(0, 1fr) auto;
              gap: 18px;
              align-items: center;
            }

            .location-row {
              min-width: 0;
            }

            .alert-slot {
              min-height: 0;
            }

            .alert-slot:empty {
              display: none;
            }

            .path-bar {
              position: relative;
              display: flex;
              align-items: center;
              gap: 7px;
              min-width: 0;
              width: 100%;
              height: 44px;
              padding: 0 8px;
              border: 0;
              border-bottom: 1px solid var(--line);
              background: transparent;
              border-radius: 0;
              color: #1f2937;
            }

            .path-bar.is-drop-target {
              border-color: #60a5fa;
              background: #f8fbff;
              box-shadow: inset 0 -2px 0 #60a5fa;
            }

            .path-up {
              display: none;
              place-items: center;
              width: 30px;
              height: 30px;
              border-radius: 6px;
              color: #475569;
              font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
              font-weight: 900;
              text-decoration: none;
            }

            .path-up:hover {
              background: #e2e8f0;
              color: #0f172a;
            }

            .path-folder-icon {
              display: inline-grid;
              place-items: center;
              flex: 0 0 auto;
              width: 22px;
              height: 22px;
              color: #1f2937;
            }

            .path-folder-icon svg,
            .path-editor-button svg {
              display: block;
              width: 22px;
              height: 22px;
            }

            .path-breadcrumbs {
              display: flex;
              align-items: center;
              gap: 0;
              min-width: 0;
              flex: 1 1 auto;
              overflow: hidden;
            }

            .path-breadcrumbs[hidden] {
              display: none;
            }

            .path-separator {
              flex: 0 0 auto;
              margin: 0 4px;
              color: #94a3b8;
              font-weight: 700;
            }

            .path-crumb {
              display: inline-flex;
              align-items: center;
              min-width: 0;
              max-width: 100%;
              overflow: hidden;
              color: #334155;
              font-size: 18px;
              font-weight: 700;
              text-overflow: ellipsis;
              white-space: nowrap;
            }

            .path-crumb:hover {
              color: var(--blue);
            }

            .path-crumb.is-current {
              color: #111827;
            }

            .path-crumb.is-drop-target {
              border-radius: 6px;
              background: #dbeafe;
              color: var(--blue);
            }

            .path-editor {
              display: grid;
              grid-template-columns: minmax(0, 1fr) auto auto auto;
              gap: 2px;
              align-items: center;
              flex: 1 1 auto;
              min-width: 0;
              margin: 0;
            }

            .path-editor[hidden] {
              display: none;
            }

            .path-editor input {
              height: 42px;
              border: 0;
              padding: 0 3px;
              background: transparent;
              color: #111827;
              font-family: Arial, sans-serif;
              font-size: 20px;
              font-weight: 400;
              letter-spacing: 0;
            }

            .path-editor input:focus {
              outline: 0;
              border-color: transparent;
            }

            .path-editor-button {
              width: 32px;
              min-height: 32px;
              margin: 0;
              padding: 0;
              border-radius: 6px;
              background: transparent;
              color: #475569;
              font-family: Arial, sans-serif;
              line-height: 1;
            }

            .path-editor-button:hover {
              background: #eef2f7;
              color: #0f172a;
            }

            .preview-shell {
              min-height: 100vh;
              display: grid;
              grid-template-rows: auto 1fr;
              gap: 18px;
              padding: 22px;
              background: var(--bg);
            }

            .preview-top {
              display: grid;
              grid-template-columns: auto minmax(0, 1fr) auto;
              gap: 16px;
              align-items: center;
            }

            .preview-title {
              min-width: 0;
            }

            .preview-title .eyebrow {
              margin-bottom: 3px;
              color: var(--muted);
              font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
              font-size: 12px;
              font-weight: 800;
            }

            .preview-title h1 {
              overflow: hidden;
              margin: 0;
              text-overflow: ellipsis;
              white-space: nowrap;
            }

            .preview-panel {
              display: grid;
              place-items: center;
              min-height: 0;
              overflow: auto;
              border: 1px solid var(--line);
              border-radius: 8px;
              background: var(--surface);
            }

            .preview-media {
              width: min(100%, 1100px);
            }

            .preview-image {
              max-height: calc(100vh - 150px);
              object-fit: contain;
            }

            .preview-video {
              max-height: calc(100vh - 150px);
              background: #111827;
            }

            .text-preview {
              width: 100%;
              min-height: 100%;
              margin: 0;
              padding: 22px;
              color: #111827;
              font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
              font-size: 14px;
              line-height: 1.55;
              white-space: pre-wrap;
              word-break: break-word;
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
              min-height: 0;
              height: 100%;
              overflow: auto;
              border: 0;
              border-radius: 0;
              background: transparent;
              box-shadow: none;
            }

            .files-panel.is-dragging {
              box-shadow: inset 0 0 0 2px rgba(37, 99, 235, 0.32);
            }

            .files-panel.is-dragging::after {
              content: "Drop files or folders to upload";
              position: absolute;
              inset: 12px;
              z-index: 4;
              display: grid;
              place-items: center;
              border: 2px dashed #60a5fa;
              border-radius: 6px;
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
              padding: 10px 0 8px;
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

            .file-actions {
              display: flex;
              align-items: center;
              justify-content: space-between;
              gap: 12px;
              width: 100%;
            }

            .file-actions-right {
              display: flex;
              align-items: center;
              gap: 10px;
            }

            .file-search {
              flex: 0 1 260px;
              min-width: 160px;
            }

            .file-search[hidden] {
              display: none;
            }

            .file-search input,
            .sort-select {
              width: 100%;
              height: 36px;
              border: 1px solid var(--line-strong);
              border-radius: 8px;
              background: #ffffff;
              color: #111827;
              font: inherit;
              font-size: 14px;
            }

            .file-search input {
              padding: 0 10px;
            }

            .file-search input:focus,
            .sort-select:focus {
              outline: 2px solid rgba(75, 159, 104, 0.35);
              outline-offset: 1px;
            }

            .sort-select {
              width: 172px;
              padding: 0 9px;
            }

            .tool-button {
              display: inline-grid;
              place-items: center;
              width: 36px;
              min-height: 36px;
              margin: 0;
              padding: 0;
              border: 1px solid var(--line-strong);
              border-radius: 8px;
              background: #ffffff;
              color: #64748b;
              box-shadow: none;
            }

            .tool-button:hover,
            .tool-button[aria-expanded="true"] {
              background: #eef4ff;
              color: var(--blue);
            }

            .search-icon {
              position: relative;
              display: block;
              width: 16px;
              height: 16px;
              border: 2px solid currentColor;
              border-radius: 999px;
            }

            .search-icon::after {
              content: "";
              position: absolute;
              right: -6px;
              bottom: -4px;
              width: 7px;
              height: 2px;
              border-radius: 999px;
              background: currentColor;
              transform: rotate(45deg);
              transform-origin: left center;
            }

            .trash-empty-button {
              display: inline-flex;
              align-items: center;
              gap: 8px;
              min-height: 34px;
              margin: 0;
              padding: 0 12px;
              border: 1px solid #fecaca;
              border-radius: 8px;
              background: #dc2626;
              color: #ffffff;
              box-shadow: none;
              font-weight: 800;
            }

            .trash-empty-button:hover {
              background: #b91c1c;
            }

            .trash-empty-button .context-icon {
              color: currentColor;
            }

            .trash-actions {
              display: inline-flex;
              align-items: center;
              gap: 8px;
            }

            .trash-actions button {
              display: inline-flex;
              align-items: center;
              gap: 8px;
              min-height: 34px;
              margin: 0;
              padding: 0 10px;
              border: 0;
              border-radius: 6px;
              background: transparent;
              color: #334155;
              box-shadow: none;
              font-weight: 700;
            }

            .trash-actions button:hover {
              background: #eef4ff;
            }

            .trash-actions button:last-child {
              color: #991b1b;
            }

            .view-switch {
              display: inline-flex;
              align-items: center;
              padding: 3px;
              border: 1px solid var(--line-strong);
              border-radius: 8px;
              background: #ffffff;
            }

            .view-button {
              width: 34px;
              min-height: 34px;
              margin: 0;
              padding: 0;
              border: 0;
              border-radius: 6px;
              background: transparent;
              color: #64748b;
              box-shadow: none;
            }

            .view-button:hover {
              background: #eef4ff;
              color: #1f2937;
            }

            .view-button.active {
              background: #dbeafe;
              color: var(--blue);
            }

            .view-list-icon,
            .view-grid-icon {
              position: relative;
              display: inline-block;
              width: 16px;
              height: 16px;
            }

            .view-list-icon::before,
            .view-list-icon::after,
            .view-grid-icon::before,
            .view-grid-icon::after {
              content: "";
              position: absolute;
              background: currentColor;
              border-radius: 1px;
            }

            .view-list-icon::before {
              left: 1px;
              top: 2px;
              width: 4px;
              height: 4px;
              box-shadow:
                0 5px 0 currentColor,
                0 10px 0 currentColor;
            }

            .view-list-icon::after {
              left: 7px;
              top: 2px;
              width: 8px;
              height: 2px;
              box-shadow:
                0 5px 0 currentColor,
                0 10px 0 currentColor;
            }

            .view-grid-icon::before {
              inset: 1px;
              border: 2px solid currentColor;
              background: transparent;
              box-shadow:
                6px 0 0 -1px currentColor,
                0 6px 0 -1px currentColor,
                6px 6px 0 -1px currentColor;
            }

            .view-grid-icon::after {
              display: none;
            }

            .compact {
              min-height: 36px;
              margin-top: 0;
              padding: 0 13px;
              font-size: 13px;
            }

            table {
              width: 100%;
              border-collapse: collapse;
              table-layout: fixed;
              user-select: none;
            }

            th, td {
              overflow: hidden;
              padding: 10px 8px;
              border-top: 1px solid #edf1f7;
              text-align: left;
              text-overflow: ellipsis;
              white-space: nowrap;
            }

            th:nth-child(1),
            td:nth-child(1) {
              width: auto;
            }

            th:nth-child(2),
            td:nth-child(2) {
              width: 130px;
            }

            th:nth-child(3),
            td:nth-child(3) {
              width: 180px;
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

            tbody tr[data-file-name] {
              cursor: default;
            }

            .selection-box {
              position: fixed;
              z-index: 30;
              border: 1px solid #2563eb;
              background: rgba(37, 99, 235, 0.14);
              pointer-events: none;
            }

            tbody tr.is-selected {
              background: #dbeafe;
              box-shadow: inset 4px 0 0 var(--blue);
            }

            tbody tr.is-selected:hover {
              background: #cfe1ff;
            }

            tbody tr.is-folder-drop-target,
            tbody tr.is-folder-drop-target:hover {
              background: rgba(75, 159, 104, 0.26);
              outline: 1px solid var(--green);
              outline-offset: -1px;
            }

            tbody tr.fresh-upload {
              background: #ecfdf5;
              box-shadow: inset 4px 0 0 #10b981;
            }

            tbody tr.fresh-upload:hover {
              background: #dffbea;
            }

            .files-grid {
              display: none;
              grid-template-columns: repeat(auto-fill, minmax(150px, 1fr));
              gap: 10px;
              padding: 6px 0 18px;
              user-select: none;
            }

            .files-panel.is-grid-view .files-table {
              display: none;
            }

            .files-panel.is-grid-view .files-grid {
              display: grid;
            }

            .file-card {
              position: relative;
              display: grid;
              grid-template-rows: 104px minmax(62px, auto);
              min-width: 0;
              overflow: hidden;
              border: 1px solid transparent;
              border-radius: 6px;
              background: transparent;
              box-shadow: none;
            }

            .file-card:hover {
              border-color: #dbe3ef;
              background: #f8fafc;
            }

            .file-card.is-selected {
              border-color: #93c5fd;
              background: #dbeafe;
              box-shadow: inset 4px 0 0 var(--blue);
            }

            .file-card.is-folder-drop-target {
              border-color: var(--green);
              background: rgba(75, 159, 104, 0.24);
              box-shadow: inset 0 0 0 1px var(--green);
            }

            .file-card-preview {
              position: relative;
              display: grid;
              place-items: center;
              min-width: 0;
              border-bottom: 1px solid transparent;
              background: transparent;
            }

            .file-card.is-selected .file-card-preview {
              border-bottom-color: #bfdbfe;
              background: #cfe1ff;
            }

            .grid-file-icon {
              width: 58px;
              height: 58px;
            }

            .grid-folder-icon {
              width: 58px;
              height: 42px;
            }

            .file-card-body {
              display: grid;
              align-content: start;
              gap: 5px;
              min-width: 0;
              padding: 10px 11px 12px;
            }

            .file-card-link {
              display: block;
              min-width: 0;
              overflow: hidden;
              color: #1f2937;
              font-size: 13px;
              font-weight: 700;
              line-height: 1.25;
              text-overflow: ellipsis;
              white-space: nowrap;
            }

            .file-card-link:hover {
              color: var(--blue);
            }

            .file-card-meta {
              overflow: hidden;
              color: var(--muted);
              font-size: 12px;
              line-height: 1.25;
              text-overflow: ellipsis;
              white-space: nowrap;
            }

            .move-drag-preview {
              position: fixed;
              top: -1000px;
              left: -1000px;
              width: 60px;
              height: 52px;
              pointer-events: none;
              user-select: none;
            }

            .move-drag-stack {
              position: relative;
              width: 100%;
              height: 100%;
            }

            .move-drag-card {
              position: absolute;
              inset: 0;
              display: grid;
              place-items: center;
              width: 24px;
              height: 30px;
              border: 1px solid rgba(148, 163, 184, 0.55);
              border-radius: 6px;
              background: rgba(255, 255, 255, 0.92);
              box-shadow: 0 8px 18px rgba(15, 23, 42, 0.14);
              opacity: 0.9;
            }

            .move-drag-card:nth-child(1) {
              transform: translate(0, 0);
            }

            .move-drag-card:nth-child(2) {
              transform: translate(7px, 5px);
              opacity: 0.72;
            }

            .move-drag-card:nth-child(3) {
              transform: translate(14px, 10px);
              opacity: 0.55;
            }

            .move-drag-card .file-icon {
              width: 13px;
              height: 16px;
              border-width: 1px;
              border-radius: 3px;
            }

            .move-drag-card .file-icon::after {
              width: 5px;
              height: 5px;
              border-width: 1px;
            }

            .move-drag-folder-icon {
              width: 16px;
              height: 16px;
            }

            .move-drag-count {
              position: absolute;
              right: -3px;
              bottom: -3px;
              display: grid;
              place-items: center;
              min-width: 18px;
              height: 18px;
              padding: 0 4px;
              border: 1px solid #bfdbfe;
              border-radius: 999px;
              background: #eff6ff;
              color: #1d4ed8;
              font-size: 11px;
              font-weight: 800;
              line-height: 1;
              box-shadow: 0 4px 10px rgba(15, 23, 42, 0.12);
            }

            .empty-grid {
              grid-column: 1 / -1;
              margin: 0;
              padding: 72px 24px;
              border-top: 1px solid #edf1f7;
              color: var(--muted);
              text-align: center;
            }

            .file-name {
              display: inline-flex;
              align-items: center;
              gap: 11px;
              min-width: 0;
              width: 100%;
              overflow: hidden;
              font-weight: 700;
            }

            .file-name > span:last-child,
            .file-name a {
              display: block;
              min-width: 0;
              overflow: hidden;
              text-overflow: ellipsis;
              white-space: nowrap;
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

            .row-file-icon {
              width: 24px;
              height: 24px;
            }

            .row-folder-icon {
              width: 24px;
              height: 24px;
            }

            .ui-confirm {
              position: fixed;
              inset: 0;
              z-index: 40;
              display: grid;
              place-items: center;
              padding: 24px;
              background: rgba(15, 23, 42, 0.28);
            }

            .ui-confirm[hidden] {
              display: none;
            }

            .ui-confirm-panel {
              width: min(420px, 100%);
              display: grid;
              gap: 14px;
              padding: 18px;
              border: 1px solid var(--line);
              border-radius: 8px;
              background: var(--surface);
              box-shadow: 0 24px 60px rgba(15, 23, 42, 0.24);
            }

            .ui-confirm-panel p {
              margin: 0;
              color: var(--muted);
              font-weight: 700;
            }

            .ui-confirm-panel input {
              margin: 0;
            }

            .file-info-panel {
              width: min(460px, calc(100vw - 32px));
            }

            .file-info-list {
              display: grid;
              grid-template-columns: 110px minmax(0, 1fr);
              gap: 8px 14px;
              margin: 0;
            }

            .file-info-list dt {
              color: var(--muted);
              font-size: 13px;
              font-weight: 800;
            }

            .file-info-list dd {
              min-width: 0;
              margin: 0;
              overflow: hidden;
              color: var(--text);
              font-size: 13px;
              font-weight: 700;
              text-overflow: ellipsis;
              white-space: nowrap;
            }

            .ui-confirm-actions {
              display: flex;
              justify-content: flex-end;
              gap: 10px;
            }

            .ui-confirm-actions button {
              min-width: 88px;
              margin-top: 0;
            }

            .ui-confirm-actions button:not(.primary) {
              background: #eef2f7;
              color: var(--text);
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

            .context-menu button[data-action="move-trash"] {
              color: #991b1b;
            }

            .context-menu button[data-action="move-selected-here"] {
              color: #1d4ed8;
            }

            .context-menu button[data-action="delete-forever"] {
              color: #991b1b;
            }

            .context-menu button[data-action="restore-trash"] {
              color: #166534;
            }

            .context-icon {
              position: relative;
              display: inline-block;
              width: 18px;
              height: 18px;
              color: #2563eb;
            }

            .move-here-icon {
              color: #1d4ed8;
            }

            .move-here-icon::before,
            .move-here-icon::after,
            .info-icon::before,
            .info-icon::after {
              content: "";
              position: absolute;
            }

            .info-icon::before {
              left: 7px;
              top: 6px;
              width: 4px;
              height: 8px;
              border-radius: 999px;
              background: currentColor;
            }

            .info-icon::after {
              left: 7px;
              top: 2px;
              width: 4px;
              height: 4px;
              border-radius: 999px;
              background: currentColor;
            }

            .move-here-icon::before {
              left: 2px;
              top: 4px;
              width: 10px;
              height: 10px;
              border: 2px solid currentColor;
              border-radius: 2px;
            }

            .move-here-icon::after {
              right: 0;
              top: 7px;
              width: 8px;
              height: 2px;
              background: currentColor;
              box-shadow: -2px -2px 0 0 currentColor, -2px 2px 0 0 currentColor;
            }

            .restore-icon {
              color: #166534;
            }

            .restore-icon::before,
            .delete-forever-icon::before,
            .delete-forever-icon::after {
              content: "";
              position: absolute;
            }

            .restore-icon::before {
              left: 3px;
              top: 5px;
              width: 8px;
              height: 8px;
              border-left: 2px solid currentColor;
              border-bottom: 2px solid currentColor;
              transform: rotate(45deg);
            }

            .restore-icon::after {
              content: "";
              position: absolute;
              left: 7px;
              top: 3px;
              width: 8px;
              height: 2px;
              background: currentColor;
              box-shadow: 0 4px 0 currentColor;
            }

            .delete-forever-icon {
              color: #991b1b;
            }

            .delete-forever-icon::before {
              left: 4px;
              top: 3px;
              width: 10px;
              height: 12px;
              border: 2px solid currentColor;
              border-top: 0;
              border-radius: 0 0 3px 3px;
            }

            .delete-forever-icon::after {
              left: 2px;
              top: 1px;
              width: 14px;
              height: 2px;
              background: currentColor;
              border-radius: 1px;
              box-shadow:
                3px 0 0 0 currentColor,
                6px 0 0 0 currentColor;
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
            .empty-row td {
              padding: 72px 24px;
            }

            .build-badge {
              border-color: var(--line-strong);
              background: rgba(13, 21, 17, 0.88);
              color: var(--muted);
            }

            .auth-card,
            .largest-files,
            .preview-panel,
            .ui-confirm-panel,
            .context-menu {
              border-color: var(--line);
              background: var(--surface);
              color: var(--text);
              box-shadow: var(--shadow);
            }

            input {
              border-color: var(--line-strong);
              background: #000000;
              color: var(--text);
            }

            input:focus {
              outline: 2px solid var(--blue-soft);
              border-color: var(--green);
            }

            button,
            .button {
              background: var(--green);
              color: #07100b;
            }

            .secondary,
            .ui-confirm-actions button:not(.primary) {
              background: var(--surface-soft);
              color: var(--text);
            }

            .sidebar {
              background: #000000;
              border-right-color: var(--line);
            }

            .nav-item {
              color: #b8c9bf;
            }

            .sidebar .nav-list {
              gap: 2px;
              margin-top: 6px;
            }

            .sidebar .places-heading {
              margin: 8px 0 2px;
              padding: 0 7px;
              font-size: 14px;
              font-weight: 400;
            }

            .sidebar .nav-item {
              min-height: 29px;
              gap: 8px;
              padding: 0 7px;
              border-radius: 3px;
              font-size: 14px;
              font-weight: 400;
            }

            .sidebar .nav-icon-img {
              width: 16px;
              height: 16px;
            }

            .sidebar .nav-item span {
              min-width: 0;
              overflow: hidden;
              text-overflow: ellipsis;
              white-space: nowrap;
            }

            .nav-item-static {
              cursor: default;
            }

            .nav-item-static:hover {
              background: transparent;
              color: #b8c9bf;
            }

            .nav-item.active,
            .nav-item:hover {
              background: var(--surface-soft);
              color: #ffffff;
            }

            .file-picker,
            .largest-title {
              border-color: var(--line);
              background: var(--surface);
              color: var(--text);
            }

            .meter,
            .progress {
              background: #18261e;
            }

            .progress span,
            .meter span {
              background: linear-gradient(90deg, #367a50, #6ab883);
            }

            .account-email,
            .section-head span,
            p,
            th {
              color: var(--muted);
            }

            .avatar {
              background: #367a50;
              color: #ffffff;
            }

            .path-bar {
              color: var(--text);
            }

            .path-folder-icon,
            .path-crumb,
            .path-editor input,
            .file-card-link,
            .file-card-meta,
            td,
            label {
              color: var(--text);
            }

            .path-separator,
            .places-heading,
            .largest-empty {
              color: var(--muted);
            }

            .path-crumb:hover,
            a {
              color: #8fcca4;
            }

            .path-crumb.is-current {
              color: #ffffff;
            }

            .path-crumb.is-drop-target,
            .view-button.active,
            .tool-button[aria-expanded="true"],
            .context-menu button:hover {
              background: var(--surface-soft);
              color: #8fcca4;
            }

            .view-button,
            .tool-button {
              color: var(--muted);
            }

            .view-button:hover,
            .tool-button:hover,
            .path-up:hover,
            .path-editor-button:hover {
              background: var(--surface-soft);
              color: var(--text);
            }

            .file-search input,
            .sort-select,
            .tool-button {
              border-color: var(--line-strong);
              background: var(--surface);
              color: var(--text);
            }

            th,
            td,
            .largest-title,
            .largest-file,
            .file-card-preview {
              border-color: var(--line);
            }

            tbody tr:hover,
            .file-card:hover {
              background: rgba(75, 159, 104, 0.08);
            }

            tbody tr.is-selected,
            .file-card.is-selected,
            .file-card.is-selected .file-card-preview {
              border-color: #4b9f68;
              background: rgba(75, 159, 104, 0.18);
              box-shadow: inset 4px 0 0 var(--green);
            }

            tbody tr.is-selected:hover {
              background: rgba(75, 159, 104, 0.24);
            }

            tbody tr.fresh-upload,
            tbody tr.fresh-upload:hover {
              background: rgba(75, 159, 104, 0.12);
              box-shadow: inset 4px 0 0 var(--green);
            }

            .selection-box {
              border-color: #6ab883;
              background: rgba(75, 159, 104, 0.16);
            }

            .alert,
            .alert.success {
              background: rgba(75, 159, 104, 0.12);
              border-color: #367a50;
              color: #c7e6d1;
            }

            .files-panel.is-dragging {
              box-shadow: inset 0 0 0 2px rgba(75, 159, 104, 0.42);
            }

            .files-panel.is-dragging::after {
              border-color: #6ab883;
              background: rgba(6, 10, 8, 0.88);
              color: #c7e6d1;
            }

            .context-icon,
            .move-here-icon,
            .restore-icon {
              color: #6ab883;
            }

            .context-menu button[data-action="move-selected-here"],
            .context-menu button[data-action="restore-trash"] {
              color: #8fcca4;
            }

            .context-menu button[data-action="move-trash"],
            .context-menu button[data-action="delete-forever"],
            .delete-forever-icon {
              color: #f87171;
            }

            .home-icon,
            .home-icon::after {
              background: #4b9f68;
            }

            .path-folder-icon {
              color: #6ab883;
            }

            .path-folder-icon svg * {
              fill: currentColor;
            }

            .path-bar.is-drop-target {
              border-color: #6ab883;
              background: rgba(75, 159, 104, 0.08);
              box-shadow: inset 0 -2px 0 #6ab883;
            }

            .file-icon {
              border-color: #6f8276;
              background: #121a15;
            }

            .file-icon::after {
              border-color: #6f8276;
              background: #101512;
            }

            .upload-file-icon::after,
            .new-file-icon::after {
              background: var(--surface);
            }

            .move-drag-card {
              border-color: var(--line-strong);
              background: rgba(16, 21, 18, 0.94);
              box-shadow: 0 8px 18px rgba(0, 0, 0, 0.32);
            }

            .move-drag-count {
              border-color: #4b9f68;
              background: var(--surface-soft);
              color: #c7e6d1;
              box-shadow: 0 4px 10px rgba(0, 0, 0, 0.28);
            }

            .empty-grid {
              border-color: var(--line);
              color: var(--muted);
            }

            .text-preview {
              color: var(--text);
            }

            .trash-empty-button {
              border-color: #5d2525;
              background: #7f1d1d;
              color: #fee2e2;
            }

            .trash-empty-button:hover {
              background: #991b1b;
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

              .file-actions {
                align-items: flex-start;
                flex-direction: column;
              }

              .file-actions-right {
                flex-wrap: wrap;
                width: 100%;
              }

              .file-search {
                flex: 1 1 100%;
                width: 100%;
              }

              .sort-select {
                flex: 1 1 170px;
                width: auto;
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
          <div class="build-badge">{{buildVersion}}</div>
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
