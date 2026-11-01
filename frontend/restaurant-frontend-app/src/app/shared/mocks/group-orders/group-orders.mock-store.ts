import { Participant } from '@app/models/group-orders/participant.model';
import { GroupSessionsResponse } from '@app/models/group-orders/group-sessions-response.model';
import { GroupReservationStatus, GroupStatusResponse } from '@app/models/group-orders/group-status.model';
import { SubmittedDish } from '@app/models/group-orders/submit-group-pre-order.model';
import { GuestInvite } from '@app/models/reservations/guest-invite.model';

/**
 * DEV-ONLY in-memory «бекенд» для групового pre-order.
 * Повторює правила з homework-backend.md. Скидається після перезавантаження сторінки.
 */
export const DEMO_RESERVATION_ID = 'demo-reservation';
export const DEMO_GROUP_ID = 'demo-group';
export const DEMO_INVITE_TOKEN = 'demo-invite-token';
export const DEMO_OWNER_SESSION_ID = 'gs-owner';

/** Відповідь GET /guest/invites/{token} для демо-бронювання (поля — як у GuestInvite). */
export const DEMO_INVITE: GuestInvite = {
  GroupReservationId: DEMO_GROUP_ID,
  LocationId: 'demo-location',
  RestaurantName: 'Green & Tasty',
  LocationAddress: 'Rustaveli Avenue 48',
  StartTime: '2026-10-21T16:00:00Z',
  EndTime: '2026-10-21T17:30:00Z',
  MaxGuestCount: 5,
  TableNumber: 7,
  InviterName: 'Yevhenii',
};

export class MockHttpError {
  constructor(readonly status: number, readonly message: string) {}
}

const dish = (id: string, name: string, price: number, image: string, weight: string) => ({
  id, name, price, weight, imageUrl: `assets/img/dishes/${image}.jpg`,
});
const SPRING = dish('dish-spring-salad', 'Spring Salad', 12, 'spring-salad', '430 g');
const AVOCADO = dish('dish-avocado-bowl', 'Avocado Pine Nut Bowl', 14.5, 'avocado-pine-nut-bowl', '380 g');
const STRAWBERRY = dish('dish-strawberry-salad', 'Fresh Strawberry Mint Salad', 11, 'fresh-strawberry-mint-salad', '350 g');
const POTATO = dish('dish-potato-lentil', 'Roasted Sweet Potato & Lentil Salad', 13, 'roasted-sweet-potato-and-lentil-salad', '400 g');

function initialParticipants(): Participant[] {
  return [
    { id: DEMO_OWNER_SESSION_ID, type: 'Owner', name: 'Yevhenii', isSubmitted: true, total: 0,
      items: [{ dish: SPRING, quantity: 1 }, { dish: AVOCADO, quantity: 1 }] },
    { id: 'gs-kateryna', type: 'Guest', name: 'Kateryna', isSubmitted: true, total: 0,
      items: [{ dish: SPRING, quantity: 2 }] },
    { id: 'gs-mariia', type: 'Guest', name: 'Mariia', isSubmitted: false, total: 0,
      items: [{ dish: STRAWBERRY, quantity: 1 }, { dish: POTATO, quantity: 1 }] },
    { id: 'gs-mariana', type: 'Guest', name: 'Mariana', isSubmitted: false, total: 0, items: [] },
  ];
}

const sum = (participant: Participant) =>
  participant.items.reduce((acc, item) => acc + item.dish.price * item.quantity, 0);

export class GroupOrdersMockStore {
  private participants = initialParticipants();
  private status: GroupReservationStatus = 'Open';
  readonly maxCapacity = 5;

  reset(): void {
    this.participants = initialParticipants();
    this.status = 'Open';
  }

  snapshot(): GroupSessionsResponse {
    const participants = this.participants.map((p) => structuredClone({ ...p, total: sum(p) }));
    return { participants, total: participants.reduce((acc, p) => acc + p.total, 0) };
  }

  groupStatus(): GroupStatusResponse {
    return { currentParticipants: this.participants.length, maxCapacity: this.maxCapacity, status: this.status };
  }

  submit(reservationId: string): SubmittedDish[] {
    if (this.status === 'Submitted') throw new MockHttpError(400, 'Group reservation was submitted.');
    if (this.status !== 'Open') throw new MockHttpError(400, `Cannot submit order. Group reservation is '${this.status}'.`);
    if (reservationId !== DEMO_RESERVATION_ID) throw new MockHttpError(400, 'Active order not found for this reservation.');

    const submitted = this.participants.filter((p) => p.isSubmitted);
    if (submitted.length === 0) throw new MockHttpError(400, 'No submitted guest sessions found to consolidate.');

    const merged = new Map<string, number>();
    for (const item of submitted.flatMap((p) => p.items)) {
      merged.set(item.dish.id, (merged.get(item.dish.id) ?? 0) + item.quantity);
    }
    if (merged.size === 0) throw new MockHttpError(400, 'No dishes selected in the submitted guest sessions.');

    this.status = 'Submitted';
    return [...merged].map(([dishId, quantity]) => ({ dishId, quantity }));
  }

  remove(guestSessionId: string): GroupSessionsResponse {
    const participant = this.participants.find((p) => p.id === guestSessionId);
    if (!participant) throw new MockHttpError(404, `Guest session with id ${guestSessionId} not found.`);
    if (participant.type === 'Owner') throw new MockHttpError(400, 'Owner session cannot be removed.');
    this.participants = this.participants.filter((p) => p.id !== guestSessionId);
    return this.snapshot();
  }
}
