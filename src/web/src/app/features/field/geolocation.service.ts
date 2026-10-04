import { DOCUMENT, Injectable, inject, signal } from '@angular/core';
import { GpsFix } from './field.api';

/** Watches the device position while the field screen is open. */
@Injectable({ providedIn: 'root' })
export class GeolocationService {
  private readonly geo = inject(DOCUMENT).defaultView?.navigator.geolocation;
  private watchId: number | null = null;
  readonly fix = signal<GpsFix | null>(null);
  readonly error = signal<string | null>(null);

  start(): void {
    if (this.watchId !== null) return;
    if (!this.geo) {
      this.error.set('GPS is not available on this device.');
      return;
    }
    this.watchId = this.geo.watchPosition(
      (p) => {
        this.error.set(null);
        this.fix.set({ lon: p.coords.longitude, lat: p.coords.latitude, accuracyM: Math.round(p.coords.accuracy * 10) / 10 });
      },
      (e) => this.error.set(e.message || 'GPS unavailable'),
      { enableHighAccuracy: true, maximumAge: 10_000, timeout: 30_000 },
    );
  }

  stop(): void {
    if (this.watchId !== null) this.geo?.clearWatch(this.watchId);
    this.watchId = null;
  }
}
