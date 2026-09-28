import { CalendarAddRegular, HeartPulseRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { TodaysSurgeriesWidget } from './widgets';

const manifest: ModuleManifest = {
  id: 'ot',
  title: 'Operation theatre',
  section: 'clinical',
  icon: HeartPulseRegular,
  order: 40,
  links: [
    { label: 'OT calendar', to: '/ot', permission: 'ot.read', keywords: ['operation theatre', 'surgery', 'ot list', 'schedule', 'utilisation'] },
    { label: 'Book surgery', to: '/ot/book', permission: 'ot.bookings.manage', keywords: ['surgery booking', 'ot booking', 'theatre slot'] },
    { label: 'Surgeries', to: '/ot/bookings', permission: 'ot.read', keywords: ['who checklist', 'sign-in', 'time-out', 'operation note', 'anaesthesia'] },
    { label: 'Theatres', to: '/ot/theatres', permission: 'ot.read', keywords: ['operating room', 'equipment'] },
    { label: 'Implant trace', to: '/ot/implants', permission: 'ot.theatres.manage', keywords: ['recall', 'lot number', 'serial number', 'device'] },
  ],
  quickActions: [
    {
      label: 'Book surgery',
      to: '/ot/book',
      permission: 'ot.bookings.manage',
      icon: CalendarAddRegular,
      description: 'Theatre, team and time with clash check',
    },
  ],
  widgets: [{ id: 'ot-today', permission: 'ot.read', span: 2, order: 40, component: TodaysSurgeriesWidget }],
};

export default manifest;
