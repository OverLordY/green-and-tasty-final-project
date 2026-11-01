import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { of, throwError } from 'rxjs';
import { GroupOrdersService } from '../../services/group-orders/group-orders.service';
import { ToastService } from '../../services/toast/toast.service';
import { GuestInvite } from '../../models/reservations/guest-invite.model';
import { GroupOrderPageComponent } from './group-order-page.component';

describe('Owner clears group dishes', () => {
  let service: jasmine.SpyObj<GroupOrdersService>;
  beforeEach(() => {
    service = jasmine.createSpyObj<GroupOrdersService>('GroupOrdersService', ['getGroupInvite$', 'getSessions$', 'getOwnerSessionId$', 'getStatus$', 'clearGroupOrders$', 'sendNotification$']);
    service.getGroupInvite$.and.returnValue(of({ GroupReservationId: 'group', StartTime: '2030-01-01T19:00:00Z' } as GuestInvite));
    service.getSessions$.and.returnValue(of({ total: 24, participants: [{ id: 'owner', name: 'Owner', type: 'Owner', isSubmitted: true,
      total: 24, items: [{ dish: { id: 'salad', name: 'Salad', price: 12 }, quantity: 2 }] }] }));
    service.getOwnerSessionId$.and.returnValue(of('owner'));
    service.getStatus$.and.returnValue(of({ currentParticipants: 1, maxCapacity: 5, status: 'Open' }));
    service.clearGroupOrders$.and.returnValue(of({ snapshot: { total: 0, participants: [] }, notificationIds: ['n1', 'n2'] }));
    service.sendNotification$.and.returnValue(of({ id: 'n', guestAccepted: true, ownerAccepted: true }));
    TestBed.configureTestingModule({ imports: [GroupOrderPageComponent], providers: [
      { provide: GroupOrdersService, useValue: service },
      { provide: ToastService, useValue: jasmine.createSpyObj('ToastService', ['success', 'error']) },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ reservationId: 'reservation' }) } } },
    ] });
  });
  it('requires an explicit confirmation and immediately applies the returned snapshot', () => {
    const fixture = TestBed.createComponent(GroupOrderPageComponent); fixture.detectChanges(); const c = fixture.componentInstance;
    c.clearAllOrders(); expect(service.clearGroupOrders$).not.toHaveBeenCalled();
    c.openGroupClear(); fixture.detectChanges(); expect(fixture.nativeElement.textContent).toContain('Очистити всі страви групи?');
    c.clearAllOrders(); fixture.detectChanges(); expect(service.clearGroupOrders$).toHaveBeenCalledWith('group');
    expect(c.consolidated().total).toBe(0); expect(c.clearGroupDialog()).toBeFalse();
    expect(service.sendNotification$).toHaveBeenCalledTimes(2);
  });
  it('keeps the cleared snapshot and offers notification retry if mail requests fail', () => {
    service.sendNotification$.and.returnValue(throwError(() => new Error('offline')));
    const fixture = TestBed.createComponent(GroupOrderPageComponent); fixture.detectChanges(); const c = fixture.componentInstance;
    c.openGroupClear(); c.clearAllOrders();
    expect(c.consolidated().total).toBe(0); expect(c.groupActionMessage()).toContain('Ненадіслані листи');
    expect(service.clearGroupOrders$).toHaveBeenCalledTimes(1);
  });
});
