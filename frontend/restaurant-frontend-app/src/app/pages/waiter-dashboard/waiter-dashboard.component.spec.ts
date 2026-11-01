import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { WaiterDashboardComponent } from './waiter-dashboard.component';
import { API_BASE_URL } from '@app/shared/config/api-base-url.token';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { EMPTY, Subject } from 'rxjs';
import { AuthenticationService } from '@app/auth/services/authentication.service';
import { ReservationService } from '@app/services/reservation/reservation.service';
import { WaitersService } from '@app/services/waiters/waiters.service';
import { Reservation } from '@app/models/reservations/reservation.model';

describe('WaiterDashboardComponent', () => {
  let component: WaiterDashboardComponent;
  let fixture: ComponentFixture<WaiterDashboardComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting(), { provide: API_BASE_URL, useValue: 'https://test.api' }],
      imports: [WaiterDashboardComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(WaiterDashboardComponent);
    component = fixture.componentInstance;
  });

  it('should create', () => {
    fixture.detectChanges();
    expect(component).toBeTruthy();
  });

  it('distinguishes loading and failure from an empty list and allows retry', () => {
    const auth = TestBed.inject(AuthenticationService);
    spyOn(auth, 'getToken').and.returnValue('test');
    spyOn(auth, 'getUser').and.returnValue({ id: 'waiter', firstName: '', lastName: '', email: 'waiter@example.test', role: 'Waiter' });
    spyOn(TestBed.inject(WaitersService), 'getWaiterLocationId').and.returnValue(EMPTY);
    const first = new Subject<Reservation[]>();
    const second = new Subject<Reservation[]>();
    const load = spyOn(TestBed.inject(ReservationService), 'getRestaurantReservations$').and.returnValues(first, second);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Завантажуємо бронювання');
    expect(fixture.nativeElement.textContent).not.toContain('0 reservations');
    first.error(new HttpErrorResponse({ status: 500 })); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Не вдалося завантажити бронювання');
    expect(fixture.nativeElement.textContent).not.toContain('0 reservations');
    const button = Array.from(fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>)
      .find(item => item.textContent?.includes('Спробувати знову'))!;
    button.click(); fixture.detectChanges();
    expect(load).toHaveBeenCalledTimes(2);
    expect(fixture.nativeElement.textContent).toContain('Завантажуємо бронювання');
    second.next([]); second.complete(); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('0 reservations');
    expect(fixture.nativeElement.textContent).not.toContain('Не вдалося завантажити');
  });

  it('explains that access is denied instead of reporting an empty list', () => {
    const auth = TestBed.inject(AuthenticationService);
    spyOn(auth, 'getToken').and.returnValue('test');
    spyOn(auth, 'getUser').and.returnValue({ id: 'waiter', firstName: '', lastName: '', email: 'waiter@example.test', role: 'Waiter' });
    spyOn(TestBed.inject(WaitersService), 'getWaiterLocationId').and.returnValue(EMPTY);
    const request = new Subject<Reservation[]>();
    spyOn(TestBed.inject(ReservationService), 'getRestaurantReservations$').and.returnValue(request);
    fixture.detectChanges();
    request.error(new HttpErrorResponse({ status: 403 })); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Немає доступу');
    expect(fixture.nativeElement.textContent).not.toContain('0 reservations');
  });

  it('refreshes periodically, preserves the last list on a network error and removes stale data when access is revoked', fakeAsync(() => {
    const auth = TestBed.inject(AuthenticationService);
    spyOn(auth, 'getToken').and.returnValue('test');
    spyOn(auth, 'getUser').and.returnValue({ id: 'waiter', firstName: '', lastName: '', email: 'waiter@example.test', role: 'Waiter' });
    spyOn(TestBed.inject(WaitersService), 'getWaiterLocationId').and.returnValue(EMPTY);
    const first = new Subject<Reservation[]>();
    const second = new Subject<Reservation[]>();
    const third = new Subject<Reservation[]>();
    const load = spyOn(TestBed.inject(ReservationService), 'getRestaurantReservations$').and.returnValues(first, second, third);
    fixture.detectChanges();
    first.next([{ id: 'reservation-1', tableId: 'table-1', startTime: new Date(), endTime: new Date(), guestCount: 2,
      status: 0, locationAddress: 'Test location', tableCapacity: 4, restaurantName: 'Test', waiterId: 'waiter', customerId: 'customer' }]);
    first.complete(); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Test location');
    tick(30000); fixture.detectChanges();
    expect(load).toHaveBeenCalledTimes(2);
    expect(fixture.nativeElement.textContent).toContain('Test location');
    second.error(new HttpErrorResponse({ status: 0 })); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Показано останні завантажені дані');
    expect(fixture.nativeElement.textContent).toContain('Test location');
    tick(30000); fixture.detectChanges();
    third.error(new HttpErrorResponse({ status: 403 })); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Немає доступу');
    expect(fixture.nativeElement.textContent).not.toContain('Test location');
    tick(30000);
    expect(load).toHaveBeenCalledTimes(3);
    fixture.destroy();
  }));
});
