import { CurrencyPipe, DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { EMPTY, Observable, Subscription, catchError, concatMap, exhaustMap, finalize, forkJoin, from, interval, map, of, switchMap, tap, toArray } from 'rxjs';
import { ParticipantCardComponent } from '../../components/group-orders/participant-card/participant-card.component';
import { OwnerOrderEditorComponent } from '../../components/group-orders/owner-order-editor/owner-order-editor.component';
import { OwnerActivityComponent } from '../../components/group-orders/owner-activity/owner-activity.component';
import { GroupSessionsResponse } from '../../models/group-orders/group-sessions-response.model';
import { Participant } from '../../models/group-orders/participant.model';
import { GroupReservationStatus } from '../../models/group-orders/group-status.model';
import { GuestInvite } from '../../models/reservations/guest-invite.model';
import { GroupOrdersService } from '../../services/group-orders/group-orders.service';
import { ToastService } from '../../services/toast/toast.service';
import { ModalComponent } from '../../shared/custom-components/modal/modal.component';
import { consolidatePreOrder } from '../../shared/helpers/consolidate-pre-order';

@Component({
  selector: 'app-group-order-page',
  imports: [CurrencyPipe, DatePipe, ParticipantCardComponent, ModalComponent, OwnerOrderEditorComponent, OwnerActivityComponent],
  templateUrl: './group-order-page.component.html',
  styleUrl: './group-order-page.component.scss',
  changeDetection: ChangeDetectionStrategy.Eager,
})
export class GroupOrderPageComponent implements OnInit {
  private readonly service = inject(GroupOrdersService);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);
  private polling?: Subscription;
  private loadingRequest?: Subscription;
  private mutationRequest?: Subscription;
  private refreshRequest?: Subscription;
  private generation = 0;
  reservationId = this.route.snapshot.paramMap.get('reservationId') ?? '';
  readonly invite = signal<GuestInvite | null>(null);
  readonly participants = signal<Participant[]>([]);
  readonly snapshotVersion = signal<number | undefined>(undefined);
  private dialogVersion: number | undefined;
  readonly ownerSessionId = signal('');
  readonly status = signal<GroupReservationStatus>('Open');
  readonly submissionOverdue = signal(false);
  readonly loading = signal(true);
  readonly error = signal('');
  readonly submitting = signal(false);
  readonly removing = signal(false);
  readonly refreshing = signal(false);
  readonly refreshError = signal(false);
  readonly updatedAt = signal<Date | null>(null);
  readonly submitDialog = signal(false);
  readonly selectedGuest = signal<Participant | null>(null);
  readonly editedGuest = signal<Participant | null>(null);
  readonly showActivity = signal(false);
  readonly clearGroupDialog = signal(false);
  readonly clearingGroup = signal(false);
  readonly groupActionMessage = signal('');
  readonly consolidated = computed(() => consolidatePreOrder(this.participants()));
  readonly pendingCount = computed(() => this.participants().filter((person) => !person.isSubmitted).length);
  readonly hasDishes = computed(() => this.participants().some((person) => person.items.length > 0));
  readonly isOpen = computed(() => this.status() === 'Open');
  readonly canSubmit = computed(() => this.isOpen() && !this.submissionOverdue() && !this.refreshError() && this.consolidated().items.length > 0 && !this.submitting() && !this.removing() && !this.refreshing() && !this.editedGuest() && !this.clearGroupDialog() && !this.clearingGroup());

  ngOnInit(): void {
    this.load();
    this.route.paramMap?.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((parameters) => {
      const reservationId = parameters.get('reservationId') ?? '';
      if (reservationId !== this.reservationId) {
        this.reservationId = reservationId;
        this.invite.set(null);
        this.participants.set([]);
        this.snapshotVersion.set(undefined);
        this.submitDialog.set(false);
        this.selectedGuest.set(null);
        this.load();
      }
    });
  }

  load(): void {
    this.editedGuest.set(null);
    this.showActivity.set(false);
    this.clearGroupDialog.set(false); this.clearingGroup.set(false); this.groupActionMessage.set('');
    this.generation++;
    this.mutationRequest?.unsubscribe();
    this.polling?.unsubscribe();
    this.refreshRequest?.unsubscribe();
    this.loadingRequest?.unsubscribe();
    this.loading.set(true);
    this.error.set('');
    this.loadingRequest = this.service.getGroupInvite$(this.reservationId).pipe(
      switchMap((invite) => forkJoin({
        invite: [invite],
        sessions: this.service.getSessions$(invite.GroupReservationId),
        owner: this.service.getOwnerSessionId$(this.reservationId),
        status: this.service.getStatus$(invite.GroupReservationId),
      })),
      takeUntilDestroyed(this.destroyRef),
      finalize(() => this.loading.set(false)),
    ).subscribe({
      next: (data) => {
        this.invite.set(data.invite);
        this.participants.set(data.sessions.participants);
        this.snapshotVersion.set(data.sessions.version);
        this.ownerSessionId.set(data.owner);
        this.status.set(data.status.status);
        this.submissionOverdue.set(data.status.submissionOverdue === true);
        this.updatedAt.set(new Date());
        this.refreshError.set(false);
        this.startPolling();
      },
      error: (error: HttpErrorResponse) => this.error.set(error.status === 404 ? 'Group reservation not found' : 'Could not load the group pre-order.'),
    });
  }

  private startPolling(): void {
    this.polling?.unsubscribe();
    if (!this.isOpen()) return;
    this.polling = interval(15_000).pipe(
      exhaustMap(() => this.refreshSnapshot()),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe();
  }

  private refreshSnapshot(): Observable<void> {
    const invite = this.invite();
    if (!invite || !this.isOpen() || this.refreshing() || this.submitting() || this.removing() || this.editedGuest() || this.clearGroupDialog() || this.clearingGroup()) return EMPTY;
    const generation = this.generation;
    this.refreshing.set(true);
    return forkJoin({
      sessions: this.service.getSessions$(invite.GroupReservationId, { silent: true }),
      status: this.service.getStatus$(invite.GroupReservationId, { silent: true }),
    }).pipe(
      tap((data) => {
        if (generation !== this.generation) return;
        if (this.submitDialog() && this.dialogVersion !== undefined && data.sessions.version !== this.dialogVersion) {
          this.submitDialog.set(false);
          this.groupActionMessage.set('Замовлення змінилося. Перегляньте оновлений склад перед надсиланням.');
        }
        this.participants.set(data.sessions.participants);
        this.snapshotVersion.set(data.sessions.version);
        this.status.set(data.status.status);
        this.submissionOverdue.set(data.status.submissionOverdue === true);
        this.updatedAt.set(new Date());
        this.refreshError.set(false);
        if (!this.isOpen()) {
          this.polling?.unsubscribe();
          this.submitDialog.set(false);
          this.selectedGuest.set(null);
        }
      }),
      map(() => undefined),
      catchError((failure: HttpErrorResponse) => {
        if (failure.status === 401 || failure.status === 403) {
          this.polling?.unsubscribe();
          this.invite.set(null); this.participants.set([]); this.ownerSessionId.set('');
          this.snapshotVersion.set(undefined); this.updatedAt.set(null);
          this.submitDialog.set(false); this.selectedGuest.set(null); this.editedGuest.set(null);
          this.showActivity.set(false); this.clearGroupDialog.set(false);
          this.error.set('Доступ завершився. Увійдіть як організатор і відкрийте бронювання знову.');
        } else this.refreshError.set(true);
        return EMPTY;
      }),
      finalize(() => this.refreshing.set(false)),
    );
  }

  refresh(): void {
    if (this.refreshing() || !this.isOpen() || this.submitting() || this.removing() || this.editedGuest() || this.clearGroupDialog() || this.clearingGroup()) return;
    this.refreshRequest = this.refreshSnapshot().pipe(takeUntilDestroyed(this.destroyRef)).subscribe();
  }

  openSubmit(): void {
    if (!this.canSubmit()) return;
    // Notification delivery can update the aggregate version after a guest was
    // removed. Refresh before showing the review so the displayed order and
    // submitted version come from the same current snapshot.
    let refreshed = false;
    this.refreshRequest = this.refreshSnapshot().pipe(
      tap(() => { refreshed = true; }), takeUntilDestroyed(this.destroyRef),
      finalize(() => {
        if (refreshed && this.canSubmit()) {
          this.dialogVersion = this.snapshotVersion(); this.submitDialog.set(true);
        }
      }),
    ).subscribe();
  }
  closeSubmit(): void { if (!this.submitting()) this.submitDialog.set(false); }

  submit(): void {
    const invite = this.invite();
    if (!invite || !this.canSubmit()) return;
    this.generation++;
    this.submitting.set(true);
    this.polling?.unsubscribe();
    this.refreshRequest?.unsubscribe();
    const submit = this.dialogVersion === undefined ? this.service.submit$(invite.GroupReservationId, this.reservationId)
      : this.service.submit$(invite.GroupReservationId, this.reservationId, this.dialogVersion);
    this.mutationRequest = submit.pipe(
      takeUntilDestroyed(this.destroyRef),
      finalize(() => this.submitting.set(false)),
    ).subscribe({
      next: () => {
        this.status.set('Submitted');
        this.submitDialog.set(false);
        this.selectedGuest.set(null);
        this.toast.success('Pre-order sent to the restaurant');
      },
      error: (error: HttpErrorResponse) => {
        if (error.status === 409) { this.refreshError.set(true); this.groupActionMessage.set('Замовлення змінилося. Оновіть список і перегляньте склад перед надсиланням.'); }
        this.submitDialog.set(false); this.startPolling();
      },
    });
  }

  chooseRemoval(person: Participant): void {
    if (this.isOpen() && person.type === 'Guest' && !this.submitting() && !this.removing() && !this.refreshing() && !this.editedGuest() && !this.clearGroupDialog() && !this.clearingGroup()) this.selectedGuest.set(person);
  }
  chooseEdit(person: Participant): void {
    if (!this.isOpen() || (person.type !== 'Owner' && (person.type !== 'Guest' || !person.confirmationStatus)) || this.submitting() || this.removing() || this.refreshing() || this.editedGuest() || this.clearGroupDialog() || this.clearingGroup()) return;
    this.generation++; this.refreshRequest?.unsubscribe(); this.polling?.unsubscribe();
    this.showActivity.set(false); this.editedGuest.set(person);
  }
  applyOwnerEdit(snapshot: GroupSessionsResponse): void {
    this.participants.set(snapshot.participants); this.snapshotVersion.set(snapshot.version); this.updatedAt.set(new Date());
    const id = this.editedGuest()?.id;
    this.editedGuest.set(snapshot.participants.find(person => person.id === id) ?? null);
  }
  closeEditor(): void { this.editedGuest.set(null); this.startPolling(); }
  openGroupClear(): void {
    if (!this.isOpen() || !this.hasDishes() || this.submitting() || this.removing() || this.refreshing() || this.editedGuest() || this.selectedGuest() || this.clearingGroup()) return;
    this.generation++; this.polling?.unsubscribe(); this.refreshRequest?.unsubscribe();
    this.showActivity.set(false); this.clearGroupDialog.set(true);
  }
  closeGroupClear(): void { if (!this.clearingGroup()) { this.clearGroupDialog.set(false); this.startPolling(); } }
  clearAllOrders(): void {
    const invite = this.invite();
    if (!invite || !this.isOpen() || !this.clearGroupDialog() || this.clearingGroup()) return;
    this.clearingGroup.set(true); this.groupActionMessage.set('');
    this.mutationRequest = this.service.clearGroupOrders$(invite.GroupReservationId).pipe(
      tap(result => { this.participants.set(result.snapshot.participants); this.snapshotVersion.set(result.snapshot.version); this.updatedAt.set(new Date()); }),
      switchMap(result => from(result.notificationIds).pipe(
        concatMap(id => this.service.sendNotification$(invite.GroupReservationId, id).pipe(catchError(() => of(null)))), toArray(),
      )), takeUntilDestroyed(this.destroyRef),
      finalize(() => { this.clearingGroup.set(false); this.clearGroupDialog.set(false); this.startPolling(); }),
    ).subscribe({
      next: notices => this.groupActionMessage.set(notices.every(n => n?.guestAccepted && n.ownerAccepted)
        ? 'Усі страви скасовано. Поштовий сервіс прийняв сповіщення.'
        : 'Усі страви скасовано. Ненадіслані листи можна повторити у розділі «Історія та сповіщення».'),
      error: () => this.groupActionMessage.set('Очищення не підтверджено сервером. Оновіть дані та перевірте граничний час.'),
    });
  }
  closeRemoval(): void { if (!this.removing()) this.selectedGuest.set(null); }

  removeGuest(): void {
    const invite = this.invite();
    const guest = this.selectedGuest();
    if (!invite || !guest || !this.isOpen() || this.removing() || this.submitting()) return;
    this.generation++;
    this.removing.set(true);
    this.polling?.unsubscribe();
    this.refreshRequest?.unsubscribe();
    this.mutationRequest = this.service.removeSession$(invite.GroupReservationId, guest.id).pipe(
      tap(response => { this.participants.set(response.participants); this.snapshotVersion.set(response.version); this.updatedAt.set(new Date()); }),
      switchMap(response => response.notificationId
        ? this.service.sendNotification$(invite.GroupReservationId, response.notificationId).pipe(
            tap(notice => this.groupActionMessage.set(notice.guestAccepted && notice.ownerAccepted
              ? 'Гостя видалено. Поштовий сервіс прийняв сповіщення.' : 'Гостя видалено. Повторіть ненадіслані листи в історії.')),
            map(() => response), catchError(() => { this.groupActionMessage.set('Гостя видалено. Сповіщення очікують надсилання в історії.'); return of(response); }),
          ) : of(response)),
      takeUntilDestroyed(this.destroyRef),
      finalize(() => { this.removing.set(false); this.startPolling(); }),
    ).subscribe({
      next: (response) => {
        this.participants.set(response.participants);
        this.snapshotVersion.set(response.version);
        this.updatedAt.set(new Date());
        this.selectedGuest.set(null);
        this.toast.success('Guest removed from the reservation');
      },
      error: () => this.selectedGuest.set(null),
    });
  }
}
