import { BoxMultipleRegular, BoxRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { PHARMACY_PERMISSIONS } from '@/modules/pharmacy/permissions';
import {
  ADJUSTMENT_READERS,
  CATALOG_READERS,
  INDENT_READERS,
  REPORT_READERS,
  STORE_READERS,
  SUPPLY_PERMISSIONS as S,
  TRANSFER_READERS,
} from './permissions';
import { NearExpiryStockWidget } from './widgets';

const manifest: ModuleManifest = {
  id: 'inventory',
  title: 'Stores and inventory',
  section: 'pharmacy',
  icon: BoxMultipleRegular,
  order: 20,
  links: [
    {
      label: 'Store overview',
      to: '/inventory',
      permission: S.stockRead,
      keywords: ['inventory', 'stock value', 'dashboard'],
    },
    {
      label: 'Stock and batches',
      to: '/inventory/stock',
      permission: S.stockRead,
      keywords: ['on hand', 'batch', 'expiry', 'fefo', 'block'],
    },
    {
      label: 'Stock ledger',
      to: '/inventory/ledger',
      permission: S.stockRead,
      keywords: ['stock card', 'movement', 'running balance'],
    },
    {
      label: 'Indents',
      to: '/inventory/indents',
      permission: INDENT_READERS,
      keywords: ['requisition', 'issue', 'sub-store', 'department'],
    },
    {
      label: 'Transfers',
      to: '/inventory/transfers',
      permission: TRANSFER_READERS,
      keywords: ['stock transfer', 'send', 'receive', 'in transit'],
    },
    {
      label: 'Stock counts',
      to: '/inventory/counts',
      permission: [S.counts, S.adjustmentsApprove, S.stockRead],
      keywords: ['physical count', 'audit', 'count sheet'],
    },
    {
      label: 'Adjustments',
      to: '/inventory/adjustments',
      permission: ADJUSTMENT_READERS,
      keywords: ['write-off', 'damage', 'breakage', 'variance'],
    },
    {
      label: 'Inventory reports',
      to: '/inventory/reports',
      permission: REPORT_READERS,
      keywords: ['near expiry', 'valuation', 'reorder', 'abc', 'ved', 'fsn'],
    },
    {
      label: 'Item master',
      to: '/inventory/items',
      permission: CATALOG_READERS,
      keywords: ['drug master', 'hsn', 'gst', 'schedule'],
    },
    { label: 'Item categories', to: '/inventory/categories', permission: CATALOG_READERS, keywords: ['groups'] },
    {
      label: 'Stores',
      to: '/inventory/stores',
      permission: STORE_READERS,
      keywords: ['central store', 'sub-store', 'location'],
    },
  ],
  quickActions: [
    {
      label: 'Raise store indent',
      to: '/inventory/indents',
      permission: S.indentsRaise,
      icon: BoxRegular,
      description: 'Request items from the central store',
    },
  ],
  widgets: [
    {
      id: 'inventory-near-expiry',
      permission: [
        S.stockRead,
        PHARMACY_PERMISSIONS.queueRead,
        PHARMACY_PERMISSIONS.sales,
        PHARMACY_PERMISSIONS.ipIssue,
      ],
      span: 2,
      order: 45,
      component: NearExpiryStockWidget,
    },
  ],
};

export default manifest;
