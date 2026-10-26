import { DeleteRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { BMW_PERMISSIONS, BMW_READERS } from './api';

const manifest: ModuleManifest = {
  id: 'bmw',
  title: 'Bio-medical waste',
  section: 'support',
  icon: DeleteRegular,
  order: 40,
  links: [
    {
      label: 'Bio-medical waste',
      to: '/bmw',
      permission: [...BMW_READERS],
      keywords: ['bmw', 'waste', 'cbwtf', 'yellow bag', 'sharps', 'form iv', 'pollution control board', 'barcode'],
    },
  ],
  quickActions: [
    {
      label: 'Weigh a waste bag',
      to: '/bmw',
      permission: BMW_PERMISSIONS.record,
      icon: DeleteRegular,
      description: 'Bar-code and weigh a bag of bio-medical waste',
    },
  ],
};

export default manifest;
