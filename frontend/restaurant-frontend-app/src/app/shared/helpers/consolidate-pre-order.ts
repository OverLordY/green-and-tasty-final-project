export interface Dish {
  id: string;
  name: string;
  price: number;
}

export interface Participant {
  id: string;
  name: string;
  isSubmitted: boolean;
  items: { dish: Dish; quantity: number }[];
}

export interface ConsolidatedOrder {
  items: { dish: Dish; quantity: number }[];
  total: number;
}

export function consolidatePreOrder(participants: Participant[]): ConsolidatedOrder {
  const byDish = new Map<string, { dish: Dish; quantity: number }>();

  for (const participant of participants.filter((person) => person.isSubmitted)) {
    for (const item of participant.items) {
      const existing = byDish.get(item.dish.id);
      // завжди НОВИЙ об'єкт — вхідні дані не змінюються
      byDish.set(item.dish.id, { dish: item.dish, quantity: (existing?.quantity ?? 0) + item.quantity });
    }
  }

  const items = [...byDish.values()];
  const total = items.reduce((sum, item) => sum + item.dish.price * item.quantity, 0);
  return { items, total };
}
