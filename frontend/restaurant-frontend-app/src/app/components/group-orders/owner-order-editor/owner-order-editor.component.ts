import { CurrencyPipe, DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { Observable, finalize } from 'rxjs';
import { GroupSessionsResponse } from '../../../models/group-orders/group-sessions-response.model';
import { OwnerChangeResult, OwnerOrderMenu } from '../../../models/group-orders/owner-order-change.model';
import { Participant, ParticipantItem } from '../../../models/group-orders/participant.model';
import { GroupOrdersService } from '../../../services/group-orders/group-orders.service';
import { ModalComponent } from '../../../shared/custom-components/modal/modal.component';

@Component({
  selector: 'app-owner-order-editor', imports: [FormsModule, CurrencyPipe, DatePipe, ModalComponent],
  templateUrl: './owner-order-editor.component.html', styleUrl: './owner-order-editor.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OwnerOrderEditorComponent implements OnInit {
  readonly groupId = input.required<string>();
  readonly participant = input.required<Participant>();
  readonly saved = output<GroupSessionsResponse>();
  readonly closed = output<void>();
  private readonly service = inject(GroupOrdersService);
  private readonly destroyRef = inject(DestroyRef);
  readonly menu = signal<OwnerOrderMenu | null>(null);
  readonly busy = signal(false);
  readonly error = signal('');
  readonly message = signal('');
  readonly confirmClear = signal(false);
  readonly lines = computed(() => this.participant().pendingItems ?? this.participant().items);
  readonly isOwner = computed(() => this.participant().type === 'Owner');
  dishId = '';
  forWhom = '';
  quantity = 1;

  ngOnInit(): void {
    this.service.getOwnerMenu$(this.groupId()).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: menu => this.menu.set(menu), error: () => this.error.set('Не вдалося завантажити меню. Закрийте вікно та спробуйте знову.'),
    });
  }
  edit(line: ParticipantItem): void { this.dishId = line.dish.id; this.forWhom = line.forWhom ?? ''; this.quantity = line.quantity; this.confirmClear.set(false); }
  apply(): void {
    if (!this.dishId || !this.forWhom.trim() || !Number.isInteger(this.quantity) || this.quantity < 0 || this.quantity > 99) {
      this.error.set('Оберіть страву, зазначте для кого та кількість від 0 до 99.'); return;
    }
    if (!this.menu()?.canChange || this.busy()) return;
    const existing = this.lines().find(line => line.dish.id === this.dishId && (line.forWhom ?? '') === this.forWhom.trim());
    if (this.forWhom.includes('@') && !existing) {
      this.error.set('Вкажіть ім’я людини. Адресу підтвердження визначає власник замовлення, а не це поле.'); return;
    }
    if (existing && existing.quantity === this.quantity) {
      this.error.set('Кількість не змінилася. Вкажіть нову кількість або закрийте вікно.'); return;
    }
    const change = { dishId: this.dishId, forWhom: this.forWhom.trim(), quantity: this.quantity };
    if (this.isOwner()) {
      this.busy.set(true); this.error.set(''); this.message.set('');
      this.service.changeOwnDish$(this.groupId(), change).pipe(takeUntilDestroyed(this.destroyRef), finalize(() => this.busy.set(false)))
        .subscribe({ next: snapshot => { this.saved.emit(snapshot); this.message.set('Ваші страви збережено.'); },
          error: () => this.error.set('Не вдалося зберегти страву. Оновіть дані та перевірте доступність і граничний час.') });
    } else this.mutate(this.service.changeGuestDish$(this.groupId(), this.participant().id, change));
  }
  clear(): void {
    if (!this.confirmClear() || !this.menu()?.canChange || this.busy()) return;
    this.mutate(this.service.clearGuestOrder$(this.groupId(), this.participant().id));
  }
  remind(): void {
    if (this.busy() || !this.menu()?.canChange || !this.participant().pendingItems?.length) return;
    this.mutate(this.service.remindGuest$(this.groupId(), this.participant().id));
  }
  close(): void { if (!this.busy()) this.closed.emit(); }
  private mutate(request: Observable<OwnerChangeResult>): void {
    this.busy.set(true); this.error.set(''); this.message.set('');
    request.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: result => {
        this.saved.emit(result.snapshot); this.confirmClear.set(false);
        this.message.set('Зміни збережено. Надсилаємо сповіщення…');
        this.service.sendNotification$(this.groupId(), result.notificationId).pipe(
          takeUntilDestroyed(this.destroyRef), finalize(() => this.busy.set(false)),
        ).subscribe({
          next: status => this.message.set(status.guestAccepted && status.ownerAccepted
            ? 'Зміни збережено. Поштовий сервіс прийняв листи для гостя та організатора.'
            : 'Зміни збережено. Частина листів очікує надсилання — повторіть у розділі «Історія та сповіщення».'),
          error: () => this.message.set('Зміни збережено. Сповіщення очікують надсилання — повторіть у розділі «Історія та сповіщення».'),
        });
      },
      error: (error: HttpErrorResponse) => {
        this.busy.set(false);
        this.error.set(error.status === 429 ? 'Зачекайте хвилину перед повторним нагадуванням.'
          : 'Зміну не підтверджено сервером. Оновіть дані й перевірте кількість, доступність страв, граничний час та підтвердження вашої пошти.');
      },
    });
  }
}
