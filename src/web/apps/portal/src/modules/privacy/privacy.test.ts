import { ApiError } from '@hc360/core';
import { describe, expect, it } from 'vitest';
import { createTranslator } from '@/i18n';
import { DICTIONARIES } from '@/i18n/dictionary';
import { formattersFor } from '@/i18n/useFormat';
import type { DataSummary, RetentionPeriods, SummaryCategory } from '@/lib/types';
import { CATEGORY, CONSENT_TYPE, ITEM_LABEL } from './labels';
import {
  awaitsRequesterCheck,
  buildNomineeRequest,
  buildRequest,
  categoryView,
  checkNominee,
  checkRequest,
  consentView,
  contactTitle,
  correctionValue,
  emptyRequestForm,
  erasureOutcome,
  eventView,
  fieldLabel,
  formatPhone,
  isEmail,
  isOpenRequest,
  isReplyLate,
  isTooManyOpen,
  itemView,
  mailtoHref,
  mapServerErrors,
  nomineeFormValues,
  noticeData,
  noticeOwner,
  noticeWhen,
  parseRequestKind,
  personRows,
  requesterText,
  requestFormPath,
  retentionText,
  sentFields,
  splitCategories,
  telHref,
  valueProblem,
  type RequestFormValues,
} from './model';
import { dataSummaryDocument } from './print';

const en = createTranslator('en');
const hi = createTranslator('hi');
const fen = formattersFor('en');
const fhi = formattersFor('hi');
const TODAY = '2026-09-30';
const EVIL = '<img src=x onerror=alert(1)>';

const form = (overrides: Partial<RequestFormValues> = {}): RequestFormValues => ({
  ...emptyRequestForm(),
  ...overrides,
});

describe('privacy requests', () => {
  it('reads the kind of request from the address, and nothing else', () => {
    expect(parseRequestKind('correction')).toBe('correction');
    expect(parseRequestKind('erasure')).toBe('erasure');
    expect(parseRequestKind('grievance')).toBe('grievance');
    expect(parseRequestKind('Access')).toBeUndefined();
    expect(parseRequestKind('constructor')).toBeUndefined();
    expect(parseRequestKind(undefined)).toBeUndefined();
  });

  it('knows the requests still waiting for a reply, and spots late ones', () => {
    expect(isOpenRequest('Submitted')).toBe(true);
    expect(isOpenRequest('InProgress')).toBe(true);
    expect(isOpenRequest('PartlyCompleted')).toBe(false);
    const now = Date.parse('2026-09-30T06:00:00Z');
    expect(isReplyLate({ status: 'InProgress', dueAt: '2026-09-29T06:00:00Z' }, now)).toBe(true);
    expect(isReplyLate({ status: 'Completed', dueAt: '2026-09-29T06:00:00Z' }, now)).toBe(false);
    expect(isReplyLate({ status: 'Submitted', dueAt: '2026-10-29T06:00:00Z' }, now)).toBe(false);
  });

  it('recognises the "too many open requests" answer', () => {
    expect(isTooManyOpen(new ApiError({ status: 422, title: 'x', code: 'privacy.too_many_open' }))).toBe(true);
    expect(isTooManyOpen(new ApiError({ status: 422, title: 'x', code: 'privacy.request_closed' }))).toBe(false);
    expect(isTooManyOpen(new Error('x'))).toBe(false);
  });

  it('tells the history in the patient language, without repeating what the page already shows', () => {
    const at = '2026-09-30T06:00:00Z';
    const nothingShown = { response: null, erasureShown: false };
    expect(
      eventView({ at, kind: 'Received', text: 'Request received through the patient portal.' }, nothingShown, hi),
    ).toEqual({ label: 'हमें आपका अनुरोध मिला', text: null });
    const erased = 'Erased: optional messages stopped. Kept as the law requires: bills and payments until 01 Oct 2034.';
    expect(eventView({ at, kind: 'Erased', text: erased }, nothingShown, en)).toEqual({
      label: 'Data erased',
      text: erased,
    });
    expect(eventView({ at, kind: 'Erased', text: erased }, { response: null, erasureShown: true }, en)).toEqual({
      label: 'Data erased',
      text: null,
    });
    expect(
      eventView({ at, kind: 'Closed', text: 'Corrected. ' }, { response: 'Corrected.', erasureShown: false }, en),
    ).toEqual({ label: 'We replied', text: null });
    expect(
      eventView({ at, kind: 'Closed', text: 'Other words' }, { response: 'Corrected.', erasureShown: false }, en).text,
    ).toBe('Other words');
    expect(eventView({ at, kind: 'Note', text: 'Something new' }, nothingShown, en)).toEqual({
      label: 'Something new',
      text: null,
    });
  });

  it('says who asked, by name when someone asked for the patient', () => {
    expect(requesterText({ requestedBy: 'Self', requesterName: null }, en)).toBe('The patient');
    expect(requesterText({ requestedBy: 'FamilyMember', requesterName: ' Ravi Verma ' }, en)).toBe(
      'Ravi Verma, as a family member',
    );
    expect(requesterText({ requestedBy: 'ParentOrGuardian', requesterName: 'Sunita' }, hi)).toBe(
      'Sunita, माता-पिता या अभिभावक के रूप में',
    );
    expect(requesterText({ requestedBy: 'Nominee', requesterName: null }, en)).toBe('The nominee');
  });

  it('waits to confirm that someone asking for another person may act for them, except for a concern', () => {
    const request = {
      type: 'Erasure',
      status: 'Submitted',
      requestedBy: 'FamilyMember',
      identityConfirmed: false,
    } as const;
    expect(awaitsRequesterCheck(request)).toBe(true);
    expect(awaitsRequesterCheck({ ...request, identityConfirmed: true })).toBe(false);
    expect(awaitsRequesterCheck({ ...request, requestedBy: 'Self' })).toBe(false);
    expect(awaitsRequesterCheck({ ...request, status: 'Declined' })).toBe(false);
    expect(awaitsRequesterCheck({ ...request, type: 'Grievance' })).toBe(false);
  });

  it('says what an erasure removed and how long the law keeps the rest', () => {
    const erasure = {
      type: 'Erasure',
      erasedItems: ['messages', 'portal', 'somethingNew'],
      retainedUntil: '2034-10-01',
      retainedPermanently: false,
    } as const;
    expect(erasureOutcome(erasure, en, fen)).toEqual({
      erased: ['Optional messages were stopped.', 'Your portal account was closed.', 'somethingNew'],
      kept: `Some records must be kept until ${fen.calendarDate('2034-10-01')}, as the law requires.`,
    });
    expect(erasureOutcome({ ...erasure, retainedPermanently: true }, hi, fhi)?.kept).toBe(
      'कानून के अनुसार कुछ रिकॉर्ड हमेशा रखने होंगे।',
    );
    expect(
      erasureOutcome({ ...erasure, erasedItems: [], retainedUntil: null, retainedPermanently: false }, en, fen),
    ).toBeNull();
    expect(erasureOutcome({ ...erasure, type: 'Correction' }, en, fen)).toBeNull();
  });

  it('shows correction values the way the patient reads them', () => {
    expect(fieldLabel('dateOfBirth', hi)).toBe('जन्म तिथि');
    expect(fieldLabel('toString', en)).toBe('toString');
    expect(correctionValue('gender', 'Female', hi, fhi)).toBe('महिला');
    expect(correctionValue('dateOfBirth', '1987-03-14', en, fen)).toBe(fen.calendarDate('1987-03-14'));
    expect(correctionValue('dateOfBirth', '14 Mar 1987', en, fen)).toBe('14 Mar 1987');
    expect(correctionValue('mobile', '+919876543210', en, fen)).toBe('+91 98765 43210');
    expect(correctionValue('mobile', '+91******3210', en, fen)).toBe('+91******3210');
    expect(correctionValue('address', '  12 MG Road ', en, fen)).toBe('12 MG Road');
  });
});

describe('the request form', () => {
  it('asks who the request is for, and the name of anyone asking for someone else', () => {
    expect(checkRequest(form({ details: 'x' }), 'grievance', TODAY)).toEqual([
      { path: ['requestedBy'], key: 'myPrivacy.new.who.required' },
    ]);
    expect(checkRequest(form({ requestedBy: 'FamilyMember', details: 'x' }), 'grievance', TODAY)).toEqual([
      { path: ['requesterName'], key: 'myPrivacy.new.yourName.required' },
    ]);
    expect(
      checkRequest(
        form({ requestedBy: 'ParentOrGuardian', requesterName: 'x'.repeat(201), details: 'x' }),
        'grievance',
        TODAY,
      ),
    ).toEqual([{ path: ['requesterName'], key: 'myPrivacy.name.tooLong' }]);
    expect(checkRequest(form({ requestedBy: 'Self', details: 'Someone saw my report' }), 'grievance', TODAY)).toEqual(
      [],
    );
  });

  it('needs details for an erasure or a concern, and the confirmation for an erasure', () => {
    expect(checkRequest(form({ requestedBy: 'Self', details: '  ' }), 'grievance', TODAY)).toEqual([
      { path: ['details'], key: 'myPrivacy.new.details.required' },
    ]);
    expect(checkRequest(form({ requestedBy: 'Self', details: 'Close my account' }), 'erasure', TODAY)).toEqual([
      { path: ['understood'], key: 'myPrivacy.new.understood.required' },
    ]);
    expect(
      checkRequest(form({ requestedBy: 'Self', details: 'x'.repeat(4001), understood: true }), 'erasure', TODAY),
    ).toEqual([{ path: ['details'], key: 'myPrivacy.new.details.tooLong' }]);
  });

  it('needs at least one detail to correct, each with a correct value in the right form', () => {
    expect(checkRequest(form({ requestedBy: 'Self' }), 'correction', TODAY)).toEqual([
      { path: ['fields'], key: 'myPrivacy.new.fields.required' },
    ]);
    const values = {
      ...emptyRequestForm().values,
      mobile: '12345',
      email: 'not-an-address',
      dateOfBirth: '2031-01-01',
    };
    expect(
      checkRequest(
        form({ requestedBy: 'Self', fields: ['email', 'mobile', 'dateOfBirth', 'name'], values }),
        'correction',
        TODAY,
      ),
    ).toEqual([
      { path: ['values', 'name'], key: 'myPrivacy.new.value.required' },
      { path: ['values', 'dateOfBirth'], key: 'myPrivacy.new.value.date' },
      { path: ['values', 'mobile'], key: 'myPrivacy.new.value.mobile' },
      { path: ['values', 'email'], key: 'myPrivacy.new.value.email' },
    ]);
  });

  it('checks each kind of value', () => {
    expect(valueProblem('mobile', '98765 43210', TODAY)).toBeNull();
    expect(valueProblem('mobile', '+91 5876543210', TODAY)).toBe('myPrivacy.new.value.mobile');
    expect(valueProblem('alternatePhone', '020-2612 3456', TODAY)).toBeNull();
    expect(valueProblem('alternatePhone', 'call me', TODAY)).toBe('myPrivacy.new.value.phone');
    expect(valueProblem('email', 'asha@example.in', TODAY)).toBeNull();
    expect(valueProblem('email', 'asha@example.in?subject=hi', TODAY)).toBe('myPrivacy.new.value.email');
    expect(valueProblem('dateOfBirth', TODAY, TODAY)).toBeNull();
    expect(valueProblem('dateOfBirth', '2026-02-30', TODAY)).toBe('myPrivacy.new.value.date');
    expect(valueProblem('dateOfBirth', '1899-12-31', TODAY)).toBe('myPrivacy.new.value.date');
    expect(valueProblem('gender', '', TODAY)).toBe('myPrivacy.new.value.choose');
    expect(valueProblem('gender', 'Unknown', TODAY)).toBe('myPrivacy.new.value.choose');
    expect(valueProblem('gender', 'Female', TODAY)).toBeNull();
    expect(valueProblem('healthRecord', 'x'.repeat(501), TODAY)).toBe('myPrivacy.new.value.tooLong');
    expect(valueProblem('other', 'The allergy listed is wrong', TODAY)).toBeNull();
  });

  it('sends a correction as the server expects it, and nothing extra for the other kinds', () => {
    const values = form({
      requestedBy: 'Self',
      requesterName: 'Ignored',
      fields: ['mobile', 'name'],
      values: { ...emptyRequestForm().values, name: ' Asha Verma ', mobile: '98765 43210', email: 'unused@example.in' },
      details: '  ',
    });
    expect(sentFields(values, 'correction')).toEqual(['name', 'mobile']);
    expect(buildRequest(values, 'correction', 'p1', 'Self')).toEqual({
      patientId: 'p1',
      type: 'Correction',
      requestedBy: 'Self',
      requesterName: null,
      details: null,
      corrections: [
        { field: 'name', requestedValue: 'Asha Verma' },
        { field: 'mobile', requestedValue: '+919876543210' },
      ],
    });
    expect(
      buildRequest(
        form({ ...values, requestedBy: 'FamilyMember', requesterName: ' Ravi ', details: ' Stop messages ' }),
        'erasure',
        'p1',
        'FamilyMember',
      ),
    ).toEqual({
      patientId: 'p1',
      type: 'Erasure',
      requestedBy: 'FamilyMember',
      requesterName: 'Ravi',
      details: 'Stop messages',
      corrections: null,
    });
    expect(sentFields(values, 'grievance')).toEqual([]);
  });

  it('puts server validation messages on the right fields', () => {
    const sent = ['name', 'mobile'] as const;
    expect(requestFormPath('requesterName', sent)).toBe('requesterName');
    expect(requestFormPath('details', sent)).toBe('details');
    expect(requestFormPath('corrections', sent)).toBe('fields');
    expect(requestFormPath('corrections[1].requestedValue', sent)).toBe('values.mobile');
    expect(requestFormPath('corrections[7].field', sent)).toBe('fields');
    expect(requestFormPath('patientId', sent)).toBeNull();

    const error = new ApiError({
      status: 400,
      title: 'Please correct the highlighted fields.',
      errors: { 'corrections[0].requestedValue': ['Enter the correct value.'], patientId: ['Choose a patient.'] },
    });
    expect(mapServerErrors(error, (field) => requestFormPath(field, sent))).toEqual({
      fields: [{ path: 'values.name', message: 'Enter the correct value.' }],
      other: ['Choose a patient.'],
    });
    expect(mapServerErrors(new ApiError({ status: 422, title: 'x', code: 'y' }), () => null)).toBeNull();
    expect(mapServerErrors(new Error('x'), () => null)).toBeNull();
  });
});

describe('the nominee form', () => {
  it('needs a name, a relationship and a mobile number or an e-mail address', () => {
    expect(checkNominee({ name: '', relationship: '', phone: '', email: '' })).toEqual([
      { path: 'name', key: 'myPrivacy.nominee.name.required' },
      { path: 'relationship', key: 'myPrivacy.nominee.relationship.required' },
      { path: 'phone', key: 'myPrivacy.nominee.contact.required' },
    ]);
    expect(checkNominee({ name: 'Ravi', relationship: 'Son', phone: '12345', email: 'ravi@' })).toEqual([
      { path: 'phone', key: 'myPrivacy.new.value.mobile' },
      { path: 'email', key: 'myPrivacy.new.value.email' },
    ]);
    expect(checkNominee({ name: 'Ravi', relationship: 'x'.repeat(65), phone: '', email: 'ravi@example.in' })).toEqual([
      { path: 'relationship', key: 'myPrivacy.nominee.relationship.tooLong' },
    ]);
    expect(checkNominee({ name: 'Ravi', relationship: 'Son', phone: '9876543210', email: '' })).toEqual([]);
  });

  it('starts from the nominee on record and sends numbers in E.164', () => {
    const values = nomineeFormValues({
      name: 'Ravi Verma',
      relationship: 'Son',
      phone: '+919876543210',
      email: null,
      recordedVia: 'FrontDesk',
      updatedAt: '2026-09-01T06:00:00Z',
      version: 7,
    });
    expect(values).toEqual({ name: 'Ravi Verma', relationship: 'Son', phone: '9876543210', email: '' });
    expect(nomineeFormValues(null)).toEqual({ name: '', relationship: '', phone: '', email: '' });
    expect(buildNomineeRequest({ ...values, name: ' Ravi Verma ', email: ' ' }, 'p1')).toEqual({
      patientId: 'p1',
      name: 'Ravi Verma',
      relationship: 'Son',
      phone: '+919876543210',
      email: null,
    });
    expect(buildNomineeRequest({ ...values, phone: '', email: 'ravi@example.in' }, 'p1').phone).toBeNull();
    // Replacing the nominee shown sends its version; one named meanwhile at the desk is then not replaced unseen.
    expect(buildNomineeRequest(values, 'p1', 7).version).toBe(7);
  });
});

describe('contact details', () => {
  it('formats Indian mobile numbers and links only what is safe to dial or write to', () => {
    expect(formatPhone('+919876543210')).toBe('+91 98765 43210');
    expect(formatPhone('020 2612 3456')).toBe('020 2612 3456');
    expect(formatPhone('  ')).toBeNull();
    expect(telHref('+91 98765 43210')).toBe('tel:+919876543210');
    expect(telHref('(020) 2612-3456')).toBe('tel:02026123456');
    expect(telHref('javascript:alert(1)')).toBeNull();
    expect(telHref('12')).toBeNull();
    expect(mailtoHref('privacy@hospital.in')).toBe('mailto:privacy@hospital.in');
    expect(mailtoHref('privacy@hospital.in?subject=x&body=y')).toBeNull();
    expect(mailtoHref('javascript:alert(1)')).toBeNull();
    expect(mailtoHref(null)).toBeNull();
    expect(isEmail('a.b-c@sub.example.co.in')).toBe(true);
    expect(isEmail('a b@example.in')).toBe(false);
  });

  it('names the officer in the patient language', () => {
    expect(contactTitle('Grievance Officer', hi)).toBe('शिकायत अधिकारी');
    expect(contactTitle(null, en)).toBe('Grievance Officer');
    expect(contactTitle('data protection officer', hi)).toBe('डेटा संरक्षण अधिकारी');
    expect(contactTitle('Patient Relations Manager', hi)).toBe('Patient Relations Manager');
  });
});

describe('the data summary', () => {
  const PERIODS: RetentionPeriods = {
    outpatientYears: 4,
    inpatientYears: 7,
    financeYears: 8,
    messageLogDays: 365,
    auditYears: 12,
  };
  const category = (
    key: string,
    retentionKind: SummaryCategory['retentionKind'],
    retention = 'The server sentence.',
  ): SummaryCategory => ({
    key,
    title: `Server title ${key}`,
    description: 'Server description',
    purpose: 'Server purpose',
    basis: 'Server basis',
    retention,
    retentionKind,
    items: [],
  });

  it('says how long each group is kept from its kind and the periods in force', () => {
    const say = (kind: SummaryCategory['retentionKind'], t = en, f = fen) =>
      retentionText(category('x', kind), PERIODS, t, f);
    expect(say('MedicalRecords')).toBe(
      'At least 4 years after an out-patient visit and 7 years after a hospital stay; medico-legal records permanently.',
    );
    expect(say('Finance')).toBe('8 years from the bill date, as tax law requires.');
    expect(say('MessageLog')).toBe(
      'The message log for 365 days; feedback, complaints and follow-up calls with your medical records.',
    );
    expect(say('Audit')).toBe('12 years.');
    expect(say('WhileInUse')).toBe('While the account is open.');
    expect(say('WithRecords')).toBe('As long as the records they relate to.');
    expect(say('MedicalRecords', hi, fhi)).toBe(
      'ओपीडी मुलाक़ात के बाद कम से कम 4 साल और अस्पताल में भर्ती के बाद 7 साल; मेडिको-लीगल मामलों के रिकॉर्ड हमेशा के लिए।',
    );
    expect(say('Audit', hi, fhi)).toBe('12 साल।');
  });

  it('keeps the server sentence for a kind it does not know, or when the periods are missing', () => {
    const unknown = category('x', 'Genomics' as SummaryCategory['retentionKind'], 'Kept for ever.');
    expect(retentionText(unknown, PERIODS, hi, fhi)).toBe('Kept for ever.');
    expect(retentionText(category('x', 'Finance', '8 years from the bill date.'), null, hi, fhi)).toBe(
      '8 years from the bill date.',
    );
    expect(retentionText(category('x', 'WithRecords'), null, hi, fhi)).toBe('जब तक उनसे जुड़े रिकॉर्ड रखे जाते हैं।');
  });

  it('translates a group by its key, and keeps the server text for a group it does not know', () => {
    const known = categoryView(category('access-log', 'Audit'), PERIODS, hi, fhi);
    expect(known.title).toBe('रिकॉर्ड देखने का लॉग');
    expect(known.retention).toBe('12 साल।');
    const unknown = categoryView(category('genomics', 'WithRecords'), PERIODS, hi, fhi);
    expect(unknown).toMatchObject({ title: 'Server title genomics', basis: 'Server basis' });
    expect(categoryView(category('constructor', 'Audit'), PERIODS, en, fen).title).toBe('Server title constructor');
  });

  it('names each kind of record in the patient language by its table, and keeps a label it does not know', () => {
    const item = { count: 2, first: null, last: null };
    expect(itemView({ ...item, key: 'clinical.encounters', label: 'Consultations' }, hi, fhi).label).toBe('परामर्श');
    expect(
      itemView(
        { ...item, key: 'clinical.controlled_drug_register', label: 'Controlled-medicine register entries' },
        hi,
        fhi,
      ).label,
    ).toBe('नियंत्रित दवाओं के रजिस्टर में प्रविष्टियाँ');
    expect(itemView({ ...item, key: 'genomics.samples', label: 'Genome samples' }, hi, fhi).label).toBe(
      'Genome samples',
    );
    expect(itemView({ ...item, key: 'toString', label: 'Odd' }, hi, fhi).label).toBe('Odd');
  });

  it('has a label in both languages for every counted source of the catalogue', () => {
    // PersonalDataCatalogue: every source without `Counted: false`.
    const counted = [
      'patients.patient_identity_checks',
      'patients.visits',
      'scheduling.appointments',
      'scheduling.waitlist_entries',
      'patients.visitor_passes',
      'clinical.encounters',
      'clinical.clinical_notes',
      'clinical.diagnoses',
      'clinical.allergies',
      'clinical.immunizations',
      'clinical.vital_signs',
      'clinical.patient_alerts',
      'clinical.clinical_orders',
      'clinical.package_bookings',
      'clinical.discharge_summaries',
      'clinical.medical_records',
      'clinical.record_requests',
      'interop.abdm_link_requests',
      'interop.abdm_transfers',
      'clinical.prescriptions',
      'pharmacy.sales',
      'pharmacy.refill_requests',
      'pharmacy.ip_issues',
      'pharmacy.controlled_drug_register',
      'clinical.controlled_drug_register',
      'diagnostics.lab_orders',
      'diagnostics.lab_reports',
      'diagnostics.imaging_studies',
      'diagnostics.radiology_reports',
      'diagnostics.cardiology_studies',
      'diagnostics.cardiology_reports',
      'diagnostics.critical_call_logs',
      'inpatient.admissions',
      'inpatient.nursing_notes',
      'inpatient.nursing_assessments',
      'inpatient.medication_orders',
      'inpatient.medication_administrations',
      'inpatient.intake_output_entries',
      'specialtycare.surgery_bookings',
      'specialtycare.admissions',
      'specialtycare.blood_requests',
      'specialtycare.blood_issues',
      'specialtycare.transfusion_reactions',
      'specialtycare.dialysis_sessions',
      'specialtycare.physio_referrals',
      'supportservices.diet_orders',
      'billing.bills',
      'billing.receipts',
      'billing.refunds',
      'billing.patient_advances',
      'billing.patient_policies',
      'billing.insurance_claims',
      'billing.pre_authorizations',
      'virtualcare.telehealth_sessions',
      'virtualcare.rpm_enrollments',
      'virtualcare.rpm_readings',
      'platform.notification_logs',
      'engagement.follow_up_tasks',
      'engagement.feedback_responses',
      'engagement.complaints',
      'engagement.campaign_recipients',
      'patients.patient_consents',
      'patients.patient_documents',
      'identity.users',
      'patients.portal_family_links',
      'quality.incidents',
      'ai.usage_records',
      'audit.audit_events',
      'privacy.data_requests',
      'privacy.nominees',
      'privacy.breach_affected_patients',
    ];
    expect(Object.keys(ITEM_LABEL).sort()).toEqual([...counted].sort());
    for (const message of Object.values(ITEM_LABEL)) {
      expect(DICTIONARIES.en[message], message).toBeTruthy();
      expect(DICTIONARIES.hi[message], message).not.toBe(DICTIONARIES.en[message]);
    }
  });

  it('covers every group the server names, with text in both languages', () => {
    const keys = [
      'registration',
      'visits',
      'clinical',
      'medicines',
      'tests',
      'stays',
      'procedures',
      'support',
      'billing',
      'virtual',
      'communication',
      'consents',
      'documents',
      'account',
      'safety',
      'ai',
      'access-log',
      'privacy',
    ];
    expect(Object.keys(CATEGORY).sort()).toEqual([...keys].sort());
    for (const message of Object.values(CATEGORY).flatMap((keysOf) => [
      keysOf.title,
      keysOf.description,
      keysOf.purpose,
      keysOf.basis,
    ])) {
      expect(DICTIONARIES.en[message], message).toBeTruthy();
      expect(DICTIONARIES.hi[message], message).toBeTruthy();
    }
    expect(Object.keys(CONSENT_TYPE)).toHaveLength(10);
  });

  it('dates each kind of record: a span, a single day, or none', () => {
    expect(
      itemView(
        {
          key: 'patients.visits',
          label: 'Hospital visits',
          count: 1234,
          first: '2021-01-03T06:00:00Z',
          last: '2026-09-28T06:00:00Z',
        },
        en,
        fen,
      ),
    ).toEqual({
      label: 'Hospital visits',
      count: 'Records: 1,234',
      dates: `From ${fen.date('2021-01-03T06:00:00Z')} to ${fen.date('2026-09-28T06:00:00Z')}`,
    });
    expect(
      itemView(
        {
          key: 'privacy.nominees',
          label: 'Nominee',
          count: 1,
          first: '2026-09-28T06:00:00Z',
          last: '2026-09-28T09:00:00Z',
        },
        en,
        fen,
      ).dates,
    ).toBe(`On ${fen.date('2026-09-28T06:00:00Z')}`);
    expect(
      itemView({ key: 'identity.users', label: 'Portal account', count: 1, first: null, last: null }, en, fen).dates,
    ).toBeNull();
  });

  it('says whether each consent was given, withdrawn or refused', () => {
    expect(
      consentView({ type: 'Telehealth', granted: true, capturedAt: '2026-01-01T06:00:00Z', revokedAt: null }, hi, fhi),
    ).toMatchObject({
      label: 'वीडियो परामर्श',
      state: 'myPrivacy.consent.given',
      tone: 'success',
    });
    expect(
      consentView(
        {
          type: 'MarketingCommunication',
          granted: true,
          capturedAt: '2026-01-01T06:00:00Z',
          revokedAt: '2026-02-01T06:00:00Z',
        },
        en,
        fen,
      ).state,
    ).toBe('myPrivacy.consent.withdrawn');
    expect(
      consentView({ type: 'Research', granted: false, capturedAt: '2026-01-01T06:00:00Z', revokedAt: null }, en, fen)
        .state,
    ).toBe('myPrivacy.consent.refused');
    expect(
      consentView({ type: 'SomethingNew', granted: true, capturedAt: '2026-01-01T06:00:00Z', revokedAt: null }, en, fen)
        .label,
    ).toBe('SomethingNew');
  });

  it('lists the registration details held, masked identifiers exactly as given', () => {
    const rows = personRows(
      {
        uhid: 'NH26000123',
        fullName: 'Asha Verma',
        gender: 'Unknown',
        dateOfBirth: null,
        isDateOfBirthEstimated: false,
        phone: '+919876543210',
        alternatePhone: null,
        email: null,
        address: '12 MG Road, Pune',
        emergencyContact: null,
        abhaNumber: 'XX-XXXX-XXXX-1234',
        abhaAddress: null,
        aadhaarLast4: 'XXXX-XXXX-1234',
        hasPhoto: false,
        registeredAt: '2021-01-03T06:00:00Z',
        registeredAtFacility: 'Nana Hospitals, Pune',
      },
      en,
      fen,
    );
    expect(rows.map((row) => row.label)).toEqual([
      'UHID',
      'Name',
      'Mobile number',
      'Address',
      'ABHA number',
      'Aadhaar (last 4 digits only)',
      'Photo for identification',
      'Registered on',
      'Registered at',
    ]);
    expect(rows.find((row) => row.label === 'ABHA number')?.value).toBe('XX-XXXX-XXXX-1234');
    expect(rows.find((row) => row.label === 'Aadhaar (last 4 digits only)')?.value).toBe('XXXX-XXXX-1234');
    expect(rows.find((row) => row.label === 'Mobile number')?.value).toBe('+91 98765 43210');
  });

  it('keeps the registration group with the registration details', () => {
    const { registration, others } = splitCategories([
      category('visits', 'MedicalRecords'),
      category('registration', 'MedicalRecords'),
    ]);
    expect(registration?.key).toBe('registration');
    expect(others.map((c) => c.key)).toEqual(['visits']);
  });
});

describe('breach notices', () => {
  it('shows a notice as the viewer own only when it concerns them', () => {
    const profiles = [{ id: 'mother' }, { id: 'child' }];
    expect(noticeOwner(['mother', 'child'], 'child', profiles)).toBe('child');
    expect(noticeOwner(['stranger', 'mother'], 'child', profiles)).toBe('mother');
    expect(noticeOwner(['stranger'], 'child', profiles)).toBe('stranger');
    expect(noticeOwner([], 'child', profiles)).toBe('child');
    expect(noticeOwner(undefined, 'child', profiles)).toBe('child');
  });

  it('says when it happened and when the hospital found out', () => {
    const awareAt = '2026-09-20T06:00:00Z';
    expect(noticeWhen({ occurredFrom: null, occurredTo: null, awareAt }, en, fen)).toEqual([
      `We found out on ${fen.dateTime(awareAt)}.`,
    ]);
    const from = '2026-09-18T06:00:00Z';
    const to = '2026-09-19T06:00:00Z';
    expect(noticeWhen({ occurredFrom: from, occurredTo: to, awareAt }, en, fen)[0]).toBe(
      `Between ${fen.dateTime(from)} and ${fen.dateTime(to)}`,
    );
    expect(noticeWhen({ occurredFrom: from, occurredTo: from, awareAt }, en, fen)[0]).toBe(`On ${fen.dateTime(from)}`);
  });

  it('names the data involved in the patient language', () => {
    expect(noticeData(['Health', 'GovernmentId', 'Unknown'], hi)).toEqual([
      'स्वास्थ्य जानकारी',
      'सरकारी पहचान पत्रों के नंबर',
      'Unknown',
    ]);
  });
});

describe('the printed summary', () => {
  const summary: DataSummary = {
    generatedAt: '2026-09-30T06:00:00Z',
    hospital: `Nana ${EVIL}`,
    person: {
      uhid: EVIL,
      fullName: EVIL,
      gender: 'Female',
      dateOfBirth: '1987-03-14',
      isDateOfBirthEstimated: false,
      phone: '+919876543210',
      alternatePhone: EVIL,
      email: EVIL,
      address: EVIL,
      emergencyContact: EVIL,
      abhaNumber: EVIL,
      abhaAddress: EVIL,
      aadhaarLast4: EVIL,
      hasPhoto: true,
      registeredAt: '2021-01-03T06:00:00Z',
      registeredAtFacility: EVIL,
    },
    categories: [
      {
        key: 'registration',
        title: EVIL,
        description: EVIL,
        purpose: EVIL,
        basis: EVIL,
        retention: EVIL,
        retentionKind: 'MedicalRecords',
        items: [],
      },
      {
        key: EVIL,
        title: EVIL,
        description: EVIL,
        purpose: EVIL,
        basis: EVIL,
        retention: EVIL,
        // A kind the portal does not know: the server's sentence is printed, escaped.
        retentionKind: EVIL as SummaryCategory['retentionKind'],
        items: [{ key: EVIL, label: EVIL, count: 2, first: '2021-01-03T06:00:00Z', last: '2026-09-28T06:00:00Z' }],
      },
    ],
    consents: [{ type: EVIL, granted: true, capturedAt: '2026-01-01T06:00:00Z', revokedAt: null }],
    sharedWith: [{ name: EVIL, role: EVIL, data: EVIL }],
    processors: [{ name: EVIL, role: EVIL, data: EVIL }],
    nominee: { name: EVIL, relationship: EVIL },
    contact: { title: EVIL, name: EVIL, email: EVIL, phone: EVIL, address: EVIL },
    periods: { outpatientYears: 3, inpatientYears: 5, financeYears: 8, messageLogDays: 365, auditYears: 10 },
  };

  it('escapes every value, the server text included', () => {
    for (const language of ['en', 'hi'] as const) {
      const doc = dataSummaryDocument(summary, {
        t: createTranslator(language),
        f: formattersFor(language),
        printedAt: '2026-09-30T07:00:00Z',
      });
      expect(doc.body).not.toContain('<img');
      expect(doc.body).toContain('&lt;img');
      expect(doc.fileName).toBe('privacy-summary-2026-09-30');
    }
  });

  it('prints the translated groups and records, and says when nothing was shared', () => {
    const doc = dataSummaryDocument(
      {
        ...summary,
        categories: [
          {
            ...summary.categories[1]!,
            key: 'billing',
            retentionKind: 'Finance',
            items: [{ key: 'billing.bills', label: 'Bills', count: 4, first: null, last: null }],
          },
        ],
        sharedWith: [],
        consents: [],
        nominee: null,
      },
      { t: hi, f: fhi, printedAt: '2026-09-30T07:00:00Z' },
    );
    expect(doc.title).toBe('आपके निजी डेटा का सारांश');
    expect(doc.body).toContain('बिल, भुगतान और बीमा');
    expect(doc.body).toContain('बिल की तारीख से 8 साल');
    expect(doc.body).toContain('<td>बिल</td><td>रिकॉर्ड: 4</td>');
    expect(doc.body).toContain('किसी बीमा कंपनी, TPA या दूसरे इलाज करने वाले को हमसे आपका डेटा नहीं मिला है।');
    expect(doc.body).toContain('अभी कोई नॉमिनी नहीं चुना गया है।');
  });
});
