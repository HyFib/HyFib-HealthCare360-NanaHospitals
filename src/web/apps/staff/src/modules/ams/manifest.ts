import { PillRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { AMS_READERS } from './api';

const manifest: ModuleManifest = {
  id: 'ams',
  title: 'Antimicrobial stewardship',
  section: 'clinical',
  icon: PillRegular,
  order: 45,
  links: [
    {
      label: 'Antibiotic approvals',
      to: '/ams',
      permission: [...AMS_READERS],
      keywords: [
        'ams',
        'antibiotic',
        'restricted antibiotic',
        'aware',
        'reserve',
        'carbapenem',
        'culture',
        'de-escalation',
        'stewardship',
      ],
    },
  ],
};

export default manifest;
