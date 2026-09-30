import { CalendarAddRegular, ClipboardHeartRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { TodaysCheckupsWidget } from './widgets';

const manifest: ModuleManifest = {
  id: 'health-checkups',
  title: 'Health check-ups',
  section: 'patients',
  icon: ClipboardHeartRegular,
  order: 40,
  links: [
    {
      label: "Today's check-ups",
      to: '/health-checkups',
      permission: ['healthcheck.manage', 'clinical.emr.read'],
      keywords: ['mhc', 'master health checkup', 'executive health check', 'itinerary', 'stations'],
    },
    {
      label: 'Book check-up',
      to: '/health-checkups/book',
      permission: 'healthcheck.manage',
      keywords: ['package', 'booking', 'health package'],
    },
    {
      label: 'Packages',
      to: '/health-checkups/packages',
      permission: 'healthcheck.manage',
      keywords: ['health package', 'price', 'tests'],
    },
    {
      label: 'Corporate batches',
      to: '/health-checkups/corporate',
      permission: 'healthcheck.manage',
      keywords: ['company', 'employees', 'camp', 'bulk booking'],
    },
  ],
  quickActions: [
    {
      label: 'Book health check-up',
      to: '/health-checkups/book',
      permission: 'healthcheck.manage',
      icon: CalendarAddRegular,
      description: 'Package, date and the day’s itinerary',
    },
  ],
  widgets: [
    {
      id: 'health-checkups-today',
      permission: 'healthcheck.manage',
      span: 2,
      order: 35,
      component: TodaysCheckupsWidget,
    },
  ],
};

export default manifest;
