import { CurrencyPipe } from '@angular/common';
import { Component, computed, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { GuestDishSelection, GuestMenuEntry } from '../../../models/group-orders/guest-confirmation.model';

@Component({
  selector: 'app-guest-personal-menu', imports: [CurrencyPipe, FormsModule],
  template: `
    <section aria-label="Додати нові страви">
      <h2>Додати нові страви</h2>
      <p>Нові страви та додаткові порції потраплять до чинного замовлення після підтвердження через пошту.</p>
      <label>Для кого<input maxlength="100" [ngModel]="recipient()" (ngModelChange)="recipient.set($event)" placeholder="Для мене" [disabled]="busy()" /></label>
      <label>Додати порцій<input type="number" min="1" max="99" step="1" [ngModel]="quantity()" (ngModelChange)="quantity.set($event)" [disabled]="busy()" /></label>
      <label>Категорія<select [ngModel]="category()" (ngModelChange)="category.set($event)"><option value="">Усі страви</option>
        @for (category of categories(); track category) { <option [value]="category">{{ category }}</option> }
      </select></label>
      <ul>
        @for (dish of visibleMenu(); track dish.id) {
          <li><div><strong>{{ dish.name }}</strong><p>{{ dish.price | currency:'USD' }} @if (!dish.available) { · Немає в наявності }</p></div>
            <button type="button" [disabled]="busy() || !dish.available || !validSelection()" [attr.aria-label]="'Додати ' + dish.name" (click)="add(dish)">Додати</button></li>
        } @empty { <li>У цій категорії поки немає страв.</li> }
      </ul>
    </section>`,
  styles: `section { padding: 20px; margin: 20px 0; background: #f5f8f2; border-radius: 12px; } label { display: grid; gap: 6px; margin: 12px 0; } input, select { padding: 10px; border: 1px solid #718c76; border-radius: 6px; min-width: 0; } ul { list-style: none; padding: 0; } li { display: flex; align-items: center; justify-content: space-between; gap: 12px; border-bottom: 1px solid #cbd9cb; padding: 12px 0; } p { line-height: 1.5; } button { padding: 10px 16px; background: #146b33; color: white; border: 0; border-radius: 8px; cursor: pointer; } button:disabled { opacity: .5; cursor: default; }`,
})
export class GuestPersonalMenuComponent {
  readonly items = input.required<GuestMenuEntry[]>();
  readonly busy = input(false);
  readonly selected = output<GuestDishSelection>();
  readonly category = signal('');
  readonly recipient = signal('');
  readonly quantity = signal(1);
  readonly categories = computed(() => [...new Set(this.items().map(item => item.category))]);
  readonly visibleMenu = computed(() => this.items().filter(item => !this.category() || item.category === this.category()));
  readonly validSelection = computed(() => Number.isInteger(this.quantity()) && this.quantity() >= 1 && this.quantity() <= 99 && this.recipient().trim().length <= 100);
  add(dish: GuestMenuEntry): void {
    if (!this.busy() && dish.available && this.validSelection())
      this.selected.emit({ dishId: dish.id, forWhom: this.recipient().trim() || 'Для мене', quantity: this.quantity() });
  }
}
