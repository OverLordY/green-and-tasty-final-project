import { HttpClient, HttpContext } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { API_BASE_URL } from '../../shared/config/api-base-url.token';
import { SKIP_ERROR_TOAST } from '../../auth/skip-error-toast.context';
import { RestaurantOrderReceipt } from '../../models/group-orders/restaurant-order.model';

@Injectable({ providedIn: 'root' })
export class RestaurantOrderService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(API_BASE_URL);
  get$(reservationId: string) {
    return this.http.get<RestaurantOrderReceipt>(`${this.baseUrl}/restaurant/reservations/${encodeURIComponent(reservationId)}/pre-order`,
      { context: new HttpContext().set(SKIP_ERROR_TOAST, true) });
  }
}
