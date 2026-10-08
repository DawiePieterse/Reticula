import { HttpBackend, HttpClient } from '@angular/common/http';
import { ErrorHandler, Injectable, inject } from '@angular/core';
import { AuthService } from './auth/auth.service';

export const APP_VERSION = '0.1.0';
const MAX_REPORTS_PER_SESSION = 20;

/**
 * Logs uncaught errors to the console and reports them to the API, so problems on a field tablet
 * reach the server log. Best effort: never throws, deduplicates, and caps reports per session.
 */
@Injectable()
export class ReportingErrorHandler implements ErrorHandler {
  // HttpBackend bypasses interceptors, so a failing report cannot trigger refresh loops.
  private readonly http = new HttpClient(inject(HttpBackend));
  private readonly auth = inject(AuthService);
  private readonly seen = new Set<string>();

  handleError(error: unknown): void {
    console.error(error);
    try {
      const token = this.auth.accessToken();
      const { message, stack } = describe(error);
      const key = `${message}|${stack?.split('\n')[1] ?? ''}`;
      if (!token || this.seen.has(key) || this.seen.size >= MAX_REPORTS_PER_SESSION) return;
      this.seen.add(key);
      this.http
        .post(
          '/api/system/client-errors',
          { message, stack, url: location.pathname, appVersion: APP_VERSION },
          { headers: { Authorization: `Bearer ${token}` } },
        )
        .subscribe({ error: () => undefined });
    } catch {
      // Reporting must never break the app.
    }
  }
}

function describe(error: unknown): { message: string; stack?: string } {
  const e = (error as { rejection?: unknown })?.rejection ?? error;
  if (e instanceof Error) return { message: `${e.name}: ${e.message}`, stack: e.stack };
  return { message: typeof e === 'string' ? e : JSON.stringify(e) ?? String(e) };
}
