import { VideoPersonRegular, VideoRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { RpmAlertsWidget, TelehealthTodayWidget } from './widgets';

const manifest: ModuleManifest = {
  id: 'virtualcare',
  title: 'Virtual care',
  section: 'virtual',
  icon: VideoPersonRegular,
  order: 10,
  links: [
    {
      label: 'Video consultations',
      to: '/telehealth',
      permission: 'telehealth.sessions.read',
      keywords: ['telehealth', 'teleconsultation', 'video call', 'online consultation', 'waiting room', 'join link'],
    },
    {
      label: 'Remote monitoring',
      to: '/rpm',
      permission: 'rpm.read',
      keywords: ['rpm', 'home monitoring', 'bp', 'glucose', 'spo2', 'readings'],
    },
    {
      label: 'Monitoring alerts',
      to: '/rpm/alerts',
      permission: 'rpm.read',
      keywords: ['rpm alerts', 'critical reading'],
    },
    {
      label: 'Monitoring devices',
      to: '/rpm/devices',
      permission: 'rpm.read',
      keywords: ['device key', 'bp monitor', 'glucometer', 'oximeter'],
    },
    {
      label: 'Monitoring programmes',
      to: '/rpm/programs',
      permission: 'rpm.read',
      keywords: ['hypertension', 'diabetes', 'cardiac', 'post discharge'],
    },
  ],
  quickActions: [
    {
      label: 'Video consultations',
      to: '/telehealth',
      permission: 'telehealth.consult',
      icon: VideoRegular,
      description: 'See who is waiting and join',
    },
  ],
  widgets: [
    {
      id: 'telehealth-today',
      permission: 'telehealth.sessions.read',
      span: 2,
      order: 40,
      component: TelehealthTodayWidget,
    },
    { id: 'rpm-alerts', permission: 'rpm.read', span: 2, order: 42, component: RpmAlertsWidget },
  ],
};

export default manifest;
