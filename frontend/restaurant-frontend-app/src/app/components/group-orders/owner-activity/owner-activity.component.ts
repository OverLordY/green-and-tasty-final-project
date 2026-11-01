import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { finalize, forkJoin } from 'rxjs';
import { NotificationDelivery, OrderAuditEntry } from '../../../models/group-orders/owner-order-change.model';
import { GroupOrdersService } from '../../../services/group-orders/group-orders.service';

@Component({
  selector: 'app-owner-activity', imports: [DatePipe], changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section aria-label="Історія та сповіщення">
      <h2>Історія та сповіщення</h2>
      <button type="button" [disabled]="busy()" (click)="load()">Оновити історію</button>
      @for (item of history(); track item.id) { <p>{{ item.at | date:'dd.MM HH:mm' }} · {{ item.summary }}</p> }
      @if (!busy() && !history().length) { <p>Організатор ще не змінював страви гостей.</p> }
      @for (notice of notifications(); track notice.id) {
        @if (!notice.guestAccepted || !notice.ownerAccepted) {
          <p>{{ notice.summary }} · Очікує надсилання: {{ !notice.guestAccepted ? 'лист гостю' : '' }} {{ !notice.ownerAccepted ? 'лист організатору' : '' }}.</p>
          <button type="button" [disabled]="busy()" (click)="retry(notice.id)">Повторити надсилання</button>
        }
      }
      @if (busy()) { <p role="status">Оновлюємо…</p> }
      @if (error()) { <p role="alert">{{ error() }}</p> }
    </section>`,
  styles: `section { margin: 24px 0; padding: 20px; background: #f4f8f3; border-radius: 12px; } p { margin: 12px 0; } button { padding: 8px 12px; cursor: pointer; }`,
})
export class OwnerActivityComponent implements OnInit {
  readonly groupId = input.required<string>();
  private readonly service = inject(GroupOrdersService);
  private readonly destroyRef = inject(DestroyRef);
  readonly history = signal<OrderAuditEntry[]>([]);
  readonly notifications = signal<NotificationDelivery[]>([]);
  readonly busy = signal(false);
  readonly error = signal('');
  ngOnInit(): void { this.load(); }
  load(): void {
    if (this.busy()) return;
    this.busy.set(true); this.error.set('');
    forkJoin({ history: this.service.getHistory$(this.groupId()), notifications: this.service.getNotifications$(this.groupId()) })
      .pipe(takeUntilDestroyed(this.destroyRef), finalize(() => this.busy.set(false))).subscribe({
        next: result => { this.history.set(result.history); this.notifications.set(result.notifications); },
        error: () => this.error.set('Не вдалося завантажити історію та статуси листів.'),
      });
  }
  retry(id: string): void {
    if (this.busy()) return;
    this.busy.set(true); this.error.set('');
    this.service.sendNotification$(this.groupId(), id).pipe(takeUntilDestroyed(this.destroyRef), finalize(() => this.busy.set(false))).subscribe({
      next: status => this.notifications.update(list => list.map(n => n.id === id ? status : n)),
      error: () => this.error.set('Листи ще не надіслано. Спробуйте пізніше.'),
    });
  }
}
