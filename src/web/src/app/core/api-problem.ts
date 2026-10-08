import { HttpErrorResponse } from '@angular/common/http';

/** A server error flattened for display: a message plus per-field messages. */
export interface ApiProblem {
  status: number;
  message: string;
  fieldErrors: Record<string, string[]>;
}

export function toApiProblem(error: unknown): ApiProblem {
  if (!(error instanceof HttpErrorResponse)) {
    return { status: -1, message: 'Unexpected error.', fieldErrors: {} };
  }
  if (error.status === 0) {
    return { status: 0, message: 'Cannot reach the server. Check your connection.', fieldErrors: {} };
  }
  const body = (error.error ?? {}) as { title?: string; detail?: string; errors?: Record<string, string[]>; traceId?: string };
  let message = body.detail ?? body.title ?? error.statusText ?? 'Request failed.';
  // Server faults carry a trace id that finds the matching API and calc log lines.
  if (error.status >= 500 && body.traceId) message += ` (reference ${body.traceId})`;
  return { status: error.status, message, fieldErrors: body.errors ?? {} };
}
