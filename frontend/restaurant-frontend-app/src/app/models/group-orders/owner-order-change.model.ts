import { GroupSessionsResponse } from './group-sessions-response.model';
import { GuestMenuEntry } from './guest-confirmation.model';

export interface OwnerLineChange { dishId: string; forWhom: string; quantity: number; }
export interface OwnerChangeResult { snapshot: GroupSessionsResponse; notificationId: string; }
export interface OwnerBulkChangeResult { snapshot: GroupSessionsResponse; notificationIds: string[]; }
export interface OwnerOrderMenu { items: GuestMenuEntry[]; changeDeadline: string; canChange: boolean; }
export interface OrderAuditEntry { id: string; guestOrderId: string; action: string; at: string; summary: string; }
export interface NotificationDelivery { id: string; guestAccepted: boolean; ownerAccepted: boolean; summary?: string; }
