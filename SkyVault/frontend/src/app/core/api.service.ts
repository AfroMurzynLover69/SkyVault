import { Injectable } from '@angular/core';
import { AppState } from './models';

export interface ChunkUploadSessionState {
  uploadId: string;
  filename: string;
  fileSize: number;
  currentPath: string;
  relativePath: string;
  chunkSize: number;
  totalChunks: number;
  uploadedChunks: number[];
  status: string;
  savedPath?: string;
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  async getState() {
    const response = await fetch('/api/state', { cache: 'no-store' });
    return await response.json() as AppState;
  }

  async getCryptoMaterial(email: string) {
    const response = await fetch('/api/auth/crypto?email=' + encodeURIComponent(email), { cache: 'no-store' });
    if (!response.ok) throw new Error('Wrong email or password.');
    return await response.json() as {
      passwordSalt: string;
      wrappedMasterKey: string;
      wrappedMasterKeyIv: string;
    };
  }

  async postForm(url: string, fields: Record<string, string>, followRedirect = false, signal?: AbortSignal) {
    return await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams(fields).toString(),
      redirect: followRedirect ? 'follow' : 'manual',
      signal,
    });
  }

  async postJsonForm<T>(url: string, fields: Record<string, string>, signal?: AbortSignal) {
    const response = await this.postForm(url, fields, false, signal);
    const payload = await response.json().catch(() => ({}));

    if (!response.ok) {
      throw new Error(typeof payload.error === 'string' ? payload.error : 'Request failed.');
    }

    return payload as T;
  }

  async uploadEncryptedFile(file: File, metadata: { encryptedVirtualPath: string; virtualPathIv: string; pathHash: string }) {
    const data = new FormData();
    data.set('currentPath', '');
    data.set('encryptedVirtualPath', metadata.encryptedVirtualPath);
    data.set('virtualPathIv', metadata.virtualPathIv);
    data.set('pathHash', metadata.pathHash);
    data.set('uploadSource', 'angular');
    data.set('file', file);
    return await fetch('/files/upload', { method: 'POST', body: data });
  }

  async startChunkUpload(fields: Record<string, string>, signal?: AbortSignal) {
    return await this.postJsonForm<ChunkUploadSessionState>('/api/uploads/start', fields, signal);
  }

  async uploadChunk(uploadId: string, chunkIndex: number, bytes: Uint8Array, signal?: AbortSignal) {
    const copy = new Uint8Array(bytes.byteLength);
    copy.set(bytes);
    const body = new Blob([copy.buffer], { type: 'application/octet-stream' });
    const response = await fetch(`/api/uploads/${encodeURIComponent(uploadId)}/chunks/${chunkIndex}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/octet-stream' },
      body,
      signal,
    });

    const payload = await response.json().catch(() => ({}));
    if (!response.ok) {
      throw new Error(typeof payload.error === 'string' ? payload.error : 'Chunk upload failed.');
    }

    return payload as ChunkUploadSessionState;
  }

  async completeChunkUpload(uploadId: string, signal?: AbortSignal) {
    return await this.postJsonForm<{ uploadId: string; status: string; savedPath: string }>(
      `/api/uploads/${encodeURIComponent(uploadId)}/complete`,
      {},
      signal,
    );
  }

  async cancelChunkUpload(uploadId: string) {
    return await this.postJsonForm<{ uploadId: string; status: string }>(
      `/api/uploads/${encodeURIComponent(uploadId)}/cancel`,
      {},
    );
  }

  async updateEncryptedPaths(updates: {
    sourcePathHash: string;
    encryptedVirtualPath: string;
    virtualPathIv: string;
    newPathHash: string;
  }[]) {
    return await this.postJsonForm<{ updated: number; conflicts: number; missing: number }>('/api/files/update-paths', {
      sourcePathHashes: updates.map((update) => update.sourcePathHash).join('\n'),
      encryptedVirtualPaths: updates.map((update) => update.encryptedVirtualPath).join('\n'),
      virtualPathIvs: updates.map((update) => update.virtualPathIv).join('\n'),
      newPathHashes: updates.map((update) => update.newPathHash).join('\n'),
    });
  }

  async copyEncryptedPaths(updates: {
    sourcePathHash: string;
    encryptedVirtualPath: string;
    virtualPathIv: string;
    newPathHash: string;
  }[]) {
    return await this.postJsonForm<{ copied: number; conflicts: number; missing: number }>('/api/files/copy-paths', {
      sourcePathHashes: updates.map((update) => update.sourcePathHash).join('\n'),
      encryptedVirtualPaths: updates.map((update) => update.encryptedVirtualPath).join('\n'),
      virtualPathIvs: updates.map((update) => update.virtualPathIv).join('\n'),
      newPathHashes: updates.map((update) => update.newPathHash).join('\n'),
    });
  }

  async downloadFile(file: { name: string; pathHash?: string }) {
    const query = file.pathHash
      ? 'pathHash=' + encodeURIComponent(file.pathHash)
      : 'path=' + encodeURIComponent(file.name);
    return await fetch('/files/download?' + query, { cache: 'no-store' });
  }

  async logout() {
    await fetch('/logout', { method: 'POST' });
  }
}
