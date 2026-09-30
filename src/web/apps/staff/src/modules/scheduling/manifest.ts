import { CalendarAddRegular, CalendarLtrRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { AppointmentsTodayWidget, DoctorLoadWidget } from './widgets';

const READ = 'scheduling.appointments.read';
const BOOK = 'scheduling.appointments.book';

const manifest: ModuleManifest = {
  id: 'scheduling',
  title: 'Appointments',
  section: 'patients',
  icon: CalendarLtrRegular,
  order: 15,
  links: [
    {
      label: "Today's appointments",
      to: '/scheduling',
      permission: READ,
      keywords: ['appointment', 'booking', 'check in', 'no-show', 'opd'],
    },
    {
      label: 'Book appointment',
      to: '/scheduling/book',
      permission: BOOK,
      keywords: ['new appointment', 'slot', 'doctor appointment', 'online booking'],
    },
    {
      label: 'Calendar',
      to: '/scheduling/calendar',
      permission: READ,
      keywords: ['day view', 'week view', 'utilisation'],
    },
    {
      label: 'Waitlist',
      to: '/scheduling/waitlist',
      permission: READ,
      keywords: ['waiting list', 'cancellation', 'free slot'],
    },
    {
      label: 'Doctor schedules',
      to: '/scheduling/schedules',
      permission: ['scheduling.schedules.manage', READ],
      keywords: ['opd timings', 'leave', 'holiday', 'block', 'extra session', 'roster'],
    },
  ],
  quickActions: [
    {
      label: 'Book appointment',
      to: '/scheduling/book',
      permission: BOOK,
      icon: CalendarAddRegular,
      description: 'Find a free slot with a doctor',
    },
  ],
  widgets: [
    { id: 'scheduling-today', permission: READ, span: 4, order: 12, component: AppointmentsTodayWidget },
    { id: 'scheduling-doctor-load', permission: READ, span: 2, order: 22, component: DoctorLoadWidget },
  ],
};

export default manifest;
