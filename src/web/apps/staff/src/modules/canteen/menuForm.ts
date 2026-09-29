import { z } from 'zod';
import { MENU_CATEGORIES, type MenuItemDto, type SaveMenuItemRequest } from './api';
import { isValidHsnSac } from './rules';

/** The add/edit menu item form. */
export const menuItemSchema = z.object({
  code: z
    .string()
    .trim()
    .min(1, 'Enter a short code, e.g. TEA.')
    .max(24)
    .regex(/^[A-Za-z0-9-]+$/, 'Use letters, digits or dashes.'),
  name: z.string().trim().min(1, 'Enter the item name.').max(100),
  category: z.enum(MENU_CATEGORIES),
  price: z
    .number({ message: 'Enter the price.' })
    .gt(0, 'The price must be more than zero.')
    .max(10_000)
    .refine((v) => Math.round(v * 100) === v * 100, 'Use at most two decimals.'),
  gstRate: z.string(),
  hsnSac: z
    .string()
    .trim()
    .refine((v) => !v || isValidHsnSac(v), 'Enter 4, 6 or 8 digits, or leave it empty for food made in the canteen.'),
  priceIncludesGst: z.boolean(),
  isVeg: z.boolean(),
  isAvailable: z.boolean(),
  trackStock: z.boolean(),
  displayOrder: z.number({ message: 'Enter a number.' }).int().min(0).max(10_000),
  description: z.string().trim().max(300),
});

export type MenuValues = z.infer<typeof menuItemSchema>;

/** Form values for an item, or for a new one (5 % GST, price including GST, as on the menu board). */
export function menuFormValues(item: MenuItemDto | null): MenuValues {
  return {
    code: item?.code ?? '',
    name: item?.name ?? '',
    category: item?.category ?? 'Snacks',
    // An empty number box until the price is typed.
    price: item?.price ?? (null as unknown as number),
    gstRate: String(item?.gstRate ?? 5),
    hsnSac: item?.hsnSac ?? '',
    priceIncludesGst: item?.priceIncludesGst ?? true,
    isVeg: item?.isVeg ?? true,
    isAvailable: item?.isAvailable ?? true,
    trackStock: item?.trackStock ?? false,
    displayOrder: item?.displayOrder ?? 0,
    description: item?.description ?? '',
  };
}

/** The request body for saving the form; an empty HSN/SAC code means the canteen SAC. */
export function menuItemRequest(values: MenuValues, item: MenuItemDto | null): SaveMenuItemRequest {
  return {
    ...values,
    gstRate: Number(values.gstRate),
    hsnSac: values.hsnSac.trim() || null,
    description: values.description.trim() || null,
    version: item?.version ?? null,
  };
}
