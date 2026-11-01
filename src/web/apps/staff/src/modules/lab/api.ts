import type { Gender, PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { ApprovalRequestDto } from '@/modules/approvals/api';
import type {
  CriticalCallDetails,
  CriticalCallDto,
  DiagnosticKind,
  OrderPriority,
  PatientClass,
  ReportLetterhead,
  ReportPatient,
  ReportSignatory,
  TatSummary,
} from './common/types';

// ------------------------------------------------------------------------------------------------ enums (as strings)

export const LAB_SECTIONS = [
  'Hematology',
  'Biochemistry',
  'Microbiology',
  'Serology',
  'ClinicalPathology',
  'Histopathology',
  'Immunology',
] as const;
export type LabSection = (typeof LAB_SECTIONS)[number];

export const SPECIMEN_TYPES = [
  'WholeBlood',
  'Serum',
  'Plasma',
  'Urine',
  'Stool',
  'Swab',
  'Sputum',
  'Csf',
  'BodyFluid',
  'Tissue',
  'Other',
] as const;
export type SpecimenType = (typeof SPECIMEN_TYPES)[number];

export const CONTAINER_TYPES = [
  'Edta',
  'Plain',
  'Fluoride',
  'Citrate',
  'Heparin',
  'UrineContainer',
  'Swab',
  'StoolContainer',
  'SterileContainer',
] as const;
export type ContainerType = (typeof CONTAINER_TYPES)[number];

export const RESULT_TYPES = ['Numeric', 'Text', 'Options', 'Culture'] as const;
export type LabResultType = (typeof RESULT_TYPES)[number];

export type ResultFlag = 'Normal' | 'Low' | 'High' | 'CriticalLow' | 'CriticalHigh' | 'Abnormal' | 'Critical';
export type LabOrderSource = 'ClinicalOrder' | 'WalkIn' | 'OutsideReferral';

export const ORDER_STATUSES = ['Ordered', 'InProgress', 'Completed', 'Cancelled'] as const;
export type LabOrderStatus = (typeof ORDER_STATUSES)[number];

export type LabOrderItemStatus =
  'Ordered' | 'Collected' | 'InLab' | 'ResultsEntered' | 'Verified' | 'Released' | 'Cancelled';

export const SAMPLE_STATUSES = [
  'Pending',
  'Collected',
  'Received',
  'InProcess',
  'Completed',
  'Rejected',
  'Cancelled',
] as const;
export type SampleStatus = (typeof SAMPLE_STATUSES)[number];

export const REJECTION_REASONS = [
  'Haemolysed',
  'Clotted',
  'InsufficientVolume',
  'WrongContainer',
  'Mislabelled',
  'Unlabelled',
  'Lipaemic',
  'DelayedTransport',
  'Leaked',
  'Contaminated',
  'Other',
] as const;
export type SampleRejectionReason = (typeof REJECTION_REASONS)[number];

export type LabResultStatus = 'Pending' | 'Entered' | 'Verified';

export const REPORT_STATUSES = ['Draft', 'Verified', 'Released', 'Amended'] as const;
export type LabReportStatus = (typeof REPORT_STATUSES)[number];

export type SusceptibilityInterpretation = 'Sensitive' | 'Intermediate' | 'Resistant';
export type InstrumentProtocol = 'Hl7' | 'Astm';

export const QC_OUTCOMES = ['Accepted', 'AcceptedWithWarning', 'Rejected'] as const;
export type QcOutcome = (typeof QC_OUTCOMES)[number];

/** Why an analyser's result could not go on a sample's test (the server's UnmatchedResultReason). */
export const UNMATCHED_REASONS = [
  'NoSampleId',
  'UnknownSample',
  'SampleNotInLab',
  'NoMatchingTest',
  'DiffersFromVerified',
  'InvalidValue',
  'RepeatedInMessage',
] as const;
export type UnmatchedResultReason = (typeof UNMATCHED_REASONS)[number];

export const UNMATCHED_STATUSES = ['Open', 'Assigned', 'Discarded'] as const;
export type UnmatchedResultStatus = (typeof UNMATCHED_STATUSES)[number];

// ------------------------------------------------------------------------------------------------ masters

export interface LabTestListItem {
  id: string;
  code: string;
  name: string;
  shortName: string | null;
  section: LabSection;
  specimenType: SpecimenType;
  container: ContainerType;
  resultType: LabResultType;
  unit: string | null;
  isPanel: boolean;
  isOrderable: boolean;
  isProvisional: boolean;
  isActive: boolean;
  tatHours: number;
  serviceItemId: string | null;
  price: number | null;
  preparation: string | null;
  componentCount: number;
}

export interface ReferenceRangeDto {
  id: string | null;
  gender: Gender | null;
  ageFromDays: number;
  ageToDays: number | null;
  low: number | null;
  high: number | null;
  criticalLow: number | null;
  criticalHigh: number | null;
  text: string | null;
  normalValues: string[] | null;
  criticalValues: string[] | null;
}

export interface LabTestComponentDto {
  testId: string;
  code: string;
  name: string;
  displayOrder: number;
  groupName: string | null;
}

export interface LabTestDetail {
  id: string;
  code: string;
  name: string;
  shortName: string | null;
  section: LabSection;
  specimenType: SpecimenType;
  container: ContainerType;
  method: string | null;
  tatHours: number;
  loinc: string | null;
  loincName: string | null;
  serviceItemId: string | null;
  isOrderable: boolean;
  isPanel: boolean;
  resultType: LabResultType;
  unit: string | null;
  decimals: number;
  options: string[];
  instrumentChannelCodes: string[];
  qcGroup: string | null;
  deltaThresholdPercent: number | null;
  allowAutoValidation: boolean;
  preparation: string | null;
  drawSeparately: boolean;
  interpretation: string | null;
  defaultReferralLabId: string | null;
  displayOrder: number;
  isProvisional: boolean;
  isActive: boolean;
  components: LabTestComponentDto[];
  ranges: ReferenceRangeDto[];
  version: number;
}

export interface SaveLabTestRequest {
  code: string;
  name: string;
  shortName: string | null;
  section: LabSection;
  specimenType: SpecimenType;
  container: ContainerType;
  method: string | null;
  tatHours: number;
  loinc: string | null;
  loincName: string | null;
  serviceItemId: string | null;
  isOrderable: boolean;
  isPanel: boolean;
  resultType: LabResultType;
  unit: string | null;
  decimals: number;
  options: string[] | null;
  instrumentChannelCodes: string[] | null;
  qcGroup: string | null;
  deltaThresholdPercent: number | null;
  allowAutoValidation: boolean;
  preparation: string | null;
  drawSeparately: boolean;
  interpretation: string | null;
  defaultReferralLabId: string | null;
  displayOrder: number;
  isActive: boolean;
  version: number | null;
}

export interface ComponentEntry {
  testId: string;
  groupName: string | null;
}

export interface LabInstrumentDto {
  id: string;
  code: string;
  name: string;
  manufacturer: string | null;
  model: string | null;
  section: LabSection;
  protocol: InstrumentProtocol;
  isBidirectional: boolean;
  autoValidationEnabled: boolean;
  location: string | null;
  isActive: boolean;
  version: number;
}

export type SaveLabInstrumentRequest = Omit<LabInstrumentDto, 'id' | 'version'> & { version: number | null };

// ---------------------------------------------------------------------------------------------------- connections

/**
 * How an analyser reaches HC360 (Integrations: `/api/v1/integrations/lab/connections`): a network port it connects to,
 * or a serial port on a lab PC whose HC360 lab agent relays it. Names follow Windows (System.IO.Ports).
 */
/** How an analyser reaches HC360; `Simulator` is the training analyser, whose results are entered on screen. */
export type LabTransport = 'Tcp' | 'Serial' | 'Simulator';
export const SERIAL_BAUD_RATES = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200] as const;
export const SERIAL_PARITIES = ['None', 'Odd', 'Even', 'Mark', 'Space'] as const;
export const SERIAL_STOP_BITS = ['One', 'OnePointFive', 'Two'] as const;
export const SERIAL_HANDSHAKES = ['None', 'XOnXOff', 'RequestToSend', 'RequestToSendXOnXOff'] as const;
export const HL7_ORDER_MESSAGES = ['OML^O21', 'ORM^O01'] as const;

export interface LabSerialPortDto {
  portName: string;
  baudRate: number;
  dataBits: number;
  parity: (typeof SERIAL_PARITIES)[number];
  stopBits: (typeof SERIAL_STOP_BITS)[number];
  handshake: (typeof SERIAL_HANDSHAKES)[number];
  dtrEnable: boolean;
  rtsEnable: boolean;
}

/**
 * Live state on the server. `state`: `listening`, `starting` or `failed` for a network port; `waiting` (no relay from
 * its agent), `connected` or `port_error` (the agent could not open the COM port) for a serial analyser.
 */
export interface LabConnectionStatusDto {
  state: string;
  activeConnections: number;
  connectedSince: string | null;
  lastMessageAt: string | null;
  lastErrorAt: string | null;
  lastError: string | null;
  messagesAccepted: number;
  messagesRefused: number;
}

export interface LabConnectionDto {
  code: string;
  name: string | null;
  protocol: 'Hl7v2' | 'Astm';
  transport: LabTransport;
  enabled: boolean;
  port: number | null;
  allowedIps: string[];
  encoding: string | null;
  sampleIdFields: string[];
  orderMessage: string | null;
  agentDeviceId: string | null;
  agentName: string | null;
  serial: LabSerialPortDto | null;
  /** The connection still carries the retired `"hostQuery": false`, which keeps host queries and orders off. */
  hostQueryRetiredOff: boolean;
  inLabSetup: boolean;
  bidirectional: boolean | null;
  /** Why the gateway ignores this connection, when it does. */
  problem: string | null;
  status: LabConnectionStatusDto | null;
  pendingOrders: number;
}

/** An enrolled HC360 lab agent. Online: it called in during the last three minutes, or relays an analyser now. */
export interface LabAgentDto {
  id: string;
  name: string;
  machineName: string | null;
  location: string | null;
  lastSeenAt: string | null;
  online: boolean;
  appVersion: string | null;
  lastIpAddress: string | null;
}

export interface LabConnectionsDto {
  gatewayEnabled: boolean;
  /** Sent back with a change: someone else's change in between gives 409. */
  listVersion: number;
  connections: LabConnectionDto[];
  agents: LabAgentDto[];
}

export interface SaveLabConnectionRequest {
  transport: LabTransport;
  enabled: boolean;
  port: number | null;
  allowedIps: string[] | null;
  encoding: string | null;
  sampleIdFields: string[] | null;
  orderMessage: string | null;
  agentDeviceId: string | null;
  serial: LabSerialPortDto | null;
  listVersion: number;
}

export interface ReferralLabDto {
  id: string;
  code: string;
  name: string;
  contactPerson: string | null;
  phone: string | null;
  email: string | null;
  address: string | null;
  accreditationNumber: string | null;
  tatDays: number;
  isActive: boolean;
  version: number;
}

export type SaveReferralLabRequest = Omit<ReferralLabDto, 'id' | 'version'> & { version: number | null };

// ------------------------------------------------------------------------------------------------ orders

export interface LabOrderListItem {
  id: string;
  accessionNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  gender: Gender;
  age: string | null;
  patientClass: PatientClass;
  source: LabOrderSource;
  priority: OrderPriority;
  status: LabOrderStatus;
  orderedAt: string;
  testCount: number;
  releasedCount: number;
  tests: string[];
  reportId: string | null;
  reportStatus: LabReportStatus | null;
}

export interface LabOrderItemDto {
  id: string;
  labTestId: string;
  testCode: string;
  testName: string;
  section: LabSection;
  status: LabOrderItemStatus;
  priority: OrderPriority;
  sampleId: string | null;
  serviceItemId: string | null;
  chargeId: string | null;
  chargeError: string | null;
  packageBookingId: string | null;
  quantity: number;
  referralLabId: string | null;
  sentOutAt: string | null;
  externalReportReceivedAt: string | null;
  verifiedAt: string | null;
  releasedAt: string | null;
  cancelReason: string | null;
}

export interface LabSampleDto {
  id: string;
  barcode: string;
  specimenType: SpecimenType;
  container: ContainerType;
  section: LabSection;
  priority: OrderPriority;
  status: SampleStatus;
  collectedAt: string | null;
  collectedBy: string | null;
  collectionSite: string | null;
  receivedAt: string | null;
  receivedBy: string | null;
  rejectionReason: SampleRejectionReason | null;
  rejectionRemarks: string | null;
  rejectedAt: string | null;
  recollectionOfSampleId: string | null;
  recollectedAsSampleId: string | null;
  tests: string[];
}

export interface LabOrderDetail {
  id: string;
  accessionNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  gender: Gender;
  age: string | null;
  visitId: string | null;
  admissionId: string | null;
  patientClass: PatientClass;
  source: LabOrderSource;
  sourceOrderNumber: string | null;
  priority: OrderPriority;
  status: LabOrderStatus;
  orderingPractitionerId: string | null;
  orderingDoctor: string | null;
  referringDoctorName: string | null;
  clinicalNotes: string | null;
  orderedAt: string;
  completedAt: string | null;
  reportId: string | null;
  reportStatus: LabReportStatus | null;
  items: LabOrderItemDto[];
  samples: LabSampleDto[];
  version: number;
}

export interface CreateLabOrderRequest {
  patientId: string;
  visitId: string | null;
  testIds: string[];
  priority: OrderPriority;
  source: 'WalkIn' | 'OutsideReferral';
  orderingPractitionerId: string | null;
  referringDoctorName: string | null;
  clinicalNotes: string | null;
}

export interface CreatedSample {
  id: string;
  barcode: string;
  container: ContainerType;
  section: LabSection;
  tests: string[];
}

export interface LabOrderCreated {
  id: string;
  accessionNumber: string;
  samples: CreatedSample[];
  chargeWarnings: string[];
}

export interface ChargeRetryOutcome {
  posted: number;
  failures: string[];
}

export interface PatientLabHistoryPoint {
  resultId: string;
  labOrderId: string;
  accessionNumber: string;
  resultAt: string;
  value: string | null;
  numericValue: number | null;
  unit: string | null;
  flag: ResultFlag | null;
  referenceText: string | null;
}

export interface PatientLabHistory {
  patientId: string;
  labTestId: string;
  testName: string;
  unit: string | null;
  points: PatientLabHistoryPoint[];
}

export interface LabTatRow {
  labTestId: string;
  testCode: string;
  testName: string;
  section: LabSection;
  targetHours: number;
  released: number;
  withinTarget: number;
  withinTargetPercent: number | null;
  orderToRelease: TatSummary;
  receiptToVerification: TatSummary;
}

export interface RejectionStat {
  reason: SampleRejectionReason;
  count: number;
}

export interface LabTatReport {
  from: string;
  to: string;
  overall: TatSummary;
  tests: LabTatRow[];
  samples: number;
  rejections: number;
  rejectionsByReason: RejectionStat[];
}

// ------------------------------------------------------------------------------------------------ samples

export interface PendingCollectionItem {
  sampleId: string;
  barcode: string;
  labOrderId: string;
  accessionNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  gender: Gender;
  age: string | null;
  patientClass: PatientClass;
  priority: OrderPriority;
  container: ContainerType;
  specimenType: SpecimenType;
  section: LabSection;
  tests: string[];
  preparation: string[];
  orderedAt: string;
  isRecollection: boolean;
}

export interface CollectSamplesRequest {
  sampleIds: string[] | null;
  collectedAt: string | null;
  collectionSite: string | null;
  /** For HIV tests on the tubes: on what each is done, recorded before its sample is drawn. */
  consents?: TestConsentInput[] | null;
}

/** On what an HIV test is done (HIV and AIDS Act 2017, s.5 and 6). */
export type ConsentBasis = 'Patient' | 'Representative' | 'CourtOrder';

/** The consent to an HIV test, after pre-test counselling: the signed form's number, or the court order's. */
export interface TestConsentInput {
  itemId: string;
  basis: ConsentBasis;
  givenBy: string | null;
  relation: string | null;
  reference: string | null;
}

/** An HIV test waiting for the patient's consent before its sample is drawn (the server's `lab.consent_required`). */
export interface ConsentTest {
  itemId: string;
  name: string;
  /** The tube the test is drawn in. */
  sampleId: string;
}

/** Data for the 50 × 25 mm barcode label. */
export interface SampleLabel {
  sampleId: string;
  barcode: string;
  accessionNumber: string;
  uhid: string;
  patientName: string;
  ageSex: string;
  container: ContainerType;
  specimenType: SpecimenType;
  section: LabSection;
  priority: OrderPriority;
  tests: string[];
  collectedAt: string | null;
}

export interface ReceiveOutcome {
  barcode: string;
  received: boolean;
  sampleId: string | null;
  message: string | null;
}

export interface RejectSampleRequest {
  reason: SampleRejectionReason;
  remarks: string | null;
  recollect: boolean;
}

export interface SampleRejected {
  sampleId: string;
  recollectionSampleId: string | null;
  recollectionBarcode: string | null;
}

export interface SampleLookup {
  id: string;
  barcode: string;
  status: SampleStatus;
  labOrderId: string;
  accessionNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  ageSex: string;
  container: ContainerType;
  specimenType: SpecimenType;
  section: LabSection;
  priority: OrderPriority;
  tests: string[];
  collectedAt: string | null;
  receivedAt: string | null;
  rejectionReason: SampleRejectionReason | null;
}

/** Unpaid test listed by the collection payment gate (Problem Details `data.tests`). */
export interface UnpaidTest {
  reference: string;
  name: string;
  state: string;
}

// ------------------------------------------------------------------------------------------------ results

export interface WorklistItem {
  sampleId: string;
  barcode: string;
  labOrderId: string;
  accessionNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  age: string | null;
  gender: Gender;
  patientClass: PatientClass;
  priority: OrderPriority;
  section: LabSection;
  status: SampleStatus;
  receivedAt: string | null;
  pending: number;
  entered: number;
  verified: number;
  critical: number;
  tests: string[];
}

export interface SusceptibilityDto {
  antibiotic: string;
  interpretation: SusceptibilityInterpretation;
  mic: string | null;
}

export interface ResultSheetLine {
  resultId: string;
  labOrderItemId: string;
  orderedTest: string;
  labTestId: string;
  testCode: string;
  testName: string;
  groupName: string | null;
  displayOrder: number;
  resultType: LabResultType;
  status: LabResultStatus;
  value: string | null;
  unit: string | null;
  decimals: number;
  options: string[];
  flag: ResultFlag | null;
  isCritical: boolean;
  referenceText: string | null;
  previousValue: number | null;
  previousResultAt: string | null;
  deltaPercent: number | null;
  deltaExceeded: boolean;
  autoValidated: boolean;
  instrumentCode: string | null;
  /** What the analyser said with its value: its unit, flags, result status (F final, P preliminary…) and comment. */
  instrumentUnit: string | null;
  instrumentFlags: string | null;
  instrumentStatus: string | null;
  instrumentComment: string | null;
  /** Why the analyser's result was not auto-verified, as the server found it when the result came. */
  heldReasons: string[];
  method: string | null;
  remarks: string | null;
  organism: string | null;
  colonyCount: string | null;
  susceptibilities: SusceptibilityDto[];
  enteredAt: string | null;
  verifiedAt: string | null;
  hasCriticalCall: boolean;
  version: number;
}

export interface ResultSheet {
  sampleId: string;
  barcode: string;
  sampleStatus: SampleStatus;
  labOrderId: string;
  accessionNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  ageSex: string;
  clinicalNotes: string | null;
  lines: ResultSheetLine[];
}

export interface ResultEntry {
  resultId: string;
  value: string | null;
  remarks: string | null;
  organism: string | null;
  colonyCount: string | null;
  susceptibilities: SusceptibilityDto[] | null;
}

export interface VerifyResultsRequest {
  resultIds: string[];
  qcOverrideReason: string | null;
  criticalCall: CriticalCallDetails | null;
  release: boolean;
}

export interface VerificationOutcome {
  verified: number;
  criticalCallsRecorded: number;
  qcOverrides: number;
  releasedItemIds: string[];
}

/**
 * A result an analyser sent that could not go on a sample's test, kept for the lab to assign to the right test or to
 * discard with a reason. The patient is known only when the barcode is a sample of this facility.
 */
export interface UnmatchedResultDto {
  id: string;
  instrumentCode: string;
  sampleBarcode: string | null;
  sampleId: string | null;
  patientId: string | null;
  uhid: string | null;
  patientName: string | null;
  /** The sample's result the value was for, when the server found one (a verified result it differs from…). */
  labResultId: string | null;
  testName: string | null;
  channelCode: string;
  value: string;
  instrumentUnit: string | null;
  instrumentFlags: string | null;
  instrumentStatus: string | null;
  instrumentComment: string | null;
  resultedAt: string | null;
  receivedAt: string;
  reason: UnmatchedResultReason;
  detail: string;
  status: UnmatchedResultStatus;
  assignedResultId: string | null;
  discardReason: string | null;
  resolvedBy: string | null;
  resolvedAt: string | null;
}

/** `data.lockouts` of a 422 lab.qc_lockout refusal. */
export interface QcLockout {
  labTestId: string;
  testName: string;
  rejectedRunIds: string[];
  violations: string[];
}

/** `data.results` of a 422 lab.critical_call_required refusal. */
export interface CriticalCallRequiredResult {
  id: string;
  testName: string;
  value: string;
  flag: ResultFlag | null;
}

// ------------------------------------------------------------------------------------------------ reports

export interface LabReportListItem {
  id: string;
  reportNumber: string;
  labOrderId: string;
  patientId: string;
  uhid: string;
  patientName: string;
  status: LabReportStatus;
  isPartial: boolean;
  revision: number;
  orderedAt: string;
  releasedAt: string | null;
}

export interface ReleaseOutcome {
  reportId: string;
  releasedItemIds: string[];
  isPartial: boolean;
  verificationCode: string | null;
}

export interface AmendmentChange {
  resultId: string;
  value: string;
  remarks: string | null;
  organism: string | null;
  colonyCount: string | null;
}

export interface AmendReportRequest {
  reason: string;
  changes: AmendmentChange[];
  criticalCall: CriticalCallDetails | null;
}

export interface LabReportResultLine {
  resultId: string;
  name: string;
  groupName: string | null;
  value: string | null;
  unit: string | null;
  flag: ResultFlag | null;
  referenceText: string | null;
  status: LabResultStatus;
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
  status: LabOrderItemStatus;
  specimenType: SpecimenType;
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
  /** Who released the correction. */
  amendedBy: string | null;
  /**
   * The pathologist who asked for it, where a second pathologist approved it (the lab requires a separate verifier);
   * null when the one who made it released it.
   */
  requestedBy?: string | null;
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
  patientClass: PatientClass;
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
  /** Server-relative verification API; resolves only on the hospital network. */
  verificationPath: string | null;
  /** Absolute portal verification page for the QR code; null until the public address (`portal.baseUrl`) is configured. */
  verificationUrl: string | null;
  /**
   * When a correction of the released report was asked for that still waits for a second pathologist's approval: its
   * results may change. Null when none waits.
   */
  amendmentWaitingSince?: string | null;
}

// ------------------------------------------------------------------------------------------------ quality control

export interface QcLotDto {
  id: string;
  labTestId: string;
  testCode: string;
  testName: string;
  material: string;
  level: number;
  lotNumber: string;
  expiryDate: string;
  targetMean: number;
  targetSd: number;
  unit: string | null;
  instrumentCode: string | null;
  isActive: boolean;
  isExpired: boolean;
  version: number;
}

export interface SaveQcLotRequest {
  labTestId: string;
  material: string;
  level: number;
  lotNumber: string;
  expiryDate: string;
  targetMean: number;
  targetSd: number;
  unit: string | null;
  instrumentCode: string | null;
  isActive: boolean;
  version: number | null;
}

export interface QcRunDto {
  id: string;
  qcLotId: string;
  labTestId: string;
  lotNumber: string;
  level: number;
  instrumentCode: string | null;
  value: number;
  zScore: number;
  runAt: string;
  outcome: QcOutcome;
  violations: string[];
  recordedBy: string | null;
  reviewedAt: string | null;
  reviewedBy: string | null;
  reviewNote: string | null;
  correctiveAction: string | null;
  overridden: boolean;
}

export interface LimitLine {
  label: string;
  value: number;
}

export interface LeveyJenningsPoint {
  runId: string;
  runAt: string;
  value: number;
  zScore: number;
  outcome: QcOutcome;
  violations: string[];
}

export interface LeveyJenningsChartDto {
  lotId: string;
  testName: string;
  material: string;
  level: number;
  lotNumber: string;
  unit: string | null;
  mean: number;
  sd: number;
  limits: LimitLine[];
  points: LeveyJenningsPoint[];
}

export interface QcStatusRow {
  labTestId: string;
  testCode: string;
  testName: string;
  lotId: string;
  lotNumber: string;
  level: number;
  instrumentCode: string | null;
  latestOutcome: QcOutcome | null;
  latestRunAt: string | null;
  locked: boolean;
  overridden: boolean;
}

// ------------------------------------------------------------------------------------------------ outsourcing

export interface OutsourcedItem {
  itemId: string;
  labOrderId: string;
  accessionNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  testName: string;
  status: LabOrderItemStatus;
  sampleId: string | null;
  barcode: string | null;
  referralLabId: string | null;
  referralLabName: string | null;
  collectedAt: string | null;
  sentOutAt: string | null;
  dispatchReference: string | null;
  externalReportReceivedAt: string | null;
  dueAt: string | null;
  isOverdue: boolean;
}

// ------------------------------------------------------------------------------------------------ filters

export interface LabOrderFilters {
  from?: string;
  to?: string;
  status?: LabOrderStatus;
  priority?: OrderPriority;
  patientClass?: PatientClass;
  patientId?: string;
  search?: string;
  page?: number;
  sort?: 'orderedAt' | 'accessionNumber' | 'priority' | 'status';
  desc?: boolean;
}

export interface PendingCollectionFilters {
  section?: LabSection;
  priority?: OrderPriority;
  patientClass?: PatientClass;
  search?: string;
  page?: number;
}

export interface UnmatchedResultFilters {
  status?: UnmatchedResultStatus;
  search?: string;
  page?: number;
}

export interface WorklistFilters {
  section?: LabSection;
  status?: SampleStatus;
  priority?: OrderPriority;
  search?: string;
  page?: number;
}

export interface ReportFilters {
  status?: LabReportStatus;
  from?: string;
  to?: string;
  search?: string;
  page?: number;
}

export interface TestSearch {
  search?: string;
  section?: LabSection;
  orderable?: boolean;
  includeInactive?: boolean;
  page?: number;
  pageSize?: number;
}

// ------------------------------------------------------------------------------------------------ queries

const PAGE = 25;

/** Query keys: every key starts with 'lab' and the area, so realtime pushes can refresh one area at a time. */
export const labKeys = {
  all: ['lab'] as const,
  masters: ['lab', 'masters'] as const,
  orders: ['lab', 'orders'] as const,
  samples: ['lab', 'samples'] as const,
  worklist: ['lab', 'worklist'] as const,
  sheets: ['lab', 'sheet'] as const,
  reports: ['lab', 'reports'] as const,
  qc: ['lab', 'qc'] as const,
  outsourcing: ['lab', 'outsourcing'] as const,
  connections: ['lab', 'connections'] as const,
  unmatched: ['lab', 'unmatched'] as const,
};

/** How often a report with a correction waiting for approval is asked for again. */
export const WAITING_REFRESH_MS = 30_000;

export const labQueries = {
  tests: (params: TestSearch) =>
    queryOptions({
      queryKey: ['lab', 'masters', 'tests', params],
      queryFn: ({ signal }) =>
        api.get<PagedResult<LabTestListItem>>('/api/v1/lab/tests', {
          query: { ...params, pageSize: params.pageSize ?? PAGE },
          signal,
        }),
      placeholderData: keepPreviousData,
      staleTime: 5 * 60_000,
    }),
  test: (id: string) =>
    queryOptions({
      queryKey: ['lab', 'masters', 'test', id],
      queryFn: ({ signal }) => api.get<LabTestDetail>(`/api/v1/lab/tests/${id}`, { signal }),
      staleTime: 10 * 60_000,
    }),
  instruments: (includeInactive = false) =>
    queryOptions({
      queryKey: ['lab', 'masters', 'instruments', includeInactive],
      queryFn: ({ signal }) =>
        api.get<LabInstrumentDto[]>('/api/v1/lab/instruments', { query: { includeInactive }, signal }),
      staleTime: 10 * 60_000,
    }),
  connections: () =>
    queryOptions({
      queryKey: labKeys.connections,
      queryFn: ({ signal }) => api.get<LabConnectionsDto>('/api/v1/integrations/lab/connections', { signal }),
      refetchInterval: 30_000,
    }),
  referralLabs: (includeInactive = false) =>
    queryOptions({
      queryKey: ['lab', 'masters', 'referral-labs', includeInactive],
      queryFn: ({ signal }) =>
        api.get<ReferralLabDto[]>('/api/v1/lab/referral-labs', { query: { includeInactive }, signal }),
      staleTime: 10 * 60_000,
    }),
  orders: (filters: LabOrderFilters) =>
    queryOptions({
      queryKey: ['lab', 'orders', 'list', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<LabOrderListItem>>('/api/v1/lab/orders', { query: { ...filters, pageSize: PAGE }, signal }),
      placeholderData: keepPreviousData,
    }),
  order: (id: string) =>
    queryOptions({
      queryKey: ['lab', 'orders', 'detail', id],
      queryFn: ({ signal }) => api.get<LabOrderDetail>(`/api/v1/lab/orders/${id}`, { signal }),
    }),
  history: (patientId: string, labTestId?: string) =>
    queryOptions({
      queryKey: ['lab', 'orders', 'history', patientId, labTestId ?? 'all'],
      queryFn: ({ signal }) =>
        api.get<PatientLabHistory[]>(`/api/v1/lab/patients/${patientId}/history`, { query: { labTestId }, signal }),
    }),
  pendingCollection: (filters: PendingCollectionFilters, pageSize = 100) =>
    queryOptions({
      queryKey: ['lab', 'samples', 'pending-collection', filters, pageSize],
      queryFn: ({ signal }) =>
        api.get<PagedResult<PendingCollectionItem>>('/api/v1/lab/samples/pending-collection', {
          query: { ...filters, pageSize },
          signal,
        }),
      placeholderData: keepPreviousData,
      refetchInterval: 60_000,
    }),
  sampleByBarcode: (barcode: string) =>
    queryOptions({
      queryKey: ['lab', 'samples', 'barcode', barcode],
      queryFn: ({ signal }) =>
        api.get<SampleLookup>(`/api/v1/lab/samples/by-barcode/${encodeURIComponent(barcode)}`, { signal }),
      staleTime: 15_000,
    }),
  worklist: (filters: WorklistFilters, pageSize = PAGE) =>
    queryOptions({
      queryKey: ['lab', 'worklist', filters, pageSize],
      queryFn: ({ signal }) =>
        api.get<PagedResult<WorklistItem>>('/api/v1/lab/worklist', { query: { ...filters, pageSize }, signal }),
      placeholderData: keepPreviousData,
      refetchInterval: 60_000,
    }),
  /** Analyser results waiting to be matched (or those assigned or discarded); the count is the lab's to-do. */
  unmatchedResults: (filters: UnmatchedResultFilters, pageSize = PAGE) =>
    queryOptions({
      queryKey: ['lab', 'unmatched', filters, pageSize],
      queryFn: ({ signal }) =>
        api.get<PagedResult<UnmatchedResultDto>>('/api/v1/lab/unmatched-results', {
          query: { ...filters, pageSize },
          signal,
        }),
      placeholderData: keepPreviousData,
      refetchInterval: 60_000,
    }),
  sheet: (sampleId: string) =>
    queryOptions({
      queryKey: ['lab', 'sheet', sampleId],
      queryFn: ({ signal }) => api.get<ResultSheet>(`/api/v1/lab/samples/${sampleId}/results`, { signal }),
      // Result entry must not be interrupted by background refetches that re-render the grid while typing.
      refetchOnWindowFocus: false,
    }),
  reports: (filters: ReportFilters) =>
    queryOptions({
      queryKey: ['lab', 'reports', 'list', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<LabReportListItem>>('/api/v1/lab/reports', {
          query: { ...filters, pageSize: PAGE },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  report: (id: string) =>
    queryOptions({
      queryKey: ['lab', 'reports', 'detail', id],
      queryFn: ({ signal }) => api.get<LabReportView>(`/api/v1/lab/reports/${id}`, { signal }),
      // While a correction waits for approval, ask again every 30 s. Its rejection, withdrawal or lapse, on any
      // workstation, sends no lab event, and the page must offer Amend again once a correction may be asked for.
      refetchInterval: (query) => (query.state.data?.amendmentWaitingSince ? WAITING_REFRESH_MS : false),
    }),
  tat: (from?: string, to?: string, section?: LabSection) =>
    queryOptions({
      queryKey: ['lab', 'tat', from ?? 'today', to ?? 'today', section ?? 'all'],
      queryFn: ({ signal }) => api.get<LabTatReport>('/api/v1/lab/tat', { query: { from, to, section }, signal }),
      refetchInterval: 5 * 60_000,
    }),
  criticalCalls: (params: { from?: string; to?: string; kind?: DiagnosticKind; page?: number }) =>
    queryOptions({
      queryKey: ['lab', 'critical-calls', params],
      queryFn: ({ signal }) =>
        api.get<PagedResult<CriticalCallDto>>('/api/v1/lab/critical-calls', {
          query: { ...params, pageSize: PAGE },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  qcLots: (includeInactive = false, labTestId?: string) =>
    queryOptions({
      queryKey: ['lab', 'qc', 'lots', includeInactive, labTestId ?? 'all'],
      queryFn: ({ signal }) =>
        api.get<QcLotDto[]>('/api/v1/lab/qc/lots', { query: { includeInactive, labTestId }, signal }),
    }),
  qcStatus: () =>
    queryOptions({
      queryKey: ['lab', 'qc', 'status'],
      queryFn: ({ signal }) => api.get<QcStatusRow[]>('/api/v1/lab/qc/status', { signal }),
      refetchInterval: 2 * 60_000,
    }),
  qcRuns: (params: {
    lotId?: string;
    labTestId?: string;
    outcome?: QcOutcome;
    from?: string;
    to?: string;
    page?: number;
  }) =>
    queryOptions({
      queryKey: ['lab', 'qc', 'runs', params],
      queryFn: ({ signal }) =>
        api.get<PagedResult<QcRunDto>>('/api/v1/lab/qc/runs', { query: { ...params, pageSize: PAGE }, signal }),
      placeholderData: keepPreviousData,
    }),
  leveyJennings: (lotId: string, from?: string, to?: string) =>
    queryOptions({
      queryKey: ['lab', 'qc', 'levey-jennings', lotId, from ?? '', to ?? ''],
      queryFn: ({ signal }) =>
        api.get<LeveyJenningsChartDto>(`/api/v1/lab/qc/lots/${lotId}/levey-jennings`, { query: { from, to }, signal }),
    }),
  outsourcing: (params: { pendingOnly: boolean; referralLabId?: string; page?: number }) =>
    queryOptions({
      queryKey: ['lab', 'outsourcing', params],
      queryFn: ({ signal }) =>
        api.get<PagedResult<OutsourcedItem>>('/api/v1/lab/outsourcing', {
          query: { ...params, pageSize: PAGE },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
};

// ------------------------------------------------------------------------------------------------ commands

/** 201 Created body of the master endpoints. */
export interface Created {
  id: string;
}

export const labApi = {
  // orders
  createOrder: (request: CreateLabOrderRequest) => api.post<LabOrderCreated>('/api/v1/lab/orders', request),
  retryCharges: (orderId: string) => api.post<ChargeRetryOutcome>(`/api/v1/lab/orders/${orderId}/charges/retry`),
  cancelItem: (itemId: string, reason: string) =>
    api.post<void>(`/api/v1/lab/orders/items/${itemId}/cancel`, { reason }),

  // samples
  collect: (orderId: string, request: CollectSamplesRequest) =>
    api.post<SampleLabel[]>(`/api/v1/lab/orders/${orderId}/collect`, request),
  labels: (orderId: string, sampleId?: string) =>
    api.get<SampleLabel[]>(`/api/v1/lab/orders/${orderId}/labels`, { query: { sampleId } }),
  receive: (barcodes: string[]) => api.post<ReceiveOutcome[]>('/api/v1/lab/samples/receive', { barcodes }),
  reject: (sampleId: string, request: RejectSampleRequest) =>
    api.post<SampleRejected>(`/api/v1/lab/samples/${sampleId}/reject`, request),
  recollect: (sampleId: string) => api.post<SampleRejected>(`/api/v1/lab/samples/${sampleId}/recollect`),
  sampleByBarcode: (barcode: string) =>
    api.get<SampleLookup>(`/api/v1/lab/samples/by-barcode/${encodeURIComponent(barcode)}`),

  // results
  enterResults: (entries: ResultEntry[]) => api.post<ResultSheetLine[]>('/api/v1/lab/results', { entries }),
  verifyResults: (request: VerifyResultsRequest) =>
    api.post<VerificationOutcome>('/api/v1/lab/results/verify', request),
  reopenResult: (resultId: string, reason: string) =>
    api.post<void>(`/api/v1/lab/results/${resultId}/reopen`, { reason }),
  recordCriticalCall: (resultId: string, call: CriticalCallDetails) =>
    api.post<string>(`/api/v1/lab/results/${resultId}/critical-calls`, call),
  sheet: (sampleId: string) => api.get<ResultSheet>(`/api/v1/lab/samples/${sampleId}/results`),
  /** Puts an analyser's unmatched result on a sample's test, where a person verifies it. */
  assignUnmatched: (id: string, resultId: string) =>
    api.post<void>(`/api/v1/lab/unmatched-results/${id}/assign`, { resultId }),
  discardUnmatched: (id: string, reason: string) =>
    api.post<void>(`/api/v1/lab/unmatched-results/${id}/discard`, { reason }),

  // reports
  releaseReport: (reportId: string) => api.post<ReleaseOutcome>(`/api/v1/lab/reports/${reportId}/release`),
  /**
   * Corrects released results at once (204, nothing returned), or, where the lab requires a separate verifier, asks for
   * the correction (202): it waits for another pathologist's approval in the approvals inbox.
   */
  amendReport: (reportId: string, request: AmendReportRequest) =>
    api.post<ApprovalRequestDto | undefined>(`/api/v1/lab/reports/${reportId}/amend`, request),

  // quality control
  saveQcLot: (id: string | null, request: SaveQcLotRequest): Promise<Created | undefined> =>
    id ? api.put<undefined>(`/api/v1/lab/qc/lots/${id}`, request) : api.post<Created>('/api/v1/lab/qc/lots', request),
  recordQcRun: (lotId: string, value: number, runAt: string | null) =>
    api.post<QcRunDto>('/api/v1/lab/qc/runs', { lotId, value, runAt }),
  reviewQcRun: (runId: string, note: string | null, correctiveAction: string | null) =>
    api.post<QcRunDto>(`/api/v1/lab/qc/runs/${runId}/review`, { note, correctiveAction }),
  overrideQc: (labTestId: string, reason: string) =>
    api.post<{ overrides: number }>('/api/v1/lab/qc/overrides', { labTestId, reason }),

  // outsourcing
  sendOut: (itemId: string, referralLabId: string, dispatchReference: string | null) =>
    api.post<void>(`/api/v1/lab/outsourcing/items/${itemId}/send`, { referralLabId, dispatchReference }),
  receiveExternalReport: (itemId: string, externalReference: string | null, reportFileId: string | null) =>
    api.post<void>(`/api/v1/lab/outsourcing/items/${itemId}/receive`, { externalReference, reportFileId }),

  // masters
  saveTest: (id: string | null, request: SaveLabTestRequest): Promise<Created | undefined> =>
    id ? api.put<undefined>(`/api/v1/lab/tests/${id}`, request) : api.post<Created>('/api/v1/lab/tests', request),
  setComponents: (id: string, components: ComponentEntry[], version: number) =>
    api.put<void>(`/api/v1/lab/tests/${id}/components`, { components, version }),
  setRanges: (id: string, ranges: ReferenceRangeDto[], version: number) =>
    api.put<void>(`/api/v1/lab/tests/${id}/ranges`, { ranges, version }),
  saveInstrument: (id: string | null, request: SaveLabInstrumentRequest): Promise<Created | undefined> =>
    id
      ? api.put<undefined>(`/api/v1/lab/instruments/${id}`, request)
      : api.post<Created>('/api/v1/lab/instruments', request),
  saveReferralLab: (id: string | null, request: SaveReferralLabRequest): Promise<Created | undefined> =>
    id
      ? api.put<undefined>(`/api/v1/lab/referral-labs/${id}`, request)
      : api.post<Created>('/api/v1/lab/referral-labs', request),

  // analyser connections (Integrations)
  saveConnection: (code: string, request: SaveLabConnectionRequest) =>
    api.put<void>(`/api/v1/integrations/lab/connections/${encodeURIComponent(code)}`, request),
  removeConnection: (code: string, listVersion: number) =>
    api.delete<void>(`/api/v1/integrations/lab/connections/${encodeURIComponent(code)}`, {
      query: { version: listVersion },
    }),
  /** The training analyser: the tests a received sample still needs on it. */
  trainingSample: (code: string, barcode: string) =>
    api.get<TrainingSampleDto>(
      `/api/v1/integrations/lab/connections/${encodeURIComponent(code)}/training/samples/${encodeURIComponent(barcode)}`,
    ),
  /** The training analyser: sends the results as the analyser would, and says whether the LIS took them. */
  sendTrainingResults: (code: string, request: TrainingResultsRequest) =>
    api.post<TrainingResultsOutcome>(
      `/api/v1/integrations/lab/connections/${encodeURIComponent(code)}/training/results`,
      request,
    ),
};

/** A sample as the training analyser sees it: whose it is, and the analyser's channel codes it still needs. */
export interface TrainingSampleDto {
  sampleBarcode: string;
  patientName: string;
  patientId: string | null;
  channels: string[];
}

export interface TrainingResultsRequest {
  sampleBarcode: string;
  results: { channel: string; value: string; units: string | null }[];
}

export interface TrainingResultsOutcome {
  accepted: boolean;
  message: string;
  rawMessage: string;
}

/** The A4 lab report as the server prints it and makes its PDF (see `@/lib/documents`). */
export const labReportDocument = (reportId: string) => `/api/v1/lab/reports/${reportId}`;

/** A released lab report as an ABDM health record (FHIR DiagnosticReport document, SRS EHR-08). */
export const abdmLabReportBundle = (reportId: string) => `/api/v1/abdm/bundles/lab-reports/${reportId}`;

/** Permission names used by the lab screens (the server enforces them again on every call). */
export const LAB_PERMISSIONS = {
  read: 'lab.orders.read',
  create: 'lab.orders.create',
  collect: 'lab.samples.collect',
  receive: 'lab.samples.receive',
  enter: 'lab.results.enter',
  verify: 'lab.results.verify',
  qc: 'lab.qc.manage',
  masters: 'lab.masters.manage',
  /** Set up how analysers connect (network ports, serial ports through lab agents). */
  connections: 'integrations.lab.connections',
  /** See whether analysers and their lab agents are connected. */
  connectionStatus: 'integrations.lab.status',
} as const;
