import { CallRegular, PeopleCommunityRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';
import { CAMPAIGN_VIEWERS, CRM_PERMISSIONS as C, CRM_USERS } from './labels';

const manifest: ModuleManifest = {
  id: 'crm',
  title: 'Patient relations',
  section: 'engagement',
  icon: PeopleCommunityRegular,
  order: 10,
  links: [
    { label: 'CRM overview', to: '/crm', permission: CRM_USERS, keywords: ['crm', 'dashboard'] },
    {
      label: 'Leads',
      to: '/crm/leads',
      permission: C.leadsManage,
      keywords: ['enquiry', 'prospect', 'pipeline', 'health package'],
    },
    {
      label: 'Follow-up calls',
      to: '/crm/follow-ups',
      permission: C.followUpsManage,
      keywords: ['call list', 'worklist', 'reminder', 'post discharge'],
    },
    {
      label: 'Referring doctors',
      to: '/crm/referrals',
      permission: C.referralsManage,
      keywords: ['referral', 'doctor', 'referred by'],
    },
    {
      label: 'Campaigns',
      to: '/crm/campaigns',
      permission: CAMPAIGN_VIEWERS,
      keywords: ['sms', 'whatsapp', 'marketing', 'audience'],
    },
    {
      label: 'Patient 360',
      to: '/crm/patients',
      permission: C.patient360Read,
      keywords: ['360', 'patient view', 'engagement', 'lifetime'],
    },
  ],
  quickActions: [
    {
      label: "Today's follow-up calls",
      to: '/crm/follow-ups',
      permission: C.followUpsManage,
      icon: CallRegular,
      description: 'Call patients who are due, record the outcome',
    },
  ],
};

export default manifest;
