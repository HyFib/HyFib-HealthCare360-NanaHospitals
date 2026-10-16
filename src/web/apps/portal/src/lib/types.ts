/**
 * DTOs of the portal and public APIs, mirroring the C# records (camelCase; enums arrive as strings; instants as ISO
 * strings with offset; DateOnly as "yyyy-MM-dd"; TimeOnly as "HH:mm:ss"; money as INR numbers).
 * Sources: Modules/<Module>/Features/**Contracts.cs — keep in step when a contract changes.
 */
import type { BloodGroup, Gender } from '@hc360/core';

// ------------------------------------------------------------------ platform / patients

export interface SystemInfo {
  product: string;
  version: string;
  serverName: string;
  facilityName: string | null;
  facilityCode: string | null;
  serverTime: string;
  /** The hospital-wide feature switches (Hospital setup > Features); missing from older servers. */
  features?: { patientPortal: boolean; telehealth: boolean };
}

/** GET /api/v1/auth/otp/request → OtpChallengeResponse. */
export interface OtpChallenge {
  challengeId: string;
  expiresAt: string;
  maskedPhone: string;
}

/** GET /api/v1/portal/profiles → PatientSummary[] (everyone registered with the verified mobile number). */
export interface PortalProfile {
  id: string;
  uhid: string;
  fullName: string;
  gender: Gender;
  dateOfBirth: string | null;
  age: string | null;
  phone: string | null;
  bloodGroup: BloodGroup;
  category: string;
  isVip: boolean;
  isDeceased: boolean;
  photoFileId: string | null;
}

// ------------------------------------------------------------------ family on other numbers (PP-01)

/** What the relative is to the person who sees their records. */
export type FamilyRelation = 'Parent' | 'Child' | 'Spouse' | 'Sibling' | 'Grandparent' | 'Grandchild' | 'Other';

export type FamilyLinkSource = 'FrontDesk' | 'PortalCode';

/** A family member on another number whose records this account sees. */
export interface PortalLinkedRelative {
  linkId: string;
  patientId: string;
  fullName: string;
  uhid: string;
  relation: FamilyRelation;
  source: FamilyLinkSource;
  linkedAt: string;
}

/** Another account that sees the records of someone on this account's own number. */
export interface PortalSharing {
  linkId: string;
  patientId: string;
  patientName: string;
  accountPhoneMasked: string;
  /** What the patient is to the person on the other number. */
  relation: FamilyRelation;
  linkedAt: string;
}

export interface PortalFamily {
  linked: PortalLinkedRelative[];
  sharedWith: PortalSharing[];
}

export interface RequestFamilyCode {
  uhid: string;
  phone: string;
  relation: FamilyRelation;
}

/** The same answer whether or not the UHID and the number belong together. */
export interface FamilyCodeSent {
  codeId: string;
  expiresAt: string;
}

// ------------------------------------------------------------------ scheduling

export type ConsultationMode = 'InPerson' | 'Telehealth' | 'Both';
export type Weekday = 'Monday' | 'Tuesday' | 'Wednesday' | 'Thursday' | 'Friday' | 'Saturday' | 'Sunday';
export const APPOINTMENT_STATUSES = [
  'Booked',
  'Confirmed',
  'CheckedIn',
  'InConsultation',
  'Completed',
  'Cancelled',
  'NoShow',
  'Rescheduled',
] as const;
export type AppointmentStatus = (typeof APPOINTMENT_STATUSES)[number];
export type AppointmentVisitType = 'New' | 'FollowUp';
export type AppointmentPaymentStatus = 'Unpaid' | 'PaidOnline' | 'PayAtHospital';
export type WaitlistStatus = 'Waiting' | 'Offered' | 'Booked' | 'Cancelled' | 'Expired';
export const PREFERRED_SESSIONS = ['Any', 'Morning', 'Afternoon', 'Evening'] as const;
export type PreferredSession = (typeof PREFERRED_SESSIONS)[number];

export interface PublicFacility {
  id: string;
  name: string;
  city: string | null;
}

export interface PublicDoctor {
  id: string;
  name: string;
  gender: Gender;
  qualification: string | null;
  specialtyId: string | null;
  specialtyName: string | null;
  departmentId: string | null;
  departmentName: string | null;
  languages: string[];
  feeNew: number;
  feeFollowUp: number;
  currency: string;
  offersTelehealth: boolean;
  facilities: PublicFacility[];
  nextAvailableAt: string | null;
  nextTelehealthAt: string | null;
}

export interface PublicSession {
  weekday: Weekday;
  startTime: string;
  endTime: string;
  mode: ConsultationMode;
  facilityId: string;
}

export interface PublicDoctorProfile {
  doctor: PublicDoctor;
  bio: string | null;
  followUpValidityDays: number;
  weeklySessions: PublicSession[];
}

export interface PublicSlot {
  startsAt: string;
  endsAt: string;
  localTime: string;
  mode: ConsultationMode;
  facilityId: string;
  seatsLeft: number;
}

export interface PublicSlotDay {
  date: string;
  slots: PublicSlot[];
}

export interface DirectoryOption {
  id: string;
  name: string;
  doctors: number;
}

export interface DirectoryFilters {
  specialties: DirectoryOption[];
  departments: DirectoryOption[];
  languages: string[];
  facilities: PublicFacility[];
  cutoffMinutes: number;
  windowDays: number;
}

export interface PortalAppointment {
  id: string;
  number: string;
  patientId: string;
  patientName: string;
  practitionerId: string;
  practitionerName: string;
  specialtyName: string | null;
  departmentName: string | null;
  facilityId: string;
  facilityName: string | null;
  startsAt: string;
  endsAt: string;
  status: AppointmentStatus;
  visitType: AppointmentVisitType;
  isTelehealth: boolean;
  paymentStatus: AppointmentPaymentStatus;
  feeAmount: number;
  currency: string;
  reason: string | null;
  canCancel: boolean;
  canReschedule: boolean;
  canPay: boolean;
}

export interface PortalBookAppointmentRequest {
  patientId: string;
  practitionerId: string;
  startsAt: string;
  visitType: AppointmentVisitType;
  isTelehealth: boolean;
  reason: string | null;
  payOnline: boolean;
}

/** What the portal needs to open the payment gateway's checkout (Razorpay-style order). */
export interface PaymentCheckout {
  provider: string;
  orderId: string;
  amount: number;
  currency: string;
  checkoutKey: string | null;
  paymentLinkUrl: string | null;
}

export interface PortalBookingResult {
  appointment: PortalAppointment;
  payment: PaymentCheckout | null;
  message: string | null;
}

export interface ConfirmPaymentRequest {
  orderId: string;
  paymentId: string;
  signature: string;
}

export interface PortalWaitlistEntry {
  id: string;
  patientId: string;
  patientName: string;
  practitionerId: string;
  practitionerName: string;
  preferredFrom: string;
  preferredTo: string;
  preferredSession: PreferredSession;
  isTelehealth: boolean;
  status: WaitlistStatus;
  offeredSlotStartsAt: string | null;
  createdAt: string;
}

export interface PortalJoinWaitlistRequest {
  patientId: string;
  practitionerId: string;
  preferredFrom: string;
  preferredTo: string;
  preferredSession: PreferredSession;
  isTelehealth: boolean;
  notes: string | null;
}

// ------------------------------------------------------------------ virtual care

export type TelehealthStatus = 'Scheduled' | 'WaitingRoom' | 'InProgress' | 'Completed' | 'Cancelled' | 'Missed';
export type TelehealthRole = 'Clinician' | 'Patient' | 'Guest';

export interface IceServerDto {
  urls: string[];
  username: string | null;
  credential: string | null;
}

export interface PortalTelehealthSession {
  id: string;
  patientId: string;
  patientName: string;
  practitionerId: string;
  practitionerName: string;
  appointmentNumber: string | null;
  status: TelehealthStatus;
  scheduledStartAt: string;
  scheduledEndAt: string;
  joinOpensAt: string;
  canJoinNow: boolean;
}

export interface TelehealthJoinInfo {
  sessionId: string;
  status: TelehealthStatus;
  scheduledStartAt: string;
  scheduledEndAt: string;
  practitionerName: string;
  departmentName: string | null;
  patientName: string;
  canJoinNow: boolean;
  joinOpensAt: string;
  hubPath: string;
  iceServers: IceServerDto[];
}

export interface TelehealthMessageDto {
  id: string;
  senderRole: TelehealthRole;
  senderName: string;
  text: string;
  sentAt: string;
}

export interface WaitingParticipant {
  participantId: string;
  role: TelehealthRole;
  displayName: string;
  joinedAt: string;
}

/** Returned by the hub's JoinAsPatient / JoinAsGuest. */
export interface TelehealthJoinResult {
  sessionId: string;
  participantId: string;
  role: TelehealthRole;
  status: TelehealthStatus;
  admitted: boolean;
  scheduledStartAt: string;
  practitionerName: string;
  patientName: string;
  iceServers: IceServerDto[];
  waiting: WaitingParticipant[];
  recentMessages: TelehealthMessageDto[];
}

// ------------------------------------------------------------------ diagnostics

export type DiagnosticKind = 'Laboratory' | 'Radiology' | 'Cardiology';
export type ResultFlag = 'Normal' | 'Low' | 'High' | 'CriticalLow' | 'CriticalHigh' | 'Abnormal' | 'Critical';
export type LabReportStatus = 'Draft' | 'Verified' | 'Released' | 'Amended';
export type DiagnosticReportStatus = 'Draft' | 'Reported' | 'Verified' | 'Released';
export type SusceptibilityInterpretation = 'Sensitive' | 'Intermediate' | 'Resistant';
export type Modality = 'XR' | 'CT' | 'MR' | 'US' | 'MG' | 'RF' | 'DX';
export type CardiologyStudyType = 'Ecg' | 'Echo' | 'Tmt' | 'Holter' | 'Abpm' | 'Other';
export type LabSection =
  'Hematology' | 'Biochemistry' | 'Microbiology' | 'Serology' | 'ClinicalPathology' | 'Histopathology' | 'Immunology';

export interface PortalReportItem {
  kind: DiagnosticKind;
  /** Lab report id, or the imaging/cardiology study id. */
  id: string;
  patientId: string;
  patientName: string;
  title: string;
  reference: string;
  releasedAt: string;
  isAmended: boolean;
  isPartial: boolean;
}

export interface ReportLetterhead {
  facilityName: string;
  address: string | null;
  phone: string | null;
  email: string | null;
  nabhNumber: string | null;
  letterheadNote: string | null;
  logoFileId: string | null;
}

export interface ReportPatient {
  id: string;
  uhid: string;
  name: string;
  gender: Gender;
  age: string | null;
  dateOfBirth: string | null;
}

export interface ReportSignatory {
  userId: string;
  name: string;
  qualification: string | null;
  registrationNumber: string | null;
  signatureFileId: string | null;
}

export interface SusceptibilityDto {
  antibiotic: string;
  interpretation: SusceptibilityInterpretation;
  mic: string | null;
}

export interface LabReportResultLine {
  resultId: string;
  name: string;
  groupName: string | null;
  value: string | null;
  unit: string | null;
  flag: ResultFlag | null;
  referenceText: string | null;
  status: string;
  autoValidated: boolean;
  remarks: string | null;
  organism: string | null;
  colonyCount: string | null;
  susceptibilities: SusceptibilityDto[];
  previousValue: number | null;
  previousResultAt: string | null;
}

export interface LabReportTest {
  orderItemId: string;
  code: string;
  name: string;
  status: string;
  specimenType: string;
  method: string | null;
  interpretation: string | null;
  referralLabName: string | null;
  collectedAt: string | null;
  receivedAt: string | null;
  verifiedAt: string | null;
  releasedAt: string | null;
  results: LabReportResultLine[];
}

export interface LabReportSection {
  section: LabSection;
  tests: LabReportTest[];
}

export interface LabAmendmentView {
  revision: number;
  reason: string;
  changes: string;
  amendedAt: string;
  amendedBy: string | null;
}

export interface LabReportView {
  reportId: string;
  labOrderId: string;
  reportNumber: string;
  status: LabReportStatus;
  isPartial: boolean;
  revision: number;
  letterhead: ReportLetterhead;
  patient: ReportPatient;
  patientClass: string;
  referredBy: string | null;
  clinicalNotes: string | null;
  orderedAt: string;
  firstReleasedAt: string | null;
  releasedAt: string | null;
  sections: LabReportSection[];
  signatories: ReportSignatory[];
  hasAutoValidatedResults: boolean;
  amendments: LabAmendmentView[];
  verificationCode: string | null;
  verificationPath: string | null;
  /** The portal's public check page for the QR code ({portal.baseUrl}/portal/verify/...); null until configured. */
  verificationUrl?: string | null;
}

export interface RadiologyAddendumDto {
  id: string;
  text: string;
  reason: string | null;
  isCriticalFinding: boolean;
  addedAt: string;
  addedBy: string | null;
}

export interface RadiologyReportView {
  reportId: string;
  imagingStudyId: string;
  letterhead: ReportLetterhead;
  patient: ReportPatient;
  accessionNumber: string;
  studyName: string;
  modality: Modality;
  bodyPart: string | null;
  referredBy: string | null;
  orderedAt: string;
  acquiredAt: string | null;
  technique: string | null;
  clinicalHistory: string | null;
  comparison: string | null;
  findings: string;
  impression: string;
  isCriticalFinding: boolean;
  status: DiagnosticReportStatus;
  verifiedAt: string | null;
  releasedAt: string | null;
  signatories: ReportSignatory[];
  addenda: RadiologyAddendumDto[];
  verificationCode: string | null;
  verificationPath: string | null;
  /** The portal's public check page for the QR code ({portal.baseUrl}/portal/verify/...); null until configured. */
  verificationUrl?: string | null;
}

export interface EcgFindings {
  heartRate: number | null;
  rhythm: string | null;
  prInterval: number | null;
  qrsDuration: number | null;
  qtInterval: number | null;
  qtcInterval: number | null;
  axis: string | null;
  stTChanges: string | null;
  interpretation: string | null;
}

export interface EchoFindings {
  lvef: number | null;
  leftVentricle: string | null;
  rightVentricle: string | null;
  leftAtrium: string | null;
  rightAtrium: string | null;
  valves: string | null;
  rwma: string | null;
  diastolicFunction: string | null;
  pericardium: string | null;
  conclusion: string | null;
}

export interface TmtFindings {
  protocol: string | null;
  durationSeconds: number | null;
  mets: number | null;
  maxHeartRate: number | null;
  targetHeartRatePercent: number | null;
  bpResponse: string | null;
  symptoms: string | null;
  stChanges: string | null;
  reasonForTermination: string | null;
  result: 'Negative' | 'Positive' | 'Inconclusive' | null;
}

export interface HolterFindings {
  durationHours: number | null;
  minHeartRate: number | null;
  meanHeartRate: number | null;
  maxHeartRate: number | null;
  ventricularEctopics: number | null;
  supraventricularEctopics: number | null;
  pauses: number | null;
  longestPause: string | null;
  arrhythmias: string | null;
  stChanges: string | null;
}

export interface AbpmFindings {
  daytimeMeanSystolic: number | null;
  daytimeMeanDiastolic: number | null;
  nighttimeMeanSystolic: number | null;
  nighttimeMeanDiastolic: number | null;
  dippingPattern: string | null;
}

export interface CardiologyReportView {
  reportId: string;
  cardiologyStudyId: string;
  letterhead: ReportLetterhead;
  patient: ReportPatient;
  accessionNumber: string;
  studyName: string;
  studyType: CardiologyStudyType;
  referredBy: string | null;
  orderedAt: string;
  acquiredAt: string | null;
  ecg: EcgFindings | null;
  echo: EchoFindings | null;
  tmt: TmtFindings | null;
  holter: HolterFindings | null;
  abpm: AbpmFindings | null;
  conclusion: string | null;
  recommendations: string | null;
  isCriticalFinding: boolean;
  status: DiagnosticReportStatus;
  verifiedAt: string | null;
  releasedAt: string | null;
  signatories: ReportSignatory[];
  verificationCode: string | null;
  verificationPath: string | null;
  /** The portal's public check page for the QR code ({portal.baseUrl}/portal/verify/...); null until configured. */
  verificationUrl?: string | null;
}

/** Public QR verification answer: proves a report is genuine without disclosing results or identity. */
export interface ReportAuthenticity {
  valid: boolean;
  kind: DiagnosticKind;
  reportNumber: string;
  facilityName: string;
  patientInitials: string;
  releasedAt: string | null;
  tests: string[];
  revision: number;
}

// ------------------------------------------------------------------ clinical

export type PrescriptionStatus = 'Draft' | 'Signed' | 'Cancelled';
export type EncounterType = 'OutPatient' | 'InPatient' | 'Emergency' | 'Telehealth' | 'HealthCheckup';
export type AllergyType = 'Drug' | 'Food' | 'Environmental' | 'Other';
export type AllergySeverity = 'Mild' | 'Moderate' | 'Severe' | 'Anaphylaxis';
export type DiagnosisType = 'Provisional' | 'Final' | 'Differential';
export type DiagnosisStatus = 'Active' | 'Resolved' | 'Chronic';
export type DischargeSummaryStatus = 'Draft' | 'Signed' | 'Amended';
export const RECORD_REQUEST_PURPOSES = ['PatientCopy', 'Insurance', 'PatientCare', 'Legal', 'Other'] as const;
export type RecordRequestPurpose = (typeof RECORD_REQUEST_PURPOSES)[number] | 'Research' | 'Audit';
export type RecordRequestStatus = 'Requested' | 'Issued' | 'Returned' | 'Overdue' | 'Cancelled';

export interface PortalPrescriptionListItem {
  id: string;
  prescriptionNumber: string;
  patientId: string;
  status: PrescriptionStatus;
  signedAt: string | null;
  doctor: string | null;
  medicines: string[];
  refillsRemaining: number;
}

export interface PortalPrescriptionLine {
  drugName: string;
  genericName: string | null;
  strength: string | null;
  dosageForm: string | null;
  dose: string;
  frequency: string;
  frequencyEnglish: string;
  frequencyHindi: string;
  durationDays: number;
  quantity: number;
  instructions: string | null;
}

export interface PortalPrescriptionDetail {
  id: string;
  prescriptionNumber: string;
  patientId: string;
  status: PrescriptionStatus;
  signedAt: string | null;
  doctor: string | null;
  doctorQualification: string | null;
  doctorRegistrationNumber: string | null;
  facility: string | null;
  advice: string | null;
  lines: PortalPrescriptionLine[];
  refillsRemaining: number;
  verificationToken: string | null;
  /** The public check page for the printed QR code, when the server knows the hospital's public address. */
  verificationUrl?: string | null;
}

/** Public QR verification of a printed prescription (no patient data). */
export interface PrescriptionVerification {
  found: boolean;
  status: string | null;
  doctor: string | null;
  qualification: string | null;
  registrationNumber: string | null;
  facility: string | null;
  issuedOn: string | null;
  itemCount: number;
}

export interface AllergySummary {
  id: string;
  allergen: string;
  type: AllergyType;
  severity: AllergySeverity;
  reaction: string | null;
}

export interface ProblemSummary {
  id: string;
  icdCode: string | null;
  title: string;
  type: DiagnosisType;
  status: DiagnosisStatus;
  onsetDate: string | null;
}

export interface PortalEncounter {
  id: string;
  type: EncounterType;
  startedAt: string;
  doctor: string | null;
  department: string | null;
  diagnoses: string[];
  followUpDate: string | null;
}

export interface PortalImmunization {
  vaccineName: string;
  doseNumber: number | null;
  administeredOn: string;
  nextDueOn: string | null;
}

export interface PortalDischargeSummaryItem {
  id: string;
  admissionId: string;
  dischargedAt: string | null;
  signedAt: string | null;
  diagnoses: string | null;
}

export interface PortalHealthRecord {
  patientId: string;
  allergies: AllergySummary[];
  problems: ProblemSummary[];
  recentEncounters: PortalEncounter[];
  immunizations: PortalImmunization[];
  dischargeSummaries: PortalDischargeSummaryItem[];
}

export interface DischargeSummaryDto {
  id: string;
  admissionId: string;
  patientId: string;
  encounterId: string | null;
  practitionerId: string | null;
  practitionerName: string | null;
  admittedAt: string | null;
  dischargedAt: string | null;
  diagnoses: string | null;
  diagnosisCodes: string[];
  presentingComplaints: string | null;
  courseInHospital: string | null;
  procedures: string | null;
  investigationsSummary: string | null;
  conditionAtDischarge: string | null;
  medications: string | null;
  advice: string | null;
  followUpDate: string | null;
  followUpInstructions: string | null;
  status: DischargeSummaryStatus;
  signedAt: string | null;
  signedByPractitionerId: string | null;
  amendmentReason: string | null;
  amendedAt: string | null;
  amendmentCount: number;
  version: number;
}

export interface PortalRecordRequest {
  patientId: string;
  purpose: RecordRequestPurpose;
  details: string | null;
}

export interface RecordRequestCreated {
  id: string;
  requestNumber: string;
  status: RecordRequestStatus;
}

// ------------------------------------------------------------------ pharmacy

export type RefillStatus = 'Requested' | 'Approved' | 'Rejected' | 'Cancelled';

export interface RefillablePrescription {
  queueEntryId: string;
  prescriptionNumber: string;
  patientId: string;
  patientName: string;
  signedAt: string;
  prescriberName: string | null;
  refillsRemaining: number;
  refillValidUntil: string | null;
  medicines: string[];
  hasOpenRequest: boolean;
}

export interface RefillRequestDto {
  id: string;
  number: string;
  queueEntryId: string;
  prescriptionNumber: string;
  patientId: string;
  patientName: string;
  uhid: string;
  channel: 'Portal' | 'Staff';
  status: RefillStatus;
  requestedAt: string;
  preferredPickupDate: string | null;
  notes: string | null;
  rejectReason: string | null;
  refillEntryId: string | null;
}

export interface PortalRefillView {
  prescriptions: RefillablePrescription[];
  requests: RefillRequestDto[];
}

export interface CreateRefillRequest {
  queueEntryId: string;
  preferredPickupDate: string | null;
  notes: string | null;
}

export interface RefillRequestCreated {
  id: string;
  number: string;
}

// ------------------------------------------------------------------ billing

export type BillType = 'OutPatient' | 'InPatient';
export type BillStatus = 'Draft' | 'Finalized' | 'PartiallyPaid' | 'Paid' | 'Cancelled' | 'Refunded';
export type GatewayOrderStatus = 'Created' | 'Paid' | 'Failed' | 'Expired' | 'Cancelled';
export type ReceiptPurpose = 'BillPayment' | 'Deposit' | 'PayerSettlement' | 'Advance';

export interface PortalBillListItem {
  id: string;
  billNumber: string | null;
  type: BillType;
  status: BillStatus;
  patientId: string;
  patientName: string | null;
  billedAt: string | null;
  netAmount: number;
  patientAmount: number;
  paidAmount: number;
  dueAmount: number;
}

export interface PortalBillLine {
  description: string;
  quantity: number;
  unitPrice: number;
  discountAmount: number;
  taxAmount: number;
  netAmount: number;
  serviceDate: string;
  isReturn: boolean;
}

export interface ReceiptSummary {
  id: string;
  receiptNumber: string;
  purpose: ReceiptPurpose;
  amount: number;
  modes: string;
  receivedAt: string;
}

export interface PortalBillDetail {
  id: string;
  billNumber: string | null;
  type: BillType;
  status: BillStatus;
  patientId: string;
  patientName: string | null;
  billedAt: string | null;
  grossAmount: number;
  discountAmount: number;
  taxAmount: number;
  roundOffAmount: number;
  netAmount: number;
  payerAmount: number;
  patientAmount: number;
  paidAmount: number;
  dueAmount: number;
  lines: PortalBillLine[];
  receipts: ReceiptSummary[];
}

/** POST /api/v1/portal/bills/{id}/pay: `id` is the gateway order row, `orderId` the gateway's own order id. */
export interface PortalPaymentOrder extends PaymentCheckout {
  id: string;
}

export interface PortalPaymentStatus {
  status: GatewayOrderStatus;
  receiptId: string | null;
  dueAmount: number;
}

// ------------------------------------------------------------------ engagement

export const FEEDBACK_ASPECTS = [
  'Overall',
  'Doctor',
  'Nursing',
  'FrontDesk',
  'Billing',
  'Cleanliness',
  'Food',
] as const;
export type FeedbackAspect = (typeof FEEDBACK_ASPECTS)[number];
export type ComplaintStatus = 'Open' | 'InProgress' | 'Resolved' | 'Closed';

export type RatingsInput = Partial<Record<Uncapitalize<FeedbackAspect>, number | null>>;

export interface PortalFeedbackForm {
  id: string;
  title: string;
  hospital: string;
  introText: string | null;
  aspects: FeedbackAspect[];
  includesNps: boolean;
  allowsComment: boolean;
}

export interface PublicFeedbackForm {
  title: string;
  introText: string | null;
  aspects: FeedbackAspect[];
  includesNps: boolean;
  allowsComment: boolean;
  hospital: string;
}

export interface SubmitFeedbackRequest {
  ratings: RatingsInput | null;
  nps: number | null;
  comment: string | null;
  wantsCallback: boolean;
  contactPhone: string | null;
}

export interface PortalFeedbackRequest extends SubmitFeedbackRequest {
  patientId: string;
  formId: string;
  visitId: string | null;
}

export interface FeedbackReceipt {
  thankYou: string;
  complaintNumber: string | null;
}

export interface PortalFeedbackResult extends FeedbackReceipt {
  responseId: string;
}

export interface PortalFeedbackItem {
  id: string;
  submittedAt: string;
  formTitle: string;
  averageRating: number | null;
  nps: number | null;
  complaintNumber: string | null;
  complaintStatus: ComplaintStatus | null;
}

export interface CommunicationPreferences {
  marketing: boolean;
  updates: boolean;
  marketingChangedAt: string | null;
  updatesChangedAt: string | null;
  /** The patient's number gets reminders and updates on WhatsApp: opted in, and no STOP in force (INT-WA). */
  whatsApp: boolean;
  whatsAppChangedAt: string | null;
  /** Signed in with the patient's own number: WhatsApp choices belong to the number, so only its holder changes them. */
  whatsAppEditable: boolean;
}

export interface CommunicationPreferencesRequest {
  patientId: string;
  marketing: boolean;
  updates: boolean;
  /** Left out to keep the WhatsApp choice as it is. */
  whatsApp?: boolean;
}

// ------------------------------------------------------------------ privacy (DPDP Act 2023)
// Sources: Modules.Privacy/Features/Portal/PortalPrivacyContracts.cs, Services/PersonalDataInventory.cs,
// Services/BreachService.cs, Services/NomineeService.cs, Features/Settings/PrivacySettingsEndpoints.cs.

export type DataRequestType = 'Access' | 'Correction' | 'Erasure' | 'Grievance';
/** The requests a patient can make in the portal (the data summary needs no request). */
export type PortalRequestType = Exclude<DataRequestType, 'Access'>;
export type DataRequestStatus = 'Submitted' | 'InProgress' | 'Completed' | 'PartlyCompleted' | 'Declined' | 'Withdrawn';
export type DataRequestChannel = 'Portal' | 'FrontDesk' | 'Email' | 'Letter' | 'Phone';
export type RequesterRelation = 'Self' | 'ParentOrGuardian' | 'Nominee' | 'FamilyMember' | 'AuthorisedRepresentative';
/** Who may make a request from the portal: the patient, a parent or guardian, or a family member. */
export const PORTAL_REQUESTERS = ['Self', 'ParentOrGuardian', 'FamilyMember'] as const;
export type PortalRequester = (typeof PORTAL_REQUESTERS)[number];
export type CorrectionDecision = 'Pending' | 'Accepted' | 'Declined';
export type DataRequestEventKind =
  | 'Received'
  | 'IdentityVerified'
  | 'Started'
  | 'Assigned'
  | 'Note'
  | 'CorrectionsDecided'
  | 'Erased'
  | 'Closed'
  | 'Withdrawn';
export type BreachDataCategory =
  'Identity' | 'Contact' | 'Health' | 'Financial' | 'GovernmentId' | 'Credentials' | 'Children';
/** Which retention period applies to a group of the data summary (server: `PersonalDataRetention`). */
export type RetentionKind = 'MedicalRecords' | 'Finance' | 'MessageLog' | 'Audit' | 'WhileInUse' | 'WithRecords';
/** What an erasure removed (server: `ErasureService` items). */
export type ErasureItem = 'messages' | 'feedback' | 'nominee' | 'portal';

/** The details a patient can ask to have corrected (server: `CorrectableFields`), in the order the form lists them. */
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

/** The grievance officer (or Data Protection Officer); the hospital's own details fill any gap. */
export interface PrivacyContact {
  title: string;
  name: string | null;
  email: string | null;
  phone: string | null;
  address: string | null;
}

/** GET /api/v1/public/privacy (anonymous). */
export interface PublicPrivacyInfo {
  hospital: string;
  contact: PrivacyContact;
  responseDays: number;
}

/** The registration details as held. ABHA and Aadhaar arrive masked, exactly as the hospital stores them. */
export interface SummaryPerson {
  uhid: string;
  fullName: string;
  gender: Gender;
  dateOfBirth: string | null;
  phone: string | null;
  alternatePhone: string | null;
  email: string | null;
  address: string | null;
  emergencyContact: string | null;
  abhaNumber: string | null;
  abhaAddress: string | null;
  aadhaarLast4: string | null;
  hasPhoto: boolean;
  registeredAt: string;
  registeredAtFacility: string | null;
}

/**
 * How many records of a kind are held, and the dates of the first and the latest. `key` is the catalogue table
 * ("clinical.encounters"), stable for translating the label.
 */
export interface SummaryItem {
  key: string;
  label: string;
  count: number;
  first: string | null;
  last: string | null;
}

/**
 * A group of personal data; `key` is stable (the portal translates by it). `retention` is the English sentence;
 * `retentionKind` with the summary's `periods` says it in the patient's language.
 */
export interface SummaryCategory {
  key: string;
  title: string;
  description: string;
  purpose: string;
  basis: string;
  retention: string;
  retentionKind: RetentionKind;
  items: SummaryItem[];
}

/** The retention periods in force, as numbers. */
export interface RetentionPeriods {
  outpatientYears: number;
  inpatientYears: number;
  financeYears: number;
  messageLogDays: number;
  auditYears: number;
}

export interface SummaryConsent {
  type: string;
  granted: boolean;
  capturedAt: string;
  revokedAt: string | null;
}

export interface SummaryRecipient {
  name: string;
  role: string;
  data: string;
}

export interface SummaryNominee {
  name: string;
  relationship: string;
}

/** GET /api/v1/portal/privacy/summary: the summary of personal data the DPDP Act (s11) entitles a patient to. */
export interface DataSummary {
  generatedAt: string;
  hospital: string;
  person: SummaryPerson;
  categories: SummaryCategory[];
  consents: SummaryConsent[];
  sharedWith: SummaryRecipient[];
  processors: SummaryRecipient[];
  nominee: SummaryNominee | null;
  contact: PrivacyContact;
  periods: RetentionPeriods;
}

/**
 * GET /api/v1/portal/privacy/status: the privacy centre at a glance. Counts and the nominee's name only, so the
 * server does not record it as access to the patient's record.
 */
export interface PortalPrivacyStatus {
  hasNominee: boolean;
  nomineeName: string | null;
  openRequests: number;
  maxOpenRequests: number;
  notices: number;
}

export interface PortalDataRequestItem {
  id: string;
  requestNumber: string;
  patientId: string;
  type: DataRequestType;
  status: DataRequestStatus;
  receivedAt: string;
  dueAt: string;
  closedAt: string | null;
}

export interface PortalCorrectionItem {
  field: string;
  currentValue: string | null;
  requestedValue: string;
  decision: CorrectionDecision;
  note: string | null;
}

export interface PortalRequestEvent {
  at: string;
  kind: DataRequestEventKind;
  text: string;
}

/**
 * A request as the patient sees it: the public history only, never staff notes. `identityConfirmed` false on a
 * request made for someone else means the hospital still has to confirm the requester may act for them.
 */
export interface PortalDataRequestDetail {
  id: string;
  requestNumber: string;
  patientId: string;
  type: DataRequestType;
  status: DataRequestStatus;
  requestedBy: RequesterRelation;
  requesterName: string | null;
  identityConfirmed: boolean;
  details: string;
  corrections: PortalCorrectionItem[];
  receivedAt: string;
  dueAt: string;
  closedAt: string | null;
  response: string | null;
  /** Erasure: what was erased (`ErasureItem` keys). */
  erasedItems: string[];
  /** Erasure: the date ("yyyy-MM-dd") until which the rest must be kept by law. */
  retainedUntil: string | null;
  retainedPermanently: boolean;
  timeline: PortalRequestEvent[];
  canWithdraw: boolean;
  contact: PrivacyContact;
}

export interface CorrectionInput {
  field: CorrectableField;
  requestedValue: string;
}

export interface PortalDataRequestCreate {
  patientId: string;
  type: PortalRequestType;
  requestedBy: PortalRequester;
  requesterName: string | null;
  details: string | null;
  corrections: CorrectionInput[] | null;
}

export interface PortalRequestCreated {
  id: string;
  requestNumber: string;
  dueAt: string;
}

export interface NomineeDto {
  name: string;
  relationship: string;
  phone: string | null;
  email: string | null;
  recordedVia: DataRequestChannel;
  updatedAt: string;
}

/** GET /api/v1/portal/privacy/nominee → `{ nominee }`. */
export interface NomineeView {
  nominee: NomineeDto | null;
}

export interface PortalNomineeRequest {
  patientId: string;
  name: string;
  relationship: string;
  phone: string | null;
  email: string | null;
}

/**
 * A personal-data breach notice (DPDP Rules 2025, rule 7(1)). `patientIds` are the people it concerns among those
 * the signed-in number may act for.
 */
export interface PortalBreachNotice {
  id: string;
  patientIds: string[];
  reference: string;
  title: string;
  notifiedAt: string;
  awareAt: string;
  occurredFrom: string | null;
  occurredTo: string | null;
  description: string;
  location: string | null;
  dataCategories: BreachDataCategory[];
  consequences: string | null;
  mitigation: string | null;
  principalSteps: string | null;
  contact: PrivacyContact;
}
