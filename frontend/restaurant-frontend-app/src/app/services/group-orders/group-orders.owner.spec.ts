import { fakeAsync, TestBed, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { API_BASE_URL } from '../../shared/config/api-base-url.token';
import { GroupOrdersService } from './group-orders.service';

describe('Owner change API contract', () => {
  let http: HttpTestingController;
  let service: GroupOrdersService;
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), { provide: API_BASE_URL, useValue: 'https://api.test' }] });
    http = TestBed.inject(HttpTestingController); service = TestBed.inject(GroupOrdersService);
  });
  afterEach(() => http.verify());
  it('sends a server-priced absolute quantity and encodes IDs', () => {
    service.changeGuestDish$('group/a', 'guest/b', { dishId: 'salad', forWhom: 'Olena', quantity: 1 }).subscribe();
    const req = http.expectOne('https://api.test/owner/groups/group%2Fa/orders/guest%2Fb/dishes');
    expect(req.request.method).toBe('POST'); expect(req.request.body).toEqual({ dishId: 'salad', forWhom: 'Olena', quantity: 1 });
    expect(req.request.body.unitPrice).toBeUndefined(); req.flush({ snapshot: { participants: [], total: 0 }, notificationId: 'notice' });
  });
  it('uses explicit POST both to clear and to retry an individual notification', () => {
    service.clearGuestOrder$('g', 'p').subscribe();
    const clear = http.expectOne('https://api.test/owner/groups/g/orders/p/clear'); expect(clear.request.method).toBe('POST'); clear.flush({});
    service.sendNotification$('g', 'n').subscribe();
    const send = http.expectOne('https://api.test/owner/groups/g/notifications/n/send'); expect(send.request.method).toBe('POST'); send.flush({ id: 'n', guestAccepted: true, ownerAccepted: true });
  });
  it('retries a partially accepted notification without repeating the order change', fakeAsync(() => {
    let result: any;
    service.sendNotification$('g', 'n').subscribe(value => result = value);
    const url = 'https://api.test/owner/groups/g/notifications/n/send';
    http.expectOne(url).flush({ id: 'n', guestAccepted: true, ownerAccepted: false });
    tick(2000);
    http.expectOne(url).flush({ id: 'n', guestAccepted: true, ownerAccepted: true });
    expect(result.ownerAccepted).toBeTrue();
    http.expectNone('https://api.test/owner/groups/g/orders/p/clear');
  }));
  it('stops after three unsuccessful delivery attempts and leaves the status visible', fakeAsync(() => {
    let result: any;
    service.sendNotification$('g', 'n').subscribe(value => result = value);
    const url = 'https://api.test/owner/groups/g/notifications/n/send';
    const pending = { id: 'n', guestAccepted: false, ownerAccepted: true };
    http.expectOne(url).flush(pending); tick(2000);
    http.expectOne(url).flush(pending); tick(4000);
    http.expectOne(url).flush(pending);
    expect(result).toEqual(pending);
    tick(10000); http.expectNone(url);
  }));
  it('does not automatically resend after an ambiguous network failure', fakeAsync(() => {
    let failed = false;
    service.sendNotification$('g', 'n').subscribe({ error: () => failed = true });
    const url = 'https://api.test/owner/groups/g/notifications/n/send';
    http.expectOne(url).error(new ProgressEvent('error'));
    tick(10000); http.expectNone(url); expect(failed).toBeTrue();
  }));
});
