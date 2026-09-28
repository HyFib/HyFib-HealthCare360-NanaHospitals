import { ClipboardTaskListLtrRegular, PersonWalkingRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { PhysioTodayWidget } from './widgets';

const manifest: ModuleManifest = {
  id: 'physio',
  title: 'Physiotherapy',
  section: 'clinical',
  icon: PersonWalkingRegular,
  order: 50,
  links: [
    { label: 'Physiotherapy worklist', to: '/physio', permission: 'physio.read', keywords: ['physio', 'sessions today', 'rehab'] },
    { label: 'Physiotherapy referrals', to: '/physio/referrals', permission: 'physio.read', keywords: ['physio', 'assessment', 'treatment plan', 'ift', 'tens'] },
  ],
  quickActions: [
    {
      label: 'Physiotherapy worklist',
      to: '/physio',
      permission: 'physio.treatment.manage',
      icon: ClipboardTaskListLtrRegular,
      description: 'Today’s sessions to record',
    },
  ],
  widgets: [{ id: 'physio-today', permission: 'physio.treatment.manage', span: 1, order: 50, component: PhysioTodayWidget }],
};

export default manifest;
