import { Location } from '@angular/common';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import { API_BASE_URL } from '../../shared/config/api-base-url.token';
import { GuestConfirmationPageComponent } from './guest-confirmation-page.component';

describe('Guest email confirmation', () => {
  let controller: HttpTestingController;
  let location: jasmine.SpyObj<Location>;
  function setup(fragment = 'order=test-order&token=test-confirmation') {
    location = jasmine.createSpyObj<Location>('Location', ['replaceState']);
    TestBed.configureTestingModule({
      imports: [GuestConfirmationPageComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(),
        { provide: API_BASE_URL, useValue: 'https://test.api' },
        { provide: ActivatedRoute, useValue: { snapshot: { fragment } } },
        { provide: Location, useValue: location }],
    });
    controller = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(GuestConfirmationPageComponent);
    fixture.detectChanges();
    return fixture;
  }
  afterEach(() => {
    controller.verify();
    sessionStorage.removeItem('guest-order-access:test-order');
  });
  it('previews the proposed dishes without confirming merely by opening the email link', async () => {
    const fixture = setup();
    const preview = controller.expectOne('https://test.api/guest/orders/test-order/confirmation-preview');
    expect(preview.request.body).toEqual({ token: 'test-confirmation' });
    preview.flush({ confirmed: [{ key: 'salad', name: 'Salad', forWhom: 'Guest', unitPrice: 12, quantity: 1 }], pending: [{ key: 'salad', name: 'Salad', forWhom: 'Guest', unitPrice: 12, quantity: 2 }] });
    await Promise.resolve(); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Salad × 1');
    expect(fixture.nativeElement.textContent).toContain('Salad × 2');
    controller.expectNone('https://test.api/guest/orders/test-order/confirm');
    expect(fixture.componentInstance.confirmed()).toBeFalse();
  });
  it('does not redeem recovery on opening the link and labels it as access rather than order confirmation', () => {
    const fixture = setup('order=test-order&recovery=recovery-token');
    controller.expectNone(() => true);
    expect(fixture.nativeElement.textContent).toContain('Відновити доступ');
    expect(fixture.nativeElement.textContent).toContain('Страви від цього не підтвердяться');
  });
  it('recovers on another device with no stored access and keeps pending dishes unconfirmed', async () => {
    sessionStorage.removeItem('guest-order-access:test-order');
    const fixture = setup('order=test-order&recovery=recovery-token');
    const action = fixture.componentInstance.confirm();
    const recovery = controller.expectOne('https://test.api/guest/orders/test-order/recover-access');
    expect(recovery.request.method).toBe('POST'); expect(recovery.request.body).toEqual({ token: 'recovery-token' });
    recovery.flush({ guestAccessToken: 'fresh-personal' }); await Promise.resolve();
    const details = controller.expectOne('https://test.api/guest/orders/test-order');
    expect(details.request.headers.get('Authorization')).toBe('Bearer fresh-personal');
    details.flush({ status: 'Choosing', confirmed: [], pending: [{ key: 'salad:Olena', dishId: 'salad', name: 'Salad', forWhom: 'Olena', unitPrice: 12, quantity: 2 }],
      notices: [], confirmedTotal: 0, canChange: true });
    await action; fixture.detectChanges();
    controller.expectNone('https://test.api/guest/orders/test-order/confirm');
    expect(fixture.componentInstance.order()?.confirmedTotal).toBe(0);
    expect(fixture.nativeElement.textContent).toContain('Страви ще не підтверджені');
    expect(location.replaceState).toHaveBeenCalledWith('/guest-confirmation#order=test-order');
    expect(sessionStorage.getItem('guest-order-access:test-order')).toBe('fresh-personal');
  });
  it('explains an expired recovery link and leaves the email recovery form available', async () => {
    const fixture = setup('order=test-order&recovery=expired'); const action = fixture.componentInstance.confirm();
    controller.expectOne('https://test.api/guest/orders/test-order/recover-access').flush({ code: 'access_link_expired' }, { status: 410, statusText: 'Gone' });
    await action; fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Термін дії посилання завершився');
    expect(fixture.nativeElement.querySelector('app-guest-access-recovery')).not.toBeNull();
    expect(fixture.componentInstance.confirmed()).toBeFalse();
  });
  it('adds a new dish for a recipient as pending without changing confirmed totals', async () => {
    sessionStorage.setItem('guest-order-access:test-order', 'personal');
    const fixture = setup('order=test-order');
    const salad = { key: 'salad:Olena', dishId: 'salad', name: 'Salad', forWhom: 'Olena', unitPrice: 12, quantity: 2 };
    const original = { status: 'Confirmed', confirmed: [salad], pending: null, notices: [], confirmedTotal: 24, canChange: true };
    controller.expectOne('https://test.api/guest/orders/test-order').flush(original); await Promise.resolve();
    const open = fixture.componentInstance.openMenu();
    controller.expectOne('https://test.api/guest/orders/test-order/menu').flush({ items: [{ id: 'soup', name: 'Soup', price: 8, category: 'Soups', available: true }], canChange: true });
    await open;
    const add = fixture.componentInstance.addDish({ dishId: 'soup', forWhom: 'Ira', quantity: 1 });
    const proposal = controller.expectOne('https://test.api/guest/orders/test-order/dishes');
    expect(proposal.request.body).toEqual({ dishId: 'soup', forWhom: 'Ira', quantity: 1 });
    proposal.flush({ ...original, status: 'ChangesAwaitingConfirmation', pending: [salad, { key: 'soup:Ira', dishId: 'soup', name: 'Soup', forWhom: 'Ira', unitPrice: 8, quantity: 1 }] });
    await add; fixture.detectChanges();
    expect(fixture.componentInstance.order()?.confirmedTotal).toBe(24); expect(fixture.nativeElement.textContent).toContain('Soup');
    expect(fixture.componentInstance.message()).toContain('підтвердьте новий склад');
  });
  it('uses the pending absolute quantity when adding another portion for the same recipient', async () => {
    sessionStorage.setItem('guest-order-access:test-order', 'personal'); const fixture = setup('order=test-order');
    const salad = { key: 'salad:Olena', dishId: 'salad', name: 'Salad', forWhom: 'Olena', unitPrice: 12, quantity: 2 };
    controller.expectOne('https://test.api/guest/orders/test-order').flush({ status: 'ChangesAwaitingConfirmation', confirmed: [salad], pending: [{ ...salad, quantity: 3 }], notices: [], confirmedTotal: 24, canChange: true });
    await Promise.resolve();
    const open = fixture.componentInstance.openMenu();
    controller.expectOne('https://test.api/guest/orders/test-order/menu').flush({ items: [{ id: 'salad', name: 'Salad', price: 12, category: 'Salads', available: true }], canChange: true }); await open;
    const add = fixture.componentInstance.addDish({ dishId: 'salad', forWhom: 'Olena', quantity: 2 });
    const proposal = controller.expectOne('https://test.api/guest/orders/test-order/dishes'); expect(proposal.request.body.quantity).toBe(5);
    proposal.flush({ status: 'ChangesAwaitingConfirmation', confirmed: [salad], pending: [{ ...salad, quantity: 5 }], notices: [], confirmedTotal: 24, canChange: true }); await add;
  });
  it('keeps recovered orders read-only when the server has closed changes', async () => {
    sessionStorage.setItem('guest-order-access:test-order', 'personal'); const fixture = setup('order=test-order');
    const line = { key: 'salad:Olena', dishId: 'salad', name: 'Salad', forWhom: 'Olena', unitPrice: 12, quantity: 2 };
    controller.expectOne('https://test.api/guest/orders/test-order').flush({ status: 'Confirmed', confirmed: [line], pending: null, notices: [], confirmedTotal: 24, canChange: false }); await Promise.resolve(); fixture.detectChanges();
    await fixture.componentInstance.change(line, 'increase'); await fixture.componentInstance.openMenu();
    controller.expectNone(() => true);
    expect(fixture.nativeElement.textContent).toContain('Ви можете переглядати замовлення');
    const increase = fixture.nativeElement.querySelector('[aria-label="Додати порцію Salad"]') as HTMLButtonElement;
    expect(increase.disabled).toBeTrue();
  });
  it('informs the guest when the organizer cancelled the reservation', async () => {
    sessionStorage.setItem('guest-order-access:test-order', 'personal'); const fixture = setup('order=test-order');
    controller.expectOne('https://test.api/guest/orders/test-order').flush({ status: 'Confirmed', confirmed: [], pending: null,
      notices: [], confirmedTotal: 0, canChange: false, reservationCancelled: true });
    await Promise.resolve(); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Організатор скасував бронювання');
  });
  it('requires a complete link', async () => {
    const fixture = setup('order=test-order');
    await fixture.componentInstance.confirm();
    expect(fixture.nativeElement.textContent).toContain('Посилання неповне');
    controller.expectNone(() => true);
  });
  it('confirms once and fetches the personal order with the new capability', async () => {
    const fixture = setup();
    controller.expectOne('https://test.api/guest/orders/test-order/confirmation-preview').flush({ confirmed: [], pending: [] });
    await Promise.resolve();
    const action = fixture.componentInstance.confirm();
    void fixture.componentInstance.confirm();
    const confirmation = controller.expectOne('https://test.api/guest/orders/test-order/confirm');
    expect(confirmation.request.method).toBe('POST');
    expect(confirmation.request.body).toEqual({ token: 'test-confirmation' });
    confirmation.flush({ guestAccessToken: 'test-personal-access' });
    await Promise.resolve();
    const order = controller.expectOne('https://test.api/guest/orders/test-order');
    expect(order.request.headers.get('Authorization')).toBe('Bearer test-personal-access');
    order.flush({ status: 'Confirmed', confirmed: [], pending: null, notices: [], confirmedTotal: 24 });
    await action;
    expect(location.replaceState).toHaveBeenCalledWith('/guest-confirmation#order=test-order');
    expect(fixture.componentInstance.confirmed()).toBeTrue();
    expect(fixture.componentInstance.order()?.confirmedTotal).toBe(24);
  });
  it('restores personal access after reload and immediately applies a decrease', async () => {
    sessionStorage.setItem('guest-order-access:test-order', 'personal');
    const fixture = setup('order=test-order');
    const line = { key: 'salad:Olena', dishId: 'salad', name: 'Salad', forWhom: 'Olena', unitPrice: 12, quantity: 2 };
    controller.expectOne('https://test.api/guest/orders/test-order').flush({
      status: 'Confirmed', confirmed: [line], pending: null, notices: [], confirmedTotal: 24,
    });
    await Promise.resolve();
    const action = fixture.componentInstance.change(line, 'decrease');
    const decrease = controller.expectOne('https://test.api/guest/orders/test-order/reduce');
    expect(decrease.request.headers.get('Authorization')).toBe('Bearer personal');
    expect(decrease.request.body).toEqual({ lineKey: line.key, quantity: 1 });
    decrease.flush({ status: 'Confirmed', confirmed: [{ ...line, quantity: 1 }], pending: null,
      notices: [{ key: line.key, message: 'Кількість порцій зменшена до 1' }], confirmedTotal: 12 });
    await action; fixture.detectChanges();
    expect(fixture.componentInstance.order()?.confirmedTotal).toBe(12);
    expect(fixture.nativeElement.textContent).toContain('Кількість порцій зменшена до 1');
  });
  it('keeps confirmed totals while requesting confirmation of extra portions', async () => {
    sessionStorage.setItem('guest-order-access:test-order', 'personal');
    const fixture = setup('order=test-order');
    const line = { key: 'salad:Olena', dishId: 'salad', name: 'Salad', forWhom: 'Olena', unitPrice: 12, quantity: 2 };
    controller.expectOne('https://test.api/guest/orders/test-order').flush({
      status: 'Confirmed', confirmed: [line], pending: null, notices: [], confirmedTotal: 24,
    });
    await Promise.resolve();
    const action = fixture.componentInstance.change(line, 'increase');
    const increase = controller.expectOne('https://test.api/guest/orders/test-order/dishes');
    expect(increase.request.body).toEqual({ dishId: 'salad', forWhom: 'Olena', quantity: 3 });
    increase.flush({ status: 'ChangesAwaitingConfirmation', confirmed: [line], pending: [{ ...line, quantity: 3 }], notices: [], confirmedTotal: 24 });
    await action; fixture.detectChanges();
    expect(fixture.componentInstance.order()?.confirmedTotal).toBe(24);
    expect(fixture.nativeElement.textContent).toContain('Зміни очікують підтвердження');
    const request = fixture.componentInstance.requestChanges();
    controller.expectOne('https://test.api/guest/orders/test-order/request-confirmation').flush({}, { status: 202, statusText: 'Accepted' });
    await request;
    expect(fixture.componentInstance.message()).toContain('Запит на лист прийнято');
  });
});
