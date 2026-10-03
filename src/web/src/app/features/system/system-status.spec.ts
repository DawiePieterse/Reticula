import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { SystemStatus } from './system-status';

describe('SystemStatus', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [SystemStatus],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
  });

  it('shows api and calc status', async () => {
    const fixture = TestBed.createComponent(SystemStatus);
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/system/health').flush({ api: 'ok', calc: 'unavailable' });
    http.expectOne('/api/system/rules').flush(['eskom/0.1.0']);
    await fixture.whenStable();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('unavailable');
    expect(text).toContain('eskom/0.1.0');
  });
});
