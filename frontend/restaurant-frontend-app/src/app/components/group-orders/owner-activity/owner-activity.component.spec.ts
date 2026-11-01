import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { GroupOrdersService } from '../../../services/group-orders/group-orders.service';
import { OwnerActivityComponent } from './owner-activity.component';

describe('Owner notification recovery', () => {
  it('shows incomplete notifications and removes the retry control after both recipients are accepted', () => {
    const service = jasmine.createSpyObj<GroupOrdersService>('GroupOrdersService', ['getHistory$', 'getNotifications$', 'sendNotification$']);
    service.getHistory$.and.returnValue(of([{ id: 'audit', guestOrderId: 'guest', action: 'ChangeQuantity', at: '2030-01-01T12:00:00Z', summary: 'Salad: 2 → 1' }]));
    service.getNotifications$.and.returnValue(of([{ id: 'notice', guestAccepted: true, ownerAccepted: false }]));
    service.sendNotification$.and.returnValue(of({ id: 'notice', guestAccepted: true, ownerAccepted: true }));
    TestBed.configureTestingModule({ imports: [OwnerActivityComponent], providers: [{ provide: GroupOrdersService, useValue: service }] });
    const fixture = TestBed.createComponent(OwnerActivityComponent); fixture.componentRef.setInput('groupId', 'group'); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('лист організатору');
    const buttons = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button'));
    buttons.find(button => button.textContent?.includes('Повторити'))!.click(); fixture.detectChanges();
    expect(service.sendNotification$).toHaveBeenCalledWith('group', 'notice');
    expect(fixture.nativeElement.textContent).not.toContain('Повторити надсилання');
    expect(fixture.nativeElement.textContent).toContain('Salad: 2 → 1');
  });
});
