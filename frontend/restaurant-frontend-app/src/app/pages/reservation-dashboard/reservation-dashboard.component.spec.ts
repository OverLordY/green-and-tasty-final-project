import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ReservationDashboardComponent } from './reservation-dashboard.component';
import { API_BASE_URL } from '@app/shared/config/api-base-url.token';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AuthenticationService } from '@app/auth/services/authentication.service';
import { Roles } from '@app/shared/constants/roles';

describe('ReservationDashboardComponent', () => {
  let component: ReservationDashboardComponent;
  let fixture: ComponentFixture<ReservationDashboardComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting(), { provide: API_BASE_URL, useValue: 'https://test.api' }],
      imports: [ReservationDashboardComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(ReservationDashboardComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('loads server reservation status without requesting unavailable legacy orders or feedback', () => {
    const auth = TestBed.inject(AuthenticationService);
    spyOn(auth, 'getToken').and.returnValue('test-token');
    spyOn(auth, 'getUser').and.returnValue({ id: 'owner', firstName: '', lastName: '', email: '', role: Roles.CUSTOMER });
    const http = TestBed.inject(HttpTestingController);

    component.ngOnInit();
    http.expectOne('https://test.api/reservations').flush([{
      id: 'reservation', tableId: 'table', startTime: '2030-01-01T18:00:00Z', endTime: '2030-01-01T19:30:00Z',
      guestCount: 2, status: 3, locationAddress: 'Location', waiterId: '', hasSubmittedPreOrder: true,
    }]);
    http.expectNone('https://test.api/orders');
    expect(http.match(request => request.url.includes('/feedback'))).toEqual([]);
    expect(component.reservations()[0].status).toBe(3);
    expect(component.reservations()[0].hasSubmittedPreOrder).toBeTrue();
    expect(component.reservationsLoaded()).toBeTrue();
    http.verify();
  });

  it('saves an edited owner reservation and updates the displayed booking', () => {
    const http = TestBed.inject(HttpTestingController);
    const original = {
      id: 'reservation', tableId: 'table', startTime: new Date('2030-01-01T18:00:00Z'),
      endTime: new Date('2030-01-01T19:30:00Z'), guestCount: 2, status: 0,
      restaurantName: 'Restaurant', locationAddress: 'Location', waiterId: '',
    };
    component.selectedReservation.set(original);
    component.reservations.set([original]);
    component.onSubmitEditReservation({ start: '2030-01-01T19:00:00Z', end: '2030-01-01T20:30:00Z' }, 3);
    const request = http.expectOne('https://test.api/reservations/edit');
    expect(request.request.method).toBe('POST');
    expect(request.request.body.guestCount).toBe(3);
    request.flush({ reservation: { ...original, guestCount: 3, startTime: '2030-01-01T19:00:00Z' }, groupId: 'group', notificationIds: [] });
    expect(component.reservations()[0].guestCount).toBe(3);
    expect(component.savingReservationEdit()).toBeFalse();
    expect(component.showEditReservationModal()).toBeFalse();
    http.verify();
  });
});
