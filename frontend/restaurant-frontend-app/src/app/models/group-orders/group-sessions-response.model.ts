import { Participant } from './participant.model';

/** GET /guest/groups/{groupReservationId}/sessions */
export interface GroupSessionsResponse {
  notificationId?: string | null;
  version?: number;
  participants: Participant[];
  /** Сума ВСІХ учасників, включно з тими, хто ще не підтвердив кошик. */
  total: number;
}
