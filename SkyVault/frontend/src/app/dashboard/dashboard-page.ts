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

interface DropSnapshot {
  items: DataTransferItem[];
  files: File[];
  types: string[];
}

interface UploadProgressState {
  completedChunks: number;
  totalChunks: number;
}

type UploadQueueStatus = 'pending' | 'checking' | 'uploading' | 'done' | 'error' | 'cancelled';

interface UploadQueueItem {
  id: string;
  name: string;
  sizeBytes: number;
  encryptedBytes: number;
  percent: number;
  status: UploadQueueStatus;
  detail: string;
  speedText: string;
}

interface StorageTypeStat {
  label: string;
  count: number;
  sizeBytes: number;
}

const viewModes = new Set<ViewMode>(['home', 'computers', 'documents', 'images', 'videos', 'music', 'recent', 'starred', 'trash', 'storage']);

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
  readonly uploadQueue = signal<UploadQueueItem[]>([]);
  readonly uploadPanelCollapsed = signal(false);
  readonly uploadCancelRequested = signal(false);
  readonly uploadActive = signal(false);
  readonly dropDebug = signal('');
  readonly typeFilter = signal('all');
  readonly sortMode = signal('modified-desc');
  readonly usedPercent = computed(() => Math.min(100, Math.max(0, this.account.usedBytes * 100 / this.account.quotaBytes)));
  readonly uploadPanelTitle = computed(() => {
    const items = this.uploadQueue();
    if (this.uploadActive()) return `Przesyłam ${items.length} ${this.polishItems(items.length)}`;
    if (items.some((item) => item.status === 'cancelled')) return `Anulowano upload`;
    if (items.some((item) => item.status === 'error')) return `Upload zakończony z błędem`;
    return `Przesłano ${items.length} ${this.polishItems(items.length)}`;
  });
  readonly uploadPanelSubtitle = computed(() => {
    const items = this.uploadQueue();
    const uploading = items.find((item) => item.status === 'uploading');
    if (uploading) {
      return uploading.speedText ? `${uploading.detail} · ${uploading.speedText}` : uploading.detail;
    }

    const done = items.filter((item) => item.status === 'done').length;
    const failed = items.filter((item) => item.status === 'error').length;
    const cancelled = items.filter((item) => item.status === 'cancelled').length;
    if (failed) return `${done}/${items.length} przesłano, błędy: ${failed}`;
    if (cancelled) return `${done}/${items.length} przesłano, anulowano: ${cancelled}`;
    return items.length ? `${done}/${items.length} przesłano` : '';
  });
  readonly workspaceTitle = computed(() => workspaceTitle(this.currentView(), this.currentPath()));
  readonly visibleFiles = computed(() => this.computeVisibleFiles());
  readonly largestFiles = computed(() => this.files
    .filter((file) => !file.isFolder)
    .sort((a, b) => b.sizeBytes - a.sizeBytes)
    .slice(0, 12));
  readonly storageTypeStats = computed(() => this.computeStorageTypeStats());
  readonly activeFileCount = computed(() => this.files.filter((file) => !file.isFolder).length);
  readonly activeFolderCount = computed(() => this.files.filter((file) => file.isFolder).length);
  readonly trashFileCount = computed(() => this.trashFiles.filter((file) => !file.isFolder).length);
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
  private activeUploadController: AbortController | null = null;
  private activeUploadSessionId: string | null = null;

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
    const url = view === 'home'
      ? (path ? '/?path=' + encodeURIComponent(path) : '/')
      : '/?view=' + encodeURIComponent(view) + (path ? '&path=' + encodeURIComponent(path) : '');
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
      this.navigate(this.currentView(), item.name);
      return;
    }
    if (this.currentView() === 'trash') {
      return;
    }
    await this.download(item);
  }

  async uploadFiles(fileList: FileList | null) {
    if (!fileList?.length) return;
    this.dropDebug.set(`Picker debug: files=${fileList.length}, collected=${fileList.length}.`);
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
    const snapshot = this.snapshotDataTransfer(event.dataTransfer);
    const uploads = await this.collectDroppedUploads(snapshot);
    const droppedFileCount = snapshot.files.length;
    const droppedItemCount = snapshot.items.length;
    if (uploads.length <= 1 && (droppedFileCount > 1 || droppedItemCount > 1)) {
      this.message.set(`Drop zebrał ${uploads.length} plik. Debug: items=${droppedItemCount}, files=${droppedFileCount}.`);
    }
    await this.uploadItems(uploads);
  }

  @HostListener('document:dragover', ['$event'])
  onDocumentDragOver(event: DragEvent) {
    if (this.hasMovedPaths(event) || !this.hasDroppedFiles(event)) return;
    event.preventDefault();
    this.isDraggingFiles.set(true);
    if (event.dataTransfer) {
      event.dataTransfer.dropEffect = 'copy';
    }
  }

  @HostListener('document:drop', ['$event'])
  async onDocumentDrop(event: DragEvent) {
    if (this.hasMovedPaths(event) || !this.hasDroppedFiles(event)) return;
    event.preventDefault();
    event.stopPropagation();
    this.isDraggingFiles.set(false);
    const snapshot = this.snapshotDataTransfer(event.dataTransfer);
    const uploads = await this.collectDroppedUploads(snapshot);
    const droppedFileCount = snapshot.files.length;
    const droppedItemCount = snapshot.items.length;
    const types = snapshot.types.join(', ') || 'none';
    const debug = `Drop debug: items=${droppedItemCount}, files=${droppedFileCount}, collected=${uploads.length}, types=${types}.`;
    this.dropDebug.set(debug);
    console.log(debug, uploads.map((upload) => upload.relativePath));
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
    const uploadItems = uploads.map((upload, index) => {
      const plan = this.crypto.getChunkedUploadPlan(upload.file.size);
      return {
        id: `${Date.now()}-${index}-${upload.file.lastModified}`,
        name: upload.relativePath,
        sizeBytes: upload.file.size,
        encryptedBytes: plan.totalEncryptedBytes,
        percent: 0,
        status: 'checking' as UploadQueueStatus,
        detail: 'Sprawdzam miejsce',
        speedText: '',
      };
    });
    const totalEncryptedBytes = uploadItems.reduce((total, item) => total + item.encryptedBytes, 0);
    const remainingBytes = Math.max(0, this.account.quotaBytes - this.account.usedBytes);
    this.uploadQueue.set(uploadItems);
    this.uploadPanelCollapsed.set(false);
    this.uploadCancelRequested.set(false);
    this.uploadActive.set(false);
    this.uploadProgress.set(0);

    if (totalEncryptedBytes > remainingBytes) {
      this.uploadQueue.update((items) => items.map((item) => ({
        ...item,
        status: 'error',
        detail: `Brakuje ${formatBytes(totalEncryptedBytes - remainingBytes)}`,
      })));
      this.message.set(`Za mało miejsca. Potrzeba ${formatBytes(totalEncryptedBytes)}, wolne ${formatBytes(remainingBytes)}.`);
      return;
    }

    this.message.set('Szyfrowanie uploadu...');
    this.uploadQueue.update((items) => items.map((item) => ({
      ...item,
      status: 'pending',
      detail: 'W kolejce',
    })));
    this.uploadActive.set(true);
    let uploadedFiles = 0;
    const failedUploads: string[] = [];
    const progress: UploadProgressState = {
      completedChunks: 0,
      totalChunks: uploads.reduce((total, upload) => total + Math.max(1, this.crypto.getChunkedUploadPlan(upload.file.size).totalChunks), 0),
    };

    for (let index = 0; index < uploads.length; index += 1) {
      if (this.uploadCancelRequested()) {
        this.markPendingUploadsCancelled(index);
        break;
      }

      const upload = uploads[index];
      const queueItem = uploadItems[index];
      const vaultPath = normalizePath(this.currentPath() ? `${this.currentPath()}/${upload.relativePath}` : upload.relativePath);
      this.updateUploadQueueItem(queueItem.id, { status: 'uploading', detail: 'Szyfrowanie', percent: 0, speedText: '' });
      const encryptedPath = await this.crypto.encryptVirtualPath(vaultPath);
      this.message.set(`Szyfrowanie ${index + 1}/${uploads.length}: ${upload.relativePath}`);
      try {
        await this.uploadFileInChunks(upload.file, encryptedPath, index, uploads.length, upload.relativePath, progress, queueItem.id);
        this.updateUploadQueueItem(queueItem.id, { status: 'done', detail: 'Przesłano', percent: 100, speedText: '' });
        uploadedFiles += 1;
      } catch (error) {
        if (this.isAbortError(error) || this.uploadCancelRequested()) {
          this.updateUploadQueueItem(queueItem.id, { status: 'cancelled', detail: 'Anulowano', speedText: '' });
          this.markPendingUploadsCancelled(index + 1);
          break;
        }

        if (this.activeUploadSessionId) {
          await this.api.cancelChunkUpload(this.activeUploadSessionId).catch(() => null);
          this.activeUploadSessionId = null;
        }
        this.activeUploadController = null;

        failedUploads.push(upload.relativePath);
        this.updateUploadQueueItem(queueItem.id, { status: 'error', detail: 'Błąd uploadu', speedText: '' });
        this.updateUploadProgressForFailedFile(progress, upload.file);
      }
    }

    this.uploadActive.set(false);
    this.activeUploadController = null;
    this.activeUploadSessionId = null;
    this.uploadProgress.set(100);
    this.message.set(this.uploadCancelRequested()
      ? `Upload anulowany. Przesłano ${uploadedFiles}/${uploads.length}.`
      : failedUploads.length
      ? `Upload zakończony: ${uploadedFiles}/${uploads.length}. Nie przesłano: ${failedUploads.slice(0, 3).join(', ')}${failedUploads.length > 3 ? '...' : ''}`
      : 'Upload zakończony.');
    this.refreshRequested.emit();
  }

  async cancelUpload() {
    this.uploadCancelRequested.set(true);
    this.message.set('Anuluję upload...');
    this.activeUploadController?.abort();

    const uploadId = this.activeUploadSessionId;
    if (uploadId) {
      await this.api.cancelChunkUpload(uploadId).catch(() => null);
    }
  }

  clearUploadQueue() {
    if (this.uploadActive()) return;
    this.uploadQueue.set([]);
    this.message.set('');
    this.uploadProgress.set(0);
    this.uploadCancelRequested.set(false);
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
    const items = this.selectedZipItems();
    if (!items.length) {
      this.message.set('Nie znaleziono zaznaczonych plików.');
      return;
    }

    const response = await this.api.postForm('/files/download-zip', {
      paths: items.map((item) => item.pathHash).join('\n'),
      zipNames: items.map((item) => item.name).join('\n'),
    });
    if (!response.ok) {
      this.message.set('Nie udało się przygotować ZIP.');
      return;
    }

    saveBlob(await response.blob(), 'skyvault-selection.zip');
    this.message.set('Pobrano ZIP.');
  }

  async moveSelectedToTrash() {
    if (!this.selectedPaths().length) return;
    const selectors = this.selectedBackendSelectors('home');
    if (!selectors.length) {
      this.message.set('Nie znaleziono zaznaczonych elementów.');
      return;
    }
    const response = await this.api.postForm('/files/trash', { paths: selectors.join('\n'), currentPath: this.currentPath() });
    await this.setMessageFromJsonResponse(response, 'Przeniesiono do kosza.');
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
    const selectors = this.selectedBackendSelectors('trash');
    if (!selectors.length) {
      this.message.set('Nie znaleziono zaznaczonych elementów w koszu.');
      return;
    }
    const response = await this.api.postForm('/files/trash/restore', { paths: selectors.join('\n'), currentPath: this.currentPath() });
    await this.setMessageFromJsonResponse(response, 'Przywrócono z kosza.');
    this.selectedPaths.set([]);
    this.refreshRequested.emit();
  }

  async deleteSelectedForever() {
    if (this.currentView() !== 'trash' || !this.selectedPaths().length) return;
    if (!(await this.showConfirm('Usunąć zaznaczone elementy na zawsze?'))) return;
    const selectors = this.selectedBackendSelectors('trash');
    if (!selectors.length) {
      this.message.set('Nie znaleziono zaznaczonych elementów w koszu.');
      return;
    }
    const response = await this.api.postForm('/files/trash/delete', { paths: selectors.join('\n'), currentPath: this.currentPath() });
    await this.setMessageFromJsonResponse(response, 'Usunięto na zawsze.');
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

    const itemMenuHeight = this.currentView() === 'trash' ? 136 : 366;
    const blankMenuHeight = this.currentView() === 'trash' ? 54 : 136;
    const menuWidth = 232;
    const menuHeight = event.file ? itemMenuHeight : blankMenuHeight;
    this.contextMenuX.set(Math.max(8, Math.min(event.x, window.innerWidth - menuWidth - 8)));
    this.contextMenuY.set(Math.max(8, Math.min(event.y, window.innerHeight - menuHeight - 8)));
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

  private getAffectedItemsForPath(item: FileItem, source = this.files) {
    if (!item.isFolder) return [item];
    const prefix = item.name + '/';
    const byName = new Map<string, FileItem>();
    for (const candidate of source) {
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

    if (view === 'home' || view === 'trash') {
      items = source.filter((item) => isDirectChild(item.name, this.currentPath()));
    } else if (view === 'documents') {
      items = source.filter((item) => this.fileType(item) === 'documents');
    } else if (view === 'images') {
      items = source.filter((item) => this.fileType(item) === 'images');
    } else if (view === 'videos') {
      items = source.filter((item) => this.fileType(item) === 'videos');
    } else if (view === 'music') {
      items = source.filter((item) => this.fileType(item) === 'music');
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

  private computeStorageTypeStats(): StorageTypeStat[] {
    const stats = new Map<string, StorageTypeStat>([
      ['documents', { label: 'Dokumenty', count: 0, sizeBytes: 0 }],
      ['images', { label: 'Obrazy', count: 0, sizeBytes: 0 }],
      ['videos', { label: 'Filmy', count: 0, sizeBytes: 0 }],
      ['music', { label: 'Muzyka', count: 0, sizeBytes: 0 }],
      ['other', { label: 'Inne', count: 0, sizeBytes: 0 }],
    ]);

    for (const file of this.files) {
      if (file.isFolder) continue;
      const stat = stats.get(this.fileType(file)) ?? stats.get('other');
      if (!stat) continue;
      stat.count += 1;
      stat.sizeBytes += file.sizeBytes;
    }

    return [...stats.values()];
  }

  private fileType(file: FileItem) {
    if (file.isFolder) return 'folder';
    const name = displayName(file.name);
    if (/\.(pdf|txt|md|doc|docx|xls|xlsx|ppt|pptx|json|xml|html|css|js|ts|cs|py)$/i.test(name)) return 'documents';
    if (/\.(png|jpe?g|gif|bmp|svg|webp|avif)$/i.test(name)) return 'images';
    if (/\.(mp4|webm|mkv|mov|avi|ogg)$/i.test(name)) return 'videos';
    if (/\.(mp3|wav|flac|m4a|aac|opus)$/i.test(name)) return 'music';
    return 'other';
  }

  private hasDroppedFiles(event: DragEvent) {
    const transfer = event.dataTransfer;
    if (!transfer) return false;
    const types = Array.from(transfer.types ?? []);
    if (types.includes('Files') || transfer.files.length > 0) return true;
    return Array.from(transfer.items ?? []).some((item) => item.kind === 'file');
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
    queueItemId?: string,
  ) {
    const plan = this.crypto.getChunkedUploadPlan(file.size);
    const controller = new AbortController();
    this.activeUploadController = controller;
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
    }, controller.signal);
    this.activeUploadSessionId = session.uploadId;
    const uploaded = new Set(session.uploadedChunks);
    let sentBytes = 0;
    let lastSpeedBytes = 0;
    let lastSpeedAt = performance.now();

    for (let chunkIndex = 0; chunkIndex < session.totalChunks; chunkIndex += 1) {
      if (this.uploadCancelRequested()) {
        controller.abort();
        throw new DOMException('Upload cancelled.', 'AbortError');
      }

      if (uploaded.has(chunkIndex)) {
        this.updateUploadProgress(progress, label, fileIndex, totalFiles, chunkIndex, session.totalChunks);
        this.updateUploadQueueProgress(queueItemId, chunkIndex + 1, session.totalChunks, 'Wznowiono', '');
        continue;
      }

      const bounds = this.getPlainChunkBounds(file.size, plan, chunkIndex);
      const plaintextChunk = file.slice(bounds.start, bounds.end);
      const encryptedChunk = await this.crypto.encryptVaultFileChunk(plaintextChunk, chunkIndex === 0);
      session = await this.api.uploadChunk(session.uploadId, chunkIndex, encryptedChunk, controller.signal);
      sentBytes += encryptedChunk.byteLength;
      const now = performance.now();
      const elapsedSeconds = Math.max(0.001, (now - lastSpeedAt) / 1000);
      const speedBytes = (sentBytes - lastSpeedBytes) / elapsedSeconds;
      lastSpeedAt = now;
      lastSpeedBytes = sentBytes;
      this.updateUploadProgress(progress, label, fileIndex, totalFiles, chunkIndex, session.totalChunks);
      this.updateUploadQueueProgress(queueItemId, chunkIndex + 1, session.totalChunks, 'Wysyłam', `${formatBytes(speedBytes)}/s`);
    }

    if (this.uploadCancelRequested()) {
      controller.abort();
      throw new DOMException('Upload cancelled.', 'AbortError');
    }

    await this.api.completeChunkUpload(session.uploadId, controller.signal);
    this.activeUploadSessionId = null;
    this.activeUploadController = null;

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

  private snapshotDataTransfer(dataTransfer: DataTransfer | null): DropSnapshot {
    return {
      items: Array.from(dataTransfer?.items ?? []),
      files: Array.from(dataTransfer?.files ?? []),
      types: Array.from(dataTransfer?.types ?? []),
    };
  }

  private async collectDroppedUploads(snapshot: DropSnapshot) {
    const uploads: PendingUpload[] = [];
    const seen = new Set<string>();
    const addUpload = (file: File | null, relativePath = '') => {
      if (!file) return;
      const upload = {
        file,
        relativePath: normalizePath(relativePath || this.getUploadRelativePath(file)) || file.name,
      };
      const identity = this.uploadIdentity(upload);
      if (seen.has(identity)) return;
      seen.add(identity);
      uploads.push(upload);
    };

    await this.collectDataTransferItems(snapshot.items, addUpload);

    for (const file of snapshot.files) {
      addUpload(file, this.getUploadRelativePath(file));
    }

    return uploads.length ? uploads : snapshot.files.map((file) => ({
      file,
      relativePath: this.getUploadRelativePath(file),
    }));
  }

  private uploadIdentity(upload: PendingUpload) {
    return `${upload.relativePath}\0${upload.file.size}\0${upload.file.lastModified}`;
  }

  private async collectDataTransferItems(items: DataTransferItem[], addUpload: (file: File | null, relativePath?: string) => void) {
    const canUseEntries = items.some((item) => this.hasEntry(item));

    for (const item of items) {
      if (item.kind !== 'file') {
        continue;
      }

      try {
        if (canUseEntries) {
          const entry = this.getEntry(item);
          if (entry) {
            await this.collectEntryFiles(entry, '', addUpload);
          }
          continue;
        }

        const handle = await this.getFileSystemHandle(item);
        if (handle) {
          await this.collectHandleFiles(handle, '', addUpload);
          continue;
        }

        addUpload(item.getAsFile());
      } catch {
      }
    }
  }

  private updateUploadProgressForFailedFile(progress: UploadProgressState | undefined, file: File) {
    if (!progress) return;
    const chunks = Math.max(1, this.crypto.getChunkedUploadPlan(file.size).totalChunks);
    progress.completedChunks = Math.min(progress.totalChunks, progress.completedChunks + chunks);
    this.uploadProgress.set(Math.round(progress.completedChunks * 100 / Math.max(1, progress.totalChunks)));
  }

  private selectedBackendSelectors(view: 'home' | 'trash') {
    const selected = this.selectedPaths();
    const source = view === 'trash' ? this.trashFiles : this.files;
    const selectors = new Set<string>();

    for (const selectedPath of selected) {
      const item = source.find((file) => file.name === selectedPath);
      if (!item) continue;

      if (item.pathHash) {
        selectors.add(item.pathHash);
      }

      if (item.isFolder) {
        for (const affectedItem of this.getAffectedItemsForPath(item, source)) {
          if (affectedItem.pathHash) {
            selectors.add(affectedItem.pathHash);
          }
        }
      }
    }

    return [...selectors];
  }

  private selectedZipItems() {
    const selected = this.selectedPaths();
    const items = new Map<string, { pathHash: string; name: string }>();

    for (const selectedPath of selected) {
      const item = this.files.find((file) => file.name === selectedPath);
      if (!item) continue;

      for (const affectedItem of this.getAffectedItemsForPath(item)) {
        if (affectedItem.isFolder || !affectedItem.pathHash) continue;
        items.set(affectedItem.pathHash, {
          pathHash: affectedItem.pathHash,
          name: affectedItem.name,
        });
      }
    }

    return [...items.values()];
  }

  private async setMessageFromJsonResponse(response: Response, fallback: string) {
    const payload = await response.json().catch(() => ({}));
    this.message.set(typeof payload.message === 'string' ? payload.message : fallback);
  }

  private updateUploadQueueProgress(queueItemId: string | undefined, completedChunks: number, totalChunks: number, detail: string, speedText: string) {
    if (!queueItemId) return;
    this.updateUploadQueueItem(queueItemId, {
      percent: Math.min(100, Math.round(completedChunks * 100 / Math.max(1, totalChunks))),
      detail: `${detail} ${completedChunks}/${totalChunks}`,
      speedText,
    });
  }

  private updateUploadQueueItem(id: string, patch: Partial<UploadQueueItem>) {
    this.uploadQueue.update((items) => items.map((item) => item.id === id ? { ...item, ...patch } : item));
  }

  private markPendingUploadsCancelled(startIndex: number) {
    this.uploadQueue.update((items) => items.map((item, index) => index >= startIndex && item.status === 'pending'
      ? { ...item, status: 'cancelled', detail: 'Anulowano', speedText: '' }
      : item));
  }

  private isAbortError(error: unknown) {
    return error instanceof DOMException && error.name === 'AbortError';
  }

  private polishItems(count: number) {
    return count === 1 ? 'element' : count >= 2 && count <= 4 ? 'elementy' : 'elementów';
  }

  private hasEntry(item: DataTransferItem) {
    const withEntry = item as DataTransferItem & { webkitGetAsEntry?: () => unknown };
    return typeof withEntry.webkitGetAsEntry === 'function';
  }

  private getEntry(item: DataTransferItem) {
    const withEntry = item as DataTransferItem & { webkitGetAsEntry?: () => unknown };
    return typeof withEntry.webkitGetAsEntry === 'function' ? withEntry.webkitGetAsEntry() : null;
  }

  private async getFileSystemHandle(item: DataTransferItem) {
    const withHandle = item as DataTransferItem & { getAsFileSystemHandle?: () => Promise<unknown> };
    return typeof withHandle.getAsFileSystemHandle === 'function' ? await withHandle.getAsFileSystemHandle() : null;
  }

  private async collectHandleFiles(handle: unknown, parentPath: string, addUpload: (file: File | null, relativePath?: string) => void): Promise<void> {
    const fileHandle = handle as {
      name: string;
      kind?: 'file' | 'directory';
      getFile?: () => Promise<File>;
      values?: () => AsyncIterable<unknown>;
    };
    const handlePath = normalizePath(parentPath ? `${parentPath}/${fileHandle.name}` : fileHandle.name);

    if (fileHandle.kind === 'file' && fileHandle.getFile) {
      const file = await fileHandle.getFile();
      addUpload(file, handlePath || file.name);
      return;
    }

    if (fileHandle.kind !== 'directory' || !fileHandle.values) {
      return;
    }

    for await (const child of fileHandle.values()) {
      await this.collectHandleFiles(child, handlePath, addUpload);
    }
  }

  private async collectEntryFiles(entry: unknown, parentPath: string, addUpload: (file: File | null, relativePath?: string) => void): Promise<void> {
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
      addUpload(file, entryPath || file.name);
      return;
    }

    if (!fileEntry.isDirectory || !fileEntry.createReader) {
      return;
    }

    const reader = fileEntry.createReader();
    const children: unknown[] = [];

    while (true) {
      const batch = await new Promise<unknown[]>((resolve, reject) => reader.readEntries(resolve, reject));
      if (!batch.length) break;
      children.push(...batch);
    }

    for (const child of children) {
      await this.collectEntryFiles(child, entryPath, addUpload);
    }
  }
}
