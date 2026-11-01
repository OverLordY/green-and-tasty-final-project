export type GroupReservationStatus = 'Open' | 'Submitted' | 'Closed';

/** GET /guest/groups/{groupReservationId}/status */
export interface GroupStatusResponse {
  currentParticipants: number;
  maxCapacity: number;
  status: GroupReservationStatus;
  submissionOverdue?: boolean;
}
