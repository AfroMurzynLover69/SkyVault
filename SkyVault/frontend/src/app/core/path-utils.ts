import { FileItem, ViewMode } from './models';

export function normalizePath(path: string) {
  return String(path || '')
    .replaceAll('\\', '/')
    .split('/')
    .map((part) => part.trim())
    .filter((part) => part && part !== '.' && part !== '..' && !part.includes('..'))
    .join('/');
}

export function displayName(path: string) {
  const normalized = normalizePath(path);
  const index = normalized.lastIndexOf('/');
  return index < 0 ? normalized : normalized.slice(index + 1);
}

export function parentPath(path: string) {
  const normalized = normalizePath(path);
  const index = normalized.lastIndexOf('/');
  return index < 0 ? '' : normalized.slice(0, index);
}

export function isDirectChild(path: string, directory: string) {
  const normalized = normalizePath(path);
  const current = normalizePath(directory);
  if (!current) return !normalized.includes('/');
  if (!normalized.startsWith(current + '/')) return false;
  return !normalized.slice(current.length + 1).includes('/');
}

export function formatBytes(bytes: number) {
  if (bytes < 1024) return bytes + ' B';
  const units = ['KB', 'MB', 'GB', 'TB'];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }
  return value.toFixed(value >= 10 ? 0 : 1) + ' ' + units[unit];
}

export function iconFor(file: FileItem) {
  if (file.isFolder) return '/assets/icons/folder.svg';
  const ext = displayName(file.name).split('.').pop()?.toLowerCase() ?? '';
  if (['png', 'jpg', 'jpeg', 'gif', 'bmp', 'svg'].includes(ext)) return '/assets/icons/image-x-generic.svg';
  if (['mp4', 'webm', 'mkv', 'ogg'].includes(ext)) return '/assets/icons/video-mp4.svg';
  if (['mp3', 'wav', 'flac'].includes(ext)) return '/assets/icons/audio-x-generic.svg';
  if (ext === 'pdf') return '/assets/icons/application-pdf.svg';
  if (['zip', '7z', 'rar', 'gz', 'tar'].includes(ext)) return '/assets/icons/application-zip.svg';
  if (['txt', 'md', 'html', 'css', 'js', 'json', 'xml', 'cs', 'py'].includes(ext)) return '/assets/icons/text-plain.svg';
  return '/assets/icons/application-octet-stream.svg';
}

export function workspaceTitle(view: ViewMode, currentPath: string) {
  if (view === 'trash') return 'Kosz';
  if (view === 'computers') return 'Komputery';
  if (view === 'documents') return 'Dokumenty';
  if (view === 'images') return 'Obrazy';
  if (view === 'videos') return 'Filmy';
  if (view === 'music') return 'Muzyka';
  if (view === 'recent') return 'Ostatnie';
  if (view === 'starred') return 'Oznaczone gwiazdką';
  return currentPath ? displayName(currentPath) : 'Mój dysk';
}
