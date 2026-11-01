import { CurrencyPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { Participant } from '../../../models/group-orders/participant.model';

@Component({
  selector: 'app-participant-card',
  imports: [CurrencyPipe],
  templateUrl: './participant-card.component.html',
  styleUrl: './participant-card.component.scss',
  changeDetection: ChangeDetectionStrategy.Eager,
})
export class ParticipantCardComponent {
  readonly participant = input.required<Participant>();
  readonly isMe = input(false);
  readonly canRemove = input(false);
  readonly canEdit = input(false);
  readonly editClick = output<Participant>();
  readonly removeClick = output<Participant>();
  readonly statusLabel = computed(() => {
    const person = this.participant();
    const labels: Record<string, string> = {
      Choosing: 'Обирає страви', AwaitingEmail: 'Очікує підтвердження пошти',
      Confirmed: 'Підтверджено', ChangesAwaitingConfirmation: 'Зміни очікують підтвердження', Cancelled: 'Скасовано',
    };
    return labels[person.confirmationStatus ?? ''] ?? (person.isSubmitted ? 'Submitted' : 'Still choosing');
  });
  readonly removedNotices = computed(() => (this.participant().notices ?? [])
    .filter(notice => !this.participant().items.some(item => item.lineKey === notice.key)));

  readonly title = computed(() => {
    const person = this.participant();
    return this.isMe() ? 'My Pre-Order' : `${person.type}: ${person.name}`;
  });
}
