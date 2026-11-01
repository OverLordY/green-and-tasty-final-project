import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { API_BASE_URL } from '../../shared/config/api-base-url.token';
import { GuestPersonalOrderService } from './guest-personal-order.service';

describe('Personal guest order API', () => {
  let controller: HttpTestingController;
  let service: GuestPersonalOrderService;
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), { provide: API_BASE_URL, useValue: 'https://api.test' }] });
    controller = TestBed.inject(HttpTestingController); service = TestBed.inject(GuestPersonalOrderService);
  });
  afterEach(() => controller.verify());
  it('requests access without a guest bearer and sends only the email and order context', () => {
    service.requestAccess$('guest@example.test', { orderId: 'order/a' }).subscribe();
    const request = controller.expectOne('https://api.test/guest/orders/order%2Fa/request-access');
    expect(request.request.method).toBe('POST'); expect(request.request.body).toEqual({ email: 'guest@example.test' });
    expect(request.request.headers.has('Authorization')).toBeFalse(); request.flush({ message: 'If matched' }, { status: 202, statusText: 'Accepted' });
    service.requestAccess$('guest@example.test', { inviteToken: 'invitation' }).subscribe();
    const invitation = controller.expectOne('https://api.test/guest/orders/recover-access');
    expect(invitation.request.body).toEqual({ email: 'guest@example.test', inviteToken: 'invitation' }); invitation.flush({ message: 'If matched' });
  });
  it('separates recovery redemption from order confirmation', () => {
    service.confirm$('order', 'recovery-token', true).subscribe();
    const request = controller.expectOne('https://api.test/guest/orders/order/recover-access');
    expect(request.request.method).toBe('POST'); expect(request.request.body).toEqual({ token: 'recovery-token' }); request.flush({ guestAccessToken: 'personal' });
    controller.expectNone('https://api.test/guest/orders/order/confirm');
  });
  it('uses the personal capability for menu and dish requests and never submits a client price', () => {
    service.menu$('order', 'personal').subscribe();
    const menu = controller.expectOne('https://api.test/guest/orders/order/menu');
    expect(menu.request.headers.get('Authorization')).toBe('Bearer personal'); menu.flush({ items: [], canChange: true });
    service.propose$('order', 'personal', { dishId: 'soup', forWhom: 'Olena', quantity: 2 }).subscribe();
    const dish = controller.expectOne('https://api.test/guest/orders/order/dishes');
    expect(dish.request.headers.get('Authorization')).toBe('Bearer personal');
    expect(dish.request.body).toEqual({ dishId: 'soup', forWhom: 'Olena', quantity: 2 }); dish.flush({});
  });
});
