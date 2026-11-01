import { HttpClient, HttpContext } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { SKIP_ERROR_TOAST } from '../../auth/skip-error-toast.context';
import { GuestAccessRequested, GuestConfirmationReceipt, GuestConfirmedOrder, GuestConfirmedLine, GuestDishSelection, GuestPersonalMenu } from '../../models/group-orders/guest-confirmation.model';
import { API_BASE_URL } from '../../shared/config/api-base-url.token';

@Injectable({ providedIn: 'root' })
export class GuestPersonalOrderService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(API_BASE_URL);
  private endpoint(orderId: string): string { return this.baseUrl + '/guest/orders/' + encodeURIComponent(orderId); }
  private options(access?: string) {
    return { context: new HttpContext().set(SKIP_ERROR_TOAST, true), ...(access ? { headers: { Authorization: 'Bearer ' + access } } : {}) };
  }
  get$(orderId: string, access: string): Observable<GuestConfirmedOrder> {
    return this.http.get<GuestConfirmedOrder>(this.endpoint(orderId), this.options(access));
  }
  menu$(orderId: string, access: string): Observable<GuestPersonalMenu> {
    return this.http.get<GuestPersonalMenu>(this.endpoint(orderId) + '/menu', this.options(access));
  }
  confirm$(orderId: string, token: string, recovery = false): Observable<GuestConfirmationReceipt> {
    return this.http.post<GuestConfirmationReceipt>(this.endpoint(orderId) + (recovery ? '/recover-access' : '/confirm'), { token }, this.options());
  }
  preview$(orderId: string, token: string): Observable<{ confirmed: GuestConfirmedLine[]; pending: GuestConfirmedLine[] }> {
    return this.http.post<{ confirmed: GuestConfirmedLine[]; pending: GuestConfirmedLine[] }>(this.endpoint(orderId) + '/confirmation-preview', { token }, this.options());
  }
  propose$(orderId: string, access: string, selection: GuestDishSelection): Observable<GuestConfirmedOrder> {
    return this.http.post<GuestConfirmedOrder>(this.endpoint(orderId) + '/dishes', selection, this.options(access));
  }
  reduce$(orderId: string, access: string, lineKey: string, quantity: number): Observable<GuestConfirmedOrder> {
    return this.http.post<GuestConfirmedOrder>(this.endpoint(orderId) + '/reduce', { lineKey, quantity }, this.options(access));
  }
  requestConfirmation$(orderId: string, access: string): Observable<void> {
    return this.http.post<void>(this.endpoint(orderId) + '/request-confirmation', {}, this.options(access));
  }
  requestAccess$(email: string, context: { orderId?: string; inviteToken?: string }): Observable<GuestAccessRequested> {
    return context.orderId
      ? this.http.post<GuestAccessRequested>(this.endpoint(context.orderId) + '/request-access', { email }, this.options())
      : this.http.post<GuestAccessRequested>(this.baseUrl + '/guest/orders/recover-access', { email, inviteToken: context.inviteToken ?? '' }, this.options());
  }
}
