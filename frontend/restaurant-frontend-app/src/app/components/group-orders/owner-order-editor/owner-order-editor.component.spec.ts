import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { GroupOrdersService } from '../../../services/group-orders/group-orders.service';
import { Participant } from '../../../models/group-orders/participant.model';
import { OwnerChangeResult } from '../../../models/group-orders/owner-order-change.model';
import { OwnerOrderEditorComponent } from './owner-order-editor.component';

describe('Organizer guest dish editor', () => {
  let service: jasmine.SpyObj<GroupOrdersService>;
  const guest: Participant = { id: 'guest', name: 'Andrii', type: 'Guest', isSubmitted: true, total: 24,
    confirmationStatus: 'Confirmed', items: [{ dish: { id: 'salad', name: 'Salad', price: 12 }, quantity: 2, forWhom: 'Olena', lineKey: 'salad:Olena' }] };
  const result: OwnerChangeResult = { snapshot: { participants: [guest], total: 24 }, notificationId: 'notice' };
  beforeEach(() => {
    service = jasmine.createSpyObj<GroupOrdersService>('GroupOrdersService', ['getOwnerMenu$', 'changeGuestDish$', 'changeOwnDish$', 'clearGuestOrder$', 'remindGuest$', 'sendNotification$']);
    service.getOwnerMenu$.and.returnValue(of({ items: [{ id: 'salad', name: 'Salad', price: 12, category: 'Salads', available: true }],
      changeDeadline: '2030-01-01T17:00:00Z', canChange: true }));
    service.changeGuestDish$.and.returnValue(of(result)); service.clearGuestOrder$.and.returnValue(of(result));
    service.remindGuest$.and.returnValue(of(result));
    service.sendNotification$.and.returnValue(of({ id: 'notice', guestAccepted: true, ownerAccepted: true }));
    TestBed.configureTestingModule({ imports: [OwnerOrderEditorComponent], providers: [{ provide: GroupOrdersService, useValue: service }] });
  });
  function create() {
    const fixture = TestBed.createComponent(OwnerOrderEditorComponent);
    fixture.componentRef.setInput('groupId', 'group'); fixture.componentRef.setInput('participant', structuredClone(guest)); fixture.detectChanges();
    return fixture;
  }
  it('selects an existing recipient without a mutation until Apply', () => {
    const fixture = create(); const c = fixture.componentInstance;
    c.edit(guest.items[0]); expect(service.changeGuestDish$).not.toHaveBeenCalled();
    c.quantity = 1; c.apply();
    expect(service.changeGuestDish$).toHaveBeenCalledWith('group', 'guest', { dishId: 'salad', forWhom: 'Olena', quantity: 1 });
    expect(service.sendNotification$).toHaveBeenCalledWith('group', 'notice');
  });
  it('uses zero to remove a line and rejects fractional or negative quantities locally', () => {
    const c = create().componentInstance; c.edit(guest.items[0]);
    c.quantity = -1; c.apply(); c.quantity = 1.5; c.apply(); expect(service.changeGuestDish$).not.toHaveBeenCalled();
    c.quantity = 0; c.apply(); expect(service.changeGuestDish$).toHaveBeenCalledTimes(1);
  });
  it('explains that an email label does not change the confirmation recipient', () => {
    const fixture = create(); const c = fixture.componentInstance;
    c.dishId = 'salad'; c.forWhom = 'other@example.test'; c.quantity = 1; c.apply();
    expect(service.changeGuestDish$).not.toHaveBeenCalled();
    expect(c.error()).toContain('Вкажіть ім’я');
    expect(fixture.nativeElement.textContent).toContain('не змінює адресу листа');
  });
  it('explains an unchanged quantity without sending a request', () => {
    const c = create().componentInstance; c.edit(guest.items[0]); c.apply();
    expect(service.changeGuestDish$).not.toHaveBeenCalled();
    expect(c.error()).toContain('Кількість не змінилася');
  });
  it('requires separate confirmation before clearing dishes', () => {
    const c = create().componentInstance; c.clear(); expect(service.clearGuestOrder$).not.toHaveBeenCalled();
    c.confirmClear.set(true); c.clear(); expect(service.clearGuestOrder$).toHaveBeenCalledWith('group', 'guest');
  });
  it('allows reminders only for pending dishes and sends the persisted notification', () => {
    const fixture = create(); const c = fixture.componentInstance;
    c.remind(); expect(service.remindGuest$).not.toHaveBeenCalled();
    fixture.componentRef.setInput('participant', { ...guest, pendingItems: guest.items }); fixture.detectChanges();
    c.remind(); expect(service.remindGuest$).toHaveBeenCalledWith('group', 'guest');
    expect(service.sendNotification$).toHaveBeenCalledWith('group', 'notice');
  });
  it('reports saved dishes accurately even when mail fails, without applying the change twice', () => {
    service.sendNotification$.and.returnValue(throwError(() => new Error('offline')));
    const c = create().componentInstance; const saved = jasmine.createSpy('saved'); c.saved.subscribe(saved);
    c.edit(guest.items[0]); c.quantity = 1; c.apply();
    expect(saved).toHaveBeenCalledWith(result.snapshot); expect(c.message()).toContain('Зміни збережено');
    expect(c.message()).toContain('очікують надсилання'); expect(service.changeGuestDish$).toHaveBeenCalledTimes(1); expect(c.busy()).toBeFalse();
  });
  it('blocks repeat Apply and closing while a mutation is in progress', () => {
    const pending = new Subject<OwnerChangeResult>(); service.changeGuestDish$.and.returnValue(pending);
    const c = create().componentInstance; const closed = jasmine.createSpy('closed'); c.closed.subscribe(closed);
    c.edit(guest.items[0]); c.quantity = 1; c.apply(); c.apply(); c.close();
    expect(service.changeGuestDish$).toHaveBeenCalledTimes(1); expect(closed).not.toHaveBeenCalled();
  });
  it('keeps controls closed when the server says the deadline has passed', () => {
    service.getOwnerMenu$.and.returnValue(of({ items: [], canChange: false, changeDeadline: '2030-01-01T17:00:00Z' }));
    const fixture = create(); const c = fixture.componentInstance; c.edit(guest.items[0]); c.quantity = 1; c.apply();
    expect(service.changeGuestDish$).not.toHaveBeenCalled(); expect(fixture.nativeElement.textContent).toContain('Редагування закрито');
  });
  it('saves the organizer’s own dishes without using the guest confirmation flow', () => {
    const fixture = create(); const c = fixture.componentInstance;
    const owner: Participant = { id: 'owner', name: 'Owner', type: 'Owner', isSubmitted: false, total: 0, items: [] };
    fixture.componentRef.setInput('participant', owner); fixture.detectChanges();
    service.changeOwnDish$.and.returnValue(of({ participants: [owner], total: 24 }));
    const saved = jasmine.createSpy('saved'); c.saved.subscribe(saved);
    c.dishId = 'salad'; c.forWhom = 'Owner'; c.quantity = 2; c.apply();
    expect(service.changeOwnDish$).toHaveBeenCalledWith('group', { dishId: 'salad', forWhom: 'Owner', quantity: 2 });
    expect(service.changeGuestDish$).not.toHaveBeenCalled();
    expect(service.sendNotification$).not.toHaveBeenCalled();
    expect(saved).toHaveBeenCalled(); expect(c.message()).toContain('Ваші страви збережено');
  });
});
