import { describe, expect, it } from 'vitest';
import { menuItemBody, type MenuItemDto } from './api';
import { menuFormValues, menuItemRequest, menuItemSchema } from './menuForm';

const water: MenuItemDto = {
  id: 'm1',
  code: 'WATER-1L',
  name: 'Packaged drinking water 1 L',
  category: 'Packaged',
  price: 20,
  gstRate: 18,
  hsnSac: '220190',
  priceIncludesGst: true,
  isVeg: true,
  isAvailable: true,
  trackStock: true,
  stockOnHand: 40,
  displayOrder: 270,
  description: null,
  version: 7,
};

const tea: MenuItemDto = {
  ...water,
  id: 'm2',
  code: 'TEA',
  name: 'Tea',
  category: 'Beverages',
  price: 10,
  gstRate: 5,
  hsnSac: null,
  trackStock: false,
};

describe('menu item form', () => {
  it('starts a new item with no HSN/SAC code, so food made in the canteen gets the canteen SAC', () => {
    expect(menuFormValues(null)).toMatchObject({
      code: '',
      category: 'Snacks',
      gstRate: '5',
      hsnSac: '',
      priceIncludesGst: true,
      description: '',
    });
  });

  it('shows an item’s own code, or an empty box when it has none', () => {
    expect(menuFormValues(water).hsnSac).toBe('220190');
    expect(menuFormValues(tea).hsnSac).toBe('');
  });

  it('sends the code, or null for the canteen SAC, with the GST rate as a number and the item’s version', () => {
    const values = menuFormValues(water);

    expect(menuItemRequest(values, water)).toMatchObject({
      hsnSac: '220190',
      gstRate: 18,
      description: null,
      version: 7,
    });
    expect(menuItemRequest({ ...values, hsnSac: ' 22019090 ' }, water).hsnSac).toBe('22019090');
    expect(menuItemRequest({ ...values, hsnSac: '  ' }, water).hsnSac).toBeNull();
    expect(menuItemRequest(menuFormValues(null), null)).toMatchObject({ hsnSac: null, gstRate: 5, version: null });
  });

  it('accepts HSN/SAC codes of 4, 6 or 8 digits or none', () => {
    const values = { ...menuFormValues(water), price: 20 };
    for (const hsnSac of ['', '2201', '220190', '22019090', ' 996333 ']) {
      expect(menuItemSchema.safeParse({ ...values, hsnSac }).success).toBe(true);
    }
    for (const hsnSac of ['22019', '2201909', '220190901', '99633A', '2201-90']) {
      const result = menuItemSchema.safeParse({ ...values, hsnSac });
      expect(result.success).toBe(false);
      expect(result.error?.issues.map((i) => i.path.join('.'))).toEqual(['hsnSac']);
    }
  });

  it('keeps the code when only availability is switched from the menu list', () => {
    expect(menuItemBody(water, { isAvailable: false })).toMatchObject({
      hsnSac: '220190',
      isAvailable: false,
      version: 7,
    });
    expect(menuItemBody(tea).hsnSac).toBeNull();
  });
});
