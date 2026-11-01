import { consolidatePreOrder, Participant } from './consolidate-pre-order';

const caesar = { id: 'dish1', name: 'Caesar Salad', price: 180 };
const tomYum = { id: 'dish2', name: 'Tom Yum', price: 220 };
const guest = (name: string, isSubmitted: boolean, items: Participant['items']): Participant => ({
  id: name,
  name,
  isSubmitted,
  items,
});

describe('consolidatePreOrder', () => {
  it('returns an empty order for no participants', () => {
    expect(consolidatePreOrder([])).toEqual({ items: [], total: 0 });
  });

  it('skips participants who have not submitted', () => {
    const result = consolidatePreOrder([
      guest('Andrii', true, [{ dish: caesar, quantity: 2 }]),
      guest('Olena', false, [{ dish: tomYum, quantity: 1 }]),
    ]);
    expect(result).toEqual({ items: [{ dish: caesar, quantity: 2 }], total: 360 });
  });

  it('merges the same dish from different participants', () => {
    const result = consolidatePreOrder([
      guest('Andrii', true, [{ dish: caesar, quantity: 2 }]),
      guest('Olena', true, [{ dish: caesar, quantity: 1 }, { dish: tomYum, quantity: 1 }]),
    ]);
    expect(result.items).toEqual([{ dish: caesar, quantity: 3 }, { dish: tomYum, quantity: 1 }]);
    expect(result.total).toBe(760);
  });

  it('does not mutate the input', () => {
    const input = [
      guest('Andrii', true, [{ dish: caesar, quantity: 2 }]),
      guest('Olena', true, [{ dish: caesar, quantity: 1 }]),
    ];
    const snapshot = structuredClone(input);

    consolidatePreOrder(input);

    expect(input).toEqual(snapshot);
  });

  it('ignores a submitted participant without dishes', () => {
    const result = consolidatePreOrder([guest('Andrii', true, []), guest('Olena', true, [{ dish: tomYum, quantity: 1 }])]);
    expect(result).toEqual({ items: [{ dish: tomYum, quantity: 1 }], total: 220 });
  });
});
