import { FilterRegular, GridKanbanRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { DialysisTodayWidget } from './widgets';

const manifest: ModuleManifest = {
  id: 'dialysis',
  title: 'Dialysis',
  section: 'clinical',
  icon: FilterRegular,
  order: 45,
  links: [
    {
      label: 'Dialysis board',
      to: '/dialysis',
      permission: 'dialysis.read',
      keywords: ['haemodialysis', 'hd', 'machines', 'shift', 'sero'],
    },
    {
      label: 'Sessions',
      to: '/dialysis/sessions',
      permission: 'dialysis.read',
      keywords: ['dialysis session', 'uf', 'readings'],
    },
    {
      label: 'Dialysis patients',
      to: '/dialysis/patients',
      permission: 'dialysis.read',
      keywords: ['enrol', 'dry weight', 'dialyzer', 'reuse', 'fistula'],
    },
    {
      label: 'Machines',
      to: '/dialysis/machines',
      permission: 'dialysis.read',
      keywords: ['disinfection', 'station', 'hbsag', 'hcv', 'hiv'],
    },
  ],
  quickActions: [
    {
      label: 'Dialysis board',
      to: '/dialysis',
      permission: 'dialysis.sessions.manage',
      icon: GridKanbanRegular,
      description: 'Today’s machines, shifts and sessions',
    },
  ],
  widgets: [
    {
      id: 'dialysis-today',
      permission: 'dialysis.sessions.manage',
      span: 1,
      order: 45,
      component: DialysisTodayWidget,
    },
  ],
};

export default manifest;
