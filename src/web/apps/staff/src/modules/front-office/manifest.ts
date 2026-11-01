import { ConferenceRoomRegular, PeopleQueueRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { DepartmentQueues, FrontOfficeKpis } from './widgets';

const manifest: ModuleManifest = {
  id: 'front-office',
  title: 'Front office',
  section: 'patients',
  icon: ConferenceRoomRegular,
  order: 20,
  links: [
    {
      label: 'Overview',
      to: '/front-office',
      permission: 'frontoffice.dashboard',
      keywords: ['dashboard', 'reception'],
    },
    {
      label: "Today's visits",
      to: '/front-office/visits',
      permission: 'visits.read',
      keywords: ['op', 'check-in', 'visit list'],
    },
    {
      label: 'Patients in hospital',
      to: '/front-office/in-hospital',
      permission: 'visits.read',
      keywords: ['pipeline', 'journey', 'where is the patient', 'board', 'time in stage'],
    },
    {
      label: 'Queue',
      to: '/front-office/queue',
      permission: ['queue.manage', 'visits.read'],
      keywords: ['token', 'call next', 'waiting'],
    },
    {
      label: 'ABHA scan and share',
      to: '/front-office/abha-shares',
      permission: 'patients.register',
      feature: 'abdm',
      keywords: ['abdm', 'qr', 'token', 'abha registration'],
    },
    {
      label: 'Enquiries',
      to: '/front-office/enquiries',
      permission: 'frontoffice.enquiries',
      keywords: ['help desk', 'phone'],
    },
    {
      label: 'Visitor passes',
      to: '/front-office/visitors',
      permission: 'frontoffice.visitors',
      keywords: ['attendant', 'gate pass'],
    },
    {
      label: 'Queue display board',
      to: '/display/queue',
      permission: 'queue.display',
      keywords: ['tv', 'token screen'],
    },
  ],
  quickActions: [
    {
      label: 'Patient queue',
      to: '/front-office/queue',
      permission: 'queue.manage',
      icon: PeopleQueueRegular,
      description: 'Call the next token',
    },
  ],
  widgets: [
    { id: 'front-office-kpis', permission: 'frontoffice.dashboard', span: 4, order: 10, component: FrontOfficeKpis },
    { id: 'front-office-queues', permission: 'frontoffice.dashboard', span: 2, order: 20, component: DepartmentQueues },
  ],
};

export default manifest;
