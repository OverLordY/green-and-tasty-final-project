import { TestBed } from '@angular/core/testing';
import { Participant } from '../../../models/group-orders/participant.model';
import { ParticipantCardComponent } from './participant-card.component';

describe('Owner view of email-confirmed guest orders', () => {
  it('keeps confirmed subtotal and labels proposed increases separately', () => {
    const fixture = TestBed.createComponent(ParticipantCardComponent);
    const dish = { id: 'salad', name: 'Salad', price: 12 };
    const person: Participant = {
      id: 'guest', type: 'Guest', name: 'Andrii', isSubmitted: true, total: 24,
      confirmationStatus: 'ChangesAwaitingConfirmation',
      items: [{ dish, quantity: 2, forWhom: 'Olena', lineKey: 'salad:Olena' }],
      pendingItems: [{ dish, quantity: 3, forWhom: 'Olena', lineKey: 'salad:Olena' }],
    };
    fixture.componentRef.setInput('participant', person); fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.subtotal')?.textContent).toContain('$24.00');
    expect(root.querySelector('.pending-changes')?.textContent).toContain('× 3');
    expect(root.textContent).toContain('Зміни очікують підтвердження');
    expect(root.textContent).toContain('Для: Olena');
  });

  it('retains the cancelled dish name and an explicit cancellation notice', () => {
    const fixture = TestBed.createComponent(ParticipantCardComponent);
    fixture.componentRef.setInput('participant', {
      id: 'guest', type: 'Guest', name: 'Andrii', isSubmitted: false, total: 0, items: [],
      notices: [{ key: 'salad:Olena', dishName: 'Salad', forWhom: 'Olena', message: 'Страву скасовано' }],
    } satisfies Participant);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.cancelled-line')?.textContent).toContain('Salad');
    expect(root.querySelector('.change-notice')?.textContent).toContain('Страву скасовано');
    expect(root.querySelector('.subtotal')?.textContent).toContain('$0.00');
  });
});
