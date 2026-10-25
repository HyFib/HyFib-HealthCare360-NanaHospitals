import { CartRegular, ClipboardTaskListLtrRegular, PillRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { COUNTER_STAFF, IP_READERS, PHARMACY_PERMISSIONS as P, QUEUE_READERS, SALE_READERS } from './permissions';
import { PharmacyQueueWidget } from './widgets';

const manifest: ModuleManifest = {
  id: 'pharmacy',
  title: 'Pharmacy',
  section: 'pharmacy',
  icon: PillRegular,
  order: 10,
  links: [
    {
      label: 'Overview',
      to: '/pharmacy',
      permission: [P.queueRead, P.sales, P.ipIssue],
      keywords: ['pharmacist', 'dashboard', 'takings'],
    },
    {
      label: 'Dispensing queue',
      to: '/pharmacy/queue',
      permission: QUEUE_READERS,
      keywords: ['e-prescription', 'rx', 'dispense', 'op pharmacy'],
    },
    {
      label: 'Counter sale',
      to: '/pharmacy/sale',
      permission: P.sales,
      keywords: ['pos', 'otc', 'billing', 'walk-in', 'barcode'],
    },
    {
      label: 'Invoices and returns',
      to: '/pharmacy/sales',
      permission: SALE_READERS,
      keywords: ['bill', 'refund', 'due', 'cancel invoice', 'reprint'],
    },
    {
      label: 'Ward indents',
      to: '/pharmacy/ward-indents',
      permission: IP_READERS,
      keywords: ['ip', 'nurse', 'stat', 'ward', 'in-patient'],
    },
    {
      label: 'IP issues and returns',
      to: '/pharmacy/ip-issues',
      permission: IP_READERS,
      keywords: ['in-patient', 'ward return', 'ip account'],
    },
    {
      label: 'Drug registers',
      to: '/pharmacy/registers',
      permission: P.registers,
      keywords: ['schedule h', 'h1', 'narcotic', 'ndps', 'controlled'],
    },
    {
      label: 'Adverse drug reactions',
      to: '/pharmacy/adr',
      permission: [P.adrReport, P.adrManage],
      keywords: ['adr', 'pvpi', 'pharmacovigilance', 'side effect', 'reaction', 'amc', 'vigiflow'],
    },
    {
      label: 'Narcotic accounts (NDPS)',
      to: '/pharmacy/ndps',
      permission: [P.registers, P.ndpsCount],
      keywords: [
        'ndps',
        'narcotic',
        'morphine',
        'form 3h',
        'form 3e',
        'form 3j',
        'shift count',
        'essential narcotic drugs',
      ],
    },
    {
      label: 'Refill requests',
      to: '/pharmacy/refills',
      permission: P.refills,
      keywords: ['chronic', 'repeat prescription', 'portal'],
    },
  ],
  quickActions: [
    {
      label: 'Dispense prescriptions',
      to: '/pharmacy/queue',
      permission: P.dispense,
      icon: PillRegular,
      description: 'Open the e-prescription queue',
    },
    {
      label: 'Counter sale',
      to: '/pharmacy/sale',
      permission: P.sales,
      icon: CartRegular,
      description: 'Sell medicines over the counter',
    },
    {
      label: 'Raise ward indent',
      to: '/pharmacy/ward-indents',
      permission: P.ipIndent,
      icon: ClipboardTaskListLtrRegular,
      description: 'Ask the IP pharmacy for medicines',
    },
  ],
  widgets: [{ id: 'pharmacy-queue', permission: COUNTER_STAFF, span: 2, order: 40, component: PharmacyQueueWidget }],
};

export default manifest;
