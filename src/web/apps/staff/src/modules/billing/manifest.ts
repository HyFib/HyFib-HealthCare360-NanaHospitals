import { ReceiptAddRegular, ReceiptMoneyRegular, WalletRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { DiscountApprovalsWidget, TodaysCollectionsWidget } from './widgets';

const BILL_READERS = ['billing.charges.read', 'billing.op.create', 'billing.ip.manage', 'billing.payments.collect'] as const;

const manifest: ModuleManifest = {
  id: 'billing',
  title: 'Billing',
  section: 'finance',
  icon: ReceiptMoneyRegular,
  order: 10,
  links: [
    {
      label: 'Cashier desk',
      to: '/billing',
      permission: ['billing.payments.collect', 'billing.shifts', 'billing.charges.read', 'billing.op.create'],
      keywords: ['cash counter', 'collections', 'dues', 'billing dashboard'],
    },
    { label: 'OP billing', to: '/billing/op', permission: 'billing.op.create', keywords: ['op bill', 'counter', 'invoice', 'cash', 'receipt', 'GST'] },
    { label: 'Bills', to: '/billing/bills', permission: BILL_READERS, keywords: ['invoice', 'reprint', 'cancel bill', 'credit note'] },
    { label: 'Receipts', to: '/billing/receipts', permission: ['billing.charges.read', 'billing.payments.collect'], keywords: ['payment', 'reprint receipt'] },
    { label: 'In-patient accounts', to: '/billing/ip', permission: ['billing.ip.manage', 'billing.charges.read'], keywords: ['ip bill', 'running bill', 'final bill', 'discharge clearance', 'package'] },
    { label: 'Collect IP advance', to: '/billing/deposits', permission: 'billing.deposits', keywords: ['deposit', 'advance', 'ip advance'] },
    { label: 'Refunds', to: '/billing/refunds', permission: ['billing.refunds.request', 'billing.refunds.approve', 'billing.charges.read'], keywords: ['refund', 'return money'] },
    { label: 'Discount approvals', to: '/billing/discount-approvals', permission: 'billing.discounts.approve', keywords: ['discount', 'concession', 'approval', 'limits'] },
    { label: 'Payment links', to: '/billing/payment-links', permission: ['billing.payments.collect', 'billing.charges.read'], keywords: ['upi link', 'online payment', 'sms link'] },
    { label: 'Cash shifts', to: '/billing/shifts', permission: 'billing.shifts', keywords: ['shift', 'cash drawer', 'counter close'] },
    { label: 'Day-end report', to: '/billing/day-end', permission: 'billing.shifts', keywords: ['day end', 'collection report', 'cash report'] },
    { label: 'Tariffs', to: '/billing/tariffs', permission: ['billing.tariffs.manage', 'billing.charges.read'], keywords: ['price list', 'rates', 'tariff plan'] },
    { label: 'Packages', to: '/billing/packages', permission: ['billing.tariffs.manage', 'billing.ip.manage', 'billing.charges.read'], keywords: ['package', 'surgery package'] },
    { label: 'Tally export', to: '/billing/tally', permission: 'billing.tally.export', keywords: ['tally', 'day book', 'accounts', 'vouchers'] },
  ],
  quickActions: [
    { label: 'OP billing', to: '/billing/op', permission: 'billing.op.create', icon: ReceiptAddRegular, description: 'Bill a visit and collect payment' },
    { label: 'Collect deposit', to: '/billing/deposits', permission: 'billing.deposits', icon: WalletRegular, description: 'Advance for an admitted patient' },
  ],
  widgets: [
    { id: 'billing-collections', permission: 'billing.payments.collect', span: 2, order: 40, component: TodaysCollectionsWidget },
    { id: 'billing-discount-approvals', permission: 'billing.discounts.approve', span: 2, order: 45, component: DiscountApprovalsWidget },
  ],
};

export default manifest;
