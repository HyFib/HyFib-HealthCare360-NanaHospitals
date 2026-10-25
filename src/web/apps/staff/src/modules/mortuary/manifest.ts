import { BedRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { MORTUARY_READERS } from './api';

const manifest: ModuleManifest = {
  id: 'mortuary',
  title: 'Mortuary',
  section: 'support',
  icon: BedRegular,
  order: 45,
  links: [
    {
      label: 'Mortuary',
      to: '/mortuary',
      permission: [...MORTUARY_READERS],
      keywords: ['mortuary', 'morgue', 'body', 'cold storage', 'dead body', 'release', 'unclaimed', 'post-mortem'],
    },
  ],
};

export default manifest;
