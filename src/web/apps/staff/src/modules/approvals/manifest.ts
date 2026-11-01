import { PeopleCheckmarkRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { APPROVAL_PERMISSIONS } from './api';

const manifest: ModuleManifest = {
  id: 'approvals',
  title: 'Approvals',
  section: 'admin',
  icon: PeopleCheckmarkRegular,
  order: 5,
  links: [
    {
      label: 'Approvals',
      to: '/approvals',
      permission: APPROVAL_PERMISSIONS,
      keywords: ['maker checker', 'four eyes', 'second approval', 'credit note', 'write-off', 'merge'],
    },
  ],
};

export default manifest;
