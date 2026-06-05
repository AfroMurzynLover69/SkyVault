export type AuthMode = 'login' | 'register';
export type ViewMode = 'home' | 'computers' | 'documents' | 'images' | 'videos' | 'music' | 'recent' | 'starred' | 'trash' | 'storage';

export interface FileItem {
  name: string;
  encryptedName?: string;
  nameIv?: string;
  pathHash?: string;
  sizeBytes: number;
  modifiedAt: string;
  isFolder: boolean;
}

export interface AccountState {
  username: string;
  quotaBytes: number;
  usedBytes: number;
  createdAt: string;
}

export interface DeviceSessionState {
  sessionId: string;
  ipAddress: string;
  deviceName: string;
  systemName: string;
  startedAt: string;
  lastSeenAt: string;
  uploadedFiles: number;
  deletedFiles: number;
  copiedOrMovedFiles: number;
  durationSeconds: number;
}

export interface AppState {
  authenticated: boolean;
  account?: AccountState;
  files?: FileItem[];
  trashFiles?: FileItem[];
  deviceSessions?: DeviceSessionState[];
  starredPaths?: string[];
}

export interface DerivedKeys {
  authVerifier: Uint8Array;
  kek: Uint8Array;
}

export interface WrappedKey {
  ciphertext: Uint8Array;
  iv: Uint8Array;
}
