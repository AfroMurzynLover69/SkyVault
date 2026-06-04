import { Injectable } from '@angular/core';
import sodium from 'libsodium-wrappers-sumo';
import { DerivedKeys, WrappedKey } from './models';
import { cryptoBytes, fromBase64, saveBlob, toBase64 } from './encoding';

@Injectable({ providedIn: 'root' })
export class CryptoService {
  private readonly encoder = new TextEncoder();
  private readonly decoder = new TextDecoder();
  private readonly encryptedBlobMagic = 'SKYVAULT-AESGCM-v1\n';
  private readonly encryptedBlobChunkedMagic = 'SKYVAULT-AESGCM-v2\n';
  private readonly argon2idOutputBytes = 32;
  private readonly argon2idMemLimitBytes = 256 * 1024 * 1024;
  private readonly uploadChunkTransportBytes = 7 * 1024 * 1024;
  private readonly chunkFrameOverheadBytes = 4 + 12 + 16;

  async deriveKeys(password: string, salt: Uint8Array): Promise<DerivedKeys> {
    await sodium.ready;
    const seed = sodium.crypto_pwhash(
      this.argon2idOutputBytes,
      password,
      salt,
      sodium.crypto_pwhash_OPSLIMIT_INTERACTIVE,
      this.argon2idMemLimitBytes,
      sodium.crypto_pwhash_ALG_ARGON2ID13,
    );

    return {
      authVerifier: await this.hkdf(seed, 'SkyVault AuthVerifier v1'),
      kek: await this.hkdf(seed, 'SkyVault MasterKey KEK v1'),
    };
  }

  async wrapMasterKey(kek: Uint8Array, masterKey: Uint8Array): Promise<WrappedKey> {
    const iv = crypto.getRandomValues(new Uint8Array(12));
    const key = await crypto.subtle.importKey('raw', cryptoBytes(kek), 'AES-GCM', false, ['encrypt']);
    const ciphertext = new Uint8Array(await crypto.subtle.encrypt({ name: 'AES-GCM', iv: cryptoBytes(iv) }, key, cryptoBytes(masterKey)));
    return { ciphertext, iv };
  }

  async unwrapMasterKey(kek: Uint8Array, wrappedMasterKey: Uint8Array, iv: Uint8Array) {
    const key = await crypto.subtle.importKey('raw', cryptoBytes(kek), 'AES-GCM', false, ['decrypt']);
    return new Uint8Array(await crypto.subtle.decrypt({ name: 'AES-GCM', iv: cryptoBytes(iv) }, key, cryptoBytes(wrappedMasterKey)));
  }

  async encryptVaultFile(file: File) {
    const key = await this.getVaultCryptoKey(['encrypt']);
    const iv = crypto.getRandomValues(new Uint8Array(12));
    const plaintext = await file.arrayBuffer();
    const ciphertext = new Uint8Array(await crypto.subtle.encrypt({ name: 'AES-GCM', iv: cryptoBytes(iv) }, key, plaintext));
    const header = this.encoder.encode(this.encryptedBlobMagic);
    const encrypted = new Uint8Array(header.length + iv.length + ciphertext.length);
    encrypted.set(header, 0);
    encrypted.set(iv, header.length);
    encrypted.set(ciphertext, header.length + iv.length);
    return new Blob([encrypted], { type: 'application/octet-stream' });
  }

  getChunkedUploadPlan(fileSize: number) {
    const headerBytes = this.encoder.encode(this.encryptedBlobChunkedMagic).length;
    const regularPlainChunkBytes = this.uploadChunkTransportBytes - this.chunkFrameOverheadBytes;
    const firstPlainChunkBytes = this.uploadChunkTransportBytes - headerBytes - this.chunkFrameOverheadBytes;
    let remaining = fileSize;
    let totalEncryptedBytes = 0;
    let totalChunks = 0;

    if (remaining > 0) {
      const firstChunkPlain = Math.min(remaining, firstPlainChunkBytes);
      totalEncryptedBytes += headerBytes + this.chunkFrameOverheadBytes + firstChunkPlain;
      remaining -= firstChunkPlain;
      totalChunks += 1;
    }

    while (remaining > 0) {
      const plainBytes = Math.min(remaining, regularPlainChunkBytes);
      totalEncryptedBytes += this.chunkFrameOverheadBytes + plainBytes;
      remaining -= plainBytes;
      totalChunks += 1;
    }

    return {
      transportChunkBytes: this.uploadChunkTransportBytes,
      totalEncryptedBytes,
      totalChunks,
      firstPlainChunkBytes,
      regularPlainChunkBytes,
    };
  }

  async encryptVaultFileChunk(chunk: Blob, includeHeader: boolean) {
    const key = await this.getVaultCryptoKey(['encrypt']);
    const iv = crypto.getRandomValues(new Uint8Array(12));
    const plaintext = await chunk.arrayBuffer();
    const ciphertext = new Uint8Array(await crypto.subtle.encrypt({ name: 'AES-GCM', iv: cryptoBytes(iv) }, key, plaintext));
    const header = includeHeader ? this.encoder.encode(this.encryptedBlobChunkedMagic) : new Uint8Array(0);
    const lengthBytes = new Uint8Array(4);
    new DataView(lengthBytes.buffer).setUint32(0, ciphertext.length, false);
    const encrypted = new Uint8Array(header.length + lengthBytes.length + iv.length + ciphertext.length);
    let offset = 0;
    encrypted.set(header, offset);
    offset += header.length;
    encrypted.set(lengthBytes, offset);
    offset += lengthBytes.length;
    encrypted.set(iv, offset);
    offset += iv.length;
    encrypted.set(ciphertext, offset);
    return encrypted;
  }

  async encryptVirtualPath(path: string) {
    const key = await this.getVaultCryptoKey(['encrypt']);
    const iv = crypto.getRandomValues(new Uint8Array(12));
    const ciphertext = new Uint8Array(await crypto.subtle.encrypt(
      { name: 'AES-GCM', iv: cryptoBytes(iv) },
      key,
      this.encoder.encode(path),
    ));

    return {
      encryptedVirtualPath: toBase64(ciphertext),
      virtualPathIv: toBase64(iv),
      pathHash: await this.pathHash(path),
    };
  }

  async decryptVirtualPath(encryptedVirtualPath: string, iv: string) {
    const key = await this.getVaultCryptoKey(['decrypt']);
    const plaintext = await crypto.subtle.decrypt(
      { name: 'AES-GCM', iv: cryptoBytes(fromBase64(iv)) },
      key,
      cryptoBytes(fromBase64(encryptedVirtualPath)),
    );
    return this.decoder.decode(plaintext);
  }

  async decryptFileList<T extends { name: string; encryptedName?: string; nameIv?: string; pathHash?: string }>(files: T[]) {
    const decrypted: T[] = [];

    for (const file of files) {
      if (!file.encryptedName || !file.nameIv) {
        decrypted.push(file);
        continue;
      }

      decrypted.push({
        ...file,
        name: await this.decryptVirtualPath(file.encryptedName, file.nameIv),
      });
    }

    return decrypted;
  }

  async decryptVaultBlob(blob: Blob) {
    const bytes = new Uint8Array(await blob.arrayBuffer());
    const header = this.encoder.encode(this.encryptedBlobMagic);
    const chunkedHeader = this.encoder.encode(this.encryptedBlobChunkedMagic);

    if (this.startsWith(bytes, chunkedHeader)) {
      const key = await this.getVaultCryptoKey(['decrypt']);
      let offset = chunkedHeader.length;
      const parts: BlobPart[] = [];

      while (offset < bytes.length) {
        if (offset + 16 > bytes.length) {
          throw new Error('Uszkodzony zaszyfrowany plik SkyVault.');
        }

        const ciphertextLength = new DataView(bytes.buffer, bytes.byteOffset + offset, 4).getUint32(0, false);
        offset += 4;
        const iv = bytes.slice(offset, offset + 12);
        offset += 12;

        if (offset + ciphertextLength > bytes.length) {
          throw new Error('Uszkodzony zaszyfrowany plik SkyVault.');
        }

        const ciphertext = bytes.slice(offset, offset + ciphertextLength);
        offset += ciphertextLength;
        parts.push(await crypto.subtle.decrypt({ name: 'AES-GCM', iv: cryptoBytes(iv) }, key, cryptoBytes(ciphertext)));
      }

      return new Blob(parts);
    }

    for (let index = 0; index < header.length; index += 1) {
      if (bytes[index] !== header[index]) throw new Error('Ten plik nie ma formatu szyfrowanego SkyVault.');
    }
    const iv = bytes.slice(header.length, header.length + 12);
    const ciphertext = bytes.slice(header.length + 12);
    const key = await this.getVaultCryptoKey(['decrypt']);
    return new Blob([await crypto.subtle.decrypt({ name: 'AES-GCM', iv: cryptoBytes(iv) }, key, cryptoBytes(ciphertext))]);
  }

  private startsWith(bytes: Uint8Array, prefix: Uint8Array) {
    if (bytes.length < prefix.length) {
      return false;
    }

    for (let index = 0; index < prefix.length; index += 1) {
      if (bytes[index] !== prefix[index]) {
        return false;
      }
    }

    return true;
  }

  storeMasterKey(email: string, masterKey: Uint8Array) {
    sessionStorage.setItem('skyvaultMasterKey', toBase64(masterKey));
    sessionStorage.setItem('skyvaultEmail', email);
  }

  clearMasterKey() {
    sessionStorage.removeItem('skyvaultMasterKey');
    sessionStorage.removeItem('skyvaultEmail');
  }

  downloadRecoverySheet(email: string, masterKey: Uint8Array) {
    const file = new Blob([
      'SkyVault Recovery Sheet\n',
      'Email: ' + email + '\n',
      'Master Key (base64): ' + toBase64(masterKey) + '\n',
      'Created: ' + new Date().toISOString() + '\n\n',
      'Keep this offline. Anyone with this key can decrypt your vault, and SkyVault cannot recover it if it is lost.\n',
    ], { type: 'text/plain' });
    saveBlob(file, 'skyvault-recovery-sheet.txt');
  }

  private async hkdf(seed: Uint8Array, info: string) {
    const key = await crypto.subtle.importKey('raw', cryptoBytes(seed), 'HKDF', false, ['deriveBits']);
    const bits = await crypto.subtle.deriveBits({
      name: 'HKDF',
      hash: 'SHA-256',
      salt: this.encoder.encode('SkyVault HKDF v1'),
      info: this.encoder.encode(info),
    }, key, 256);
    return new Uint8Array(bits);
  }

  private async pathHash(path: string) {
    const key = await crypto.subtle.importKey(
      'raw',
      cryptoBytes(fromBase64(sessionStorage.getItem('skyvaultMasterKey') || '')),
      { name: 'HMAC', hash: 'SHA-256' },
      false,
      ['sign'],
    );
    const signature = new Uint8Array(await crypto.subtle.sign('HMAC', key, this.encoder.encode('SkyVault path v1:' + path)));
    return Array.from(signature, (byte) => byte.toString(16).padStart(2, '0')).join('');
  }

  private async getVaultCryptoKey(usages: KeyUsage[]) {
    const masterKey = sessionStorage.getItem('skyvaultMasterKey') || '';
    if (!masterKey) throw new Error('Brak klucza vault w tej sesji. Zaloguj się ponownie.');
    return await crypto.subtle.importKey('raw', cryptoBytes(fromBase64(masterKey)), 'AES-GCM', false, usages);
  }
}
