/** CRM enums exactly as the Engagement server sends them, with plain words for the screens. */

export const CRM_PERMISSIONS = {
  leadsManage: 'crm.leads.manage',
  followUpsManage: 'crm.followups.manage',
  campaignsManage: 'crm.campaigns.manage',
  campaignsSend: 'crm.campaigns.send',
  referralsManage: 'crm.referrals.manage',
  patient360Read: 'crm.patient360.read',
} as const;

const C = CRM_PERMISSIONS;

export const CAMPAIGN_VIEWERS = [C.campaignsManage, C.campaignsSend] as const;
/** Anyone who uses the CRM workspace (the /crm overview). */
export const CRM_USERS = [
  C.leadsManage,
  C.followUpsManage,
  C.campaignsManage,
  C.campaignsSend,
  C.referralsManage,
  C.patient360Read,
] as const;
/** May record a marketing opt-out for a patient. */
export const OPT_OUT_RECORDERS = [
  C.campaignsManage,
  C.followUpsManage,
  C.leadsManage,
  C.patient360Read,
  'patients.consents',
] as const;

export const LEAD_SOURCES = [
  'Website',
  'WalkIn',
  'Phone',
  'HealthCamp',
  'Referral',
  'SocialMedia',
  'Campaign',
  'Corporate',
] as const;
export type LeadSource = (typeof LEAD_SOURCES)[number];
export const leadSourceLabel: Record<LeadSource, string> = {
  Website: 'Website',
  WalkIn: 'Walk-in',
  Phone: 'Phone call',
  HealthCamp: 'Health camp',
  Referral: 'Doctor referral',
  SocialMedia: 'Social media',
  Campaign: 'Campaign',
  Corporate: 'Corporate',
};

export const LEAD_STATUSES = ['New', 'Contacted', 'Qualified', 'Converted', 'Lost'] as const;
export type LeadStatus = (typeof LEAD_STATUSES)[number];
export const leadStatusLabel: Record<LeadStatus, string> = {
  New: 'New',
  Contacted: 'Contacted',
  Qualified: 'Interested',
  Converted: 'Converted',
  Lost: 'Lost',
};

export const LEAD_ACTIVITY_TYPES = ['Call', 'Sms', 'WhatsApp', 'Email', 'Visit', 'Note'] as const;
export type LeadActivityType = (typeof LEAD_ACTIVITY_TYPES)[number];
export const leadActivityLabel: Record<LeadActivityType, string> = {
  Call: 'Phone call',
  Sms: 'SMS',
  WhatsApp: 'WhatsApp',
  Email: 'Email',
  Visit: 'Visit',
  Note: 'Note',
};

export const FOLLOW_UP_TYPES = [
  'PostConsultation',
  'PostDischarge',
  'Vaccination',
  'ChronicReview',
  'HealthCheckupDue',
  'PaymentDue',
  'Feedback',
] as const;
export type FollowUpType = (typeof FOLLOW_UP_TYPES)[number];
export const followUpTypeLabel: Record<FollowUpType, string> = {
  PostConsultation: 'After consultation',
  PostDischarge: 'After discharge',
  Vaccination: 'Vaccination due',
  ChronicReview: 'Chronic care review',
  HealthCheckupDue: 'Health check-up due',
  PaymentDue: 'Payment due',
  Feedback: 'Feedback call',
};

export const FOLLOW_UP_STATUSES = ['Open', 'InProgress', 'Completed', 'Cancelled'] as const;
export type FollowUpStatus = (typeof FOLLOW_UP_STATUSES)[number];
export const followUpStatusLabel: Record<FollowUpStatus, string> = {
  Open: 'To call',
  InProgress: 'Calling again',
  Completed: 'Done',
  Cancelled: 'Cancelled',
};

export const FOLLOW_UP_PRIORITIES = ['Normal', 'High'] as const;
export type FollowUpPriority = (typeof FOLLOW_UP_PRIORITIES)[number];

export const FOLLOW_UP_OUTCOMES = [
  'Reached',
  'AppointmentBooked',
  'NotInterested',
  'WrongNumber',
  'Deceased',
  'NotReachable',
  'CallBackLater',
  'Other',
] as const;
export type FollowUpOutcome = (typeof FOLLOW_UP_OUTCOMES)[number];
/** What a task records: a call outcome, or "Visited" when the patient came back and no call was needed. */
export type RecordedFollowUpOutcome = FollowUpOutcome | 'Visited';
export const followUpOutcomeLabel: Record<RecordedFollowUpOutcome, string> = {
  Reached: 'Spoke to the patient',
  AppointmentBooked: 'Appointment booked',
  NotInterested: 'Not interested',
  WrongNumber: 'Wrong number',
  Deceased: 'Patient has passed away',
  NotReachable: 'Not reachable',
  CallBackLater: 'Asked to call back later',
  Other: 'Other',
  Visited: 'Came back, no call needed',
};

export type FollowUpSource = 'Manual' | 'Discharge' | 'System';
export const followUpSourceLabel: Record<FollowUpSource, string> = {
  Manual: 'By staff',
  Discharge: 'Automatically, after discharge',
  System: 'Automatically',
};

export const CAMPAIGN_CHANNELS = ['Sms', 'WhatsApp'] as const;
export type CampaignChannel = (typeof CAMPAIGN_CHANNELS)[number];
export const campaignChannelLabel: Record<CampaignChannel, string> = { Sms: 'SMS', WhatsApp: 'WhatsApp' };

export const CAMPAIGN_STATUSES = ['Draft', 'Scheduled', 'Sending', 'Completed', 'Cancelled'] as const;
export type CampaignStatus = (typeof CAMPAIGN_STATUSES)[number];

/** Template every campaign uses (seeded by the server; it must contain the {{optout}} link). */
export const CAMPAIGN_TEMPLATE_KEY = 'campaign.message';
