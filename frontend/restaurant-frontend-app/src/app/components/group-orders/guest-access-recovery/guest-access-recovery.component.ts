import { Component, DestroyRef, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { finalize } from 'rxjs';
import { GuestPersonalOrderService } from '../../../services/group-orders/guest-personal-order.service';

@Component({
  selector: 'app-guest-access-recovery', imports: [FormsModule],
  template: `
    <details [open]="open()">
      <summary>Вже замовляли? Відновити доступ через пошту</summary>
      <p>Вкажіть пошту, яку використовували для замовлення. Реєстрація та пароль не потрібні.</p>
      @if (orderId() || inviteToken()) {
        <form (ngSubmit)="send()">
          <label>Електронна пошта для відновлення
            <input type="email" name="recoveryEmail" autocomplete="email" maxlength="254" required [disabled]="busy()" [ngModel]="email()" (ngModelChange)="email.set($event)" />
          </label>
          <button type="submit" [disabled]="busy()">{{ busy() ? 'Обробляємо…' : 'Надіслати посилання для доступу' }}</button>
        </form>
      } @else { <p>Відкрийте запрошення організатора або будь-який попередній лист про ваше замовлення.</p> }
      @if (message()) { <p role="status">{{ message() }}</p> }
      @if (error()) { <p role="alert">{{ error() }}</p> }
    </details>`,
  styles: `details { margin: 24px 0; padding: 18px; background: #f1f7f1; border: 1px solid #b8cdbb; border-radius: 12px; } summary { cursor: pointer; font-weight: 600; } p { line-height: 1.5; margin: 12px 0; } label { display: grid; gap: 6px; } input { padding: 10px; border: 1px solid #718c76; border-radius: 6px; min-width: 0; } form { display: grid; gap: 12px; } button { padding: 12px; background: #146b33; color: white; border: 0; border-radius: 8px; cursor: pointer; } button:disabled { opacity: .6; } [role=alert] { color: #a62924; }`,
})
export class GuestAccessRecoveryComponent {
  readonly orderId = input('');
  readonly inviteToken = input('');
  readonly open = input(false);
  readonly email = signal('');
  readonly busy = signal(false);
  readonly message = signal('');
  readonly error = signal('');
  private readonly service = inject(GuestPersonalOrderService);
  private readonly destroyRef = inject(DestroyRef);
  send(): void {
    if (this.busy() || (!this.orderId() && !this.inviteToken())) return;
    const email = this.email().trim();
    this.message.set(''); this.error.set('');
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email) || email.length > 254) { this.error.set('Вкажіть коректну електронну адресу.'); return; }
    this.busy.set(true);
    this.service.requestAccess$(email, { orderId: this.orderId(), inviteToken: this.inviteToken() })
      .pipe(takeUntilDestroyed(this.destroyRef), finalize(() => this.busy.set(false))).subscribe({
        next: () => this.message.set('Якщо замовлення пов’язане з цією поштою, надішлемо лист для відновлення доступу. Перевірте також папку «Спам». Повторний запит доступний через хвилину.'),
        error: () => this.error.set('Запит не вдалося виконати. Зачекайте хвилину та спробуйте ще раз.'),
      });
  }
}
