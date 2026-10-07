import { BoxArrowUpRegular, BoxCheckmarkRegular, ClipboardTaskRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { CssdWidget } from './widgets';

const manifest: ModuleManifest = {
  id: 'cssd',
  title: 'CSSD',
  section: 'support',
  icon: BoxCheckmarkRegular,
  order: 10,
  links: [
    {
      label: 'Overview',
      to: '/cssd',
      permission: 'cssd.read',
      keywords: ['sterile', 'CSSD dashboard', 'sterilization'],
    },
    {
      label: 'Requests',
      to: '/cssd/requests',
      permission: ['cssd.read', 'cssd.requests'],
      keywords: ['sterile sets', 'indent', 'OT trays'],
    },
    {
      label: 'Soiled receipt',
      to: '/cssd/receipts',
      permission: 'cssd.read',
      keywords: ['used trays', 'instrument count', 'returned'],
    },
    {
      label: 'Wash cycles',
      to: '/cssd/washes',
      permission: ['cssd.read', 'cssd.process'],
      keywords: ['washer-disinfector', 'decontamination', 'A0', 'cleaning'],
    },
    {
      label: 'Sterilizer loads',
      to: '/cssd/loads',
      permission: ['cssd.read', 'cssd.process'],
      keywords: ['autoclave', 'cycle', 'biological indicator', 'ETO', 'plasma'],
    },
    {
      label: 'Sterile stock',
      to: '/cssd/packs',
      permission: ['cssd.read', 'cssd.process'],
      keywords: ['packs', 'expiry', 'shelf'],
    },
    {
      label: 'Issue packs',
      to: '/cssd/issue',
      permission: 'cssd.process',
      keywords: ['issue', 'dispatch', 'scan pack'],
    },
    {
      label: 'Issued packs',
      to: '/cssd/issues',
      permission: ['cssd.read', 'cssd.recalls'],
      keywords: ['used on patient', 'traceability'],
    },
    {
      label: 'Recalls',
      to: '/cssd/recalls',
      permission: ['cssd.recalls', 'cssd.read'],
      keywords: ['recall', 'failed load', 'infection control'],
    },
    {
      label: 'Instrument sets',
      to: '/cssd/sets',
      permission: ['cssd.masters.manage', 'cssd.read'],
      keywords: ['count sheet', 'trays', 'masters'],
    },
  ],
  quickActions: [
    {
      label: 'Request sterile sets',
      to: '/cssd/requests',
      permission: 'cssd.requests',
      icon: ClipboardTaskRegular,
      description: 'Ask CSSD for trays and packs',
    },
    {
      label: 'Issue sterile packs',
      to: '/cssd/issue',
      permission: 'cssd.process',
      icon: BoxArrowUpRegular,
      description: 'Scan packs out to a department',
    },
  ],
  widgets: [{ id: 'cssd-overview', permission: 'cssd.read', span: 2, order: 60, component: CssdWidget }],
};

export default manifest;
