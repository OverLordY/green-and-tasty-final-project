import { CurrencyPipe, DatePipe, Location } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { Observable, firstValueFrom, interval } from 'rxjs';
import { GuestAccessRecoveryComponent } from '../../components/group-orders/guest-access-recovery/guest-access-recovery.component';
import { GuestPersonalMenuComponent } from '../../components/group-orders/guest-personal-menu/guest-personal-menu.component';
import { GuestConfirmedLine, GuestConfirmedOrder, GuestDishSelection, GuestPersonalMenu } from '../../models/group-orders/guest-confirmation.model';
import { GuestPersonalOrderService } from '../../services/group-orders/guest-personal-order.service';

@Component({
  selector: 'app-guest-confirmation-page',
  imports: [CurrencyPipe, DatePipe, GuestAccessRecoveryComponent, GuestPersonalMenuComponent],
  templateUrl: './guest-confirmation-page.component.html',
  styleUrl: './guest-confirmation-page.component.scss',
})
export class GuestConfirmationPageComponent implements OnInit {
  private readonly service = inject(GuestPersonalOrderService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly location = inject(Location);
  private readonly parameters = new URLSearchParams(inject(ActivatedRoute).snapshot.fragment ?? '');
  readonly orderId = this.parameters.get('order') ?? '';
  private readonly confirmationToken = this.parameters.get('token') ?? '';
  private readonly recoveryToken = this.parameters.get('recovery') ?? '';
  readonly isRecoveryLink = !!this.recoveryToken;
  private accessToken = this.storedAccess();
  readonly validLink = this.orderId.length > 0 && !(this.confirmationToken && this.recoveryToken)
    && (!!this.confirmationToken || !!this.recoveryToken || !!this.accessToken);
  readonly busy = signal(false);
  // Preserved for existing callers: true means the personal order has been opened.
  // Email access recovery does not imply that pending dishes have been confirmed.
  readonly confirmed = signal(false);
  readonly preview = signal<{ confirmed: GuestConfirmedLine[]; pending: GuestConfirmedLine[] } | null>(null);
  previewTotal(lines: GuestConfirmedLine[]): number { return lines.reduce((sum, line) => sum + line.unitPrice * line.quantity, 0); }
  readonly order = signal<GuestConfirmedOrder | null>(null);
  readonly menu = signal<GuestPersonalMenu | null>(null);
  readonly showMenu = signal(false);
  readonly error = signal('');
  readonly message = signal('');
  private readonly now = signal(Date.now());
  readonly canEdit = computed(() => {
    const order = this.order();
    return !!order && order.canChange !== false && order.status !== 'Cancelled'
      && (!order.changeDeadline || this.now() < Date.parse(order.changeDeadline));
  });
  readonly statusLabel = computed(() => {
    const labels: Record<GuestConfirmedOrder['status'], string> = {
      Choosing: 'Страви ще не підтверджені', AwaitingEmail: 'Очікує підтвердження через пошту',
      Confirmed: 'Замовлення підтверджено', ChangesAwaitingConfirmation: 'Зміни очікують підтвердження', Cancelled: 'Замовлення скасовано',
    };
    const status = this.order()?.status;
    return status ? labels[status] : '';
  });
  ngOnInit(): void {
    interval(1000).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.now.set(Date.now()));
    if (this.accessToken && !this.confirmationToken && !this.recoveryToken) void this.resume();
    if (this.confirmationToken && !this.recoveryToken && this.validLink) void this.loadPreview();
  }
  private async loadPreview(): Promise<void> {
    this.busy.set(true); this.error.set('');
    try { this.preview.set(await this.request(this.service.preview$(this.orderId, this.confirmationToken))); }
    catch (failure) {
      this.error.set(failure instanceof HttpErrorResponse && [400, 401, 404, 409].includes(failure.status)
        ? 'Посилання вже використане, термін його дії завершився або замовлення змінилося. Відновіть доступ через пошту нижче.'
        : 'Не вдалося завантажити замовлення. Перевірте з’єднання та спробуйте ще раз.');
    }
    finally { this.busy.set(false); }
  }
  private request<T>(request: Observable<T>): Promise<T> {
    return firstValueFrom(request.pipe(takeUntilDestroyed(this.destroyRef)));
  }
  private storedAccess(): string {
    try { return sessionStorage.getItem('guest-order-access:' + this.orderId) ?? ''; } catch { return ''; }
  }
  private remember(access: string): void {
    this.accessToken = access;
    try { sessionStorage.setItem('guest-order-access:' + this.orderId, access); } catch { /* Access can be recovered through email. */ }
  }
  private async resume(): Promise<void> {
    this.busy.set(true); this.error.set('');
    try {
      this.order.set(await this.request(this.service.get$(this.orderId, this.accessToken)));
      this.confirmed.set(true);
    } catch (failure) {
      if (failure instanceof HttpErrorResponse && failure.status === 401) {
        this.accessToken = '';
        try { sessionStorage.removeItem('guest-order-access:' + this.orderId); } catch { /* Storage can be disabled. */ }
      }
      this.error.set('Не вдалося відкрити замовлення. Відновіть доступ через пошту нижче або спробуйте оновити сторінку.');
    } finally { this.busy.set(false); }
  }
  async refresh(): Promise<void> { if (this.accessToken && !this.busy()) await this.resume(); }
  async change(line: GuestConfirmedLine, action: 'increase' | 'decrease' | 'remove'): Promise<void> {
    if (!this.accessToken || this.busy() || !this.canEdit()) return;
    const current = this.order()?.pending?.find(item => item.key === line.key) ?? line;
    const quantity = action === 'remove' ? 0 : current.quantity + (action === 'increase' ? 1 : -1);
    if (quantity > 99) { this.error.set('Максимум 99 порцій однієї страви для одного отримувача.'); return; }
    this.busy.set(true); this.error.set(''); this.message.set('');
    try {
      this.order.set(await this.request(action === 'increase'
        ? this.service.propose$(this.orderId, this.accessToken, { dishId: line.dishId, forWhom: line.forWhom, quantity })
        : this.service.reduce$(this.orderId, this.accessToken, line.key, quantity)));
      if (action === 'increase') this.message.set('Додаткові порції очікують підтвердження. Надішліть новий лист.');
    } catch { this.error.set('Не вдалося змінити замовлення. Оновіть дані та перевірте термін змін.'); }
    finally { this.busy.set(false); }
  }
  async openMenu(): Promise<void> {
    if (!this.accessToken || this.busy() || !this.canEdit()) return;
    this.busy.set(true); this.error.set('');
    try {
      const menu = await this.request(this.service.menu$(this.orderId, this.accessToken));
      this.menu.set(menu); this.showMenu.set(true);
      if (!menu.canChange) { this.order.update(order => order ? { ...order, canChange: false } : null); }
    } catch { this.error.set('Не вдалося завантажити меню. Спробуйте ще раз.'); }
    finally { this.busy.set(false); }
  }
  async addDish(selection: GuestDishSelection): Promise<void> {
    const menu = this.menu();
    if (!this.accessToken || this.busy() || !this.canEdit() || !menu?.canChange) return;
    const dish = menu.items.find(item => item.id === selection.dishId);
    const recipient = selection.forWhom.trim();
    const current = (this.order()?.pending ?? this.order()?.confirmed ?? []).find(line => line.dishId === selection.dishId && line.forWhom === recipient);
    const quantity = (current?.quantity ?? 0) + selection.quantity;
    if (!dish?.available || !recipient || recipient.length > 100 || !Number.isInteger(selection.quantity) || selection.quantity < 1 || quantity > 99) {
      this.error.set('Перевірте отримувача, кількість порцій та доступність страви.'); return;
    }
    this.busy.set(true); this.error.set(''); this.message.set('');
    try {
      this.order.set(await this.request(this.service.propose$(this.orderId, this.accessToken, { dishId: dish.id, forWhom: recipient, quantity })));
      this.message.set('Страву додано до запропонованих змін. Надішліть лист і підтвердьте новий склад замовлення.');
    } catch { this.error.set('Не вдалося додати страву. Оновіть замовлення та перевірте термін змін.'); }
    finally { this.busy.set(false); }
  }
  async requestChanges(): Promise<void> {
    if (!this.accessToken || this.busy() || !this.canEdit() || !this.order()?.pending?.length) return;
    this.busy.set(true); this.error.set(''); this.message.set('');
    try {
      await this.request(this.service.requestConfirmation$(this.orderId, this.accessToken));
      this.message.set('Запит на лист прийнято. Перевірте пошту; до підтвердження чинним залишається попереднє підтверджене замовлення.');
    } catch { this.error.set('Лист не вдалося надіслати. Зачекайте щонайменше 30 секунд перед повторною спробою та перевірте термін змін.'); }
    finally { this.busy.set(false); }
  }
  async confirm(): Promise<void> {
    if (!this.validLink || this.busy() || this.confirmed() || (!this.confirmationToken && !this.recoveryToken)) return;
    this.busy.set(true); this.error.set('');
    try {
      const receipt = await this.request(this.service.confirm$(this.orderId, this.recoveryToken || this.confirmationToken, this.isRecoveryLink));
      if (!receipt.guestAccessToken) throw new Error('Missing access capability.');
      this.remember(receipt.guestAccessToken);
      this.confirmed.set(true);
      this.location.replaceState('/guest-confirmation#order=' + encodeURIComponent(this.orderId));
      if (this.isRecoveryLink) this.message.set('Доступ відновлено. Непідтверджені страви залишаються непідтвердженими.');
      try { this.order.set(await this.request(this.service.get$(this.orderId, this.accessToken))); }
      catch { this.error.set('Дію виконано, але деталі не вдалося завантажити. Натисніть «Оновити замовлення» або оновіть сторінку.'); }
    } catch (failure) {
      const status = failure instanceof HttpErrorResponse ? failure.status : 0;
      const code = failure instanceof HttpErrorResponse ? (failure.error as { code?: string } | null)?.code : '';
      this.error.set(code === 'access_link_expired'
        ? 'Термін дії посилання завершився. Запитайте новий лист через форму нижче.'
        : status === 401 || status === 409 || status === 410
          ? 'Посилання вже використане, термін його дії завершився або замовлення змінилося. Відновіть доступ через пошту нижче.'
          : 'Не вдалося виконати дію. Спробуйте ще раз.');
    } finally { this.busy.set(false); }
  }
}
