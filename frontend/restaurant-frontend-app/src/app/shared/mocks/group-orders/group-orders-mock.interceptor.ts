import { HttpErrorResponse, HttpInterceptorFn, HttpResponse } from '@angular/common/http';
import { Observable, switchMap, throwError, timer } from 'rxjs';
import {
  DEMO_GROUP_ID,
  DEMO_INVITE,
  DEMO_INVITE_TOKEN,
  DEMO_OWNER_SESSION_ID,
  DEMO_RESERVATION_ID,
  GroupOrdersMockStore,
  MockHttpError,
} from './group-orders.mock-store';
import { isDemoAuthorization } from '../auth/demo-auth';

const LATENCY_MS = 400;
export const groupOrdersMockStore = new GroupOrdersMockStore();

/**
 * DEV-ONLY. Перехоплює запити ЛИШЕ для демо-бронювання `demo-reservation` / `demo-group`.
 * Усі інші запити йдуть на справжній API без змін.
 * Відкрий: /reservations/demo-reservation/group-order?demo  (?demo — мок-логін, див. shared/mocks/auth/demo-auth.ts)
 */
export const groupOrdersMockInterceptor: HttpInterceptorFn = (req, next) => {
  const path = req.url.split('?')[0];
  const store = groupOrdersMockStore;
  const groupBase = `/guest/groups/${DEMO_GROUP_ID}/sessions`;

  const respond = (handler: () => unknown): Observable<HttpResponse<unknown>> =>
    timer(LATENCY_MS).pipe(
      switchMap(() => {
        try {
          return [new HttpResponse({ status: 200, url: req.url, body: handler() })];
        } catch (error) {
          if (error instanceof MockHttpError) {
            return throwError(() => new HttpErrorResponse({ status: error.status, url: req.url, error: { error: error.message } }));
          }
          throw error;
        }
      }),
    );

  if (req.method === 'GET' && path.endsWith(`/reservations/${DEMO_RESERVATION_ID}/invite-token`)) {
    return respond(() => ({ token: DEMO_INVITE_TOKEN }));
  }
  if (req.method === 'GET' && path.endsWith(`/guest/invites/${DEMO_INVITE_TOKEN}`)) {
    return respond(() => ({ ...DEMO_INVITE, MaxGuestCount: store.maxCapacity }));
  }
  if (req.method === 'GET' && path.endsWith(`/reservations/${DEMO_RESERVATION_ID}/guest-session-id`)) {
    return respond(() => ({ guestSessionId: DEMO_OWNER_SESSION_ID }));
  }
  if (req.method === 'GET' && path.endsWith(groupBase)) {
    return respond(() => store.snapshot());
  }
  if (req.method === 'GET' && path.endsWith(`/guest/groups/${DEMO_GROUP_ID}/status`)) {
    return respond(() => store.groupStatus());
  }
  if (req.method === 'POST' && path.endsWith(`/group-reservations/${DEMO_GROUP_ID}/submit`)) {
    return respond(() => store.submit((req.body as { reservationId?: string } | null)?.reservationId ?? ''));
  }
  if (req.method === 'DELETE' && path.includes(`${groupBase}/`)) {
    const guestSessionId = decodeURIComponent(path.slice(path.lastIndexOf('/') + 1));
    return respond(() => store.remove(guestSessionId));
  }

  // Демо-логін (?demo): справжній API демо-токен не прийме, тож кошик у шапці показуємо порожнім.
  if (req.method === 'GET' && path.endsWith('/orders') && isDemoAuthorization(req.headers.get('Authorization'))) {
    return respond(() => []);
  }

  return next(req);
};
