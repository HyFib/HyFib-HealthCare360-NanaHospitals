/**
 * Plain-language labels for lab enums (sentence case, what a phlebotomist or technician says) and the vacutainer cap
 * colours used in Indian labs, so tubes on screen look like the tubes in the rack. Pure data: safe to use in tests and
 * print templates.
 */
import type { OrderPriority, PatientClass } from './common/types';
import type {
  ContainerType,
  LabOrderItemStatus,
  LabOrderSource,
  LabReportStatus,
  LabResultType,
  LabSection,
  QcOutcome,
  SampleRejectionReason,
  SampleStatus,
  SpecimenType,
} from './api';

export const sectionLabel: Record<LabSection, string> = {
  Hematology: 'Haematology',
  Biochemistry: 'Biochemistry',
  Microbiology: 'Microbiology',
  Serology: 'Serology',
  ClinicalPathology: 'Clinical pathology',
  Histopathology: 'Histopathology',
  Immunology: 'Immunology',
};

/** Short bench code printed on the tube label. */
export const sectionShort: Record<LabSection, string> = {
  Hematology: 'HAEM',
  Biochemistry: 'BIO',
  Microbiology: 'MICRO',
  Serology: 'SERO',
  ClinicalPathology: 'CP',
  Histopathology: 'HISTO',
  Immunology: 'IMMUNO',
};

export const specimenLabel: Record<SpecimenType, string> = {
  WholeBlood: 'Whole blood',
  Serum: 'Serum',
  Plasma: 'Plasma',
  Urine: 'Urine',
  Stool: 'Stool',
  Swab: 'Swab',
  Sputum: 'Sputum',
  Csf: 'CSF',
  BodyFluid: 'Body fluid',
  Tissue: 'Tissue',
  Other: 'Other',
};

export interface ContainerInfo {
  label: string;
  /** Short name on the label, e.g. "EDTA". */
  short: string;
  /** Cap colour (CSS colour) and its name, so colour is never the only signal. */
  cap: string;
  capName: string;
}

export const containerInfo: Record<ContainerType, ContainerInfo> = {
  Edta: { label: 'EDTA tube', short: 'EDTA', cap: '#8E6BBF', capName: 'lavender cap' },
  Plain: { label: 'Plain / clot activator tube', short: 'PLAIN', cap: '#D13438', capName: 'red cap' },
  Fluoride: { label: 'Fluoride tube', short: 'FLUORIDE', cap: '#8A8886', capName: 'grey cap' },
  Citrate: { label: 'Citrate tube', short: 'CITRATE', cap: '#6CB4E4', capName: 'light-blue cap' },
  Heparin: { label: 'Heparin tube', short: 'HEPARIN', cap: '#2E8B57', capName: 'green cap' },
  UrineContainer: { label: 'Urine container', short: 'URINE', cap: '#E0B400', capName: 'yellow lid' },
  Swab: { label: 'Swab', short: 'SWAB', cap: '#F7F7F7', capName: 'white cap' },
  StoolContainer: { label: 'Stool container', short: 'STOOL', cap: '#8B5A2B', capName: 'brown lid' },
  SterileContainer: { label: 'Sterile container', short: 'STERILE', cap: '#FFFFFF', capName: 'sterile, white lid' },
};

export const priorityLabel: Record<OrderPriority, string> = { Stat: 'STAT', Urgent: 'Urgent', Routine: 'Routine' };

export const patientClassLabel: Record<PatientClass, string> = {
  OutPatient: 'OP',
  InPatient: 'IP',
  Emergency: 'Emergency',
  DayCare: 'Day care',
  External: 'Walk-in',
};

export const sourceLabel: Record<LabOrderSource, string> = {
  ClinicalOrder: "Doctor's order",
  WalkIn: 'Walk-in',
  OutsideReferral: 'Outside referral',
};

export const sampleStatusLabel: Record<SampleStatus, string> = {
  Pending: 'To collect',
  Collected: 'Collected',
  Received: 'In lab',
  InProcess: 'In process',
  Completed: 'Completed',
  Rejected: 'Rejected',
  Cancelled: 'Cancelled',
};

export const itemStatusLabel: Record<LabOrderItemStatus, string> = {
  Ordered: 'To collect',
  Collected: 'Collected',
  InLab: 'In lab',
  ResultsEntered: 'Awaiting verification',
  Verified: 'Verified',
  Released: 'Released',
  Cancelled: 'Cancelled',
};

export const reportStatusLabel: Record<LabReportStatus, string> = {
  Draft: 'In progress',
  Verified: 'Ready to release',
  Released: 'Released',
  Amended: 'Amended',
};

export const resultTypeLabel: Record<LabResultType, string> = {
  Numeric: 'Number',
  Text: 'Text',
  Options: 'Choice from a list',
  Culture: 'Culture and sensitivity',
};

export const rejectionReasonLabel: Record<SampleRejectionReason, string> = {
  Haemolysed: 'Haemolysed',
  Clotted: 'Clotted',
  InsufficientVolume: 'Not enough sample',
  WrongContainer: 'Wrong tube',
  Mislabelled: 'Label does not match',
  Unlabelled: 'No label',
  Lipaemic: 'Lipaemic',
  DelayedTransport: 'Delayed transport',
  Leaked: 'Leaked',
  Contaminated: 'Contaminated',
  Other: 'Other reason',
};

export const qcOutcomeLabel: Record<QcOutcome, string> = {
  Accepted: 'Accepted',
  AcceptedWithWarning: 'Warning (1-2s)',
  Rejected: 'Rejected',
};

/** Westgard rule names in words, for the violations list. */
export const westgardRuleText: Record<string, string> = {
  '1-2s': '1-2s warning: one result beyond 2 SD',
  '1-3s': '1-3s: one result beyond 3 SD (random error)',
  '2-2s': '2-2s: two results in a row beyond 2 SD on the same side (systematic error)',
  'R-4s': 'R-4s: range of two results exceeds 4 SD (random error)',
  '4-1s': '4-1s: four results in a row beyond 1 SD on the same side (systematic error)',
  '10x': '10x: ten results in a row on the same side of the mean (systematic error)',
};

/** Billing state of a test's charge, as reported by the collection payment gate. */
export const billingStateLabel: Record<string, string> = {
  NotFound: 'no charge found',
  Unbilled: 'not billed yet',
  Billed: 'billed, not paid',
  Paid: 'paid',
  Cancelled: 'charge cancelled',
};

/** Status of a whole order in words. */
export const orderStatusLabel: Record<'Ordered' | 'InProgress' | 'Completed' | 'Cancelled', string> = {
  Ordered: 'Ordered',
  InProgress: 'In progress',
  Completed: 'Completed',
  Cancelled: 'Cancelled',
};
