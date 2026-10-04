import { DOCUMENT, Injectable, inject, signal } from '@angular/core';

interface BeforeInstallPromptEvent extends Event {
  prompt(): Promise<void>;
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>;
}

/** Captures the browser's install prompt so the app can offer "Install" on the tablet. */
@Injectable({ providedIn: 'root' })
export class PwaInstallService {
  private deferred: BeforeInstallPromptEvent | null = null;
  private readonly _canInstall = signal(false);
  readonly canInstall = this._canInstall.asReadonly();

  constructor() {
    const win = inject(DOCUMENT).defaultView;
    win?.addEventListener('beforeinstallprompt', (e) => {
      e.preventDefault();
      this.deferred = e as BeforeInstallPromptEvent;
      this._canInstall.set(true);
    });
    win?.addEventListener('appinstalled', () => this.reset());
  }

  async install(): Promise<void> {
    if (!this.deferred) return;
    await this.deferred.prompt();
    await this.deferred.userChoice;
    this.reset();
  }

  private reset(): void {
    this.deferred = null;
    this._canInstall.set(false);
  }
}
