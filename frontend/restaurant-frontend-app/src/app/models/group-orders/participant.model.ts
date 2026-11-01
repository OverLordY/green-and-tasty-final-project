export type ParticipantType = 'Owner' | 'Guest';

export interface DishShort {
  id: string;
  name: string;
  price: number;
  imageUrl?: string;
  weight?: string;
}

export interface ParticipantItem {
  dish: DishShort;
  quantity: number;
  forWhom?: string;
  lineKey?: string;
}

export interface Participant {
  id: string;
  type: ParticipantType;
  name: string;
  items: ParticipantItem[];
  total: number;
  isSubmitted: boolean;
  pendingItems?: ParticipantItem[] | null;
  confirmationStatus?: string | null;
  ownerChangeSummary?: string | null;
  contactEmailMasked?: string | null;
  notices?: { key: string; message: string; dishName?: string; forWhom?: string }[] | null;
}
