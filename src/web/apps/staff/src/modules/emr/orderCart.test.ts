import { describe, expect, it } from 'vitest';
import {
  addManyToCart,
  addToCart,
  maxQuantity,
  removeFromCart,
  setNotes,
  setQuantity,
  toOrderLines,
  type CartService,
} from './orderCart';

const cbc: CartService = { id: 's1', code: 'LAB-CBC', name: 'Complete blood count', category: 'Laboratory' };
const prbc: CartService = { id: 's2', code: 'BB-PRBC', name: 'Packed red cells', category: 'BloodBank' };
const physio: CartService = { id: 's3', code: 'PHY-SESSION', name: 'Physiotherapy session', category: 'Physiotherapy' };

describe('order cart', () => {
  it('adds each service once', () => {
    const cart = addToCart(addToCart([], cbc), cbc);
    expect(cart).toHaveLength(1);
    expect(addManyToCart(cart, [cbc, prbc, physio]).map((l) => l.code)).toEqual(['LAB-CBC', 'BB-PRBC', 'PHY-SESSION']);
  });

  it('clamps quantities to what the category allows', () => {
    expect(maxQuantity('Laboratory')).toBe(1);
    expect(maxQuantity('BloodBank')).toBe(10);
    expect(maxQuantity('Dialysis')).toBe(30);
    let cart = addManyToCart([], [cbc, prbc, physio]);
    cart = setQuantity(cart, 's1', 3);
    cart = setQuantity(cart, 's2', 22);
    cart = setQuantity(cart, 's3', 0);
    expect(cart.map((l) => l.quantity)).toEqual([1, 10, 1]);
  });

  it('builds request lines with notes and quantities only when needed', () => {
    let cart = addManyToCart([], [cbc, prbc]);
    cart = setQuantity(cart, 's2', 2);
    cart = setNotes(cart, 's1', '  fasting sample ');
    expect(toOrderLines(cart)).toEqual([
      { serviceItemId: 's1', notes: 'fasting sample', quantity: null },
      { serviceItemId: 's2', notes: null, quantity: 2 },
    ]);
  });

  it('removes lines', () => {
    expect(removeFromCart(addManyToCart([], [cbc, prbc]), 's1').map((l) => l.serviceItemId)).toEqual(['s2']);
  });
});
