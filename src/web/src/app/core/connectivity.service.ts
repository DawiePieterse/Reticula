import { DOCUMENT, Injectable, inject, signal } from '@angular/core';

@Injectable({ providedIn: 'root' })
export class ConnectivityService {
  private readonly win = inject(DOCUMENT).defaultView;
  private readonly _online = signal(this.win?.navigator.onLine ?? true);
  readonly online = this._online.asReadonly();

  constructor() {
    this.win?.addEventListener('online', () => this._online.set(true));
    this.win?.addEventListener('offline', () => this._online.set(false));
  }
}
