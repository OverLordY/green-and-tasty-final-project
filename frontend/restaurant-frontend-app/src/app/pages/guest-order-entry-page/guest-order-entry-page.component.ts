import { CurrencyPipe, DatePipe } from '@angular/common';
import { HttpClient, HttpContext, HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { SKIP_ERROR_TOAST } from '../../auth/skip-error-toast.context';
import { API_BASE_URL } from '../../shared/config/api-base-url.token';
import { GuestConfirmedLine, GuestConfirmedOrder, GuestDraftCredentials, GuestMenuEntry, GuestMenuPreview } from '../../models/group-orders/guest-confirmation.model';
import { GuestAccessRecoveryComponent } from '../../components/group-orders/guest-access-recovery/guest-access-recovery.component';

@Component({
  selector: 'app-guest-order-entry-page',
  imports: [CurrencyPipe, DatePipe, FormsModule, GuestAccessRecoveryComponent],
  templateUrl: './guest-order-entry-page.component.html',
  styleUrl: './guest-order-entry-page.component.scss',
})
export class GuestOrderEntryPageComponent implements OnInit {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(API_BASE_URL);
  readonly inviteToken = inject(ActivatedRoute).snapshot.paramMap.get('token') ?? '';
  private credentials: GuestDraftCredentials | null = null;
  private remote: GuestConfirmedOrder | null = null;
  readonly preview = signal<GuestMenuPreview | null>(null);
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly error = signal('');
  readonly emailSent = signal(false);
  readonly guestName = signal('');
  readonly email = signal('');
  readonly recipient = signal('');
  readonly category = signal('');
  readonly selection = signal<GuestConfirmedLine[]>([]);
  readonly maskedEmail = signal('');
  readonly hasDraft = signal(false);
  readonly categories = computed(() => [...new Set(this.preview()?.items.map(item => item.category) ?? [])]);
  readonly visibleMenu = computed(() => (this.preview()?.items ?? []).filter(item => !this.category() || item.category === this.category()));
  readonly total = computed(() => this.selection().reduce((sum, item) => sum + item.unitPrice * item.quantity, 0));
  readonly canEdit = computed(() => !!this.preview()?.canChange && !this.busy());

  ngOnInit(): void { void this.load(); }
  private options(token?: string) {
    return { context: new HttpContext().set(SKIP_ERROR_TOAST, true),
      ...(token ? { headers: { Authorization: 'Bearer ' + token } } : {}) };
  }
  async load(): Promise<void> {
    this.loading.set(true); this.error.set('');
    try {
      this.preview.set(await firstValueFrom(this.http.post<GuestMenuPreview>(
        this.baseUrl + '/guest/orders/preview', { inviteToken: this.inviteToken }, this.options())));
    } catch { this.error.set('Не вдалося відкрити запрошення. Перевірте посилання або спробуйте ще раз.'); }
    finally { this.loading.set(false); }
  }
  add(dish: GuestMenuEntry): void {
    if (!this.canEdit() || !dish.available) return;
    const forWhom = this.recipient().trim() || 'Для мене';
    if (forWhom.length > 100) { this.error.set('Ім’я одержувача має містити до 100 символів.'); return; }
    const key = dish.id + ':' + forWhom;
    const current = this.selection().find(item => item.key === key);
    if (current && current.quantity >= 99) { this.error.set('Максимум 99 порцій однієї страви для одного отримувача.'); return; }
    this.selection.set(current
      ? this.selection().map(item => item.key === key ? { ...item, quantity: item.quantity + 1 } : item)
      : [...this.selection(), { key, dishId: dish.id, name: dish.name, forWhom, unitPrice: dish.price, quantity: 1 }]);
    this.emailSent.set(false);
  }
  decrease(key: string): void {
    if (!this.canEdit()) return;
    this.selection.set(this.selection().map(item => item.key === key ? { ...item, quantity: item.quantity - 1 } : item).filter(item => item.quantity > 0));
    this.emailSent.set(false);
  }
  async send(): Promise<void> {
    if (!this.canEdit() || this.selection().length === 0) return;
    if (!this.credentials && (!this.guestName().trim() || this.guestName().trim().length > 100 ||
      !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(this.email().trim()) || this.email().trim().length > 254)) {
      this.error.set('Вкажіть ім’я та коректну електронну адресу.'); return;
    }
    this.busy.set(true); this.error.set(''); this.emailSent.set(false);
    try {
      if (!this.credentials) {
        this.credentials = await firstValueFrom(this.http.post<GuestDraftCredentials>(this.baseUrl + '/guest/orders',
          { inviteToken: this.inviteToken, guestName: this.guestName().trim(), email: this.email().trim() }, this.options()));
        this.hasDraft.set(true);
      }
      const endpoint = this.baseUrl + '/guest/orders/' + encodeURIComponent(this.credentials.orderId);
      const options = this.options(this.credentials.draftAccessToken);
      this.remote = await firstValueFrom(this.http.get<GuestConfirmedOrder>(endpoint, options));
      this.maskedEmail.set(this.remote.maskedEmail ?? '');
      // Absolute quantities allow retries after a partially completed update.
      const desired = this.selection();
      for (const existing of this.remote.pending ?? this.remote.confirmed) {
        if (!desired.some(item => item.key === existing.key)) {
          this.remote = await firstValueFrom(this.http.post<GuestConfirmedOrder>(endpoint + '/reduce', { lineKey: existing.key, quantity: 0 }, options));
        }
      }
      for (const item of desired) {
        const existing: GuestConfirmedLine | undefined = (this.remote.pending ?? this.remote.confirmed).find(line => line.key === item.key);
        if (existing?.quantity === item.quantity) continue;
        this.remote = existing && existing.quantity > item.quantity
          ? await firstValueFrom(this.http.post<GuestConfirmedOrder>(endpoint + '/reduce', { lineKey: item.key, quantity: item.quantity }, options))
          : await firstValueFrom(this.http.post<GuestConfirmedOrder>(endpoint + '/dishes',
            { dishId: item.dishId, forWhom: item.forWhom, quantity: item.quantity }, options));
      }
      await firstValueFrom(this.http.post(endpoint + '/request-confirmation', {}, options));
      this.emailSent.set(true);
    } catch (failure) {
      const status = failure instanceof HttpErrorResponse ? failure.status : 0;
      this.error.set(status === 503 ? 'Лист не вдалося надіслати. Спробуйте пізніше.'
        : status === 401 ? 'Доступ змінився. Для керування підтвердженим замовленням відкрийте персональне посилання з листа.'
        : status === 409 ? 'Не вдалося зберегти зміни або повторно надіслати лист. Перевірте термін оформлення та зачекайте щонайменше 30 секунд перед повторним надсиланням.'
        : 'Не вдалося завершити дію. Спробуйте ще раз.');
    } finally { this.busy.set(false); }
  }
}
