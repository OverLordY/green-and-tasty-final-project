import { inject, Injectable } from '@angular/core';
import { API_BASE_URL } from '@app/shared/config/api-base-url.token';
import { HttpClient, HttpContext } from '@angular/common/http';
import { SKIP_ERROR_TOAST } from '@app/auth/skip-error-toast.context';
import { Observable, catchError, concatMap, from, map, of, switchMap, toArray } from 'rxjs';
import { GroupOrdersService } from '../group-orders/group-orders.service';
import { TimeRange } from '@app/models/tables/time-range.model';
import {
  Reservation,
  ReservationCustomer,
  MakeReservationRequestBody,
  CancelReservationResponse
} from '@app/models/reservations/reservation.model';
import { FeedbackReservationResponseItem } from '@app/models/feedbacks/feedback-reservation-response-item.model';

@Injectable({
  providedIn: 'root',
})
export class ReservationService {
  private readonly baseUrl = inject(API_BASE_URL);
  private http: HttpClient = inject(HttpClient);
  private readonly groupOrders = inject(GroupOrdersService);

  // makeReservation$(tableId: string, timeRange: TimeRange, guests: number): Observable<Reservation> {
  //   const url = `${this.baseUrl}/reservations`;
  //   const body = {
  //     tableId: tableId,
  //     startTime: timeRange.start,
  //     endTime: timeRange.end,
  //     guestCount: guests,
  //   };

  //   return this.http.post<Reservation>(url, body);
  // }

  makeReservation$(
    tableId: string,
    timeRange: TimeRange,
    guests: number,
    customerEmail?: string | null,
    skipErrorToast = false
  ): Observable<Reservation> {
    const url = `${this.baseUrl}/reservations`;
    const body: MakeReservationRequestBody = {
      tableId: tableId,
      startTime: timeRange.start,
      endTime: timeRange.end,
      guestCount: guests,
    };
    console.log('[SEND] POST body:', body);

    if (customerEmail != null) {
      body.customerEmail = customerEmail;
    }

    return this.http.post<Reservation>(url, body, {
      context: new HttpContext().set(SKIP_ERROR_TOAST, skipErrorToast),
    });
  }

  editReservation$(reservationId: string, timeRange: TimeRange, guestCount: number): Observable<Reservation> {
    return this.http.put<Reservation>(`${this.baseUrl}/reservations`, {
      reservationId, guestCount, startTime: timeRange.start, endTime: timeRange.end,
    });
  }

  editOwnerReservation$(
    reservationId: string,
    timeRange: TimeRange,
    guestCount: number
  ): Observable<{ reservation: Reservation; notificationsPending: boolean }> {
    const url = `${this.baseUrl}/reservations/edit`;
    const body = {
      reservationId,
      guestCount,
      startTime: timeRange.start,
      endTime: timeRange.end,
    };

    return this.http.post<{ reservation: Reservation; groupId: string; notificationIds: string[] }>(url, body).pipe(
      switchMap(response => response.notificationIds.length
        ? from(response.notificationIds).pipe(
            concatMap(id => this.groupOrders.sendNotification$(response.groupId, id).pipe(catchError(() => of(null)))),
            toArray(), map(statuses => ({ reservation: response.reservation, notificationsPending: statuses.some(status => !status?.guestAccepted || !status.ownerAccepted) })),
          ) : of({ reservation: response.reservation, notificationsPending: false })),
    );
  }

  cancelReservation$(reservationId: string): Observable<CancelReservationResponse> {
    const url = `${this.baseUrl}/reservations`;

    return this.http.delete<CancelReservationResponse>(url, { body: { id: reservationId } }).pipe(
      switchMap(response => response.groupId && response.notificationIds?.length
        ? from(response.notificationIds).pipe(
            concatMap(id => this.groupOrders.sendNotification$(response.groupId!, id).pipe(catchError(() => of(null)))),
            toArray(), map(statuses => ({ ...response, notificationsPending: statuses.some(status => !status?.guestAccepted || !status.ownerAccepted) })),
          ) : of(response)),
    );
  }

  getReservationsForUser$(): Observable<Reservation[]> {
    const url = `${this.baseUrl}/reservations`;
    return this.http.get<Reservation[]>(url);
  }
  getRestaurantReservations$(): Observable<Reservation[]> {
    return this.http.get<Reservation[]>(`${this.baseUrl}/restaurant/reservations`, {
      context: new HttpContext().set(SKIP_ERROR_TOAST, true),
    });
  }

  getReservationFeedback(
    reservationId: string
  ): Observable<FeedbackReservationResponseItem | null> {
    return this.http.get<FeedbackReservationResponseItem>(
      `${this.baseUrl}/reservations/${reservationId}/feedback`
    );
  }

  getReservationById(reservationId: string): Observable<ReservationCustomer> {
    return this.http.get<ReservationCustomer>(`${this.baseUrl}/reservations/${reservationId}`);
  }

  changeReservationStatus$(id: string, newStatus: number): Observable<{ id: string; status: number }> {
    const url = `${this.baseUrl}/restaurant/reservations/${encodeURIComponent(id)}/status`;
    const body = { newStatus };
    return this.http.post<{ id: string; status: number }>(url, body);
  }
}
