import { TestBed } from '@angular/core/testing';
import { GuestPersonalMenuComponent } from './guest-personal-menu.component';

describe('Personal order menu', () => {
  it('filters categories and emits a recipient and quantity only for available dishes', () => {
    const fixture = TestBed.createComponent(GuestPersonalMenuComponent);
    const salad = { id: 'salad', name: 'Salad', price: 12, category: 'Salads', available: true };
    const soup = { id: 'soup', name: 'Soup', price: 8, category: 'Soups', available: false };
    fixture.componentRef.setInput('items', [salad, soup]); fixture.detectChanges();
    const component = fixture.componentInstance; const selected = jasmine.createSpy('selected'); component.selected.subscribe(selected);
    component.category.set('Salads'); expect(component.visibleMenu()).toEqual([salad]);
    component.recipient.set('  Olena  '); component.quantity.set(2); component.add(soup); expect(selected).not.toHaveBeenCalled();
    component.add(salad); expect(selected).toHaveBeenCalledWith({ dishId: 'salad', forWhom: 'Olena', quantity: 2 });
  });
  it('prevents invalid quantities and repeat clicks while saving', () => {
    const fixture = TestBed.createComponent(GuestPersonalMenuComponent);
    const salad = { id: 'salad', name: 'Salad', price: 12, category: 'Salads', available: true };
    fixture.componentRef.setInput('items', [salad]); const component = fixture.componentInstance;
    const selected = jasmine.createSpy('selected'); component.selected.subscribe(selected);
    component.quantity.set(0); component.add(salad); component.quantity.set(1.5); component.add(salad); component.quantity.set(100); component.add(salad);
    component.quantity.set(1); fixture.componentRef.setInput('busy', true); component.add(salad);
    expect(selected).not.toHaveBeenCalled();
  });
});
