import { BoxArrowLeftRegular, VehicleTruckProfileRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { PURCHASE_READERS, SUPPLY_PERMISSIONS as S, VENDOR_READERS } from '@/modules/inventory/permissions';

const manifest: ModuleManifest = {
  id: 'purchase',
  title: 'Purchase',
  section: 'pharmacy',
  icon: VehicleTruckProfileRegular,
  order: 30,
  links: [
    { label: 'Purchase overview', to: '/purchase', permission: PURCHASE_READERS, keywords: ['procurement', 'approvals'] },
    { label: 'Requisitions', to: '/purchase/requisitions', permission: PURCHASE_READERS, keywords: ['purchase request', 'reorder', 'pr'] },
    { label: 'Purchase orders', to: '/purchase/orders', permission: PURCHASE_READERS, keywords: ['po', 'order', 'vendor order'] },
    { label: 'Goods receipts', to: '/purchase/grns', permission: PURCHASE_READERS, keywords: ['grn', 'inward', 'vendor invoice', 'receive'] },
    { label: 'Vendors', to: '/purchase/vendors', permission: VENDOR_READERS, keywords: ['supplier', 'gstin', 'drug licence'] },
  ],
  quickActions: [
    {
      label: 'Receive goods',
      to: '/purchase/grns/new',
      permission: S.grnReceive,
      icon: BoxArrowLeftRegular,
      description: 'Record a vendor invoice (GRN)',
    },
  ],
};

export default manifest;
