import { CurrencyPipe, DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { finalize } from 'rxjs';
import { RestaurantOrderReceipt } from '../../models/group-orders/restaurant-order.model';
import { RestaurantOrderService } from '../../services/group-orders/restaurant-order.service';

@Component({
  selector: 'app-restaurant-order-page', imports: [CurrencyPipe, DatePipe],
  template: `<main><h1>Спільне замовлення для ресторану</h1>
    <button type="button" [disabled]="loading()" (click)="load()">Оновити</button>
    @if (loading()) { <p role="status">Завантажуємо замовлення…</p> }
    @if (error()) { <p role="alert">{{ error() }}</p> }
    @if (order(); as receipt) {
      <p>Столик {{ receipt.tableNumber }} · {{ receipt.reservationStart | date:'dd.MM.yyyy HH:mm' }}</p>
      <p>Надіслано організатором: {{ receipt.submittedAt | date:'dd.MM.yyyy HH:mm' }}</p>
      <p>Це зафіксоване спільне замовлення. Непідтверджені страви до нього не входять.</p>
      <ul>@for (item of receipt.items; track item.dishId) { <li>{{ item.name }} × {{ item.quantity }} <strong>{{ item.total | currency:'USD' }}</strong></li> }</ul>
      <p class="total">Разом: {{ receipt.total | currency:'USD' }}</p>
    }
  </main>`,
  styles: `main { max-width: 800px; margin: 40px auto; padding: 24px; color: #233c2a; } h1 { font-size: 28px; } p { margin: 16px 0; line-height: 1.6; } ul { padding: 0; list-style: none; } li { display: flex; justify-content: space-between; padding: 16px 0; border-bottom: 1px solid #cfdece; gap: 16px; } .total { font-weight: bold; } button { padding: 12px; border-radius: 8px; background: #146b33; color: white; border: 0; cursor: pointer; } [role=alert] { color: #a62924; }`,
})
export class RestaurantOrderPageComponent implements OnInit {
  private readonly service = inject(RestaurantOrderService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);
  readonly order = signal<RestaurantOrderReceipt | null>(null);
  readonly loading = signal(false);
  readonly error = signal('');
  ngOnInit(): void { this.load(); }
  load(): void {
    if (this.loading()) return;
    this.loading.set(true); this.error.set(''); this.order.set(null);
    this.service.get$(this.route.snapshot.paramMap.get('reservationId') ?? '').pipe(
      takeUntilDestroyed(this.destroyRef), finalize(() => this.loading.set(false)),
    ).subscribe({ next: receipt => this.order.set(receipt), error: (error: HttpErrorResponse) => this.error.set(
      error.status === 401 ? 'Увійдіть як працівник ресторану.' : error.status === 403 ? 'Немає доступу до замовлень цієї локації.'
        : error.status === 404 ? 'Надісланого замовлення ще немає або бронювання не знайдено.' : 'Не вдалося завантажити замовлення. Спробуйте ще раз.'),
    });
  }
}
