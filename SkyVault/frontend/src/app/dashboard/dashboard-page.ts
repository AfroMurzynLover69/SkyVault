import { CommonModule } from '@angular/common';
import { Component, EventEmitter, HostListener, Input, Output, computed, signal } from '@angular/core';
import { AccountState, DeviceSessionState, FileItem, ViewMode } from '../core/models';
import { ApiService } from '../core/api.service';
import { CryptoService } from '../core/crypto.service';
import { displayName, formatBytes, isDirectChild, normalizePath, parentPath, workspaceTitle } from '../core/path-utils';
import { FileBrowser } from './file-browser';
import { saveBlob } from '../core/encoding';

interface PendingUpload {
  file: File;
  relativePath: string;
}

interface UploadProgressState {
  completedChunks: number;
  totalChunks: number;
}

const viewModes = new Set<ViewMode>(['home', 'computers', 'documents', 'images', 'videos', 'music', 'recent', 'starred', 'trash']);

@Component({
  selector: 'app-dashboard-page',
  imports: [CommonModule, FileBrowser],
  templateUrl: './dashboard-page.html',
  styleUrl: './dashboard-page.css',
})
export class DashboardPage {
  @Input({ required: true }) account!: AccountState;
  @Input() files: FileItem[] = [];
  @Input() trashFiles: FileItem[] = [];
  @Input() deviceSessions: DeviceSessionState[] = [];
  @Input() starredPaths: string[] = [];
  @Output() refreshRequested = new EventEmitter<void>();
  @Output() logoutRequested = new EventEmitter<void>();

  readonly currentView = signal<ViewMode>(this.getInitialView());
  readonly currentPath = signal(new URLSearchParams(window.location.search).get('path') || '');
  readonly searchQuery = signal('');
  readonly selectedPaths = signal<string[]>([]);
  readonly fileViewMode = signal(localStorage.getItem('skyvaultFileView') || 'list');
  readonly message = signal('');
  readonly showAccountPanel = signal(false);
  readonly showSettingsPanel = signal(false);
  readonly showAboutPanel = signal(false);
  readonly showFileSearch = signal(false);
  readonly showPathEditor = signal(false);
  readonly editedPath = signal('/home/');
  readonly showContextMenu = signal(false);
  readonly contextMenuX = signal(0);
  readonly contextMenuY = signal(0);
  readonly contextTarget = signal<FileItem | null>(null);
  readonly customModal = signal<{
    type: 'prompt' | 'confirm';
    message: string;
    defaultValue: string;
    placeholder: string;
  } | null>(null);
  readonly modalInputValue = signal('');
  private modalResolve: ((value: string | null) => void) | null = null;
  readonly showInfoPanel = signal(false);
  readonly infoItem = signal<FileItem | null>(null);
  readonly isDraggingFiles = signal(false);
  readonly pathDropTarget = signal<string | null>(null);
  readonly uploadProgress = signal(0);
  readonly typeFilter = signal('all');
  readonly sortMode = signal('modified-desc');
  readonly usedPercent = computed(() => Math.min(100, Math.max(0, this.account.usedBytes * 100 / this.account.quotaBytes)));
  readonly workspaceTitle = computed(() => workspaceTitle(this.currentView(), this.currentPath()));
  readonly visibleFiles = computed(() => this.computeVisibleFiles());
  readonly accountInitial = computed(() => (this.account.username || '?').trim().slice(0, 1).toUpperCase());
  readonly parentDirectory = computed(() => parentPath(this.currentPath()));
  readonly virtualPath = computed(() => this.currentPath() ? '/home/' + this.currentPath() + '/' : '/home/');
  readonly breadcrumbs = computed(() => {
    const parts = this.currentPath().split('/').filter(Boolean);
    const crumbs = [{ label: 'home', path: '' }];
    let path = '';
    for (const part of parts) {
      path = path ? path + '/' + part : part;
      crumbs.push({ label: part, path });
    }
    return crumbs;
  });

  formatBytes = formatBytes;
  displayName = displayName;

  constructor(
    private readonly api: ApiService,
    private readonly crypto: CryptoService,
  ) {}

  navigate(view: ViewMode, path = '', event?: Event) {
    event?.preventDefault();
    this.currentView.set(view);
    this.currentPath.set(normalizePath(path));
    this.selectedPaths.set([]);
    this.hideContextMenu();
    this.showPathEditor.set(false);
    const url = view === 'home' ? (path ? '/?path=' + encodeURIComponent(path) : '/') : '/?view=' + encodeURIComponent(view);
    window.history.replaceState(null, '', url);
  }

  isActive(view: ViewMode) {
    return this.currentView() === view ? 'active' : '';
  }

  toggleViewMode(mode: string) {
    this.fileViewMode.set(mode);
    localStorage.setItem('skyvaultFileView', mode);
  }

  toggleAccountPanel() {
    this.showAccountPanel.update((value) => !value);
    this.showSettingsPanel.set(false);
  }

  toggleSettingsPanel() {
    this.showSettingsPanel.update((value) => !value);
    this.showAccountPanel.set(false);
  }

  toggleFileSearch() {
    this.showFileSearch.update((value) => !value);
  }

  beginPathEdit() {
    this.editedPath.set(this.virtualPath());
    this.showPathEditor.set(true);
    this.hideContextMenu();
  }

  commitPathEdit() {
    const raw = this.editedPath().trim().replace(/^\/home\/?/, '');
    this.showPathEditor.set(false);
    this.navigate('home', normalizePath(raw));
  }

  clearPathEdit() {
    this.editedPath.set('/home/');
  }

  toggleSelect(path: string) {
    const selected = new Set(this.selectedPaths());
    selected.has(path) ? selected.delete(path) : selected.add(path);
    this.selectedPaths.set([...selected]);
  }

  async openItem(item: FileItem) {
    if (item.isFolder) {
      this.navigate('home', item.name);
      return;
    }
    await this.download(item);
  }

  async uploadFiles(fileList: FileList | null) {
    if (!fileList?.length) return;
    const uploads = Array.from(fileList).map((file) => ({
      file,
      relativePath: this.getUploadRelativePath(file),
    }));
    await this.uploadItems(uploads);
  }

  onDragOver(event: DragEvent) {
    if (!this.hasDroppedFiles(event)) return;
    event.preventDefault();
    event.stopPropagation();
    this.isDraggingFiles.set(true);
    if (event.dataTransfer) {
      event.dataTransfer.dropEffect = 'copy';
    }
  }

  onDragLeave(event: DragEvent) {
    if (!event.currentTarget || !event.relatedTarget) {
      this.isDraggingFiles.set(false);
      return;
    }

    const panel = event.currentTarget as HTMLElement;
    if (!panel.contains(event.relatedTarget as Node)) {
      this.isDraggingFiles.set(false);
    }
  }

  async onDrop(event: DragEvent) {
    if (!this.hasDroppedFiles(event)) return;
    event.preventDefault();
    event.stopPropagation();
    this.isDraggingFiles.set(false);
    const uploads = await this.collectDroppedUploads(event.dataTransfer);
    await this.uploadItems(uploads);
  }

  onPathDragOver(event: DragEvent, destination: string) {
    if (!this.hasMovedPaths(event)) return;
    event.preventDefault();
    event.stopPropagation();
    this.pathDropTarget.set(normalizePath(destination));
    if (event.dataTransfer) {
      event.dataTransfer.dropEffect = 'move';
    }
  }

  onPathDragLeave(event: DragEvent, destination: string) {
    const target = event.currentTarget as HTMLElement | null;
    if (target?.contains(event.relatedTarget as Node)) {
      return;
    }

    if (this.pathDropTarget() === normalizePath(destination)) {
      this.pathDropTarget.set(null);
    }
  }

  async onPathDrop(event: DragEvent, destination: string) {
    if (!this.hasMovedPaths(event)) return;
    event.preventDefault();
    event.stopPropagation();
    this.pathDropTarget.set(null);

    const paths = this.readMovedPaths(event);
    if (!paths.length) return;
    await this.movePathsToFolder(paths, destination);
  }

  private async uploadItems(uploads: PendingUpload[]) {
    if (!uploads.length) return;
    this.message.set('Szyfrowanie uploadu...');
    this.uploadProgress.set(0);
    const progress: UploadProgressState = {
      completedChunks: 0,
      totalChunks: uploads.reduce((total, upload) => total + Math.max(1, this.crypto.getChunkedUploadPlan(upload.file.size).totalChunks), 0),
    };

    for (let index = 0; index < uploads.length; index += 1) {
      const upload = uploads[index];
      const vaultPath = normalizePath(this.currentPath() ? `${this.currentPath()}/${upload.relativePath}` : upload.relativePath);
      const encryptedPath = await this.crypto.encryptVirtualPath(vaultPath);
      this.message.set(`Szyfrowanie ${index + 1}/${uploads.length}: ${upload.relativePath}`);
      await this.uploadFileInChunks(upload.file, encryptedPath, index, uploads.length, upload.relativePath, progress);
    }

    this.uploadProgress.set(100);
    this.message.set('Upload zakończony.');
    this.refreshRequested.emit();
  }

  async download(item: FileItem) {
    this.message.set('Pobieram i odszyfrowuję...');
    const response = await this.api.downloadFile(item);
    if (!response.ok) {
      this.message.set('Nie udało się pobrać pliku.');
      return;
    }
    const decrypted = await this.crypto.decryptVaultBlob(await response.blob());
    saveBlob(decrypted, displayName(item.name));
    this.message.set('Pobrano.');
  }

  showFileInfo(item: FileItem) {
    this.infoItem.set(item);
    this.showInfoPanel.set(true);
    this.hideContextMenu();
  }

  showPrompt(message: string, defaultValue = '', placeholder = ''): Promise<string | null> {
    this.modalInputValue.set(defaultValue);
    this.customModal.set({ type: 'prompt', message, defaultValue, placeholder });
    return new Promise((resolve) => {
      this.modalResolve = resolve;
    });
  }

  showConfirm(message: string): Promise<boolean> {
    this.customModal.set({ type: 'confirm', message, defaultValue: '', placeholder: '' });
    return new Promise((resolve) => {
      this.modalResolve = (val) => resolve(val !== null);
    });
  }

  onModalSubmit(event?: Event) {
    event?.preventDefault();
    const resolve = this.modalResolve;
    const modal = this.customModal();
    if (resolve && modal) {
      this.customModal.set(null);
      this.modalResolve = null;
      if (modal.type === 'prompt') {
        resolve(this.modalInputValue());
      } else {
        resolve(''); // resolves as true
      }
    }
  }

  onModalCancel() {
    const resolve = this.modalResolve;
    if (resolve) {
      this.customModal.set(null);
      this.modalResolve = null;
      resolve(null);
    }
  }

  async createFolder() {
    const folderName = await this.showPrompt('Nazwa folderu');
    if (!folderName) return;
    const vaultPath = normalizePath(this.currentPath() ? `${this.currentPath()}/${folderName}` : folderName);
    const encryptedPath = await this.crypto.encryptVirtualPath(vaultPath);
    await this.api.postForm('/folders/create', encryptedPath);
    this.refreshRequested.emit();
  }

  async renameSelected() {
    const selected = this.selectedPaths();
    if (selected.length !== 1 || this.currentView() === 'trash') return;
    const item = this.files.find((file) => file.name === selected[0]);
    if (!item) return;
    await this.renameItem(item);
  }

  async renameItem(item: FileItem) {
    const currentName = displayName(item.name);
    const nextName = await this.showPrompt('Nowa nazwa', currentName);
    if (!nextName || nextName === currentName) return;

    const targetBase = normalizePath(parentPath(item.name) ? `${parentPath(item.name)}/${nextName}` : nextName);
    if (!targetBase) return;

    const affected = this.getAffectedItemsForPath(item);
    const updates = [];

    for (const affectedItem of affected) {
      if (!affectedItem.pathHash) continue;
      const suffix = affectedItem.name === item.name ? '' : affectedItem.name.slice(item.name.length);
      const nextPath = normalizePath(targetBase + suffix);
      const encryptedPath = await this.crypto.encryptVirtualPath(nextPath);
      updates.push({
        sourcePathHash: affectedItem.pathHash,
        encryptedVirtualPath: encryptedPath.encryptedVirtualPath,
        virtualPathIv: encryptedPath.virtualPathIv,
        newPathHash: encryptedPath.pathHash,
      });
    }

    if (!updates.length) {
      this.message.set('Brak zaszyfrowanych wpisów do zmiany nazwy.');
      return;
    }

    try {
      const result = await this.api.updateEncryptedPaths(updates);
      this.message.set(result.conflicts ? 'Nazwa docelowa już istnieje.' : `Zmieniono nazwę ${result.updated} elementów.`);
      this.selectedPaths.set([]);
      this.refreshRequested.emit();
    } catch (error) {
      this.message.set(error instanceof Error ? error.message : 'Nie udało się zmienić nazwy.');
    }
  }

  async moveSelectedToFolder() {
    if (!this.selectedPaths().length || this.currentView() === 'trash') return;
    const destination = normalizePath((await this.showPrompt('Docelowy folder', this.currentPath())) || '');
    await this.movePathsToFolder(this.selectedPaths(), destination);
  }

  async copySelectedToFolder() {
    if (!this.selectedPaths().length || this.currentView() === 'trash') return;
    const destination = normalizePath((await this.showPrompt('Folder dla kopii', this.currentPath())) || '');
    const updates = [];

    for (const selectedPath of this.selectedPaths()) {
      const root = this.files.find((file) => file.name === selectedPath);
      if (!root) continue;
      const affected = this.getAffectedItemsForPath(root);
      const targetBase = normalizePath(destination ? `${destination}/${displayName(root.name)}` : displayName(root.name));

      for (const affectedItem of affected) {
        if (!affectedItem.pathHash) continue;
        const suffix = affectedItem.name === root.name ? '' : affectedItem.name.slice(root.name.length);
        const nextPath = normalizePath(targetBase + suffix);
        const encryptedPath = await this.crypto.encryptVirtualPath(nextPath);
        updates.push({
          sourcePathHash: affectedItem.pathHash,
          encryptedVirtualPath: encryptedPath.encryptedVirtualPath,
          virtualPathIv: encryptedPath.virtualPathIv,
          newPathHash: encryptedPath.pathHash,
        });
      }
    }

    if (!updates.length) return;

    try {
      const result = await this.api.copyEncryptedPaths(updates);
      this.message.set(result.conflicts ? 'Część kopii już istnieje.' : `Skopiowano ${result.copied} elementów.`);
      this.selectedPaths.set([]);
      this.refreshRequested.emit();
    } catch (error) {
      this.message.set(error instanceof Error ? error.message : 'Nie udało się skopiować.');
    }
  }

  async movePathsToFolder(paths: string[], destination: string) {
    if (!paths.length || this.currentView() === 'trash') return;
    destination = normalizePath(destination);
    const updates = [];
    let skippedInvalid = 0;

    for (const selectedPath of paths) {
      const root = this.files.find((file) => file.name === selectedPath);
      if (!root) continue;

      if (root.isFolder && (destination === root.name || destination.startsWith(root.name + '/'))) {
        skippedInvalid += 1;
        continue;
      }

      const affected = this.getAffectedItemsForPath(root);
      const targetBase = normalizePath(destination ? `${destination}/${displayName(root.name)}` : displayName(root.name));

      if (targetBase === root.name) {
        skippedInvalid += 1;
        continue;
      }

      for (const affectedItem of affected) {
        if (!affectedItem.pathHash) continue;
        const suffix = affectedItem.name === root.name ? '' : affectedItem.name.slice(root.name.length);
        const nextPath = normalizePath(targetBase + suffix);
        const encryptedPath = await this.crypto.encryptVirtualPath(nextPath);
        updates.push({
          sourcePathHash: affectedItem.pathHash,
          encryptedVirtualPath: encryptedPath.encryptedVirtualPath,
          virtualPathIv: encryptedPath.virtualPathIv,
          newPathHash: encryptedPath.pathHash,
        });
      }
    }

    if (!updates.length) {
      if (skippedInvalid > 0) {
        this.message.set('Nie można przenieść folderu do niego samego ani w to samo miejsce.');
      }
      return;
    }

    try {
      const result = await this.api.updateEncryptedPaths(updates);
      this.message.set(result.conflicts ? 'Część docelowych elementów już istnieje.' : `Przeniesiono ${result.updated} elementów.`);
      this.selectedPaths.set([]);
      this.refreshRequested.emit();
    } catch (error) {
      this.message.set(error instanceof Error ? error.message : 'Nie udało się przenieść.');
    }
  }

  async downloadSelectedZip() {
    if (!this.selectedPaths().length) return;
    const response = await this.api.postForm('/files/download-zip', { paths: this.selectedPaths().join('\n') });
    if (!response.ok) {
      this.message.set('Nie udało się przygotować ZIP.');
      return;
    }

    saveBlob(await response.blob(), 'skyvault-selection.zip');
    this.message.set('Pobrano ZIP.');
  }

  async moveSelectedToTrash() {
    if (!this.selectedPaths().length) return;
    await this.api.postForm('/files/trash', { paths: this.selectedPaths().join('\n'), currentPath: this.currentPath() });
    this.selectedPaths.set([]);
    this.refreshRequested.emit();
  }

  async toggleSelectedStars() {
    if (!this.selectedPaths().length) return;
    await this.api.postForm('/files/star', { paths: this.selectedPaths().join('\n'), currentPath: this.currentPath() });
    this.refreshRequested.emit();
  }

  async restoreSelectedFromTrash() {
    if (this.currentView() !== 'trash' || !this.selectedPaths().length) return;
    await this.api.postForm('/files/trash/restore', { paths: this.selectedPaths().join('\n'), currentPath: this.currentPath() });
    this.selectedPaths.set([]);
    this.refreshRequested.emit();
  }

  async deleteSelectedForever() {
    if (this.currentView() !== 'trash' || !this.selectedPaths().length) return;
    if (!(await this.showConfirm('Usunąć zaznaczone elementy na zawsze?'))) return;
    await this.api.postForm('/files/trash/delete', { paths: this.selectedPaths().join('\n'), currentPath: this.currentPath() });
    this.selectedPaths.set([]);
    this.refreshRequested.emit();
  }

  async emptyTrash() {
    if (!(await this.showConfirm('Opróżnić kosz?'))) return;
    await this.api.postForm('/files/trash/empty', { currentPath: this.currentPath() });
    this.selectedPaths.set([]);
    this.refreshRequested.emit();
  }

  openContextMenu(event: { file: FileItem | null; x: number; y: number }) {
    if (event.file) {
      if (!this.selectedPaths().includes(event.file.name)) {
        this.selectedPaths.set([event.file.name]);
      }
      this.contextTarget.set(event.file);
    } else {
      this.selectedPaths.set([]);
      this.contextTarget.set(null);
    }

    this.contextMenuX.set(event.x);
    this.contextMenuY.set(event.y);
    this.showContextMenu.set(true);
  }

  hideContextMenu() {
    this.showContextMenu.set(false);
  }

  async runContextAction(action: 'open' | 'download' | 'rename' | 'move' | 'copy' | 'trash' | 'restore' | 'delete' | 'star' | 'info' | 'zip' | 'new-folder' | 'upload-files' | 'upload-folder' | 'empty-trash') {
    const target = this.contextTarget();
    this.hideContextMenu();

    switch (action) {
      case 'open':
        if (target) {
          await this.openItem(target);
        }
        break;
      case 'download':
        if (target && !target.isFolder) {
          await this.download(target);
        } else {
          await this.downloadSelectedZip();
        }
        break;
      case 'rename':
        if (target) {
          await this.renameItem(target);
        }
        break;
      case 'move':
        await this.moveSelectedToFolder();
        break;
      case 'copy':
        await this.copySelectedToFolder();
        break;
      case 'trash':
        await this.moveSelectedToTrash();
        break;
      case 'restore':
        await this.restoreSelectedFromTrash();
        break;
      case 'delete':
        await this.deleteSelectedForever();
        break;
      case 'star':
        await this.toggleSelectedStars();
        break;
      case 'info':
        if (target) {
          this.showFileInfo(target);
        }
        break;
      case 'zip':
        await this.downloadSelectedZip();
        break;
      case 'new-folder':
        await this.createFolder();
        break;
      case 'upload-files':
        document.getElementById('fileInput')?.click();
        break;
      case 'upload-folder':
        document.getElementById('folderInput')?.click();
        break;
      case 'empty-trash':
        await this.emptyTrash();
        break;
    }
  }

  @HostListener('window:click')
  onWindowClick() {
    this.hideContextMenu();
  }

  @HostListener('window:keydown', ['$event'])
  async onWindowKeyDown(event: KeyboardEvent) {
    const target = event.target as HTMLElement | null;
    const editable = target?.tagName === 'INPUT' || target?.tagName === 'TEXTAREA' || target?.isContentEditable;

    if (event.key === 'Escape') {
      this.hideContextMenu();
      this.showPathEditor.set(false);
      this.showInfoPanel.set(false);
      return;
    }

    if (editable) {
      return;
    }

    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'a') {
      event.preventDefault();
      this.selectedPaths.set(this.visibleFiles().map((file) => file.name));
      return;
    }

    if (event.key === 'Delete') {
      event.preventDefault();
      if (this.currentView() === 'trash') {
        await this.deleteSelectedForever();
      } else {
        await this.moveSelectedToTrash();
      }
      return;
    }

    if (event.key === 'F2') {
      event.preventDefault();
      await this.renameSelected();
      return;
    }

    if (event.key === 'Enter' && this.selectedPaths().length === 1) {
      const item = this.visibleFiles().find((file) => file.name === this.selectedPaths()[0]);
      if (item) {
        event.preventDefault();
        await this.openItem(item);
      }
    }
  }

  private getAffectedItemsForPath(item: FileItem) {
    if (!item.isFolder) return [item];
    const prefix = item.name + '/';
    const byName = new Map<string, FileItem>();
    for (const candidate of this.files) {
      if (candidate.name === item.name || candidate.name.startsWith(prefix)) {
        byName.set(candidate.name, candidate);
      }
    }
    return [...byName.values()];
  }

  private computeVisibleFiles() {
    const query = this.searchQuery().trim().toLowerCase();
    const view = this.currentView();
    const source = view === 'trash' ? this.trashFiles : this.files;
    let items = source;

    if (view === 'home') {
      items = source.filter((item) => isDirectChild(item.name, this.currentPath()));
    } else if (view === 'documents') {
      items = source.filter((item) => !item.isFolder && /\.(pdf|txt|md|doc|docx|xls|xlsx|ppt|pptx)$/i.test(item.name));
    } else if (view === 'images') {
      items = source.filter((item) => !item.isFolder && /\.(png|jpe?g|gif|bmp|svg)$/i.test(item.name));
    } else if (view === 'videos') {
      items = source.filter((item) => !item.isFolder && /\.(mp4|webm|mkv|ogg)$/i.test(item.name));
    } else if (view === 'music') {
      items = source.filter((item) => !item.isFolder && /\.(mp3|wav|flac)$/i.test(item.name));
    } else if (view === 'starred') {
      const starred = new Set(this.starredPaths);
      items = source.filter((item) => starred.has(item.name));
    } else if (view === 'recent') {
      items = [...source].filter((item) => !item.isFolder).sort((a, b) => +new Date(b.modifiedAt) - +new Date(a.modifiedAt)).slice(0, 50);
    } else if (view === 'computers') {
      items = [];
    }

    if (query) {
      items = items.filter((item) => displayName(item.name).toLowerCase().startsWith(query));
    }

    if (this.typeFilter() === 'folder') {
      items = items.filter((item) => item.isFolder);
    } else if (this.typeFilter() === 'file') {
      items = items.filter((item) => !item.isFolder);
    }

    const sorted = [...items].sort((a, b) => {
      const folderOrder = Number(b.isFolder) - Number(a.isFolder);
      if (folderOrder) return folderOrder;
      const [field, direction] = this.sortMode().split('-');
      const sign = direction === 'asc' ? 1 : -1;
      if (field === 'modified') return (+new Date(a.modifiedAt) - +new Date(b.modifiedAt)) * sign;
      if (field === 'size') return (a.sizeBytes - b.sizeBytes) * sign;
      return displayName(a.name).localeCompare(displayName(b.name)) * sign;
    });

    return sorted;
  }

  private hasDroppedFiles(event: DragEvent) {
    return Array.from(event.dataTransfer?.types ?? []).includes('Files');
  }

  private getInitialView() {
    const view = new URLSearchParams(window.location.search).get('view') as ViewMode | null;
    return view && viewModes.has(view) ? view : 'home';
  }

  private hasMovedPaths(event: DragEvent) {
    return Array.from(event.dataTransfer?.types ?? []).includes('application/x-skyvault-paths');
  }

  private readMovedPaths(event: DragEvent) {
    const raw = event.dataTransfer?.getData('application/x-skyvault-paths');
    if (!raw) return [];

    try {
      const paths = JSON.parse(raw);
      return Array.isArray(paths) ? paths.filter((path): path is string => typeof path === 'string') : [];
    } catch {
      return [];
    }
  }

  private async uploadFileInChunks(
    file: File,
    encryptedPath: { encryptedVirtualPath: string; virtualPathIv: string; pathHash: string },
    fileIndex: number,
    totalFiles: number,
    label = file.name,
    progress?: UploadProgressState,
  ) {
    const plan = this.crypto.getChunkedUploadPlan(file.size);
    let session = await this.api.startChunkUpload({
      filename: encryptedPath.pathHash,
      relativePath: encryptedPath.pathHash,
      currentPath: '',
      lastModified: String(file.lastModified),
      fileSize: String(plan.totalEncryptedBytes),
      chunkSize: String(plan.transportChunkBytes),
      encryptedVirtualPath: encryptedPath.encryptedVirtualPath,
      virtualPathIv: encryptedPath.virtualPathIv,
      pathHash: encryptedPath.pathHash,
    });
    const uploaded = new Set(session.uploadedChunks);

    for (let chunkIndex = 0; chunkIndex < session.totalChunks; chunkIndex += 1) {
      if (uploaded.has(chunkIndex)) {
        this.updateUploadProgress(progress, label, fileIndex, totalFiles, chunkIndex, session.totalChunks);
        continue;
      }

      const bounds = this.getPlainChunkBounds(file.size, plan, chunkIndex);
      const plaintextChunk = file.slice(bounds.start, bounds.end);
      const encryptedChunk = await this.crypto.encryptVaultFileChunk(plaintextChunk, chunkIndex === 0);
      session = await this.api.uploadChunk(session.uploadId, chunkIndex, encryptedChunk);
      this.updateUploadProgress(progress, label, fileIndex, totalFiles, chunkIndex, session.totalChunks);
    }

    await this.api.completeChunkUpload(session.uploadId);

    if (session.totalChunks === 0) {
      this.updateUploadProgress(progress, label, fileIndex, totalFiles, 0, 1);
    }
  }

  private updateUploadProgress(
    progress: UploadProgressState | undefined,
    label: string,
    fileIndex: number,
    totalFiles: number,
    chunkIndex: number,
    totalChunks: number,
  ) {
    if (progress) {
      progress.completedChunks = Math.min(progress.totalChunks, progress.completedChunks + 1);
      this.uploadProgress.set(Math.round(progress.completedChunks * 100 / Math.max(1, progress.totalChunks)));
    }

    this.message.set(`Upload ${fileIndex + 1}/${totalFiles}: ${label} chunk ${Math.min(chunkIndex + 1, totalChunks)}/${totalChunks}`);
  }

  private getPlainChunkBounds(
    fileSize: number,
    plan: { firstPlainChunkBytes: number; regularPlainChunkBytes: number },
    chunkIndex: number,
  ) {
    if (chunkIndex === 0) {
      return {
        start: 0,
        end: Math.min(fileSize, plan.firstPlainChunkBytes),
      };
    }

    const start = plan.firstPlainChunkBytes + (chunkIndex - 1) * plan.regularPlainChunkBytes;
    return {
      start,
      end: Math.min(fileSize, start + plan.regularPlainChunkBytes),
    };
  }

  private getUploadRelativePath(file: File) {
    const relativePath = (file as File & { webkitRelativePath?: string }).webkitRelativePath || file.name;
    return normalizePath(relativePath) || file.name;
  }

  private async collectDroppedUploads(dataTransfer: DataTransfer | null) {
    if (!dataTransfer) return [];
    const itemUploads = await this.collectDataTransferItems(dataTransfer);
    const droppedFiles = Array.from(dataTransfer.files);
    if (itemUploads.length >= droppedFiles.length) {
      return itemUploads;
    }

    const uploads = [...itemUploads];
    const seen = new Set(uploads.map((upload) => this.uploadIdentity(upload)));

    for (const file of droppedFiles) {
      const upload = {
        file,
        relativePath: this.getUploadRelativePath(file),
      };
      const identity = this.uploadIdentity(upload);
      if (!seen.has(identity)) {
        seen.add(identity);
        uploads.push(upload);
      }
    }

    return uploads;
  }

  private uploadIdentity(upload: PendingUpload) {
    return `${upload.relativePath}\0${upload.file.size}\0${upload.file.lastModified}`;
  }

  private async collectDataTransferItems(dataTransfer: DataTransfer) {
    const items = Array.from(dataTransfer.items ?? []);
    const uploads: PendingUpload[] = [];

    for (const item of items) {
      const entry = this.getEntry(item);
      if (entry) {
        uploads.push(...await this.collectEntryFiles(entry, ''));
        continue;
      }

      const file = item.getAsFile();
      if (file) {
        uploads.push({ file, relativePath: file.name });
      }
    }

    return uploads;
  }

  private getEntry(item: DataTransferItem) {
    const withEntry = item as DataTransferItem & { webkitGetAsEntry?: () => unknown };
    return typeof withEntry.webkitGetAsEntry === 'function' ? withEntry.webkitGetAsEntry() : null;
  }

  private async collectEntryFiles(entry: unknown, parentPath: string): Promise<PendingUpload[]> {
    const fileEntry = entry as {
      name: string;
      isFile?: boolean;
      isDirectory?: boolean;
      file?: (success: (file: File) => void, error: (error: unknown) => void) => void;
      createReader?: () => { readEntries: (success: (entries: unknown[]) => void, error: (error: unknown) => void) => void };
    };
    const entryPath = normalizePath(parentPath ? `${parentPath}/${fileEntry.name}` : fileEntry.name);

    if (fileEntry.isFile && fileEntry.file) {
      const file = await new Promise<File>((resolve, reject) => fileEntry.file?.(resolve, reject));
      return [{ file, relativePath: entryPath || file.name }];
    }

    if (!fileEntry.isDirectory || !fileEntry.createReader) {
      return [];
    }

    const reader = fileEntry.createReader();
    const children: unknown[] = [];

    while (true) {
      const batch = await new Promise<unknown[]>((resolve, reject) => reader.readEntries(resolve, reject));
      if (!batch.length) break;
      children.push(...batch);
    }

    const uploads: PendingUpload[] = [];
    for (const child of children) {
      uploads.push(...await this.collectEntryFiles(child, entryPath));
    }
    return uploads;
  }
}
