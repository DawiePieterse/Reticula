import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from './core/auth/auth.service';
import { ConnectivityService } from './core/connectivity.service';
import { PwaInstallService } from './core/pwa-install.service';
import { FieldSync } from './features/field/sync/field-sync.service';

@Component({
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  selector: 'app-root',
  styleUrl: './app.scss',
  templateUrl: './app.html',
})
export class App {
  protected readonly auth = inject(AuthService);
  protected readonly connectivity = inject(ConnectivityService);
  protected readonly pwa = inject(PwaInstallService);
  /** Started with the app so queued field changes sync whenever the server can be reached. */
  protected readonly sync = inject(FieldSync);

  protected signOut(): void {
    const n = this.sync.queued() + this.sync.needsDecision();
    if (n && !confirm(`${n} field change${n === 1 ? ' has' : 's have'} not reached the server. They stay on this tablet and sync when you sign in again. Sign out?`)) return;
    this.auth.logout();
  }
}
