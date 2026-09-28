/** Feedback enums exactly as the Engagement server sends them, with plain words for the screens. */

export const FEEDBACK_PERMISSIONS = {
  read: 'feedback.read',
  manage: 'feedback.manage',
  complaintsManage: 'feedback.complaints.manage',
} as const;

const F = FEEDBACK_PERMISSIONS;

export const FORM_VIEWERS = [F.read, F.manage] as const;
/** May record feedback taken by phone or in person and send personal feedback links. */
export const FEEDBACK_CAPTURERS = [F.manage, 'crm.followups.manage'] as const;
export const FEEDBACK_USERS = [F.read, F.manage, F.complaintsManage] as const;

export const FORM_TYPES = ['OP', 'IP', 'Portal', 'General'] as const;
export type FeedbackFormType = (typeof FORM_TYPES)[number];
export const formTypeLabel: Record<FeedbackFormType, string> = {
  OP: 'Out-patients',
  IP: 'In-patients (after discharge)',
  Portal: 'Patient portal',
  General: 'General (QR posters, kiosks)',
};

export const ASPECTS = ['Doctor', 'Nursing', 'FrontDesk', 'Billing', 'Cleanliness', 'Food', 'Overall'] as const;
export type FeedbackAspect = (typeof ASPECTS)[number];
export const aspectLabel: Record<FeedbackAspect, string> = {
  Doctor: 'Doctor',
  Nursing: 'Nursing care',
  FrontDesk: 'Front desk and registration',
  Billing: 'Billing',
  Cleanliness: 'Cleanliness',
  Food: 'Food',
  Overall: 'Overall experience',
};
/** A complaint's area: complaints raised by low scores carry the aspect's code ("FrontDesk"), staff type free text. */
export function categoryLabel(category: string | null | undefined): string {
  if (!category) {
    return 'General';
  }
  return (ASPECTS as readonly string[]).includes(category) ? aspectLabel[category as FeedbackAspect] : category;
}

/** Question shown to the patient for each aspect. */
export const aspectQuestion: Record<FeedbackAspect, string> = {
  Doctor: 'How was your experience with the doctor?',
  Nursing: 'How was the nursing care?',
  FrontDesk: 'How was the front desk and registration?',
  Billing: 'How was the billing process?',
  Cleanliness: 'How clean was the hospital?',
  Food: 'How was the food?',
  Overall: 'Overall, how was your experience?',
};

/** Words for 1–5 stars, as the patient sees them. */
export const RATING_WORDS = ['Very poor', 'Poor', 'Average', 'Good', 'Excellent'] as const;

export function ratingWord(value: number | null | undefined): string {
  return value ? (RATING_WORDS[Math.round(value) - 1] ?? '') : '';
}

export const CHANNELS = ['Kiosk', 'QrCode', 'Portal', 'SmsLink', 'Staff'] as const;
export type FeedbackChannel = (typeof CHANNELS)[number];
export const channelLabel: Record<FeedbackChannel, string> = {
  Kiosk: 'Kiosk',
  QrCode: 'QR poster',
  Portal: 'Patient portal',
  SmsLink: 'SMS link',
  Staff: 'Taken by staff',
};

export const SENTIMENTS = ['Positive', 'Neutral', 'Negative'] as const;
export type Sentiment = (typeof SENTIMENTS)[number];

export const COMPLAINT_SOURCES = ['Feedback', 'WalkIn', 'Phone', 'Email', 'Portal'] as const;
export type ComplaintSource = (typeof COMPLAINT_SOURCES)[number];
export const complaintSourceLabel: Record<ComplaintSource, string> = {
  Feedback: 'Low feedback score',
  WalkIn: 'In person',
  Phone: 'Phone call',
  Email: 'Email',
  Portal: 'Patient portal',
};
/** Sources staff can choose when recording a complaint (feedback complaints are raised automatically). */
export const MANUAL_COMPLAINT_SOURCES = ['WalkIn', 'Phone', 'Email', 'Portal'] as const;

export const COMPLAINT_STATUSES = ['Open', 'InProgress', 'Resolved', 'Closed'] as const;
export type ComplaintStatus = (typeof COMPLAINT_STATUSES)[number];
export const complaintStatusLabel: Record<ComplaintStatus, string> = {
  Open: 'Open',
  InProgress: 'Being handled',
  Resolved: 'Resolved',
  Closed: 'Closed',
};

export const COMPLAINT_PRIORITIES = ['Normal', 'High'] as const;
export type ComplaintPriority = (typeof COMPLAINT_PRIORITIES)[number];
