import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { API_BASE_URL } from '../../shared/config/api-base-url.token';
import { RestaurantOrderService } from './restaurant-order.service';

describe('Restaurant order API contract', () => {
  it('loads a reservation from the protected API using an encoded identifier', () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), { provide: API_BASE_URL, useValue: 'https://api.test' }] });
    const controller = TestBed.inject(HttpTestingController);
    TestBed.inject(RestaurantOrderService).get$('reservation/a').subscribe();
    const request = controller.expectOne('https://api.test/restaurant/reservations/reservation%2Fa/pre-order');
    expect(request.request.method).toBe('GET'); request.flush({}); controller.verify();
  });
});
