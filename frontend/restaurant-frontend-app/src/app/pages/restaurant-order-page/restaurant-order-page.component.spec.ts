import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { RestaurantOrderService } from '../../services/group-orders/restaurant-order.service';
import { RestaurantOrderPageComponent } from './restaurant-order-page.component';

describe('Restaurant submitted order', () => {
  function create(fails = false) {
    const service = jasmine.createSpyObj<RestaurantOrderService>('RestaurantOrderService', ['get$']);
    service.get$.and.returnValue(fails ? throwError(() => new HttpErrorResponse({ status: 403 })) : of({
      orderId: 'order', reservationId: 'reservation', locationId: 'location', tableNumber: 7,
      reservationStart: '2030-01-01T19:00:00Z', submittedAt: '2030-01-01T18:00:00Z',
      items: [{ dishId: 'salad', name: 'Salad', quantity: 3, total: 36 }], total: 36,
    }));
    TestBed.configureTestingModule({ imports: [RestaurantOrderPageComponent], providers: [
      { provide: RestaurantOrderService, useValue: service },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ reservationId: 'reservation' }) } } },
    ] });
    const fixture = TestBed.createComponent(RestaurantOrderPageComponent); fixture.detectChanges(); return fixture;
  }
  it('loads from the route without navigation state and displays the fixed total', () => {
    const fixture = create(); expect(fixture.nativeElement.textContent).toContain('Salad × 3');
    expect(fixture.nativeElement.textContent).toContain('$36.00');
    expect(fixture.nativeElement.textContent).toContain('Столик 7');
  });
  it('shows location authorization errors instead of order data', () => {
    const fixture = create(true); expect(fixture.nativeElement.textContent).toContain('Немає доступу');
    expect(fixture.componentInstance.order()).toBeNull();
  });
});
