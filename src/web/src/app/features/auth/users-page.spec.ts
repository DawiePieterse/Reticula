import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { AppUserRow, UsersPage } from './users-page';

const engineer: AppUserRow = {
  id: 'u1',
  email: 'eng@x',
  displayName: 'A Engineer',
  registrationNo: null,
  roles: ['engineer'],
};
const inspector: AppUserRow = {
  id: 'u2',
  email: 'insp@x',
  displayName: 'An Inspector',
  registrationNo: null,
  roles: ['inspector'],
};

async function setup() {
  TestBed.configureTestingModule({
    imports: [UsersPage],
    providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
  });
  const fixture = TestBed.createComponent(UsersPage);
  const http = TestBed.inject(HttpTestingController);
  http.expectOne('/api/users').flush([engineer, inspector]);
  await fixture.whenStable();
  return { fixture, http, el: fixture.nativeElement as HTMLElement };
}

const button = (el: HTMLElement, text: string) =>
  [...el.querySelectorAll('button')].find((b) => b.textContent?.trim() === text)!;

describe('UsersPage', () => {
  it('says which engineer cannot sign off', async () => {
    const { el } = await setup();
    const rows = [...el.querySelectorAll('tbody tr')].map((r) => r.textContent ?? '');
    expect(rows[0]).toContain('None: cannot sign off');
    expect(rows[1]).not.toContain('cannot sign off');
  });

  it('records the ECSA registration number', async () => {
    const { fixture, http, el } = await setup();
    button(el, 'Edit').click();
    await fixture.whenStable();
    const reg = el.querySelector<HTMLInputElement>('input[aria-label="ECSA registration"]')!;
    reg.value = '20100123';
    reg.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    button(el, 'Save').click();
    const put = http.expectOne('/api/users/u1');
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toEqual({ displayName: 'A Engineer', registrationNo: '20100123' });
    put.flush({ ...engineer, registrationNo: '20100123' });
    await fixture.whenStable();
    expect(el.textContent).toContain('20100123');
    expect(el.textContent).not.toContain('cannot sign off');
  });
});
