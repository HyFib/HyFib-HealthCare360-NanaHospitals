import type { PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

export const MENU_CATEGORIES = [
  'Breakfast',
  'Meals',
  'Snacks',
  'Beverages',
  'Juices',
  'Sweets',
  'Packaged',
  'Other',
] as const;
export type MenuCategory = (typeof MENU_CATEGORIES)[number];

export const CUSTOMER_TYPES = ['Staff', 'Attendant', 'Visitor', 'Patient'] as const;
export type CanteenCustomerType = (typeof CUSTOMER_TYPES)[number];

export const PAYMENT_MODES = ['Cash', 'Upi', 'Card', 'StaffCredit'] as const;
export type CanteenPaymentMode = (typeof PAYMENT_MODES)[number];

export type CanteenSaleStatus = 'Completed' | 'Cancelled';
export type StockMovementKind = 'Receipt' | 'Adjustment' | 'Wastage';
export type StaffCreditEntryKind = 'Sale' | 'Cancellation' | 'PayrollDeduction' | 'Payment';

export const PAYMENT_LABEL: Record<CanteenPaymentMode, string> = {
  Cash: 'Cash',
  Upi: 'UPI',
  Card: 'Card',
  StaffCredit: 'Staff credit',
};
export const CUSTOMER_LABEL: Record<CanteenCustomerType, string> = {
  Staff: 'Staff',
  Attendant: 'Attendant',
  Visitor: 'Visitor',
  Patient: 'Patient',
};
export const CREDIT_KIND_LABEL: Record<StaffCreditEntryKind, string> = {
  Sale: 'Canteen purchase',
  Cancellation: 'Bill cancelled',
  PayrollDeduction: 'Salary deduction',
  Payment: 'Paid at counter',
};

/** GST slabs the server accepts for menu items. */
export const GST_RATES = [0, 5, 12, 18, 28] as const;

/** SAC "services provided by canteens" that the server puts on bills for items without their own code (`MenuItem.DefaultSac`). */
export const CANTEEN_SAC = '996333';

/** "SAC 996333" for services (SAC codes are chapter 99), "HSN 220190" for goods. */
export function hsnSacLabel(code: string): string {
  return `${code.startsWith('99') ? 'SAC' : 'HSN'} ${code}`;
}

export interface MenuItemDto {
  id: string;
  code: string;
  name: string;
  category: MenuCategory;
  price: number;
  gstRate: number;
  /** HSN code of packaged goods or a SAC; null for food made in the canteen (billed under {@link CANTEEN_SAC}). */
  hsnSac: string | null;
  priceIncludesGst: boolean;
  isVeg: boolean;
  isAvailable: boolean;
  trackStock: boolean;
  stockOnHand: number;
  displayOrder: number;
  description: string | null;
  version: number;
}

export interface SaveMenuItemRequest {
  code: string;
  name: string;
  category: MenuCategory;
  price: number;
  gstRate: number;
  hsnSac: string | null;
  priceIncludesGst: boolean;
  isVeg: boolean;
  isAvailable: boolean;
  trackStock: boolean;
  displayOrder: number;
  description: string | null;
  version: number | null;
}

export interface CreateSaleRequest {
  customerType: CanteenCustomerType;
  customerName: string | null;
  employeeCode: string | null;
  employeeName: string | null;
  patientId: string | null;
  paymentMode: CanteenPaymentMode;
  paymentReference: string | null;
  lines: { menuItemId: string; quantity: number }[];
}

export interface CanteenSaleLineDto {
  menuItemId: string;
  itemName: string;
  /** Copied from the menu item when the bill was made; null only on bills made before codes were recorded. */
  hsnSac: string | null;
  quantity: number;
  unitPrice: number;
  gstRate: number;
  taxableAmount: number;
  cgstAmount: number;
  sgstAmount: number;
  lineTotal: number;
}

export interface CanteenSaleDto {
  id: string;
  billNumber: string;
  soldAt: string;
  businessDate: string;
  customerType: CanteenCustomerType;
  customerName: string | null;
  employeeCode: string | null;
  employeeName: string | null;
  taxableAmount: number;
  cgstAmount: number;
  sgstAmount: number;
  grossAmount: number;
  roundOff: number;
  netAmount: number;
  paymentMode: CanteenPaymentMode;
  paymentReference: string | null;
  status: CanteenSaleStatus;
  cashierName: string | null;
  cancelledAt: string | null;
  cancelReason: string | null;
  lines: CanteenSaleLineDto[];
}

export interface CanteenSaleListItem {
  id: string;
  billNumber: string;
  soldAt: string;
  customerType: CanteenCustomerType;
  customerName: string | null;
  employeeCode: string | null;
  netAmount: number;
  paymentMode: CanteenPaymentMode;
  status: CanteenSaleStatus;
  cashierName: string | null;
}

export interface AmountByKey {
  key: string;
  bills: number;
  amount: number;
}

export interface ItemConsumption {
  menuItemId: string;
  itemName: string;
  category: MenuCategory | null;
  quantitySold: number;
  amount: number;
  closingStock: number | null;
}

/** Completed bill lines of one HSN/SAC code and GST rate (for the GSTR-1 HSN summary). */
export interface HsnSacSummary {
  /** Null for lines billed before codes were recorded. */
  hsnSac: string | null;
  gstRate: number;
  quantity: number;
  taxableAmount: number;
  cgst: number;
  sgst: number;
  /** Taxable value + CGST + SGST, before the bills' round-off. */
  total: number;
}

export interface DailySalesSummary {
  date: string;
  bills: number;
  cancelledBills: number;
  taxableAmount: number;
  cgst: number;
  sgst: number;
  roundOff: number;
  netSales: number;
  byPaymentMode: AmountByKey[];
  byCustomerType: AmountByKey[];
  byCategory: AmountByKey[];
  items: ItemConsumption[];
  byHsnSac: HsnSacSummary[];
}

export interface StaffBalanceDto {
  employeeCode: string;
  employeeName: string | null;
  balance: number;
  lastEntryDate: string;
}

export interface StaffStatementLine {
  date: string;
  kind: StaffCreditEntryKind;
  amount: number;
  reference: string | null;
  balance: number;
}

export interface StaffStatement {
  employeeCode: string;
  employeeName: string | null;
  period: string;
  openingBalance: number;
  purchases: number;
  credits: number;
  closingBalance: number;
  lines: StaffStatementLine[];
}

/**
 * Result of a payroll settlement. `employees` and `amount` count only balances posted to payroll as CANTEEN
 * deductions; `skipped` has one plain-language line per balance that was not deducted (collect those at the counter).
 */
export interface SettlementResult {
  period: string;
  employees: number;
  amount: number;
  skipped?: string[] | null;
}

export interface SalesFilters {
  date?: string;
  status?: CanteenSaleStatus;
  paymentMode?: CanteenPaymentMode;
  employeeCode?: string;
  search?: string;
  page?: number;
}

export const canteenKeys = {
  all: ['canteen'] as const,
  menu: (availableOnly: boolean) => ['canteen', 'menu', availableOnly] as const,
  sales: (filters: SalesFilters) => ['canteen', 'sales', filters] as const,
  sale: (id: string) => ['canteen', 'sale', id] as const,
  daily: (date: string) => ['canteen', 'daily', date] as const,
  balances: (outstandingOnly: boolean) => ['canteen', 'balances', outstandingOnly] as const,
  statement: (employeeCode: string, period: string) => ['canteen', 'statement', employeeCode, period] as const,
};

export const canteenQueries = {
  menu: (availableOnly: boolean) =>
    queryOptions({
      queryKey: canteenKeys.menu(availableOnly),
      queryFn: ({ signal }) =>
        api.get<MenuItemDto[]>('/api/v1/canteen/menu', {
          query: { availableOnly: availableOnly || undefined },
          signal,
        }),
      // Stock of packaged items changes with every bill at every counter.
      refetchInterval: 60_000,
    }),
  sales: (filters: SalesFilters) =>
    queryOptions({
      queryKey: canteenKeys.sales(filters),
      queryFn: ({ signal }) =>
        api.get<PagedResult<CanteenSaleListItem>>('/api/v1/canteen/sales', {
          query: { pageSize: 25, ...filters },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  sale: (id: string) =>
    queryOptions({
      queryKey: canteenKeys.sale(id),
      queryFn: ({ signal }) => api.get<CanteenSaleDto>(`/api/v1/canteen/sales/${id}`, { signal }),
    }),
  daily: (date: string) =>
    queryOptions({
      queryKey: canteenKeys.daily(date),
      queryFn: ({ signal }) => api.get<DailySalesSummary>('/api/v1/canteen/reports/daily', { query: { date }, signal }),
    }),
  balances: (outstandingOnly: boolean) =>
    queryOptions({
      queryKey: canteenKeys.balances(outstandingOnly),
      queryFn: ({ signal }) =>
        api.get<StaffBalanceDto[]>('/api/v1/canteen/staff-credit/balances', { query: { outstandingOnly }, signal }),
    }),
  statement: (employeeCode: string, period: string) =>
    queryOptions({
      queryKey: canteenKeys.statement(employeeCode, period),
      queryFn: ({ signal }) =>
        api.get<StaffStatement>('/api/v1/canteen/staff-credit/statement', { query: { employeeCode, period }, signal }),
    }),
};

export const canteenApi = {
  createMenuItem: (body: SaveMenuItemRequest) => api.post<MenuItemDto>('/api/v1/canteen/menu', body),
  updateMenuItem: (id: string, body: SaveMenuItemRequest) => api.put<void>(`/api/v1/canteen/menu/${id}`, body),
  moveStock: (id: string, body: { kind: StockMovementKind; quantity: number; reason: string | null }) =>
    api.post<MenuItemDto>(`/api/v1/canteen/menu/${id}/stock`, body),
  createSale: (body: CreateSaleRequest) => api.post<CanteenSaleDto>('/api/v1/canteen/sales', body),
  cancelSale: (id: string, reason: string) =>
    api.post<CanteenSaleDto>(`/api/v1/canteen/sales/${id}/cancel`, { reason }),
  recordPayment: (body: { employeeCode: string; amount: number; reference: string | null }) =>
    api.post<StaffBalanceDto>('/api/v1/canteen/staff-credit/payments', body),
  settle: (body: { period: string; reference: string; employeeCodes: string[] | null }) =>
    api.post<SettlementResult>('/api/v1/canteen/staff-credit/settlements', body),
  exportCsv: (period: string) => api.blob('/api/v1/canteen/staff-credit/export', { query: { period } }),
};

/** The body for updating a menu item from its DTO (used by quick toggles), keeping every other field. */
export function menuItemBody(item: MenuItemDto, patch: Partial<SaveMenuItemRequest> = {}): SaveMenuItemRequest {
  return {
    code: item.code,
    name: item.name,
    category: item.category,
    price: item.price,
    gstRate: item.gstRate,
    hsnSac: item.hsnSac,
    priceIncludesGst: item.priceIncludesGst,
    isVeg: item.isVeg,
    isAvailable: item.isAvailable,
    trackStock: item.trackStock,
    displayOrder: item.displayOrder,
    description: item.description,
    version: item.version,
    ...patch,
  };
}
