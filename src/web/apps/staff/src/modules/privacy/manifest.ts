import { PersonNoteRegular, ShieldLockRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { PRIVACY_PERMISSIONS as P } from './labels';
import { PrivacyOverviewWidget } from './widgets';

const manifest: ModuleManifest = {
  id: 'privacy',
  title: 'Privacy',
  section: 'admin',
  icon: ShieldLockRegular,
  order: 850,
  links: [
    {
      label: 'Privacy requests',
      to: '/privacy/requests',
      permission: P.requestsRead,
      keywords: [
        'dpdp',
        'data protection',
        'personal data',
        'grievance',
        'access',
        'data summary',
        'correction',
        'erasure',
        'delete my data',
        'right to be forgotten',
        'nominee',
        'data principal',
      ],
    },
    {
      // Front-desk staff hold only the "log" permission: this link keeps the module (and its quick action) visible to them.
      label: 'Log a privacy request',
      to: '/privacy/requests/new',
      permission: P.requestsLog,
      keywords: ['dpdp', 'grievance', 'correction', 'erasure', 'access', 'data request', 'nominee'],
    },
    {
      label: 'Data breaches',
      to: '/privacy/breaches',
      permission: P.breachesManage,
      keywords: ['dpdp', 'breach', 'data leak', 'data protection board', '72 hours', 'notice', 'incident'],
    },
    {
      label: 'Retention',
      to: '/privacy/retention',
      permission: P.retentionManage,
      keywords: ['dpdp', 'retention', 'clean-up', 'purge', 'delete old data', 'how long we keep', 'records'],
    },
    {
      label: 'Privacy settings',
      to: '/privacy/settings',
      permission: P.settingsManage,
      keywords: ['dpdp', 'grievance officer', 'data protection officer', 'dpo', 'response period', 'contact'],
    },
  ],
  quickActions: [
    {
      label: 'Log a privacy request',
      to: '/privacy/requests/new',
      permission: P.requestsLog,
      icon: PersonNoteRegular,
      description: 'Access, correction, erasure or a grievance about personal data',
    },
  ],
  widgets: [
    { id: 'privacy-overview', permission: P.requestsRead, span: 1, order: 88, component: PrivacyOverviewWidget },
  ],
};

export default manifest;
