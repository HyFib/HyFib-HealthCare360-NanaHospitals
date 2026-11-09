import { ShieldCheckmarkRegular, ShieldErrorRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { QUALITY_MANAGERS, QUALITY_PERMISSIONS as Q } from './labels';
import { OpenIncidentsWidget } from './widgets';

const manifest: ModuleManifest = {
  id: 'quality',
  title: 'Quality and safety',
  section: 'engagement',
  icon: ShieldCheckmarkRegular,
  order: 30,
  links: [
    {
      label: 'Report an incident',
      to: '/quality/report',
      permission: Q.incidentsReport,
      keywords: ['incident', 'adverse event', 'near miss', 'fall', 'medication error', 'anonymous'],
    },
    {
      label: 'My reports and actions',
      to: '/quality/my-reports',
      permission: Q.incidentsReport,
      keywords: ['my incidents', 'my capa'],
    },
    {
      label: 'Quality dashboard',
      to: '/quality',
      permission: QUALITY_MANAGERS,
      keywords: ['nabh', 'irms', 'patient safety'],
    },
    {
      label: 'Incidents',
      to: '/quality/incidents',
      permission: Q.incidentsManage,
      keywords: ['sentinel', 'rca', 'investigation', 'root cause'],
    },
    {
      label: 'Device adverse events (MvPI)',
      to: '/quality/mvpi',
      permission: Q.incidentsManage,
      keywords: ['mvpi', 'materiovigilance', 'medical device', 'mdae', 'implant', 'equipment failure', 'ipc'],
    },
    {
      label: 'Corrective actions (CAPA)',
      to: '/quality/capas',
      permission: Q.capaManage,
      keywords: ['capa', 'corrective', 'preventive', 'effectiveness'],
    },
    {
      label: 'Risk register',
      to: '/quality/risks',
      permission: Q.risksManage,
      keywords: ['risk', 'heat map', 'likelihood', 'impact'],
    },
    {
      label: 'Quality indicators',
      to: '/quality/indicators',
      permission: Q.indicatorsManage,
      keywords: ['kpi', 'nabh indicators', 'scorecard', 'trend'],
    },
    {
      label: 'Infection surveillance (HAI)',
      to: '/quality/hai',
      permission: [Q.haiManage, Q.haiDevices],
      keywords: [
        'hai',
        'clabsi',
        'cauti',
        'vap',
        'ssi',
        'infection control',
        'central line',
        'catheter',
        'ventilator',
        'device days',
        'nhsn',
      ],
    },
    {
      label: 'Internal audits',
      to: '/quality/audits',
      permission: Q.incidentsReport,
      keywords: ['audit', 'nabh', 'checklist', 'findings', 'aac', 'cop', 'mom', 'hic', 'fms', 'self-assessment'],
    },
    {
      label: 'Downtime',
      to: '/quality/downtime',
      permission: 'quality.downtime.manage',
      keywords: ['downtime', 'outage', 'business continuity', 'drill', 'back-entry', 'paper'],
    },
  ],
  quickActions: [
    {
      label: 'Report an incident',
      to: '/quality/report',
      permission: Q.incidentsReport,
      icon: ShieldErrorRegular,
      description: 'Near miss, fall, medication error… you can stay anonymous',
    },
  ],
  widgets: [
    { id: 'quality-open-incidents', permission: Q.incidentsManage, span: 1, order: 80, component: OpenIncidentsWidget },
  ],
};

export default manifest;
