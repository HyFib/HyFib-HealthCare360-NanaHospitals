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
      label: 'ICD catalogue',
      to: '/mrd/icd',
      permission: ['mrd.coding', 'clinical.emr.read', 'mrd.catalogue.import'],
      keywords: ['icd', 'icd-10', 'icd-11', 'icd-10-pcs', 'diagnosis codes', 'who', 'import', 'claml'],
    },
    {
      label: 'Procedure catalogue',
      to: '/mrd/procedure-codes',
      permission: ['mrd.coding', 'clinical.emr.read', 'mrd.catalogue.import'],
      keywords: ['procedure codes', 'icd-10-pcs', 'pcs', 'surgery codes', 'operation codes'],
    },
    {
      label: 'Morbidity report',
      to: '/mrd/morbidity',
      permission: 'mrd.records',
      keywords: ['disease index', 'statistics', 'mis'],
    },
    {
      label: 'Births',
      to: '/mrd/births',
      permission: ['mrd.births.record', 'mrd.vital.register'],
      keywords: ['birth register', 'birth report', 'form 1', 'form 3', 'still birth', 'crs', 'registrar'],
    },
    {
      label: 'Deaths',
      to: '/mrd/deaths',
      permission: ['mrd.deaths.certify', 'mrd.vital.register'],
      keywords: ['death register', 'death certificate', 'mccd', 'form 4', 'cause of death', 'crs', 'registrar'],
    },
    {
      label: 'Notifiable diseases',
      to: '/mrd/idsp',
      permission: 'mrd.idsp.report',
      keywords: ['idsp', 'ihip', 'surveillance', 'p form', 'l form', 'outbreak', 'notifiable', 'epidemic'],
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
