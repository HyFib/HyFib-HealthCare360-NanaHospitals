import { DropRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { BloodStockWidget } from './widgets';

const manifest: ModuleManifest = {
  id: 'blood-bank',
  title: 'Blood bank',
  section: 'diagnostics',
  icon: DropRegular,
  order: 60,
  links: [
    { label: 'Blood stock', to: '/blood-bank', permission: 'bloodbank.read', keywords: ['blood bank', 'inventory', 'expiring', 'units'] },
    { label: 'Blood requests', to: '/blood-bank/requests', permission: 'bloodbank.read', keywords: ['cross-match', 'issue', 'transfusion', 'emergency release'] },
    { label: 'Request blood', to: '/blood-bank/requests/new', permission: 'bloodbank.requests.create', keywords: ['transfusion', 'prbc', 'ffp', 'platelets'] },
    { label: 'Blood units', to: '/blood-bank/units', permission: 'bloodbank.read', keywords: ['unit number', 'barcode', 'label', 'quarantine'] },
    { label: 'Donors', to: '/blood-bank/donors', permission: 'bloodbank.donors.manage', keywords: ['donor', 'screening', 'eligibility', 'collection'] },
    { label: 'Donations', to: '/blood-bank/donations', permission: 'bloodbank.inventory.manage', keywords: ['bag', 'components', 'separation', 'tti', 'grouping'] },
    { label: 'Transfusion reactions', to: '/blood-bank/reactions', permission: 'bloodbank.read', keywords: ['haemovigilance', 'adverse reaction', 'hvpi'] },
  ],
  quickActions: [
    {
      label: 'Request blood',
      to: '/blood-bank/requests/new',
      permission: 'bloodbank.requests.create',
      icon: DropRegular,
      description: 'Component, units and urgency for a patient',
    },
  ],
  widgets: [{ id: 'blood-stock', permission: 'bloodbank.read', span: 2, order: 60, component: BloodStockWidget }],
};

export default manifest;
