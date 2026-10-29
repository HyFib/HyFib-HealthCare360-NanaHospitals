import type { LookupItem } from '@hc360/core';
import type { FeatureDto } from '@/modules/features/api';
import type { DepartmentDto, FacilityDto, PractitionerDto, ServiceItemDto, SpecialtyDto } from '@/modules/masters/api';
import type { NumberSeriesItem, NumberSeriesList } from '@/modules/numbering/api';
import type { PrintTemplateItem, PrintTemplateList } from '@/modules/print-templates/api';
import { FACILITY_ID, paged, STAFF_USER, type FakeAnswers, type FakeRequest } from '../fakeApi';

// Hospital setup: the organisation masters (facilities, departments, specialities, practitioners, the service
// catalogue) and the look-ups every screen picks from, the feature switches, number formats and print templates.

const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;

// ---------------------------------------------------------------------------------------------------- facilities

const address = (city: string, district: string, pinCode: string, line1: string) => ({
  line1,
  line2: null,
  city,
  district,
  state: 'Maharashtra',
  pinCode,
  country: 'India',
});

const FACILITIES: FacilityDto[] = [
  {
    id: FACILITY_ID,
    code: 'NHP',
    name: 'Nana Hospitals, Pune',
    legalName: 'Nana Healthcare Private Limited',
    address: address('Pune', 'Pune', '411004', '12 Karve Road, Erandwane'),
    phone: '020 2545 6789',
    email: 'pune@nanahospitals.in',
    website: 'https://nanahospitals.in',
    gstin: '27AAACN1234F1Z5',
    pan: 'AAACN1234F',
    registrationNumber: 'PMC/HOSP/2011/0456',
    nabhNumber: 'H-2016-0789',
    hfrId: 'IN2710000123',
    timeZone: 'Asia/Kolkata',
    currency: 'INR',
    logoFileId: null,
    letterheadNote: 'NABH accredited hospital',
    isActive: true,
    version: 6,
  },
  {
    id: '0192a000-0000-7000-8000-0000000000f2',
    code: 'NHM',
    name: 'Nana Hospitals, Mumbai',
    legalName: 'Nana Healthcare Private Limited',
    address: address('Mumbai', 'Mumbai Suburban', '400050', '7 Hill Road, Bandra West'),
    phone: '022 2640 1122',
    email: 'mumbai@nanahospitals.in',
    website: null,
    gstin: '27AAACN1234F2Z4',
    pan: 'AAACN1234F',
    registrationNumber: 'MCGM/NH/2019/1123',
    nabhNumber: null,
    hfrId: null,
    timeZone: 'Asia/Kolkata',
    currency: 'INR',
    logoFileId: null,
    letterheadNote: null,
    isActive: true,
    version: 2,
  },
];

// ---------------------------------------------------------------------------------------------------- specialities, departments

const CARDIOLOGY = id('5e01');
const GENERAL_MEDICINE = id('5e02');
const ORTHOPAEDICS = id('5e03');
const PATHOLOGY = id('5e04');

const SPECIALTIES: SpecialtyDto[] = [
  { id: GENERAL_MEDICINE, code: 'GM', name: 'General medicine', description: null, isActive: true, version: 1 },
  {
    id: CARDIOLOGY,
    code: 'CARD',
    name: 'Cardiology',
    description: 'Heart and blood vessels',
    isActive: true,
    version: 1,
  },
  { id: ORTHOPAEDICS, code: 'ORTH', name: 'Orthopaedics', description: null, isActive: true, version: 2 },
  { id: PATHOLOGY, code: 'PATH', name: 'Pathology', description: null, isActive: true, version: 1 },
  { id: id('5e05'), code: 'HOM', name: 'Homoeopathy', description: 'No longer offered', isActive: false, version: 3 },
];

const OPD = '0192a000-0000-7000-8000-00000000de01';
const DEPT_CARDIOLOGY = id('de02');
const DEPT_ORTHO = id('de03');
const DEPT_LAB = id('de06');
const DOCTOR = STAFF_USER.practitionerId!;
const DR_RAO = '0192a000-0000-7000-8000-0000000000d2';

const department = (
  departmentId: string,
  code: string,
  name: string,
  kind: DepartmentDto['kind'],
  specialtyId: string | null,
  displayOrder: number,
  more: Partial<DepartmentDto> = {},
): DepartmentDto => ({
  id: departmentId,
  facilityId: FACILITY_ID,
  code,
  name,
  kind,
  specialtyId,
  headPractitionerId: null,
  location: null,
  phone: null,
  tokenPrefix: kind === 'Clinical' ? code : null,
  displayOrder,
  isActive: true,
  version: 1,
  ...more,
});

const DEPARTMENTS: DepartmentDto[] = [
  department(OPD, 'GM', 'General medicine', 'Clinical', GENERAL_MEDICINE, 1, {
    headPractitionerId: DOCTOR,
    location: 'Ground floor, OPD block',
    phone: '2101',
  }),
  department(DEPT_CARDIOLOGY, 'CARD', 'Cardiology', 'Clinical', CARDIOLOGY, 2, {
    location: 'First floor',
    phone: '2201',
  }),
  department(DEPT_ORTHO, 'ORTH', 'Orthopaedics', 'Clinical', ORTHOPAEDICS, 3),
  department(DEPT_LAB, 'LAB', 'Laboratory', 'Diagnostic', PATHOLOGY, 4, { location: 'Basement' }),
  department(id('de07'), 'HK', 'Housekeeping', 'Support', null, 5),
  department(id('de08'), 'ACC', 'Accounts', 'Administrative', null, 6, { isActive: false, version: 4 }),
];

// ---------------------------------------------------------------------------------------------------- practitioners

const practitioner = (
  practitionerId: string,
  code: string,
  title: string,
  firstName: string,
  lastName: string,
  gender: PractitionerDto['gender'],
  kind: PractitionerDto['kind'],
  specialty: SpecialtyDto | null,
  departmentId: string | null,
  fees: [number, number],
  more: Partial<PractitionerDto> = {},
): PractitionerDto => ({
  id: practitionerId,
  code,
  title,
  firstName,
  lastName,
  displayName: `${title} ${firstName} ${lastName}`.trim(),
  gender,
  qualification: kind === 'Nurse' ? 'B.Sc. Nursing' : 'MBBS, MD',
  registrationCouncil: kind === 'Nurse' ? 'Maharashtra Nursing Council' : 'Maharashtra Medical Council',
  registrationNumber: `MMC-${code.slice(1)}2011`,
  hprId: null,
  specialtyId: specialty?.id ?? null,
  specialtyName: specialty?.name ?? null,
  departmentId,
  departmentName: DEPARTMENTS.find((d) => d.id === departmentId)?.name ?? null,
  kind,
  phone: '98******45',
  email: null,
  consultationFeeNew: fees[0],
  consultationFeeFollowUp: fees[1],
  followUpValidityDays: 7,
  languages: ['English', 'Hindi', 'Marathi'],
  bio: null,
  photoFileId: null,
  signatureFileId: null,
  availableForOnlineBooking: kind === 'Consultant',
  availableForTelehealth: false,
  facilityIds: [],
  isActive: true,
  version: 1,
  ...more,
});

const PRACTITIONERS: PractitionerDto[] = [
  practitioner(DOCTOR, 'D001', 'Dr.', 'Meera', 'Iyer', 'Female', 'Consultant', SPECIALTIES[0]!, OPD, [600, 300], {
    availableForTelehealth: true,
  }),
  practitioner(
    DR_RAO,
    'D002',
    'Dr.',
    'Anil',
    'Rao',
    'Male',
    'Consultant',
    SPECIALTIES[1]!,
    DEPT_CARDIOLOGY,
    [1000, 500],
  ),
  practitioner(
    id('d3'),
    'D003',
    'Dr.',
    'Farah',
    'Khan',
    'Female',
    'Visiting',
    SPECIALTIES[2]!,
    DEPT_ORTHO,
    [1200, 600],
    {
      facilityIds: [FACILITY_ID],
    },
  ),
  practitioner(id('d4'), 'D004', 'Dr.', 'Rohit', 'Deshmukh', 'Male', 'Resident', SPECIALTIES[0]!, OPD, [0, 0]),
  practitioner(id('d5'), 'N001', 'Sr.', 'Mary', 'Thomas', 'Female', 'Nurse', null, null, [0, 0], {
    languages: ['English', 'Malayalam'],
  }),
];

// ---------------------------------------------------------------------------------------------------- services

const service = (
  serviceId: string,
  code: string,
  name: string,
  category: ServiceItemDto['category'],
  basePrice: number,
  departmentId: string | null,
  more: Partial<ServiceItemDto> = {},
): ServiceItemDto => ({
  id: serviceId,
  code,
  name,
  shortName: null,
  category,
  departmentId,
  basePrice,
  taxRate: 0,
  sacCode: '999311',
  isDiscountAllowed: true,
  isPriceEditable: false,
  description: null,
  isActive: true,
  version: 1,
  ...more,
});

/** One or more of every kind the screens pick from (consultations, tests, beds, surgery, diets). */
const SERVICES: ServiceItemDto[] = [
  service(id('5a01'), 'REG', 'Registration', 'Registration', 100, null),
  service(id('5a02'), 'CONS-GM', 'Consultation, general medicine', 'Consultation', 600, OPD, {
    shortName: 'OPD consult',
  }),
  service(id('5a03'), 'LAB-CBC', 'Complete blood count', 'Laboratory', 350, DEPT_LAB, { shortName: 'CBC' }),
  service(id('5a04'), 'LAB-HBA1C', 'HbA1c (glycated haemoglobin)', 'Laboratory', 550, DEPT_LAB, { shortName: 'HbA1c' }),
  service(id('5a05'), 'RAD-CXR', 'X-ray chest PA view', 'Radiology', 450, null),
  service(id('5a06'), 'CARD-ECG', 'ECG, 12 lead', 'Cardiology', 300, DEPT_CARDIOLOGY),
  service(id('5a07'), 'BED-GEN', 'General ward bed (per day)', 'BedCharge', 1500, null, { isDiscountAllowed: false }),
  service(id('5a08'), 'BED-ICU', 'ICU bed (per day)', 'BedCharge', 6000, null, { isDiscountAllowed: false }),
  service(id('5a09'), 'SURG-LCHOLE', 'Laparoscopic cholecystectomy', 'Surgery', 55_000, null, {
    isPriceEditable: true,
  }),
  service(id('5a0a'), 'SURG-TKR', 'Total knee replacement (one knee)', 'Surgery', 1_85_000, DEPT_ORTHO, {
    isPriceEditable: true,
  }),
  service(id('5a0b'), 'PROC-DRESS', 'Dressing, small wound', 'Procedure', 250, null),
  service(id('5a0c'), 'DIET-TH', 'Therapeutic diet (per day)', 'Dietary', 400, null),
  service(id('5a0d'), 'DIET-REG', 'Hospital diet (per day)', 'Dietary', 250, null),
  service(id('5a0e'), 'NURS-IV', 'IV cannulation', 'Nursing', 200, null),
  service(id('5a0f'), 'AMB-BLS', 'Ambulance, basic life support (within city)', 'Ambulance', 1200, null, {
    taxRate: 5,
  }),
  service(id('5a10'), 'MISC-CERT', 'Medical certificate', 'Miscellaneous', 200, null, {
    taxRate: 18,
    isActive: false,
    version: 3,
  }),
];

/** The look-up a picker searches: active services, of one category when asked. */
const lookupServices = (request: FakeRequest) => {
  const category = request.url.searchParams.get('category');
  return SERVICES.filter((s) => s.isActive && (!category || s.category === category));
};

/** One master by the id at the end of the path; a 404 for anything else. */
const byId =
  <T extends { id: string }>(items: readonly T[]) =>
  (request: FakeRequest) =>
    items.find((item) => request.path.endsWith(`/${item.id}`));

const SPECIALTY_LOOKUP: LookupItem[] = SPECIALTIES.filter((s) => s.isActive).map(({ id: specialtyId, code, name }) => ({
  id: specialtyId,
  code,
  name,
}));

// ---------------------------------------------------------------------------------------------------- features

const FEATURES: FeatureDto[] = [
  {
    key: 'features.telehealth',
    code: 'telehealth',
    name: 'Video consultations',
    description: 'Doctors see patients by video from the worklist and the patient portal.',
    whenOff: 'Video appointments can no longer be booked or started; booked ones stay in the calendar.',
    defaultEnabled: false,
    enabled: true,
    version: 2,
  },
  {
    key: 'features.patientPortal',
    code: 'patientPortal',
    name: 'Patient portal',
    description: 'Patients book appointments, see reports and pay bills online.',
    whenOff: 'Patients cannot sign in to the portal.',
    defaultEnabled: true,
    enabled: true,
    version: null,
  },
  {
    key: 'features.whatsapp',
    code: 'whatsapp',
    name: 'WhatsApp messages',
    description: 'Reminders, reports and bills are sent on WhatsApp.',
    whenOff: 'Messages go by SMS only.',
    defaultEnabled: false,
    enabled: false,
    version: null,
  },
];

// ---------------------------------------------------------------------------------------------------- number formats

const series = (
  key: string,
  name: string,
  area: string,
  format: string,
  lastNumber: number,
  nextExample: string,
  more: Partial<NumberSeriesItem> = {},
): NumberSeriesItem => ({
  key,
  name,
  area,
  scope: 'facility',
  format,
  resetPolicy: 'Yearly',
  lastNumber,
  nextExample,
  facilityCode: 'NHP',
  isCustomised: false,
  isLocked: false,
  lockedReason: null,
  maxLength: 30,
  defaultFormat: format,
  defaultResetPolicy: 'Yearly',
  ...more,
});

const NUMBER_SERIES: NumberSeriesList = {
  facilityCount: 2,
  longestFacilityCode: 3,
  series: [
    series('patient.uhid', 'UHID (patient number)', 'Patients', 'NH{YY}{SEQ:6}', 123, 'NH26000124', {
      scope: 'global',
      facilityCode: null,
      resetPolicy: 'Never',
      defaultResetPolicy: 'Never',
      isLocked: true,
      lockedReason: 'Patients already carry cards with this number.',
    }),
    series('billing.op-bill', 'OP bill', 'Billing', '{FAC}/OP/{YY}/{SEQ:5}', 4812, 'NHP/OP/26/04813', {
      isCustomised: true,
      defaultFormat: 'OP{YY}{SEQ:6}',
    }),
    series('billing.receipt', 'Receipt', 'Billing', '{FAC}/RC/{YY}/{SEQ:5}', 6120, 'NHP/RC/26/06121'),
    series('lab.accession', 'Accession number', 'Laboratory', 'L{YY}{MM}{SEQ:5}', 0, 'L261000001', {
      resetPolicy: 'Monthly',
      defaultResetPolicy: 'Monthly',
    }),
  ],
};

// ---------------------------------------------------------------------------------------------------- print templates

const PRINTED = {
  letterhead: 'Printed',
  topSpaceMm: 0,
  bottomSpaceMm: 0,
  showLogo: true,
  closingNote: null,
  paper: null,
} as const;

const template = (
  key: PrintTemplateItem['key'],
  name: string,
  area: string,
  covers: string,
  more: Partial<PrintTemplateItem> = {},
): PrintTemplateItem => ({
  key,
  name,
  area,
  covers,
  papers: [],
  allowsPreprinted: true,
  hospitalWide: { ...PRINTED },
  facility: null,
  hospitalWideVersion: null,
  facilityVersion: null,
  ...more,
});

const PRINT_TEMPLATES: PrintTemplateList = {
  facilityId: FACILITY_ID,
  facilityName: 'Nana Hospitals, Pune',
  facilityCount: 2,
  templates: [
    template('bill', 'Bills', 'Billing', 'OP and IP bills, A4 and 80 mm.', {
      hospitalWide: { ...PRINTED, closingNote: 'Thank you. Get well soon.' },
      hospitalWideVersion: 2,
    }),
    template('receipt', 'Receipts', 'Billing', 'Payment and advance receipts.', { allowsPreprinted: false }),
    template('prescription', 'Prescriptions', 'Doctor', 'Prescriptions and OPD case sheets.', {
      papers: ['A5', 'A4'],
      facility: {
        ...PRINTED,
        letterhead: 'Preprinted',
        topSpaceMm: 40,
        bottomSpaceMm: 15,
        showLogo: false,
        paper: 'A5',
      },
      facilityVersion: 1,
    }),
    template('report', 'Reports', 'Laboratory', 'Lab and radiology reports.'),
  ],
};

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/masters/facilities': FACILITIES,
  'GET /api/v1/masters/facilities/*': byId(FACILITIES),
  'GET /api/v1/masters/departments': DEPARTMENTS,
  'GET /api/v1/masters/departments/*': byId(DEPARTMENTS),
  'GET /api/v1/masters/specialties': SPECIALTIES,
  'GET /api/v1/masters/specialties/*': byId(SPECIALTIES),
  'GET /api/v1/masters/practitioners': paged(PRACTITIONERS),
  'GET /api/v1/masters/practitioners/*': byId(PRACTITIONERS),
  'GET /api/v1/masters/services': paged(SERVICES),
  'GET /api/v1/masters/services/*': byId(SERVICES),
  // Departments and practitioners are answered by the clinical screens' data (accessibility/fixtures/clinical.ts).
  'GET /api/v1/masters/lookups/specialties': SPECIALTY_LOOKUP,
  'GET /api/v1/masters/lookups/services': lookupServices,
  'GET /api/v1/admin/features': FEATURES,
  'GET /api/v1/admin/number-series': NUMBER_SERIES,
  'GET /api/v1/admin/print-templates': PRINT_TEMPLATES,
};
