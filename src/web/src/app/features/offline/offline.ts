import { Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ConnectivityService } from '../../core/connectivity.service';

@Component({
  selector: 'app-offline',
  imports: [RouterLink],
  template: `
    <h2>You are offline</h2>
    <p>This screen needs a connection to the server.</p>
    @if (connectivity.online()) {
      <p>You are back online. <a routerLink="/projects">Continue</a></p>
    }
  `,
})
export class Offline {
  protected readonly connectivity = inject(ConnectivityService);
}
