import { ArchiveRegular, ClipboardTaskListLtrRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { MedicalRecordsWidget } from './widgets';

const manifest: ModuleManifest = {
  id: 'mrd',
  title: 'Medical records',
  section: 'clinical',
  icon: ArchiveRegular,
  order: 30,
  links: [
    {
      label: 'Overview',
      to: '/mrd',
      permission: ['mrd.coding', 'mrd.records'],
      keywords: ['mrd', 'medical records department'],
    },
    {
      label: 'Coding queue',
      to: '/mrd/coding',
      permission: 'mrd.coding',
      keywords: ['icd', 'icd-10', 'clinical coding', 'discharge coding', 'verify'],
    },
    {
      label: 'Patient charts',
      to: '/mrd/records',
      permission: 'mrd.records',
      keywords: ['chart', 'deficiency', 'completion', 'archive', 'retention', 'file'],
    },
    {
      label: 'Record requests',
      to: '/mrd/requests',
      permission: 'mrd.records',
      keywords: ['file issue', 'return', 'medical records request', 'copy'],
    },
    {
      label: 'ICD-10 browser',
      to: '/mrd/icd',
      permission: ['mrd.coding', 'clinical.emr.read'],
      keywords: ['icd', 'diagnosis codes', 'who'],
    },
    {
      label: 'Morbidity report',
      to: '/mrd/morbidity',
      permission: 'mrd.records',
      keywords: ['disease index', 'statistics', 'mis'],
    },
  ],
  quickActions: [
    {
      label: 'Code discharges',
      to: '/mrd/coding',
      permission: 'mrd.coding',
      icon: ClipboardTaskListLtrRegular,
      description: 'ICD-10 coding waiting after discharge',
    },
  ],
  widgets: [
    { id: 'mrd-work', permission: ['mrd.coding', 'mrd.records'], span: 2, order: 40, component: MedicalRecordsWidget },
  ],
};

export default manifest;
