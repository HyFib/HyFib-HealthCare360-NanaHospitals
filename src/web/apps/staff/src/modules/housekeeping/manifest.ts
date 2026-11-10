import { BroomRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { HOUSEKEEPING_PERMISSIONS, HOUSEKEEPING_READERS } from './api';

const manifest: ModuleManifest = {
  id: 'housekeeping',
  title: 'Housekeeping',
  section: 'support',
  icon: BroomRegular,
  order: 50,
  links: [
    {
      label: 'Housekeeping',
      to: '/housekeeping',
      permission: [...HOUSEKEEPING_READERS],
      keywords: ['housekeeping', 'cleaning', 'spill', 'terminal cleaning', 'checklist', 'sanitation', 'kayakalp'],
    },
  ],
  quickActions: [
    {
      label: 'Ask for a cleaning',
      to: '/housekeeping',
      permission: HOUSEKEEPING_PERMISSIONS.request,
      icon: BroomRegular,
      description: 'A spill, a terminal cleaning or anything else on the ward',
    },
  ],
};

export default manifest;
