import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AuthenticationService } from '../../auth/services/authentication.service';
import { tokenInterceptor } from '../../auth/token.interceptor';
import { API_BASE_URL } from '../../shared/config/api-base-url.token';
import { ToastService } from '../toast/toast.service';
import { GuestInviteService } from '../reservation/guest-invite.service';
import { GroupOrdersService } from './group-orders.service';

describe('Owner group requests through tokenInterceptor', () => {
  let service: GroupOrdersService;
  let http: HttpClient;
  let controller: HttpTestingController;
  let toast: jasmine.SpyObj<ToastService>;

  beforeEach(() => {
    toast = jasmine.createSpyObj<ToastService>('ToastService', ['error']);
    TestBed.configureTestingModule({ providers: [
      provideHttpClient(withInterceptors([tokenInterceptor])), provideHttpClientTesting(),
      { provide: API_BASE_URL, useValue: 'https://test.api' },
      { provide: AuthenticationService, useValue: { getTokens: () => ({ idToken: 'owner-id-token', accessToken: 'access-token' }) } },
      { provide: GuestInviteService, useValue: {} },
      { provide: ToastService, useValue: toast },
    ] });
    service = TestBed.inject(GroupOrdersService);
    http = TestBed.inject(HttpClient);
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  it('sends the owner token for sessions, status and remove', () => {
    service.getSessions$('g1').subscribe();
    service.getStatus$('g1').subscribe();
    service.removeSession$('g1', 's1').subscribe();
    for (const path of ['sessions', 'status', 'sessions/s1']) {
      const request = controller.expectOne(`https://test.api/guest/groups/g1/${path}`);
      expect(request.request.headers.get('Authorization')).toBe('Bearer owner-id-token');
      request.flush(path === 'status' ? { status: 'Open' } : { participants: [], total: 0 });
    }
  });

  it('suppresses toast for failed silent owner refresh', () => {
    service.getSessions$('g1', { silent: true }).subscribe({ error: () => undefined });
    controller.expectOne('https://test.api/guest/groups/g1/sessions').flush(
      { error: 'Unavailable' }, { status: 500, statusText: 'Server Error' },
    );
    expect(toast.error).not.toHaveBeenCalled();
  });

  it('does not attach the owner token to guest session requests', () => {
    http.post('https://test.api/guest/sessions', { name: 'Guest' }).subscribe();
    const request = controller.expectOne('https://test.api/guest/sessions');
    expect(request.request.headers.has('Authorization')).toBeFalse();
    request.flush({});
  });
});
