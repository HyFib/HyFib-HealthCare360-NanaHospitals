import { describe, expect, it } from 'vitest';
import { toFormPath } from './serverErrors';

describe('toFormPath', () => {
  it('turns collection indexers into react-hook-form dot paths', () => {
    expect(toFormPath('lines[0].quantity')).toBe('lines.0.quantity');
    expect(toFormPath('rows[12].items[3].name')).toBe('rows.12.items.3.name');
  });

  it('leaves plain paths alone', () => {
    expect(toFormPath('mobile')).toBe('mobile');
    expect(toFormPath('address.pinCode')).toBe('address.pinCode');
  });

  it('handles a root-level index', () => {
    expect(toFormPath('[0].name')).toBe('0.name');
  });
});
