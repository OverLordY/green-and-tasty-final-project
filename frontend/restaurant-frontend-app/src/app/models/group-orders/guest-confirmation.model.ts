export interface GuestConfirmedLine {
  key: string;
  dishId: string;
  name: string;
  forWhom: string;
  unitPrice: number;
  quantity: number;
}
export interface GuestConfirmedOrder {
  status: 'Choosing' | 'AwaitingEmail' | 'Confirmed' | 'ChangesAwaitingConfirmation' | 'Cancelled';
  confirmed: GuestConfirmedLine[];
  pending: GuestConfirmedLine[] | null;
  notices: { key: string; message: string; dishName?: string; forWhom?: string }[];
  confirmedTotal: number;
  guestName?: string;
  maskedEmail?: string;
  changeDeadline?: string;
  ownerChangeSummary?: string | null;
  canChange?: boolean;
  submissionOverdue?: boolean;
  reservationCancelled?: boolean;
}
export interface GuestConfirmationReceipt { guestAccessToken: string | null; }
export interface GuestMenuEntry {
  id: string; name: string; price: number; available: boolean;
  category: string; imageUrl?: string; weight?: string;
}
export interface GuestMenuPreview {
  invite: { RestaurantName: string; LocationAddress: string; StartTime: string };
  items: GuestMenuEntry[];
  deadlines: { changesClose: string; submissionCloses: string };
  canChange: boolean;
}
export interface GuestDraftCredentials { orderId: string; draftAccessToken: string; }
export interface GuestPersonalMenu { items: GuestMenuEntry[]; changeDeadline?: string; canChange: boolean; }
export interface GuestDishSelection { dishId: string; forWhom: string; quantity: number; }
export interface GuestAccessRequested { message: string; }
