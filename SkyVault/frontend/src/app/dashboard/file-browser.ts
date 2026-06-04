import { CommonModule } from '@angular/common';
import { Component, ElementRef, EventEmitter, HostListener, Input, Output } from '@angular/core';
import { FileItem } from '../core/models';
import { displayName, formatBytes, iconFor } from '../core/path-utils';

@Component({
  selector: 'app-file-browser',
  imports: [CommonModule],
  templateUrl: './file-browser.html',
  styleUrl: './file-browser.css',
})
export class FileBrowser {
  @Input({ required: true }) files: FileItem[] = [];
  @Input() selectedPaths: string[] = [];
  @Input() allowOpen = true;
  @Input() viewMode = 'list';
  @Output() open = new EventEmitter<FileItem>();
  @Output() toggleSelect = new EventEmitter<string>();
  @Output() selectionChange = new EventEmitter<string[]>();
  @Output() contextMenu = new EventEmitter<{ file: FileItem; x: number; y: number }>();
  @Output() moveToFolder = new EventEmitter<{ paths: string[]; targetPath: string }>();

  displayName = displayName;
  formatBytes = formatBytes;
  iconFor = iconFor;
  selectionBox: { left: number; top: number; width: number; height: number } | null = null;
  private selectionStart: { x: number; y: number } | null = null;
  private selectionBase = new Set<string>();
  private hasDraggedSelection = false;

  constructor(private readonly elementRef: ElementRef<HTMLElement>) {}

  isSelected(path: string) {
    return this.selectedPaths.includes(path);
  }

  entryKind(file: FileItem) {
    return file.isFolder ? 'folder' : 'file';
  }

  sortModified(file: FileItem) {
    return Math.floor(new Date(file.modifiedAt).getTime() / 1000);
  }

  onSurfaceMouseDown(event: MouseEvent) {
    if (event.button !== 0 || this.isInteractiveTarget(event.target)) {
      return;
    }

    this.selectionStart = { x: event.clientX, y: event.clientY };
    this.selectionBase = event.ctrlKey || event.metaKey ? new Set(this.selectedPaths) : new Set<string>();
    this.hasDraggedSelection = false;
  }

  onItemClick(event: MouseEvent, path: string) {
    if (this.hasDraggedSelection) {
      event.preventDefault();
      return;
    }

    this.toggleSelect.emit(path);
  }

  @HostListener('document:mousemove', ['$event'])
  onDocumentMouseMove(event: MouseEvent) {
    if (!this.selectionStart) {
      return;
    }

    const deltaX = event.clientX - this.selectionStart.x;
    const deltaY = event.clientY - this.selectionStart.y;

    if (!this.hasDraggedSelection && Math.hypot(deltaX, deltaY) < 4) {
      return;
    }

    event.preventDefault();
    this.hasDraggedSelection = true;
    const rect = this.normalizeRect(this.selectionStart.x, this.selectionStart.y, event.clientX, event.clientY);
    this.selectionBox = rect;
    this.selectionChange.emit(this.pathsIntersecting(rect));
  }

  @HostListener('document:mouseup')
  onDocumentMouseUp() {
    this.selectionStart = null;
    this.selectionBox = null;
  }

  onContextMenu(event: MouseEvent, file: FileItem) {
    event.preventDefault();
    event.stopPropagation();
    this.contextMenu.emit({ file, x: event.clientX, y: event.clientY });
  }

  onSurfaceContextMenu(event: MouseEvent) {
    if (this.isInteractiveTarget(event.target)) {
      return;
    }
    event.preventDefault();
    this.contextMenu.emit({ file: null as any, x: event.clientX, y: event.clientY });
  }

  onDragStart(event: DragEvent, file: FileItem) {
    this.selectionStart = null;
    this.selectionBox = null;
    const paths = this.selectedPaths.includes(file.name) ? this.selectedPaths : [file.name];
    event.dataTransfer?.setData('application/x-skyvault-paths', JSON.stringify(paths));
    if (event.dataTransfer) {
      event.dataTransfer.effectAllowed = 'move';
      const preview = this.createDragPreview(paths);
      document.body.append(preview);
      event.dataTransfer.setDragImage(preview, 22, 22);
      window.setTimeout(() => preview.remove(), 0);
    }
  }

  onDragOverFolder(event: DragEvent, file: FileItem) {
    if (!file.isFolder || !event.dataTransfer?.types.includes('application/x-skyvault-paths')) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    event.dataTransfer.dropEffect = 'move';
  }

  onDropOnFolder(event: DragEvent, file: FileItem) {
    if (!file.isFolder) {
      return;
    }

    const raw = event.dataTransfer?.getData('application/x-skyvault-paths');
    if (!raw) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();

    try {
      const paths = JSON.parse(raw) as string[];
      if (Array.isArray(paths) && paths.length) {
        this.moveToFolder.emit({ paths, targetPath: file.name });
      }
    } catch {
    }
  }

  private pathsIntersecting(selectionRect: DOMRectLike) {
    const selected = new Set(this.selectionBase);
    const entries = this.elementRef.nativeElement.querySelectorAll<HTMLElement>('tr[data-file-path], .file-card[data-file-path]');

    entries.forEach((entry) => {
      if (this.rectsIntersect(selectionRect, entry.getBoundingClientRect())) {
        const path = entry.dataset['filePath'];
        if (path) {
          selected.add(path);
        }
      }
    });

    return [...selected];
  }

  private normalizeRect(startX: number, startY: number, endX: number, endY: number) {
    const left = Math.min(startX, endX);
    const top = Math.min(startY, endY);
    return {
      left,
      top,
      width: Math.abs(endX - startX),
      height: Math.abs(endY - startY),
      right: left + Math.abs(endX - startX),
      bottom: top + Math.abs(endY - startY),
    };
  }

  private rectsIntersect(a: DOMRectLike, b: DOMRect) {
    return a.left <= b.right && a.right >= b.left && a.top <= b.bottom && a.bottom >= b.top;
  }

  private isInteractiveTarget(target: EventTarget | null) {
    return target instanceof HTMLElement && Boolean(target.closest('button, a, input, select, textarea'));
  }

  private createDragPreview(paths: string[]) {
    const preview = document.createElement('div');
    preview.className = 'move-drag-preview';
    preview.setAttribute('aria-hidden', 'true');

    const stack = document.createElement('div');
    stack.className = 'move-drag-stack';
    preview.append(stack);

    const visibleCards = Math.min(3, Math.max(1, paths.length));
    for (let index = 0; index < visibleCards; index += 1) {
      const card = document.createElement('div');
      card.className = 'move-drag-card';

      const path = paths[index] ?? paths[0];
      const file = this.files.find((candidate) => candidate.name === path);
      const icon = document.createElement('img');
      icon.className = file?.isFolder ? 'move-drag-folder-icon' : 'move-drag-file-icon';
      icon.src = file ? iconFor(file) : '/assets/icons/application-octet-stream.svg';
      icon.alt = '';
      card.append(icon);
      stack.append(card);
    }

    const count = document.createElement('span');
    count.className = 'move-drag-count';
    count.textContent = String(paths.length);
    stack.append(count);

    return preview;
  }
}

interface DOMRectLike {
  left: number;
  top: number;
  right: number;
  bottom: number;
}
