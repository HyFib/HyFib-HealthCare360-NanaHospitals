/**
 * Privacy (DPDP Act 2023) enums exactly as the server sends them, with the plain words the screens use. The server
 * stays the authority for every rule; these labels only describe.
 */

export { consentTypeLabel } from '@/modules/patients/consents';

export const PRIVACY_PERMISSIONS = {
  /** View requests, the data summary and the erasure assessment. */
  requestsRead: 'privacy.requests.read',
  /** Start, check identity, assign, note, decide corrections, erase and answer. */
  requestsManage: 'privacy.requests.manage',
  /** Record a request at the desk (or from e-mail, letter or phone). */
  requestsLog: 'privacy.requests.log',
  breachesManage: 'privacy.breaches.manage',
  retentionManage: 'privacy.retention.manage',
  settingsManage: 'privacy.settings.manage',
} as const;

// ------------------------------------------------------------------ requests

export const REQUEST_TYPES = ['Access', 'Correction', 'Erasure', 'Grievance'] as const;
export type DataRequestType = (typeof REQUEST_TYPES)[number];
/** The words the patient's SMS and e-mail use too. */
export const requestTypeLabel: Record<DataRequestType, string> = {
  Access: 'Access',
  Correction: 'Correction',
  Erasure: 'Erasure',
  Grievance: 'Grievance',
};
export const requestTypeHelp: Record<DataRequestType, string> = {
  Access: 'A summary of the personal data we hold about them, why we hold it and who we shared it with.',
  Correction: 'Correct, complete or update their details, such as a wrong name, date of birth or address.',
  Erasure: 'Delete personal data we no longer need. Medical records and bills are kept as the law requires.',
  Grievance: 'A complaint about how the hospital has handled their personal data.',
};

export const REQUEST_STATUSES = [
  'Submitted',
  'InProgress',
  'Completed',
  'PartlyCompleted',
  'Declined',
  'Withdrawn',
] as const;
export type DataRequestStatus = (typeof REQUEST_STATUSES)[number];
export const requestStatusLabel: Record<DataRequestStatus, string> = {
  Submitted: 'New',
  InProgress: 'In progress',
  Completed: 'Completed',
  PartlyCompleted: 'Partly completed',
  Declined: 'Declined',
  Withdrawn: 'Withdrawn',
};

/** The ways a request is closed by staff. */
export const REQUEST_OUTCOMES = ['Completed', 'PartlyCompleted', 'Declined'] as const;
export type RequestOutcome = (typeof REQUEST_OUTCOMES)[number];
export const requestOutcomeHelp: Record<RequestOutcome, string> = {
  Completed: 'Everything they asked for was done.',
  PartlyCompleted: 'Some of it was done, for example when the law requires records to be kept.',
  Declined: 'Nothing was done. Say why in the reply, and on what grounds.',
};

export const REQUEST_CHANNELS = ['Portal', 'FrontDesk', 'Email', 'Letter', 'Phone'] as const;
export type DataRequestChannel = (typeof REQUEST_CHANNELS)[number];
export const requestChannelLabel: Record<DataRequestChannel, string> = {
  Portal: 'Patient portal',
  FrontDesk: 'At the front desk',
  Email: 'E-mail',
  Letter: 'Letter',
  Phone: 'Phone',
};
/** Channels staff can record (portal requests come from the portal itself). */
export const STAFF_CHANNELS = ['FrontDesk', 'Email', 'Letter', 'Phone'] as const;
export type StaffChannel = (typeof STAFF_CHANNELS)[number];

export const REQUESTER_RELATIONS = [
  'Self',
  'ParentOrGuardian',
  'Nominee',
  'FamilyMember',
  'AuthorisedRepresentative',
] as const;
export type RequesterRelation = (typeof REQUESTER_RELATIONS)[number];
export const requesterRelationLabel: Record<RequesterRelation, string> = {
  Self: 'The patient',
  ParentOrGuardian: 'Parent or guardian',
  Nominee: 'Nominee',
  FamilyMember: 'Family member',
  AuthorisedRepresentative: 'Authorised representative',
};
export const requesterRelationHelp: Record<RequesterRelation, string> = {
  Self: 'The person the data is about.',
  ParentOrGuardian: 'For a child, or for someone who cannot act for themselves.',
  Nominee: 'Named by the patient to act for them after their death or if they cannot act.',
  FamilyMember: 'A relative asking on the patient’s behalf.',
  AuthorisedRepresentative: 'Someone the patient authorised in writing, such as a lawyer.',
};

export const IDENTITY_CHECKS = ['PortalSignIn', 'PhotoId', 'KnownToStaff', 'Other'] as const;
export type IdentityCheck = (typeof IDENTITY_CHECKS)[number];
export const identityCheckLabel: Record<IdentityCheck, string> = {
  PortalSignIn: 'Signed in to the patient portal with a code sent to the registered mobile',
  PhotoId: 'Photo ID seen',
  KnownToStaff: 'Known to staff',
  Other: 'Other',
};
/** How staff can check identity (portal sign-in is recorded by the portal only). */
export const STAFF_IDENTITY_CHECKS = ['PhotoId', 'KnownToStaff', 'Other'] as const;
export type StaffIdentityCheck = (typeof STAFF_IDENTITY_CHECKS)[number];
export const staffIdentityCheckHelp: Record<StaffIdentityCheck, string> = {
  PhotoId: 'Aadhaar, PAN, passport, driving licence or voter ID, with proof of authority when acting for the patient.',
  KnownToStaff: 'A member of staff knows them, and knows they may act for the patient.',
  Other: 'Say how in the note.',
};

export const CORRECTION_DECISIONS = ['Pending', 'Accepted', 'Declined'] as const;
export type CorrectionDecision = (typeof CORRECTION_DECISIONS)[number];
export const correctionDecisionLabel: Record<CorrectionDecision, string> = {
  Pending: 'Not decided',
  Accepted: 'Accepted',
  Declined: 'Declined',
};

/** The details a patient can ask to have corrected (server CorrectableFields). */
export const CORRECTABLE_FIELDS = [
  'name',
  'dateOfBirth',
  'gender',
  'mobile',
  'alternatePhone',
  'email',
  'address',
  'emergencyContact',
  'guardian',
  'language',
  'healthRecord',
  'other',
] as const;
export type CorrectableField = (typeof CORRECTABLE_FIELDS)[number];
export const correctableFieldLabel: Record<CorrectableField, string> = {
  name: 'Name',
  dateOfBirth: 'Date of birth',
  gender: 'Gender',
  mobile: 'Mobile number',
  alternatePhone: 'Other phone number',
  email: 'E-mail address',
  address: 'Address',
  emergencyContact: 'Emergency contact',
  guardian: 'Guardian',
  language: 'Preferred language',
  healthRecord: 'Something in the health record',
  other: 'Something else',
};
/** At most this many changes in one correction request. */
export const MAX_CORRECTIONS = 12;

export const EVENT_KINDS = [
  'Received',
  'IdentityVerified',
  'Started',
  'Assigned',
  'Note',
  'CorrectionsDecided',
  'Erased',
  'Closed',
  'Withdrawn',
] as const;
export type DataRequestEventKind = (typeof EVENT_KINDS)[number];
export const eventKindLabel: Record<DataRequestEventKind, string> = {
  Received: 'Received',
  IdentityVerified: 'Identity checked',
  Started: 'Started',
  Assigned: 'Owner changed',
  Note: 'Staff note',
  CorrectionsDecided: 'Changes decided',
  Erased: 'Data erased',
  Closed: 'Answered',
  Withdrawn: 'Withdrawn',
};

export const ERASURE_ITEMS = ['messages', 'feedback', 'nominee', 'portal'] as const;
export type ErasureItem = (typeof ERASURE_ITEMS)[number];
export const erasureItemLabel: Record<ErasureItem, string> = {
  messages: 'Stop optional messages',
  feedback: 'Remove the patient from feedback, complaints and enquiries',
  nominee: 'Remove the nominee',
  portal: 'Close the portal account',
};
/** What was done, for the request's record ("Erased: …"). */
export const erasureItemDoneLabel: Record<ErasureItem, string> = {
  messages: 'Optional messages stopped',
  feedback: 'Removed from feedback, complaints and enquiries',
  nominee: 'Nominee removed',
  portal: 'Portal account closed',
};

export function erasureDoneLabel(item: string): string {
  return (ERASURE_ITEMS as readonly string[]).includes(item) ? erasureItemDoneLabel[item as ErasureItem] : item;
}

export const genderLabel: Record<string, string> = {
  Male: 'Male',
  Female: 'Female',
  Other: 'Other',
  Unknown: 'Not recorded',
};

// ------------------------------------------------------------------ breaches

export const BREACH_STATUSES = ['Open', 'BoardInformed', 'ReportSubmitted', 'Closed'] as const;
export type BreachStatus = (typeof BREACH_STATUSES)[number];
export const breachStatusLabel: Record<BreachStatus, string> = {
  Open: 'Board not told yet',
  BoardInformed: 'Board told',
  ReportSubmitted: 'Report sent',
  Closed: 'Closed',
};

export const BREACH_NATURES = ['Confidentiality', 'Integrity', 'Availability'] as const;
export type BreachNature = (typeof BREACH_NATURES)[number];
export const breachNatureLabel: Record<BreachNature, string> = {
  Confidentiality: 'Seen, taken or shared by someone who should not have it',
  Integrity: 'Changed or corrupted',
  Availability: 'Lost, or could not be reached',
};
/** The legal term, for the Board's report. */
export const breachNatureTerm: Record<BreachNature, string> = {
  Confidentiality: 'Confidentiality',
  Integrity: 'Integrity',
  Availability: 'Availability',
};

export const BREACH_DATA_CATEGORIES = [
  'Identity',
  'Contact',
  'Health',
  'Financial',
  'GovernmentId',
  'Credentials',
  'Children',
] as const;
export type BreachDataCategory = (typeof BREACH_DATA_CATEGORIES)[number];
export const breachDataCategoryLabel: Record<BreachDataCategory, string> = {
  Identity: 'Names and identity details',
  Contact: 'Contact details',
  Health: 'Health information',
  Financial: 'Bills and payment details',
  GovernmentId: 'Government ID numbers (such as Aadhaar or ABHA)',
  Credentials: 'Sign-in details',
  Children: 'Children’s data',
};

/** The short notice goes by SMS; the server keeps it to this many characters. */
export const NOTICE_MAX_LENGTH = 320;

// ------------------------------------------------------------------ retention

export const RETENTION_ACTIONS = ['Delete', 'Review', 'Keep'] as const;
export type RetentionAction = (typeof RETENTION_ACTIONS)[number];
export const retentionActionLabel: Record<RetentionAction, string> = {
  Delete: 'Deleted automatically',
  Review: 'Reviewed by medical records',
  Keep: 'Kept',
};

export const RETENTION_UNITS = ['Days', 'Years'] as const;
export type RetentionUnit = (typeof RETENTION_UNITS)[number];

export const RETENTION_TRIGGERS = ['Scheduled', 'Manual'] as const;
export type RetentionRunTrigger = (typeof RETENTION_TRIGGERS)[number];

/** Retention keys whose change moves the retention date of completed charts (Clinical handles it). */
export const MEDICAL_RECORD_RULES = ['records.outpatient', 'records.inpatient'] as const;

/** Short names for the per-kind results of a clean-up run. */
export const retentionKeyLabel: Record<string, string> = {
  'messages.outbox': 'Messages being sent',
  'messages.log': 'Message log',
  'integrations.log': 'Integration log',
  'signin.log': 'Sign-in records',
  'records.outpatient': 'Out-patient medical records',
  'records.inpatient': 'In-patient medical records',
  'records.mlc': 'Medico-legal records',
  finance: 'Bills, payments and insurance claims',
  audit: 'Audit trail',
  backups: 'Database backups',
};

export function retentionKeyName(key: string): string {
  return retentionKeyLabel[key] ?? key;
}

// ------------------------------------------------------------------ settings

export const DEFAULT_OFFICER_TITLE = 'Grievance Officer';
/** The DPDP Rules 2025 allow a published response period of at most 90 days. */
export const MAX_RESPONSE_DAYS = 90;
