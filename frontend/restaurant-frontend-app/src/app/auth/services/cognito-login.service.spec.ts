import { TestBed } from '@angular/core/testing';
import { API_BASE_URL } from '../../shared/config/api-base-url.token';
import { AuthenticationService } from './authentication.service';
import { COGNITO_CONFIG, CognitoLoginService } from './cognito-login.service';

describe('Cognito authorization code callback', () => {
  let auth: jasmine.SpyObj<AuthenticationService>;
  let service: CognitoLoginService;
  beforeEach(() => {
    auth = jasmine.createSpyObj<AuthenticationService>('AuthenticationService', ['acceptValidatedSession']);
    TestBed.configureTestingModule({ providers: [
      { provide: API_BASE_URL, useValue: 'https://api.example.test' },
      { provide: AuthenticationService, useValue: auth },
      { provide: COGNITO_CONFIG, useValue: { domain: 'https://example.auth.eu-north-1.amazoncognito.com', clientId: 'client123' } },
    ] });
    service = TestBed.inject(CognitoLoginService);
  });
  afterEach(() => sessionStorage.removeItem('cognito-login-request'));
  function request(state = 'expected', createdAt = Date.now(), returnPath = '/reservations') {
    sessionStorage.setItem('cognito-login-request', JSON.stringify({ state, verifier: 'test-verifier', createdAt, returnPath }));
  }
  it('opens registration directly with its own PKCE challenge and state', async () => {
    const url = new URL(await service.prepare('/reservations', 'signup'));
    const saved = JSON.parse(sessionStorage.getItem('cognito-login-request')!);
    expect(url.pathname).toBe('/signup'); expect(url.searchParams.get('state')).toBe(saved.state);
    expect(url.searchParams.get('code_challenge_method')).toBe('S256');
    expect(url.searchParams.get('code_challenge')).toBeTruthy(); expect(saved.verifier).toBeTruthy();
    expect(saved.returnPath).toBe('/reservations');
  });
  it('builds the Cognito logout URL to clear the hosted session', () => {
    const url = new URL(service.signOutUrl());
    expect(url.pathname).toBe('/logout');
    expect(url.searchParams.get('client_id')).toBe('client123');
    expect(url.searchParams.get('logout_uri')).toBe(window.location.origin);
  });
  it('rejects a mismatched or expired state without making a token request', async () => {
    const fetchSpy = spyOn(window, 'fetch'); request();
    await expectAsync(service.complete(new URLSearchParams('code=code&state=other'))).toBeRejected();
    request('expected', Date.now() - 600001);
    await expectAsync(service.complete(new URLSearchParams('code=code&state=expected'))).toBeRejected();
    expect(fetchSpy).not.toHaveBeenCalled(); expect(auth.acceptValidatedSession).not.toHaveBeenCalled();
  });
  it('exchanges the code with the verifier and validates the ID token through the API before saving', async () => {
    request();
    const fetchSpy = spyOn(window, 'fetch').and.returnValues(
      Promise.resolve(new Response(JSON.stringify({ id_token: 'id-test', access_token: 'access-test' }), { status: 200 })),
      Promise.resolve(new Response(JSON.stringify({ id: 'subject', firstName: '', lastName: '', email: 'a@example.test', role: 'Customer' }), { status: 200 })),
    );
    expect(await service.complete(new URLSearchParams('code=code&state=expected'))).toBe('/reservations');
    const tokenRequest = fetchSpy.calls.first().args[1]!;
    expect((tokenRequest.body as URLSearchParams).get('code_verifier')).toBe('test-verifier');
    expect(fetchSpy.calls.mostRecent().args[0]).toBe('https://api.example.test/me');
    expect(auth.acceptValidatedSession).toHaveBeenCalledTimes(1);
    expect(sessionStorage.getItem('cognito-login-request')).toBeNull();
  });
  it('does not save tokens when server rejects the ID token', async () => {
    request(); spyOn(window, 'fetch').and.returnValues(
      Promise.resolve(new Response(JSON.stringify({ id_token: 'id-test', access_token: 'access-test' }), { status: 200 })),
      Promise.resolve(new Response('', { status: 401 })),
    );
    await expectAsync(service.complete(new URLSearchParams('code=code&state=expected'))).toBeRejected();
    expect(auth.acceptValidatedSession).not.toHaveBeenCalled();
  });
  it('rejects reuse of a completed login request', async () => {
    request(); spyOn(window, 'fetch').and.returnValue(Promise.resolve(new Response('', { status: 400 })));
    await expectAsync(service.complete(new URLSearchParams('code=code&state=expected'))).toBeRejected();
    await expectAsync(service.complete(new URLSearchParams('code=code&state=expected'))).toBeRejected();
  });
});
