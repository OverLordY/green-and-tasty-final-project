import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { GroupSessionsResponse } from '../../models/group-orders/group-sessions-response.model';
import { GroupStatusResponse } from '../../models/group-orders/group-status.model';
import { SubmittedDish } from '../../models/group-orders/submit-group-pre-order.model';
import { GuestInvite } from '../../models/reservations/guest-invite.model';
import { GroupOrdersService } from '../../services/group-orders/group-orders.service';
import { ToastService } from '../../services/toast/toast.service';
import { GroupOrderPageComponent } from './group-order-page.component';

const SYNC_INTERVAL = 15_000;
const INVITE: GuestInvite = {
  GroupReservationId: 'group-resilience', LocationId: 'location', RestaurantName: 'Green & Tasty',
  LocationAddress: '48 Rustaveli Avenue', StartTime: '2026-10-28T16:00:00Z',
  EndTime: '2026-10-28T17:30:00Z', MaxGuestCount: 5, TableNumber: 1, InviterName: 'Owner',
};
const SESSIONS: GroupSessionsResponse = {
  participants: [
    { id: 'owner', type: 'Owner', name: 'Owner', isSubmitted: true, total: 10,
      items: [{ dish: { id: 'salad', name: 'Salad', price: 10 }, quantity: 1 }] },
    { id: 'guest', type: 'Guest', name: 'Alex', isSubmitted: true, total: 20,
      items: [{ dish: { id: 'salad', name: 'Salad', price: 10 }, quantity: 2 }] },
  ], total: 30,
};
const OPEN_STATUS: GroupStatusResponse = { currentParticipants: 2, maxCapacity: 5, status: 'Open' };

function buttonByText(element: ParentNode, label: string): HTMLButtonElement | undefined {
  return Array.from(element.querySelectorAll<HTMLButtonElement>('button'))
    .find((button) => button.textContent?.trim() === label);
}

describe('Owner page resilience beyond supplied homework tests', () => {
  let service: jasmine.SpyObj<GroupOrdersService>;
  let fixture: ComponentFixture<GroupOrderPageComponent>;

  beforeEach(() => {
    service = jasmine.createSpyObj<GroupOrdersService>('GroupOrdersService', [
      'getGroupInvite$', 'getSessions$', 'getOwnerSessionId$', 'getStatus$', 'submit$', 'removeSession$',
    ]);
    service.getGroupInvite$.and.returnValue(of(INVITE));
    service.getSessions$.and.returnValue(of(structuredClone(SESSIONS)));
    service.getOwnerSessionId$.and.returnValue(of('owner'));
    service.getStatus$.and.returnValue(of(OPEN_STATUS));
    TestBed.configureTestingModule({
      imports: [GroupOrderPageComponent],
      providers: [
        { provide: GroupOrdersService, useValue: service },
        { provide: ToastService, useValue: jasmine.createSpyObj('ToastService', ['success', 'error', 'info']) },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ reservationId: 'resilience' }) } } },
      ],
    });
  });

  function create(): GroupOrderPageComponent {
    fixture = TestBed.createComponent(GroupOrderPageComponent);
    fixture.detectChanges();
    return fixture.componentInstance;
  }

  afterEach(() => fixture?.destroy());

  it('keeps the initial loading state until all initial requests finish', fakeAsync(() => {
    const pending = new Subject<GroupSessionsResponse>();
    service.getSessions$.and.returnValue(pending);
    const page = create();
    expect(page.loading()).toBeTrue();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Loading group pre-order');
    tick(SYNC_INTERVAL * 2);
    expect(service.getSessions$).toHaveBeenCalledTimes(1);
    pending.next(structuredClone(SESSIONS));
    pending.complete();
    fixture.detectChanges();
    expect(page.loading()).toBeFalse();
    expect(page.participants().length).toBe(2);
    fixture.destroy();
  }));

  it('clears private group data when access expires during refresh', fakeAsync(() => {
    const page = create();
    expect(page.participants().length).toBe(2);
    service.getSessions$.and.returnValue(throwError(() => new HttpErrorResponse({ status: 401 })));
    page.refresh();
    expect(page.participants()).toEqual([]);
    expect(page.invite()).toBeNull();
    expect(page.error()).toContain('Доступ завершився');
    tick(SYNC_INTERVAL * 2);
    expect(service.getSessions$).toHaveBeenCalledTimes(2);
    fixture.destroy();
  }));

  it('does not overlap a slow background refresh with timer or manual refresh', fakeAsync(() => {
    const page = create();
    const pending = new Subject<GroupSessionsResponse>();
    service.getSessions$.and.returnValue(pending);
    tick(SYNC_INTERVAL);
    expect(page.refreshing()).toBeTrue();
    page.refresh();
    tick(SYNC_INTERVAL * 3);
    expect(service.getSessions$).toHaveBeenCalledTimes(2);
    expect(service.getStatus$).toHaveBeenCalledTimes(2);
    pending.next(structuredClone(SESSIONS));
    pending.complete();
    expect(page.refreshing()).toBeFalse();
    service.getSessions$.and.returnValue(of(structuredClone(SESSIONS)));
    tick(SYNC_INTERVAL);
    expect(service.getSessions$).toHaveBeenCalledTimes(3);
    fixture.destroy();
  }));

  it('does not overlap manual refresh with the next timer tick', fakeAsync(() => {
    const page = create();
    const pending = new Subject<GroupSessionsResponse>();
    service.getSessions$.and.returnValue(pending);
    page.refresh();
    tick(SYNC_INTERVAL * 2);
    expect(service.getSessions$).toHaveBeenCalledTimes(2);
    expect(page.refreshing()).toBeTrue();
    pending.next(structuredClone(SESSIONS));
    pending.complete();
    expect(page.refreshing()).toBeFalse();
    fixture.destroy();
  }));

  it('retry replaces the polling schedule rather than adding another timer', fakeAsync(() => {
    const page = create();
    tick(10_000);
    page.load();
    tick(5_000);
    expect(service.getSessions$).toHaveBeenCalledTimes(2);
    tick(10_000);
    expect(service.getSessions$).toHaveBeenCalledTimes(3);
    fixture.destroy();
  }));

  it('does not start polling after initial failure and recovers on retry', fakeAsync(() => {
    service.getGroupInvite$.and.returnValue(throwError(() => new HttpErrorResponse({ status: 500 })));
    const page = create();
    expect(page.loading()).toBeFalse();
    expect(page.error()).toBeTruthy();
    tick(SYNC_INTERVAL * 2);
    expect(service.getSessions$).not.toHaveBeenCalled();
    service.getGroupInvite$.and.returnValue(of(INVITE));
    page.load();
    expect(page.error()).toBe('');
    tick(SYNC_INTERVAL);
    expect(service.getSessions$).toHaveBeenCalledTimes(2);
    fixture.destroy();
  }));

  it('remote Submitted closes pending dialogs and prevents later refresh calls', fakeAsync(() => {
    const page = create();
    page.openSubmit();
    page.chooseRemoval(SESSIONS.participants[1]);
    service.getStatus$.and.returnValue(of({ ...OPEN_STATUS, status: 'Submitted' }));
    tick(SYNC_INTERVAL);
    expect(page.isOpen()).toBeFalse();
    expect(page.submitDialog()).toBeFalse();
    expect(page.selectedGuest()).toBeNull();
    const sessionRequests = service.getSessions$.calls.count();
    const statusRequests = service.getStatus$.calls.count();
    tick(SYNC_INTERVAL * 3);
    page.refresh();
    expect(service.getSessions$.calls.count()).toBe(sessionRequests);
    expect(service.getStatus$.calls.count()).toBe(statusRequests);
    fixture.destroy();
  }));

  it('blocks refresh and a second mutation while submit is pending', fakeAsync(() => {
    const pending = new Subject<SubmittedDish[]>();
    service.submit$.and.returnValue(pending);
    const page = create();
    page.openSubmit();
    page.submit();
    const sessionRequests = service.getSessions$.calls.count();
    page.submit();
    page.refresh();
    page.chooseRemoval(SESSIONS.participants[1]);
    tick(SYNC_INTERVAL * 2);
    expect(service.submit$).toHaveBeenCalledTimes(1);
    expect(service.getSessions$.calls.count()).toBe(sessionRequests);
    expect(page.selectedGuest()).toBeNull();
    pending.next([{ dishId: 'salad', quantity: 3 }]);
    pending.complete();
    expect(page.status()).toBe('Submitted');
    fixture.destroy();
  }));

  it('requires the owner to review again when polling finds a changed order', fakeAsync(() => {
    service.getSessions$.and.returnValue(of({ ...structuredClone(SESSIONS), version: 5 }));
    const page = create();
    page.openSubmit(); expect(page.submitDialog()).toBeTrue();
    service.getSessions$.and.returnValue(of({ ...structuredClone(SESSIONS), version: 6 }));
    tick(SYNC_INTERVAL);
    expect(page.submitDialog()).toBeFalse();
    expect(page.groupActionMessage()).toContain('Замовлення змінилося');
    expect(service.submit$).not.toHaveBeenCalled();
    fixture.destroy();
  }));

  it('sends the reviewed version and blocks resubmission after a server conflict until refresh', fakeAsync(() => {
    service.getSessions$.and.returnValue(of({ ...structuredClone(SESSIONS), version: 5 }));
    service.submit$.and.returnValue(throwError(() => new HttpErrorResponse({ status: 409 })));
    const page = create(); page.openSubmit(); page.submit();
    expect(service.submit$).toHaveBeenCalledWith('group-resilience', 'resilience', 5);
    expect(page.refreshError()).toBeTrue(); expect(page.canSubmit()).toBeFalse();
    service.getSessions$.and.returnValue(of({ ...structuredClone(SESSIONS), version: 6 }));
    page.refresh();
    expect(page.refreshError()).toBeFalse(); expect(page.snapshotVersion()).toBe(6);
    expect(page.canSubmit()).toBeTrue();
    fixture.destroy();
  }));

  it('refreshes the reviewed snapshot before opening the submit confirmation', fakeAsync(() => {
    service.getSessions$.and.returnValue(of({ ...structuredClone(SESSIONS), version: 5 }));
    service.submit$.and.returnValue(of([{ dishId: 'salad', quantity: 3 }]));
    const page = create();
    service.getSessions$.and.returnValue(of({ ...structuredClone(SESSIONS), version: 6 }));
    page.openSubmit();
    expect(page.submitDialog()).toBeTrue();
    expect(page.snapshotVersion()).toBe(6);
    page.submit();
    expect(service.submit$).toHaveBeenCalledWith('group-resilience', 'resilience', 6);
    fixture.destroy();
  }));

  it('does not open the submit confirmation when its fresh snapshot cannot be loaded', fakeAsync(() => {
    const page = create();
    service.getSessions$.and.returnValue(throwError(() => new HttpErrorResponse({ status: 503 })));
    page.openSubmit();
    expect(page.submitDialog()).toBeFalse();
    expect(page.refreshError()).toBeTrue();
    expect(service.submit$).not.toHaveBeenCalled();
    fixture.destroy();
  }));

  it('removal cancels a pending polling snapshot so it cannot restore a removed guest', fakeAsync(() => {
    const page = create();
    page.chooseRemoval(SESSIONS.participants[1]);
    const stale = new Subject<GroupSessionsResponse>();
    service.getSessions$.and.returnValue(stale);
    tick(SYNC_INTERVAL);
    expect(stale.observed).toBeTrue();
    const remaining = { participants: [structuredClone(SESSIONS.participants[0])], total: 10 };
    service.removeSession$.and.returnValue(of(remaining));
    page.removeGuest();
    expect(stale.observed).toBeFalse();
    stale.next(structuredClone(SESSIONS));
    stale.complete();
    expect(page.participants().map((person) => person.id)).toEqual(['owner']);
    expect(page.consolidated().total).toBe(10);
    fixture.destroy();
  }));

  it('Cancel in either dialog never issues a mutation request', fakeAsync(() => {
    const page = create();
    const element = fixture.nativeElement as HTMLElement;
    page.openSubmit();
    fixture.detectChanges();
    buttonByText(element.querySelector('[role="dialog"]')!, 'Cancel')!.click();
    fixture.detectChanges();
    expect(page.submitDialog()).toBeFalse();
    expect(service.submit$).not.toHaveBeenCalled();
    page.chooseRemoval(SESSIONS.participants[1]);
    fixture.detectChanges();
    buttonByText(element.querySelector('[role="dialog"]')!, 'Cancel')!.click();
    fixture.detectChanges();
    expect(page.selectedGuest()).toBeNull();
    expect(service.removeSession$).not.toHaveBeenCalled();
    fixture.destroy();
  }));
});
