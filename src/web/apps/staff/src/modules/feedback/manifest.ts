import { ChatMultipleRegular, PersonFeedbackRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { FEEDBACK_PERMISSIONS as F, FORM_VIEWERS } from './labels';
import { FeedbackNpsWidget } from './widgets';

const manifest: ModuleManifest = {
  id: 'feedback',
  title: 'Patient feedback',
  section: 'engagement',
  icon: PersonFeedbackRegular,
  order: 20,
  links: [
    { label: 'Feedback analytics', to: '/feedback', permission: F.read, keywords: ['nps', 'net promoter', 'satisfaction', 'sentiment', 'ratings'] },
    { label: 'Feedback responses', to: '/feedback/responses', permission: F.read, keywords: ['feedback', 'survey', 'comments', 'low score'] },
    { label: 'Complaints', to: '/feedback/complaints', permission: F.complaintsManage, keywords: ['complaint', 'grievance', 'sla', 'tat'] },
    { label: 'Feedback forms and QR codes', to: '/feedback/forms', permission: FORM_VIEWERS, keywords: ['qr', 'kiosk', 'poster', 'questionnaire'] },
  ],
  quickActions: [
    {
      label: 'Record a complaint',
      to: '/feedback/complaints/new',
      permission: F.complaintsManage,
      icon: ChatMultipleRegular,
      description: 'Walk-in, phone or e-mail complaint, with its resolution deadline',
    },
  ],
  widgets: [{ id: 'feedback-nps-month', permission: F.read, span: 1, order: 85, component: FeedbackNpsWidget }],
};

export default manifest;
