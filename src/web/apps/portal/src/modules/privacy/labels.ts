import type { FluentIcon } from '@fluentui/react-icons';
import { ChatWarningRegular, EraserRegular, FolderPersonRegular, PersonEditRegular } from '@fluentui/react-icons';
import type { Tone } from '@/components/ListRow';
import type { MessageKey } from '@/i18n';
import type {
  BreachDataCategory,
  CorrectableField,
  CorrectionDecision,
  DataRequestEventKind,
  DataRequestStatus,
  DataRequestType,
  ErasureItem,
  PortalRequester,
  PortalRequestType,
  RequesterRelation,
} from '@/lib/types';

/** The requests a patient starts from the portal, as they appear in the address (`?type=correction`). */
export const REQUEST_KINDS = ['correction', 'erasure', 'grievance'] as const;
export type RequestKind = (typeof REQUEST_KINDS)[number];

export const KIND_TYPE: Record<RequestKind, PortalRequestType> = {
  correction: 'Correction',
  erasure: 'Erasure',
  grievance: 'Grievance',
};

/** What each kind of request is called where the patient starts it (the hub's cards and the form's title). */
export const KIND_LABEL: Record<RequestKind, { title: MessageKey; hint: MessageKey; icon: FluentIcon }> = {
  correction: { title: 'myPrivacy.hub.correct', hint: 'myPrivacy.hub.correct.hint', icon: PersonEditRegular },
  erasure: { title: 'myPrivacy.hub.erase', hint: 'myPrivacy.hub.erase.hint', icon: EraserRegular },
  grievance: { title: 'myPrivacy.hub.concern', hint: 'myPrivacy.hub.concern.hint', icon: ChatWarningRegular },
};

export const REQUEST_TYPE: Record<DataRequestType, { key: MessageKey; icon: FluentIcon }> = {
  Access: { key: 'myPrivacy.type.Access', icon: FolderPersonRegular },
  Correction: { key: 'myPrivacy.type.Correction', icon: PersonEditRegular },
  Erasure: { key: 'myPrivacy.type.Erasure', icon: EraserRegular },
  Grievance: { key: 'myPrivacy.type.Grievance', icon: ChatWarningRegular },
};

export const REQUEST_STATUS: Record<DataRequestStatus, { key: MessageKey; tone: Tone }> = {
  Submitted: { key: 'myPrivacy.status.Submitted', tone: 'warning' },
  InProgress: { key: 'myPrivacy.status.InProgress', tone: 'brand' },
  Completed: { key: 'myPrivacy.status.Completed', tone: 'success' },
  PartlyCompleted: { key: 'myPrivacy.status.PartlyCompleted', tone: 'success' },
  Declined: { key: 'myPrivacy.status.Declined', tone: 'danger' },
  Withdrawn: { key: 'myPrivacy.status.Withdrawn', tone: 'neutral' },
};

export const DECISION: Record<CorrectionDecision, { key: MessageKey; tone: Tone }> = {
  Pending: { key: 'myPrivacy.decision.Pending', tone: 'warning' },
  Accepted: { key: 'myPrivacy.decision.Accepted', tone: 'success' },
  Declined: { key: 'myPrivacy.decision.Declined', tone: 'danger' },
};

export const REQUESTER: Record<RequesterRelation, MessageKey> = {
  Self: 'myPrivacy.requester.Self',
  ParentOrGuardian: 'myPrivacy.requester.ParentOrGuardian',
  Nominee: 'myPrivacy.requester.Nominee',
  FamilyMember: 'myPrivacy.requester.FamilyMember',
  AuthorisedRepresentative: 'myPrivacy.requester.AuthorisedRepresentative',
};

/** "{name}, as a family member": who asked, when it was not the patient and the request names them. */
export const REQUESTER_NAMED: Record<Exclude<RequesterRelation, 'Self'>, MessageKey> = {
  ParentOrGuardian: 'myPrivacy.requester.named.ParentOrGuardian',
  Nominee: 'myPrivacy.requester.named.Nominee',
  FamilyMember: 'myPrivacy.requester.named.FamilyMember',
  AuthorisedRepresentative: 'myPrivacy.requester.named.AuthorisedRepresentative',
};

/** What an erasure removed, in the patient's words. */
export const ERASED_ITEM: Record<ErasureItem, MessageKey> = {
  messages: 'myPrivacy.erased.messages',
  feedback: 'myPrivacy.erased.feedback',
  nominee: 'myPrivacy.erased.nominee',
  portal: 'myPrivacy.erased.portal',
};

/** "Who is this request for?" in the form. `{name}` is the person whose records are being viewed. */
export const REQUESTER_CHOICE: Record<PortalRequester, { title: MessageKey; hint: MessageKey }> = {
  Self: { title: 'myPrivacy.new.who.Self', hint: 'myPrivacy.new.who.Self.hint' },
  ParentOrGuardian: { title: 'myPrivacy.new.who.ParentOrGuardian', hint: 'myPrivacy.new.who.ParentOrGuardian.hint' },
  FamilyMember: { title: 'myPrivacy.new.who.FamilyMember', hint: 'myPrivacy.new.who.FamilyMember.hint' },
};

/** The public history a patient sees. Staff-only kinds (assigned, notes, decisions) never reach the portal. */
export const EVENT_KIND: Partial<Record<DataRequestEventKind, MessageKey>> = {
  Received: 'myPrivacy.event.Received',
  IdentityVerified: 'myPrivacy.event.IdentityVerified',
  Started: 'myPrivacy.event.Started',
  Erased: 'myPrivacy.event.Erased',
  Closed: 'myPrivacy.event.Closed',
  Withdrawn: 'myPrivacy.event.Withdrawn',
};

export const BREACH_DATA: Record<BreachDataCategory, MessageKey> = {
  Identity: 'myPrivacy.breachData.Identity',
  Contact: 'myPrivacy.breachData.Contact',
  Health: 'myPrivacy.breachData.Health',
  Financial: 'myPrivacy.breachData.Financial',
  GovernmentId: 'myPrivacy.breachData.GovernmentId',
  Credentials: 'myPrivacy.breachData.Credentials',
  Children: 'myPrivacy.breachData.Children',
};

/** Consent types as the server names them (Patients `ConsentType`); an unknown type is shown as sent. */
export const CONSENT_TYPE: Readonly<Record<string, MessageKey>> = {
  GeneralTreatment: 'myPrivacy.consentType.GeneralTreatment',
  DataSharing: 'myPrivacy.consentType.DataSharing',
  Telehealth: 'myPrivacy.consentType.Telehealth',
  Research: 'myPrivacy.consentType.Research',
  SmsWhatsAppUpdates: 'myPrivacy.consentType.SmsWhatsAppUpdates',
  MarketingCommunication: 'myPrivacy.consentType.MarketingCommunication',
  Photography: 'myPrivacy.consentType.Photography',
  HighRiskProcedure: 'myPrivacy.consentType.HighRiskProcedure',
  Anaesthesia: 'myPrivacy.consentType.Anaesthesia',
  BloodTransfusion: 'myPrivacy.consentType.BloodTransfusion',
};

export interface CategoryKeys {
  title: MessageKey;
  description: MessageKey;
  purpose: MessageKey;
  basis: MessageKey;
}

/**
 * The data summary's groups by their stable server key (PersonalDataCatalogue). A group the portal does not know yet
 * keeps the server's English text.
 */
export const CATEGORY: Readonly<Record<string, CategoryKeys>> = {
  registration: {
    title: 'myPrivacy.category.registration.title',
    description: 'myPrivacy.category.registration.description',
    purpose: 'myPrivacy.category.registration.purpose',
    basis: 'myPrivacy.category.registration.basis',
  },
  visits: {
    title: 'myPrivacy.category.visits.title',
    description: 'myPrivacy.category.visits.description',
    purpose: 'myPrivacy.category.visits.purpose',
    basis: 'myPrivacy.category.visits.basis',
  },
  clinical: {
    title: 'myPrivacy.category.clinical.title',
    description: 'myPrivacy.category.clinical.description',
    purpose: 'myPrivacy.category.clinical.purpose',
    basis: 'myPrivacy.basis.records',
  },
  medicines: {
    title: 'myPrivacy.category.medicines.title',
    description: 'myPrivacy.category.medicines.description',
    purpose: 'myPrivacy.category.medicines.purpose',
    basis: 'myPrivacy.category.medicines.basis',
  },
  tests: {
    title: 'myPrivacy.category.tests.title',
    description: 'myPrivacy.category.tests.description',
    purpose: 'myPrivacy.category.tests.purpose',
    basis: 'myPrivacy.basis.care',
  },
  stays: {
    title: 'myPrivacy.category.stays.title',
    description: 'myPrivacy.category.stays.description',
    purpose: 'myPrivacy.category.stays.purpose',
    basis: 'myPrivacy.basis.records',
  },
  procedures: {
    title: 'myPrivacy.category.procedures.title',
    description: 'myPrivacy.category.procedures.description',
    purpose: 'myPrivacy.category.procedures.purpose',
    basis: 'myPrivacy.basis.records',
  },
  support: {
    title: 'myPrivacy.category.support.title',
    description: 'myPrivacy.category.support.description',
    purpose: 'myPrivacy.category.support.purpose',
    basis: 'myPrivacy.basis.care',
  },
  billing: {
    title: 'myPrivacy.category.billing.title',
    description: 'myPrivacy.category.billing.description',
    purpose: 'myPrivacy.category.billing.purpose',
    basis: 'myPrivacy.category.billing.basis',
  },
  virtual: {
    title: 'myPrivacy.category.virtual.title',
    description: 'myPrivacy.category.virtual.description',
    purpose: 'myPrivacy.category.virtual.purpose',
    basis: 'myPrivacy.category.virtual.basis',
  },
  communication: {
    title: 'myPrivacy.category.communication.title',
    description: 'myPrivacy.category.communication.description',
    purpose: 'myPrivacy.category.communication.purpose',
    basis: 'myPrivacy.category.communication.basis',
  },
  consents: {
    title: 'myPrivacy.category.consents.title',
    description: 'myPrivacy.category.consents.description',
    purpose: 'myPrivacy.category.consents.purpose',
    basis: 'myPrivacy.category.consents.basis',
  },
  documents: {
    title: 'myPrivacy.category.documents.title',
    description: 'myPrivacy.category.documents.description',
    purpose: 'myPrivacy.category.documents.purpose',
    basis: 'myPrivacy.basis.care',
  },
  account: {
    title: 'myPrivacy.category.account.title',
    description: 'myPrivacy.category.account.description',
    purpose: 'myPrivacy.category.account.purpose',
    basis: 'myPrivacy.category.account.basis',
  },
  safety: {
    title: 'myPrivacy.category.safety.title',
    description: 'myPrivacy.category.safety.description',
    purpose: 'myPrivacy.category.safety.purpose',
    basis: 'myPrivacy.category.safety.basis',
  },
  ai: {
    title: 'myPrivacy.category.ai.title',
    description: 'myPrivacy.category.ai.description',
    purpose: 'myPrivacy.category.ai.purpose',
    basis: 'myPrivacy.category.ai.basis',
  },
  'access-log': {
    title: 'myPrivacy.category.accessLog.title',
    description: 'myPrivacy.category.accessLog.description',
    purpose: 'myPrivacy.category.accessLog.purpose',
    basis: 'myPrivacy.category.accessLog.basis',
  },
  privacy: {
    title: 'myPrivacy.category.privacy.title',
    description: 'myPrivacy.category.privacy.description',
    purpose: 'myPrivacy.category.privacy.purpose',
    basis: 'myPrivacy.category.privacy.basis',
  },
};

/**
 * The label of each counted source of the catalogue (PersonalDataCatalogue), by its table: the server's `SummaryItem.key`.
 * Sources that share a label arrive under the first one's table; the other is listed too, in case the order changes.
 * An unknown table keeps the server's English label.
 */
export const ITEM_LABEL: Readonly<Record<string, MessageKey>> = {
  'patients.patient_identity_checks': 'myPrivacy.item.patients.patient_identity_checks',
  'patients.visits': 'myPrivacy.item.patients.visits',
  'scheduling.appointments': 'myPrivacy.item.scheduling.appointments',
  'scheduling.waitlist_entries': 'myPrivacy.item.scheduling.waitlist_entries',
  'patients.visitor_passes': 'myPrivacy.item.patients.visitor_passes',
  'clinical.encounters': 'myPrivacy.item.clinical.encounters',
  'clinical.clinical_notes': 'myPrivacy.item.clinical.clinical_notes',
  'clinical.diagnoses': 'myPrivacy.item.clinical.diagnoses',
  'clinical.allergies': 'myPrivacy.item.clinical.allergies',
  'clinical.immunizations': 'myPrivacy.item.clinical.immunizations',
  'clinical.vital_signs': 'myPrivacy.item.clinical.vital_signs',
  'clinical.patient_alerts': 'myPrivacy.item.clinical.patient_alerts',
  'clinical.clinical_orders': 'myPrivacy.item.clinical.clinical_orders',
  'clinical.package_bookings': 'myPrivacy.item.clinical.package_bookings',
  'clinical.discharge_summaries': 'myPrivacy.item.clinical.discharge_summaries',
  'clinical.medical_records': 'myPrivacy.item.clinical.medical_records',
  'clinical.record_requests': 'myPrivacy.item.clinical.record_requests',
  'interop.abdm_link_requests': 'myPrivacy.item.interop.abdm_link_requests',
  'interop.abdm_transfers': 'myPrivacy.item.interop.abdm_transfers',
  'clinical.prescriptions': 'myPrivacy.item.clinical.prescriptions',
  'pharmacy.sales': 'myPrivacy.item.pharmacy.sales',
  'pharmacy.refill_requests': 'myPrivacy.item.pharmacy.refill_requests',
  'pharmacy.ip_issues': 'myPrivacy.item.pharmacy.ip_issues',
  'pharmacy.controlled_drug_register': 'myPrivacy.item.pharmacy.controlled_drug_register',
  'clinical.controlled_drug_register': 'myPrivacy.item.pharmacy.controlled_drug_register',
  'diagnostics.lab_orders': 'myPrivacy.item.diagnostics.lab_orders',
  'diagnostics.lab_reports': 'myPrivacy.item.diagnostics.lab_reports',
  'diagnostics.imaging_studies': 'myPrivacy.item.diagnostics.imaging_studies',
  'diagnostics.radiology_reports': 'myPrivacy.item.diagnostics.radiology_reports',
  'diagnostics.cardiology_studies': 'myPrivacy.item.diagnostics.cardiology_studies',
  'diagnostics.cardiology_reports': 'myPrivacy.item.diagnostics.cardiology_reports',
  'diagnostics.critical_call_logs': 'myPrivacy.item.diagnostics.critical_call_logs',
  'inpatient.admissions': 'myPrivacy.item.inpatient.admissions',
  'inpatient.nursing_notes': 'myPrivacy.item.inpatient.nursing_notes',
  'inpatient.nursing_assessments': 'myPrivacy.item.inpatient.nursing_assessments',
  'inpatient.medication_orders': 'myPrivacy.item.inpatient.medication_orders',
  'inpatient.medication_administrations': 'myPrivacy.item.inpatient.medication_administrations',
  'inpatient.intake_output_entries': 'myPrivacy.item.inpatient.intake_output_entries',
  'specialtycare.surgery_bookings': 'myPrivacy.item.specialtycare.surgery_bookings',
  'specialtycare.admissions': 'myPrivacy.item.specialtycare.admissions',
  'specialtycare.blood_requests': 'myPrivacy.item.specialtycare.blood_requests',
  'specialtycare.blood_issues': 'myPrivacy.item.specialtycare.blood_issues',
  'specialtycare.transfusion_reactions': 'myPrivacy.item.specialtycare.transfusion_reactions',
  'specialtycare.dialysis_sessions': 'myPrivacy.item.specialtycare.dialysis_sessions',
  'specialtycare.physio_referrals': 'myPrivacy.item.specialtycare.physio_referrals',
  'supportservices.diet_orders': 'myPrivacy.item.supportservices.diet_orders',
  'billing.bills': 'myPrivacy.item.billing.bills',
  'billing.receipts': 'myPrivacy.item.billing.receipts',
  'billing.refunds': 'myPrivacy.item.billing.refunds',
  'billing.patient_advances': 'myPrivacy.item.billing.patient_advances',
  'billing.patient_policies': 'myPrivacy.item.billing.patient_policies',
  'billing.insurance_claims': 'myPrivacy.item.billing.insurance_claims',
  'billing.pre_authorizations': 'myPrivacy.item.billing.pre_authorizations',
  'virtualcare.telehealth_sessions': 'myPrivacy.item.virtualcare.telehealth_sessions',
  'virtualcare.rpm_enrollments': 'myPrivacy.item.virtualcare.rpm_enrollments',
  'virtualcare.rpm_readings': 'myPrivacy.item.virtualcare.rpm_readings',
  'platform.notification_logs': 'myPrivacy.item.platform.notification_logs',
  'engagement.follow_up_tasks': 'myPrivacy.item.engagement.follow_up_tasks',
  'engagement.feedback_responses': 'myPrivacy.item.engagement.feedback_responses',
  'engagement.complaints': 'myPrivacy.item.engagement.complaints',
  'engagement.campaign_recipients': 'myPrivacy.item.engagement.campaign_recipients',
  'patients.patient_consents': 'myPrivacy.item.patients.patient_consents',
  'patients.patient_documents': 'myPrivacy.item.patients.patient_documents',
  'identity.users': 'myPrivacy.item.identity.users',
  'patients.portal_family_links': 'myPrivacy.item.patients.portal_family_links',
  'quality.incidents': 'myPrivacy.item.quality.incidents',
  'ai.usage_records': 'myPrivacy.item.ai.usage_records',
  'audit.audit_events': 'myPrivacy.item.audit.audit_events',
  'privacy.data_requests': 'myPrivacy.item.privacy.data_requests',
  'privacy.nominees': 'myPrivacy.item.privacy.nominees',
  'privacy.breach_affected_patients': 'myPrivacy.item.privacy.breach_affected_patients',
};

export const FIELD_LABEL: Record<CorrectableField, MessageKey> = {
  name: 'myPrivacy.field.name',
  dateOfBirth: 'myPrivacy.field.dateOfBirth',
  gender: 'myPrivacy.field.gender',
  mobile: 'myPrivacy.field.mobile',
  alternatePhone: 'myPrivacy.field.alternatePhone',
  email: 'myPrivacy.field.email',
  address: 'myPrivacy.field.address',
  emergencyContact: 'myPrivacy.field.emergencyContact',
  guardian: 'myPrivacy.field.guardian',
  language: 'myPrivacy.field.language',
  healthRecord: 'myPrivacy.field.healthRecord',
  other: 'myPrivacy.field.other',
};

/** How the correct value is asked for. */
export type FieldInput = 'text' | 'date' | 'gender' | 'mobile' | 'phone' | 'email' | 'long';

export const FIELD_FORM: Record<CorrectableField, { input: FieldInput; label: MessageKey; hint?: MessageKey }> = {
  name: { input: 'text', label: 'myPrivacy.fieldValue.name' },
  dateOfBirth: { input: 'date', label: 'myPrivacy.fieldValue.dateOfBirth' },
  gender: { input: 'gender', label: 'myPrivacy.fieldValue.gender' },
  mobile: { input: 'mobile', label: 'myPrivacy.fieldValue.mobile' },
  alternatePhone: { input: 'phone', label: 'myPrivacy.fieldValue.alternatePhone' },
  email: { input: 'email', label: 'myPrivacy.fieldValue.email' },
  address: { input: 'long', label: 'myPrivacy.fieldValue.address', hint: 'myPrivacy.fieldHint.address' },
  emergencyContact: {
    input: 'text',
    label: 'myPrivacy.fieldValue.emergencyContact',
    hint: 'myPrivacy.fieldHint.emergencyContact',
  },
  guardian: { input: 'text', label: 'myPrivacy.fieldValue.guardian', hint: 'myPrivacy.fieldHint.guardian' },
  language: { input: 'text', label: 'myPrivacy.fieldValue.language', hint: 'myPrivacy.fieldHint.language' },
  healthRecord: {
    input: 'long',
    label: 'myPrivacy.fieldValue.healthRecord',
    hint: 'myPrivacy.fieldHint.healthRecord',
  },
  other: { input: 'long', label: 'myPrivacy.fieldValue.other' },
};
