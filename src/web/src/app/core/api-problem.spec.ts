import { HttpErrorResponse } from '@angular/common/http';
import { toApiProblem } from './api-problem';

describe('toApiProblem', () => {
  it('adds the trace id to server errors', () => {
    const p = toApiProblem(new HttpErrorResponse({ status: 503, error: { title: 'Calculation service unavailable', traceId: '00-abc-01' } }));
    expect(p.message).toBe('Calculation service unavailable (reference 00-abc-01)');
  });

  it('keeps field errors from validation problems', () => {
    const p = toApiProblem(new HttpErrorResponse({ status: 400, error: { title: 'Invalid', errors: { name: ['Name is required.'] } } }));
    expect(p.fieldErrors['name']).toEqual(['Name is required.']);
    expect(p.message).toBe('Invalid');
  });

  it('explains a network failure', () => {
    expect(toApiProblem(new HttpErrorResponse({ status: 0 })).message).toContain('Cannot reach the server');
  });
});
