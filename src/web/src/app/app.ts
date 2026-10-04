import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from './core/auth/auth.service';
import { ConnectivityService } from './core/connectivity.service';
import { PwaInstallService } from './core/pwa-install.service';

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
}
