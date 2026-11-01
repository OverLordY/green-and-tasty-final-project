import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { API_BASE_URL } from '../../shared/config/api-base-url.token';
import { AuthenticationService } from './authentication.service';

describe('Restoring an organizer session', () => {
  beforeEach(() => {
    localStorage.removeItem('auth'); localStorage.removeItem('user');
    TestBed.configureTestingModule({ providers: [provideHttpClient(), { provide: API_BASE_URL, useValue: 'https://api.example.test' }] });
  });
  afterEach(() => { localStorage.removeItem('auth'); localStorage.removeItem('user'); });
  it('does not mark a malformed stored session as signed in', async () => {
    localStorage.setItem('auth', '{invalid'); localStorage.setItem('user', '{invalid');
    const service = TestBed.inject(AuthenticationService);
    expect(await firstValueFrom(service.isAuthorised$)).toBeFalse();
    expect(service.getToken()).toBeUndefined(); expect(service.getUser()).toBeNull();
  });
  it('removes an expired token and stale profile on startup', async () => {
    const expired = 'eyJhbGciOiJub25lIn0.' + btoa(JSON.stringify({ exp: 1 })) + '.signature';
    localStorage.setItem('auth', JSON.stringify({ idToken: expired, accessToken: 'access' }));
    localStorage.setItem('user', JSON.stringify({ id: 'old' }));
    const service = TestBed.inject(AuthenticationService);
    expect(await firstValueFrom(service.isAuthorised$)).toBeFalse();
    expect(localStorage.getItem('auth')).toBeNull(); expect(service.getUser()).toBeNull();
  });
});
