import { ShirtRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { LINEN_READERS } from './api';

const manifest: ModuleManifest = {
  id: 'linen',
  title: 'Linen and laundry',
  section: 'support',
  icon: ShirtRegular,
  order: 55,
  links: [
    {
      label: 'Linen and laundry',
      to: '/linen',
      permission: [...LINEN_READERS],
      keywords: ['linen', 'laundry', 'bed sheet', 'soiled', 'wash', 'par level', 'condemn', 'infected linen'],
    },
  ],
};

export default manifest;
