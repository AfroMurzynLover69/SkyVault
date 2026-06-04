import { CommonModule } from '@angular/common';
import { Component, EventEmitter, Output, computed, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../core/auth.service';
import { AuthMode } from '../core/models';

@Component({
  selector: 'app-auth-page',
  imports: [CommonModule, FormsModule],
  templateUrl: './auth-page.html',
  styleUrl: './auth-page.css',
})
export class AuthPage {
  @Output() authenticated = new EventEmitter<void>();

  readonly mode = signal<AuthMode>(new URLSearchParams(window.location.search).get('mode') === 'register' ? 'register' : 'login');
  readonly email = signal('');
  readonly password = signal('');
  readonly verificationEmail = signal('');
  readonly verificationCode = signal('');
  readonly busy = signal(false);
  readonly message = signal('');
  readonly verifying = computed(() => this.verificationEmail().length > 0);
  readonly title = computed(() => (this.mode() === 'register' ? 'Create account' : 'Sign in'));
  readonly buttonLabel = computed(() => {
    if (this.busy()) return this.mode() === 'register' ? 'Creating keys...' : 'Unlocking...';
    return this.mode() === 'register' ? 'Create account' : 'Sign in';
  });

  constructor(private readonly auth: AuthService) {}

  switchMode(nextMode: AuthMode) {
    this.mode.set(nextMode);
    this.message.set('');
    this.verificationEmail.set('');
    this.verificationCode.set('');
    window.history.replaceState(null, '', nextMode === 'register' ? '/?mode=register' : '/');
  }

  async submit() {
    if (this.busy()) return;
    this.message.set('');
    this.busy.set(true);
    await new Promise((resolve) => requestAnimationFrame(resolve));

    try {
      if (this.verifying()) {
        await this.auth.verify(this.verificationEmail(), this.verificationCode());
      } else if (this.mode() === 'register') {
        const result = await this.auth.register(this.email(), this.password());

        if (result.verificationRequired) {
          this.verificationEmail.set(result.email || this.email());
          this.message.set('Verification code sent. Check your mailbox.');
          return;
        }
      } else {
        await this.auth.login(this.email(), this.password());
      }
      this.authenticated.emit();
    } catch (error) {
      this.message.set(error instanceof Error ? error.message : 'Could not unlock your account.');
    } finally {
      this.busy.set(false);
    }
  }
}
