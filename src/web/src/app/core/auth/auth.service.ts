import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, finalize, firstValueFrom, map, shareReplay, throwError } from 'rxjs';

export const ENGINEER = 'engineer';
export const INSPECTOR = 'inspector';

export interface CurrentUser {
  id: string;
  email: string;
  displayName: string;
  registrationNo: string | null;
  roles: string[];
}

interface AccessTokenResponse {
  tokenType: string;
  accessToken: string;
  expiresIn: number;
  refreshToken: string;
}

interface TokenSet {
  accessToken: string;
  refreshToken: string;
  expiresAt: number;
}

interface StoredAuth {
  tokens: TokenSet;
  user: CurrentUser | null;
}

export const AUTH_STORAGE_KEY = 'reticula.auth';

/**
 * Bearer-token session. Tokens and the user profile are kept in localStorage so the
 * installed PWA can start offline already signed in; the refresh token lasts 14 days.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);

  private readonly state = signal<StoredAuth | null>(readStored());
  private refreshInFlight: Observable<TokenSet> | null = null;

  readonly user = computed(() => this.state()?.user ?? null);
  readonly isAuthenticated = computed(() => this.state() !== null);
  readonly isEngineer = computed(() => this.user()?.roles.includes(ENGINEER) ?? false);

  accessToken(): string | null {
    return this.state()?.tokens.accessToken ?? null;
  }

  hasRefreshToken(): boolean {
    return !!this.state()?.tokens.refreshToken;
  }

  async login(email: string, password: string): Promise<void> {
    const r = await firstValueFrom(this.http.post<AccessTokenResponse>('/api/auth/login', { email, password }));
    this.setTokens(r);
    await this.loadMe();
  }

  async loadMe(): Promise<void> {
    const me = await firstValueFrom(this.http.get<CurrentUser>('/api/auth/me'));
    const s = this.state();
    if (s) this.save({ ...s, user: me });
  }

  /** Exchanges the refresh token once, sharing the call between concurrent 401s. */
  refresh(): Observable<TokenSet> {
    const refreshToken = this.state()?.tokens.refreshToken;
    if (!refreshToken) return throwError(() => new Error('No refresh token.'));
    this.refreshInFlight ??= this.http.post<AccessTokenResponse>('/api/auth/refresh', { refreshToken }).pipe(
      map((r) => this.setTokens(r)),
      finalize(() => (this.refreshInFlight = null)),
      shareReplay(1),
    );
    return this.refreshInFlight;
  }

  logout(): void {
    this.save(null);
    void this.router.navigate(['/login']);
  }

  private setTokens(r: AccessTokenResponse): TokenSet {
    const tokens: TokenSet = {
      accessToken: r.accessToken,
      refreshToken: r.refreshToken,
      expiresAt: Date.now() + r.expiresIn * 1000,
    };
    this.save({ tokens, user: this.state()?.user ?? null });
    return tokens;
  }

  private save(value: StoredAuth | null): void {
    this.state.set(value);
    try {
      if (value) localStorage.setItem(AUTH_STORAGE_KEY, JSON.stringify(value));
      else localStorage.removeItem(AUTH_STORAGE_KEY);
    } catch {
      // Storage blocked: session lasts for this tab only.
    }
  }
}

function readStored(): StoredAuth | null {
  try {
    const raw = localStorage.getItem(AUTH_STORAGE_KEY);
    const parsed = raw ? (JSON.parse(raw) as StoredAuth) : null;
    return parsed?.tokens?.accessToken ? parsed : null;
  } catch {
    return null;
  }
}
