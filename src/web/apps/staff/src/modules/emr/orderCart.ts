/**
 * The order composer's cart (pure, unit-tested): one line per service, quantities only where the server allows them
 * (blood units, physiotherapy and dialysis sessions), and the request lines.
 */

export type ServiceCategory =
  | 'Registration'
  | 'Consultation'
  | 'Laboratory'
  | 'Radiology'
  | 'Cardiology'
  | 'Procedure'
  | 'Surgery'
  | 'BedCharge'
  | 'Nursing'
  | 'DoctorVisit'
  | 'Package'
  | 'HealthCheckup'
  | 'Physiotherapy'
  | 'Dialysis'
  | 'Dietary'
  | 'BloodBank'
  | 'Ambulance'
  | 'Consumable'
  | 'Miscellaneous';

/** Categories the EMR may order (server `ClinicalOrderService.OrderableCategories`). */
export const ORDERABLE_CATEGORIES: readonly ServiceCategory[] = [
  'Laboratory',
  'Radiology',
  'Cardiology',
  'Procedure',
  'Surgery',
  'Physiotherapy',
  'Dialysis',
  'Dietary',
  'BloodBank',
  'Consultation',
];

export const serviceCategoryLabel: Record<ServiceCategory, string> = {
  Registration: 'Registration',
  Consultation: 'Consultation',
  Laboratory: 'Lab',
  Radiology: 'Imaging',
  Cardiology: 'Cardiology',
  Procedure: 'Procedure',
  Surgery: 'Surgery',
  BedCharge: 'Bed charge',
  Nursing: 'Nursing',
  DoctorVisit: 'Doctor visit',
  Package: 'Package',
  HealthCheckup: 'Health check-up',
  Physiotherapy: 'Physiotherapy',
  Dialysis: 'Dialysis',
  Dietary: 'Diet',
  BloodBank: 'Blood bank',
  Ambulance: 'Ambulance',
  Consumable: 'Consumable',
  Miscellaneous: 'Other',
};

/** Tabs of the composer: the common groups first, everything else under "Other". */
export const ORDER_GROUPS = [
  { id: 'all', label: 'All', categories: null },
  { id: 'lab', label: 'Lab', categories: ['Laboratory'] },
  { id: 'imaging', label: 'Imaging', categories: ['Radiology'] },
  { id: 'cardiology', label: 'Cardiology', categories: ['Cardiology'] },
  { id: 'procedures', label: 'Procedures', categories: ['Procedure', 'Surgery'] },
  { id: 'other', label: 'Other', categories: ['Physiotherapy', 'Dialysis', 'Dietary', 'BloodBank', 'Consultation'] },
] as const satisfies readonly { id: string; label: string; categories: readonly ServiceCategory[] | null }[];

export type OrderGroupId = (typeof ORDER_GROUPS)[number]['id'];

/** Largest quantity one line may carry: blood components ≤ 10 units, physiotherapy/dialysis ≤ 30 sessions, else 1. */
export function maxQuantity(category: ServiceCategory): number {
  if (category === 'BloodBank') {
    return 10;
  }
  return category === 'Physiotherapy' || category === 'Dialysis' ? 30 : 1;
}

export function quantityUnit(category: ServiceCategory): string {
  return category === 'BloodBank' ? 'units' : 'sessions';
}

export interface CartLine {
  serviceItemId: string;
  code: string;
  name: string;
  category: ServiceCategory;
  quantity: number;
  notes: string;
}

export interface CartService {
  id: string;
  code: string;
  name: string;
  category: ServiceCategory;
}

/** Adds a service once (a second add is ignored; repeat tests are separate orders). */
export function addToCart(cart: readonly CartLine[], service: CartService): CartLine[] {
  if (cart.some((line) => line.serviceItemId === service.id)) {
    return [...cart];
  }
  return [...cart, { serviceItemId: service.id, code: service.code, name: service.name, category: service.category, quantity: 1, notes: '' }];
}

export function addManyToCart(cart: readonly CartLine[], services: readonly CartService[]): CartLine[] {
  return services.reduce<CartLine[]>((next, service) => addToCart(next, service), [...cart]);
}

export function removeFromCart(cart: readonly CartLine[], serviceItemId: string): CartLine[] {
  return cart.filter((line) => line.serviceItemId !== serviceItemId);
}

/** Sets a line's quantity, clamped to what its category allows. */
export function setQuantity(cart: readonly CartLine[], serviceItemId: string, quantity: number): CartLine[] {
  return cart.map((line) => {
    if (line.serviceItemId !== serviceItemId) {
      return line;
    }
    const whole = Number.isFinite(quantity) ? Math.round(quantity) : 1;
    return { ...line, quantity: Math.min(Math.max(whole, 1), maxQuantity(line.category)) };
  });
}

export function setNotes(cart: readonly CartLine[], serviceItemId: string, notes: string): CartLine[] {
  return cart.map((line) => (line.serviceItemId === serviceItemId ? { ...line, notes: notes.slice(0, 500) } : line));
}

/** Request lines; quantity is sent only when it differs from the default of 1. */
export function toOrderLines(cart: readonly CartLine[]): { serviceItemId: string; notes: string | null; quantity: number | null }[] {
  return cart.map((line) => ({
    serviceItemId: line.serviceItemId,
    notes: line.notes.trim() || null,
    quantity: line.quantity > 1 ? line.quantity : null,
  }));
}
