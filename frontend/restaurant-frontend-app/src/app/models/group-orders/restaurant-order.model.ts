export interface RestaurantOrderReceipt {
  orderId: string; reservationId: string; locationId: string; tableNumber: number;
  reservationStart: string; submittedAt: string; total: number;
  items: { dishId: string; name: string; quantity: number; total: number }[];
}
