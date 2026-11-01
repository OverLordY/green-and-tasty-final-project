import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { API_BASE_URL } from '../../shared/config/api-base-url.token';
import { GuestMenuPreview } from '../../models/group-orders/guest-confirmation.model';
import { GuestOrderEntryPageComponent } from './guest-order-entry-page.component';

const preview: GuestMenuPreview = {
  invite: { RestaurantName: 'Test', LocationAddress: 'Test address', StartTime: '2030-01-01T18:00:00Z' },
  items: [{ id: 'salad', name: 'Salad', price: 12, available: true, category: 'Appetizers' },
    { id: 'soup', name: 'Soup', price: 10, available: false, category: 'Main Courses' }],
  deadlines: { changesClose: '2030-01-01T17:15:00Z', submissionCloses: '2030-01-01T17:30:00Z' }, canChange: true,
};

describe('Guest ordering without registration', () => {
  let controller: HttpTestingController;
  function create() {
    TestBed.configureTestingModule({ imports: [GuestOrderEntryPageComponent], providers: [
      provideHttpClient(), provideHttpClientTesting(),
      { provide: API_BASE_URL, useValue: 'https://test.api' },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ token: 'invite' }) } } },
    ] });
    controller = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(GuestOrderEntryPageComponent); fixture.detectChanges();
    const request = controller.expectOne('https://test.api/guest/orders/preview');
    expect(request.request.body).toEqual({ inviteToken: 'invite' }); request.flush(preview);
    return fixture;
  }
  afterEach(() => controller.verify());
  it('prevents selecting unavailable food and keeps recipients separate', async () => {
    const fixture = create(); await Promise.resolve();
    fixture.componentInstance.add(preview.items[1]);
    expect(fixture.componentInstance.selection().length).toBe(0);
    fixture.componentInstance.add(preview.items[0]);
    fixture.componentInstance.recipient.set('Olena'); fixture.componentInstance.add(preview.items[0]);
    expect(fixture.componentInstance.selection().length).toBe(2);
    expect(fixture.componentInstance.total()).toBe(24);
  });
  it('creates a draft then sends only dish ids and quantities before requesting email', async () => {
    const fixture = create(); await Promise.resolve();
    const component = fixture.componentInstance;
    component.add(preview.items[0]); component.guestName.set('Andrii'); component.email.set('andrii@example.test');
    const sending = component.send(); void component.send();
    controller.expectOne('https://test.api/guest/orders').flush({ orderId: 'order', draftAccessToken: 'draft' });
    await Promise.resolve();
    const lookup = controller.expectOne('https://test.api/guest/orders/order');
    expect(lookup.request.headers.get('Authorization')).toBe('Bearer draft');
    lookup.flush({ status: 'Choosing', confirmed: [], pending: [], notices: [], confirmedTotal: 0, maskedEmail: 'a***@example.test' });
    await Promise.resolve();
    const dish = controller.expectOne('https://test.api/guest/orders/order/dishes');
    expect(dish.request.body).toEqual({ dishId: 'salad', forWhom: 'Для мене', quantity: 1 });
    dish.flush({ status: 'Choosing', confirmed: [], pending: component.selection(), notices: [], confirmedTotal: 0 });
    await Promise.resolve();
    controller.expectOne('https://test.api/guest/orders/order/request-confirmation').flush({}, { status: 202, statusText: 'Accepted' });
    await sending; expect(component.emailSent()).toBeTrue(); expect(component.maskedEmail()).toBe('a***@example.test');
  });
  it('does not create a draft when contact information is missing', async () => {
    const fixture = create(); await Promise.resolve();
    fixture.componentInstance.add(preview.items[0]);
    await fixture.componentInstance.send();
    controller.expectNone('https://test.api/guest/orders');
    expect(fixture.componentInstance.error()).toContain('коректну');
  });
});
