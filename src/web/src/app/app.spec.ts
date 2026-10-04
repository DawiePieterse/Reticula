import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideRouter } from '@angular/router';
import { App } from './app';

describe('App', () => {
  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({ imports: [App], providers: [provideRouter([]), provideHttpClient()] }).compileComponents();
  });

  it('renders the brand and connectivity status, without nav when signed out', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.brand')?.textContent).toContain('Reticula');
    expect(el.querySelector('.status')?.textContent).toMatch(/Online|Offline/);
    expect(el.querySelector('nav')).toBeNull();
  });
});
