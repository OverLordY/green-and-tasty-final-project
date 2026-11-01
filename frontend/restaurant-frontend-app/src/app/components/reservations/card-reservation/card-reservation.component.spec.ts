import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { Roles } from '@app/shared/constants/roles';
import { CardReservationComponent } from './card-reservation.component';
import { API_BASE_URL } from '@app/shared/config/api-base-url.token';
import { ReservationCustomer, ReservationStatusCustomer } from '@app/models/reservations/reservation.model';

const RESERVATION: ReservationCustomer = {
  id: 'res-1',
  tableId: 'table-1',
  startTime: new Date('2026-10-21T16:00:00Z'),
  endTime: new Date('2026-10-21T17:30:00Z'),
  guestCount: 2,
  status: ReservationStatusCustomer.Reserved,
  restaurantName: 'Green & Tasty',
  locationAddress: 'Rustaveli Avenue 48',
  waiterId: 'waiter-1',
};

describe('CardReservationComponent', () => {
  let component: CardReservationComponent;
  let fixture: ComponentFixture<CardReservationComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      providers: [{ provide: API_BASE_URL, useValue: 'https://test.api' }, provideRouter([])],
      imports: [CardReservationComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(CardReservationComponent);
    component = fixture.componentInstance;
    // r — обов'язковий input: без нього компонент падає з NG0950
    fixture.componentRef.setInput('r', RESERVATION);
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('opens the submitted group order using the reservation ID', () => {
    fixture.componentRef.setInput('role', Roles.WAITER);
    fixture.componentRef.setInput('r', { ...RESERVATION, hasSubmittedPreOrder: true, submittedDishCount: 2 }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Pre-order: 2 dishes');
    expect(component.canOpenWaiterPreOrder()).toBeFalse();
    const navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    const button = fixture.nativeElement.querySelector('.group-order-link') as HTMLButtonElement;
    button.click();
    expect(navigate).toHaveBeenCalledWith(['/restaurant/reservations', 'res-1', 'pre-order']);
  });
  it('does not offer a restaurant receipt for an unsubmitted pre-order', () => {
    fixture.componentRef.setInput('role', Roles.WAITER); fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.group-order-link')).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Pre-order: not submitted');
  });
  it('shows the assigned table and receipt without unsupported waiter actions', () => {
    fixture.componentRef.setInput('role', Roles.WAITER);
    fixture.componentRef.setInput('waiterActionsAvailable', false);
    fixture.componentRef.setInput('selectedTableLabel', 'Table 1');
    fixture.componentRef.setInput('r', { ...RESERVATION, hasSubmittedPreOrder: true, submittedDishCount: 2 });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Table 1');
    expect(fixture.nativeElement.querySelector('.change-table app-custom-select')).toBeNull();
    expect(fixture.nativeElement.querySelector('.actions')).toBeNull();
    expect(fixture.nativeElement.querySelector('.group-order-link')).not.toBeNull();
  });
  it('offers only the next service status for a submitted group order', () => {
    fixture.componentRef.setInput('role', Roles.WAITER);
    fixture.componentRef.setInput('waiterActionsAvailable', false);
    fixture.componentRef.setInput('waiterServiceStatusAvailable', true);
    fixture.componentRef.setInput('r', { ...RESERVATION, hasSubmittedPreOrder: true });
    fixture.detectChanges();
    const emitted = spyOn(component.statusChanged, 'emit');
    let button = Array.from(fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>)
      .find(item => item.textContent?.includes('Почати обслуговування'))!;
    button.click();
    expect(emitted).toHaveBeenCalledWith({ id: 'res-1', newStatus: ReservationStatusCustomer.InProgress });
    fixture.componentRef.setInput('statusChangePending', true); fixture.detectChanges();
    expect(button.disabled).toBeTrue();
    fixture.componentRef.setInput('statusChangePending', false);
    fixture.componentRef.setInput('r', { ...RESERVATION, hasSubmittedPreOrder: true, status: ReservationStatusCustomer.InProgress });
    fixture.detectChanges();
    button = Array.from(fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>)
      .find(item => item.textContent?.includes('Завершити обслуговування'))!;
    button.click();
    expect(emitted).toHaveBeenCalledWith({ id: 'res-1', newStatus: ReservationStatusCustomer.Finished });
  });
  it('lets the owner reopen their group order from the reservation card', () => {
    fixture.componentRef.setInput('role', Roles.CUSTOMER); fixture.detectChanges();
    const navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    const button = fixture.nativeElement.querySelector('.group-order-link') as HTMLButtonElement;
    button.click();
    expect(navigate).toHaveBeenCalledWith(['/reservations', 'res-1', 'group-order']);
  });
  it('explains unavailable editing and directs pre-order to the working group flow', () => {
    fixture.componentRef.setInput('customerReservationEditingAvailable', false);
    fixture.componentRef.setInput('customerGroupPreOrder', true);
    fixture.detectChanges();
    const buttons = Array.from(fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>);
    expect(buttons.find(button => button.textContent?.trim() === 'Edit')?.disabled).toBeTrue();
    expect(fixture.nativeElement.textContent).toContain('Редагування бронювання тимчасово недоступне');
    const navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    buttons.find(button => button.textContent?.trim() === 'Pre-order')!.click();
    expect(navigate).toHaveBeenCalledWith(['/reservations', 'res-1', 'group-order']);
  });
  it('does not offer unsupported feedback for a completed owner booking', () => {
    fixture.componentRef.setInput('role', Roles.CUSTOMER);
    fixture.componentRef.setInput('customerFeedbackAvailable', false);
    fixture.componentRef.setInput('r', { ...RESERVATION, status: ReservationStatusCustomer.Finished });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).not.toContain('Leave Feedback');
    expect(fixture.nativeElement.querySelector('.group-order-link')).not.toBeNull();
  });
});
