import { Component, OnInit, signal } from '@angular/core';
import { AuthPage } from './auth/auth-page';
import { DashboardPage } from './dashboard/dashboard-page';
import { ApiService } from './core/api.service';
import { AccountState, DeviceSessionState, FileItem } from './core/models';
import { AuthService } from './core/auth.service';
import { CryptoService } from './core/crypto.service';
import { parentPath } from './core/path-utils';

@Component({
  selector: 'app-root',
  imports: [AuthPage, DashboardPage],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App implements OnInit {
  readonly loading = signal(true);
  readonly authenticated = signal(false);
  readonly account = signal<AccountState | null>(null);
  readonly files = signal<FileItem[]>([]);
  readonly trashFiles = signal<FileItem[]>([]);
  readonly deviceSessions = signal<DeviceSessionState[]>([]);
  readonly starredPaths = signal<string[]>([]);

  constructor(
    private readonly api: ApiService,
    private readonly auth: AuthService,
    private readonly crypto: CryptoService,
  ) {}

  async ngOnInit() {
    await this.refreshState();
  }

  async refreshState() {
    this.loading.set(true);
    const state = await this.api.getState();
    this.authenticated.set(state.authenticated);
    this.account.set(state.account ?? null);
    this.files.set(this.withSyntheticFolders(await this.crypto.decryptFileList(state.files ?? [])));
    this.trashFiles.set(this.withSyntheticFolders(await this.crypto.decryptFileList(state.trashFiles ?? [])));
    this.deviceSessions.set(state.deviceSessions ?? []);
    this.starredPaths.set(state.starredPaths ?? []);
    this.loading.set(false);
  }

  async logout() {
    await this.auth.logout();
    await this.refreshState();
  }

  private withSyntheticFolders(files: FileItem[]) {
    const byName = new Map(files.map((file) => [file.name, file]));

    for (const file of files) {
      let folder = parentPath(file.name);
      while (folder) {
        if (!byName.has(folder)) {
          byName.set(folder, {
            name: folder,
            sizeBytes: 0,
            modifiedAt: file.modifiedAt,
            isFolder: true,
          });
        }
        folder = parentPath(folder);
      }
    }

    return [...byName.values()];
  }
}
