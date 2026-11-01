import { GuestInvite } from '@app/models/reservations/guest-invite.model';
import { GroupSessionsResponse } from '@app/models/group-orders/group-sessions-response.model';
import { DEMO_INVITE, DEMO_OWNER_SESSION_ID, GroupOrdersMockStore } from './group-orders.mock-store';

/**
 * Статичні дані для РІВНЯ 1 (верстка): імпортуй у сторінку замість HTTP.
 * Ті самі дані, що віддає мок-бекенд, тож на рівні 2 екран виглядатиме так само.
 */
export const GROUP_ORDER_PREVIEW: {
  invite: GuestInvite;
  sessions: GroupSessionsResponse;
  ownerSessionId: string;
} = {
  invite: DEMO_INVITE,
  sessions: new GroupOrdersMockStore().snapshot(),
  ownerSessionId: DEMO_OWNER_SESSION_ID,
};
