import { Injectable } from '@angular/core';
import { fromBase64, toBase64 } from './encoding';
import { ApiService } from './api.service';
import { CryptoService } from './crypto.service';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private pendingRegistration: { email: string; masterKey: Uint8Array } | null = null;

  constructor(
    private readonly api: ApiService,
    private readonly crypto: CryptoService,
  ) {}

  async register(email: string, password: string) {
    const normalizedEmail = this.normalizeEmail(email);
    const salt = crypto.getRandomValues(new Uint8Array(16));
    const masterKey = crypto.getRandomValues(new Uint8Array(32));
    const keys = await this.crypto.deriveKeys(password, salt);
    const wrapped = await this.crypto.wrapMasterKey(keys.kek, masterKey);

    this.pendingRegistration = { email: normalizedEmail, masterKey };

    return await this.api.postJsonForm<{ authenticated: boolean; verificationRequired: boolean; email?: string }>('/api/auth/register', {
      email: normalizedEmail,
      authVerifier: toBase64(keys.authVerifier),
      passwordSalt: toBase64(salt),
      wrappedMasterKey: toBase64(wrapped.ciphertext),
      wrappedMasterKeyIv: toBase64(wrapped.iv),
    });
  }

  async login(email: string, password: string) {
    this.pendingRegistration = null;
    const normalizedEmail = this.normalizeEmail(email);
    const material = await this.api.getCryptoMaterial(normalizedEmail);
    const keys = await this.crypto.deriveKeys(password, fromBase64(material.passwordSalt));
    const masterKey = await this.crypto.unwrapMasterKey(
      keys.kek,
      fromBase64(material.wrappedMasterKey),
      fromBase64(material.wrappedMasterKeyIv),
    );

    this.crypto.storeMasterKey(normalizedEmail, masterKey);
    await this.api.postForm('/login', {
      email: normalizedEmail,
      authVerifier: toBase64(keys.authVerifier),
    }, true);
  }

  async logout() {
    this.pendingRegistration = null;
    await this.api.logout();
    this.crypto.clearMasterKey();
  }

  async verify(email: string, code: string) {
    const normalizedEmail = this.normalizeEmail(email);
    const response = await this.api.postJsonForm<{ authenticated: boolean }>('/api/auth/verify', {
      email: normalizedEmail,
      code,
    });

    if (this.pendingRegistration?.email === normalizedEmail) {
      this.crypto.storeMasterKey(normalizedEmail, this.pendingRegistration.masterKey);
      this.crypto.downloadRecoverySheet(normalizedEmail, this.pendingRegistration.masterKey);
      this.pendingRegistration = null;
    }

    return response;
  }

  private normalizeEmail(email: string) {
    return email.trim().toLowerCase();
  }
}
