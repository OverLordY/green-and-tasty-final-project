import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { GuestAccessRequested } from '../../../models/group-orders/guest-confirmation.model';
import { GuestPersonalOrderService } from '../../../services/group-orders/guest-personal-order.service';
import { GuestAccessRecoveryComponent } from './guest-access-recovery.component';

describe('Guest access recovery form', () => {
  let service: jasmine.SpyObj<GuestPersonalOrderService>;
  beforeEach(() => {
    service = jasmine.createSpyObj<GuestPersonalOrderService>('GuestPersonalOrderService', ['requestAccess$']);
    service.requestAccess$.and.returnValue(of({ message: 'If matched' }));
    TestBed.configureTestingModule({ imports: [GuestAccessRecoveryComponent], providers: [{ provide: GuestPersonalOrderService, useValue: service }] });
  });
  function create() {
    const fixture = TestBed.createComponent(GuestAccessRecoveryComponent); fixture.componentRef.setInput('orderId', 'order'); fixture.detectChanges(); return fixture;
  }
  it('validates email and shows a conditional response without claiming successful delivery', () => {
    const fixture = create(); const component = fixture.componentInstance;
    component.email.set('incorrect'); component.send(); expect(service.requestAccess$).not.toHaveBeenCalled();
    component.email.set('  guest@example.test  '); component.send(); fixture.detectChanges();
    expect(service.requestAccess$).toHaveBeenCalledWith('guest@example.test', { orderId: 'order', inviteToken: '' });
    expect(fixture.nativeElement.textContent).toContain('Якщо замовлення пов’язане');
    expect(fixture.nativeElement.textContent).not.toContain('Замовлення підтверджено');
  });
  it('prevents repeated requests while waiting for the server', () => {
    const pending = new Subject<GuestAccessRequested>(); service.requestAccess$.and.returnValue(pending);
    const component = create().componentInstance; component.email.set('guest@example.test'); component.send(); component.send();
    expect(service.requestAccess$).toHaveBeenCalledTimes(1); expect(component.busy()).toBeTrue();
  });
  it('reports network errors without exposing whether the email has orders', () => {
    service.requestAccess$.and.returnValue(throwError(() => new Error('offline')));
    const component = create().componentInstance; component.email.set('guest@example.test'); component.send();
    expect(component.error()).toContain('Запит не вдалося виконати'); expect(component.busy()).toBeFalse();
  });
});
