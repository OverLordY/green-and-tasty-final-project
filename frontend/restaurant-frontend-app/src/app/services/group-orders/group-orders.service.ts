import { HttpClient, HttpContext } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { map, Observable, of, switchMap, timer } from 'rxjs';
import { SKIP_ERROR_TOAST } from '../../auth/skip-error-toast.context';
import { OWNER_GROUP_REQUEST } from '../../auth/owner-group-request.context';
import { GroupSessionsResponse } from '../../models/group-orders/group-sessions-response.model';
import { GroupStatusResponse } from '../../models/group-orders/group-status.model';
import { OwnerSessionResponse } from '../../models/group-orders/owner-session.model';
import {
  SubmittedDish,
  SubmitGroupPreOrderRequest,
} from '../../models/group-orders/submit-group-pre-order.model';
import { GuestInvite } from '../../models/reservations/guest-invite.model';
import { API_BASE_URL } from '../../shared/config/api-base-url.token';
import { NotificationDelivery, OrderAuditEntry, OwnerBulkChangeResult, OwnerChangeResult, OwnerLineChange, OwnerOrderMenu } from '../../models/group-orders/owner-order-change.model';
import { GuestInviteService } from '../reservation/guest-invite.service';

@Injectable({ providedIn: 'root' })
export class GroupOrdersService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(API_BASE_URL);
  private readonly guestInviteService = inject(GuestInviteService);

  getOwnerMenu$(groupId: string): Observable<OwnerOrderMenu> {
    return this.http.get<OwnerOrderMenu>(this.ownerUrl(groupId) + '/menu');
  }
  changeGuestDish$(groupId: string, orderId: string, change: OwnerLineChange): Observable<OwnerChangeResult> {
    return this.http.post<OwnerChangeResult>(`${this.ownerUrl(groupId)}/orders/${encodeURIComponent(orderId)}/dishes`, change);
  }
  changeOwnDish$(groupId: string, change: OwnerLineChange): Observable<GroupSessionsResponse> {
    return this.http.post<GroupSessionsResponse>(`${this.ownerUrl(groupId)}/own-order/dishes`, change);
  }
  clearGuestOrder$(groupId: string, orderId: string): Observable<OwnerChangeResult> {
    return this.http.post<OwnerChangeResult>(`${this.ownerUrl(groupId)}/orders/${encodeURIComponent(orderId)}/clear`, {});
  }
  remindGuest$(groupId: string, orderId: string): Observable<OwnerChangeResult> {
    return this.http.post<OwnerChangeResult>(`${this.ownerUrl(groupId)}/orders/${encodeURIComponent(orderId)}/remind`, {});
  }
  clearGroupOrders$(groupId: string): Observable<OwnerBulkChangeResult> {
    return this.http.post<OwnerBulkChangeResult>(this.ownerUrl(groupId) + '/orders/clear', {});
  }
  getHistory$(groupId: string): Observable<OrderAuditEntry[]> {
    return this.http.get<OrderAuditEntry[]>(this.ownerUrl(groupId) + '/history');
  }
  getNotifications$(groupId: string): Observable<NotificationDelivery[]> {
    return this.http.get<NotificationDelivery[]>(this.ownerUrl(groupId) + '/notifications');
  }
  sendNotification$(groupId: string, id: string): Observable<NotificationDelivery> {
    const send = (attempt: number): Observable<NotificationDelivery> =>
      this.http.post<NotificationDelivery>(`${this.ownerUrl(groupId)}/notifications/${encodeURIComponent(id)}/send`, {},
        { context: new HttpContext().set(SKIP_ERROR_TOAST, true) }).pipe(
        switchMap(status => status.guestAccepted && status.ownerAccepted || attempt >= 3
          ? of(status)
          : timer(attempt * 2000).pipe(switchMap(() => send(attempt + 1)))),
      );
    return send(1);
  }
  private ownerUrl(groupId: string): string { return `${this.baseUrl}/owner/groups/${encodeURIComponent(groupId)}`; }

  getGroupInvite$(reservationId: string): Observable<GuestInvite> {
    return this.guestInviteService
      .getInviteToken$(reservationId)
      .pipe(switchMap(({ token }) => this.guestInviteService.getInvite$(token)));
  }

  getSessions$(
    groupReservationId: string,
    options: { silent?: boolean } = {},
  ): Observable<GroupSessionsResponse> {
    return this.http.get<GroupSessionsResponse>(
      `${this.baseUrl}/guest/groups/${encodeURIComponent(groupReservationId)}/sessions`,
      { context: new HttpContext().set(OWNER_GROUP_REQUEST, true).set(SKIP_ERROR_TOAST, options.silent === true) },
    );
  }

  getOwnerSessionId$(reservationId: string): Observable<string> {
    return this.http
      .get<OwnerSessionResponse>(
        `${this.baseUrl}/reservations/${encodeURIComponent(reservationId)}/guest-session-id`,
      )
      .pipe(map((response) => response.guestSessionId));
  }

  getStatus$(
    groupReservationId: string,
    options: { silent?: boolean } = {},
  ): Observable<GroupStatusResponse> {
    return this.http.get<GroupStatusResponse>(
      `${this.baseUrl}/guest/groups/${encodeURIComponent(groupReservationId)}/status`,
      { context: new HttpContext().set(OWNER_GROUP_REQUEST, true).set(SKIP_ERROR_TOAST, options.silent === true) },
    );
  }

  submit$(groupReservationId: string, reservationId: string, expectedVersion?: number): Observable<SubmittedDish[]> {
    const request: SubmitGroupPreOrderRequest = { reservationId, ...(expectedVersion === undefined ? {} : { expectedVersion }) };
    return this.http.post<SubmittedDish[]>(
      `${this.baseUrl}/group-reservations/${encodeURIComponent(groupReservationId)}/submit`,
      request,
    );
  }

  removeSession$(groupReservationId: string, guestSessionId: string): Observable<GroupSessionsResponse> {
    return this.http.delete<GroupSessionsResponse>(
      `${this.baseUrl}/guest/groups/${encodeURIComponent(groupReservationId)}/sessions/${encodeURIComponent(guestSessionId)}`,
      { context: new HttpContext().set(OWNER_GROUP_REQUEST, true) },
    );
  }
}
