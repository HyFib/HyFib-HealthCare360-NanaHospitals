import { todayIso } from '@hc360/core';
import type {
  BookingDto,
  CorporateBatchDetail,
  CorporateBatchDto,
  CorporatePayer,
  ItineraryItemDto,
  PackageDto,
  PackageEligibility,
  PackageItemDto,
} from '@/modules/health-checkups/api';
import { paged, type FakeAnswers, type FakeRequest } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// Health check-ups for today: packages, a booked patient to check in and one going round the stations (the record),
// and a corporate batch.

const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;
const now = Date.now();
const minutesAgo = (minutes: number) => new Date(now - minutes * 60_000).toISOString();
const today = todayIso();

const packageItem = (
  n: string,
  kind: PackageItemDto['kind'],
  serviceCode: string,
  name: string,
  category: PackageItemDto['category'],
  sequence: number,
  extra: Partial<PackageItemDto> = {},
): PackageItemDto => ({
  id: id(n),
  kind,
  serviceItemId: kind === 'Investigation' ? id(`${n}0`) : null,
  serviceCode,
  name,
  category,
  departmentCode: kind === 'Consultation' ? 'GM' : null,
  gender: 'Any',
  minAgeYears: null,
  sequence,
  ...extra,
});

const EXECUTIVE: PackageDto = {
  id: RECORD_ID,
  code: 'HC-EXEC',
  name: 'Executive health check',
  description: 'Blood tests, ECG, chest X-ray, ultrasound abdomen and a physician review.',
  gender: 'Any',
  minAgeYears: 18,
  maxAgeYears: null,
  price: 4999,
  serviceItemId: id('4c00'),
  isActive: true,
  items: [
    packageItem('4c01', 'Investigation', 'LAB-CBC', 'Complete blood count', 'Laboratory', 1),
    packageItem('4c02', 'Investigation', 'LAB-LIPID', 'Lipid profile', 'Laboratory', 2),
    packageItem('4c03', 'Investigation', 'CAR-ECG', 'ECG (12-lead)', 'Cardiology', 3),
    packageItem('4c04', 'Investigation', 'RAD-CXR', 'X-ray chest PA view', 'Radiology', 4),
    packageItem('4c05', 'Investigation', 'LAB-PAP', 'Pap smear', 'Laboratory', 5, {
      gender: 'Female',
      minAgeYears: 21,
    }),
    packageItem('4c06', 'Consultation', 'CON-GM', 'Physician review', 'Consultation', 6),
  ],
  version: 3,
};

const PACKAGES: PackageDto[] = [
  EXECUTIVE,
  {
    id: id('4d00'),
    code: 'HC-WOMEN',
    name: 'Well-woman check',
    description: 'For women over 30: blood tests, Pap smear, mammogram and a gynaecologist review.',
    gender: 'Female',
    minAgeYears: 30,
    maxAgeYears: null,
    price: 3499,
    serviceItemId: id('4d10'),
    isActive: true,
    items: [
      packageItem('4d01', 'Investigation', 'LAB-CBC', 'Complete blood count', 'Laboratory', 1),
      packageItem('4d02', 'Investigation', 'RAD-MAMMO', 'Mammogram (bilateral)', 'Radiology', 2),
      packageItem('4d03', 'Consultation', 'CON-OBG', 'Gynaecologist review', 'Consultation', 3),
    ],
    version: 1,
  },
  {
    id: id('4e00'),
    code: 'HC-BASIC',
    name: 'Basic health check (2025)',
    description: null,
    gender: 'Any',
    minAgeYears: null,
    maxAgeYears: null,
    price: 1499,
    serviceItemId: null,
    isActive: false,
    items: [],
    version: 5,
  },
];

const ASHA = { patientId: '0192a000-0000-7000-8000-000000000001', uhid: 'NH26000123', patientName: 'Asha Devi' };
const RAVI = { patientId: '0192a000-0000-7000-8000-000000000002', uhid: 'NH26000124', patientName: 'Ravi Shankar' };
const NEHA = { patientId: id('4f03'), uhid: 'NH26000244', patientName: 'Neha Agarwal' };

const stop = (
  n: string,
  sequence: number,
  departmentName: string,
  description: string,
  status: ItineraryItemDto['status'],
): ItineraryItemDto => ({
  id: id(n),
  sequence,
  departmentId: null,
  departmentName,
  description,
  status,
  completedAt: status === 'Done' ? minutesAgo(40 - sequence * 5) : null,
  notes: null,
});

const booking = (
  n: string,
  bookingNumber: string,
  who: typeof ASHA,
  status: BookingDto['status'],
  extra: Partial<BookingDto> = {},
): BookingDto => ({
  id: n === 'record' ? RECORD_ID : id(n),
  bookingNumber,
  ...who,
  packageId: RECORD_ID,
  packageCode: 'HC-EXEC',
  packageName: 'Executive health check',
  scheduledDate: today,
  status,
  visitId: status === 'Booked' ? null : id('4b11'),
  orderId: status === 'Booked' ? null : id('4b12'),
  corporateBatchId: null,
  corporateName: null,
  corporatePayerId: null,
  notes: null,
  checkedInAt: status === 'Booked' ? null : minutesAgo(70),
  summary: null,
  recommendations: null,
  reviewedByPractitionerId: null,
  reviewedAt: null,
  completedAt: null,
  cancelReason: null,
  itinerary: [],
  version: 2,
  ...extra,
});

const ROUND: BookingDto = booking('record', 'HC26-00318', ASHA, 'InProgress', {
  notes: 'Fasting since 10 pm.',
  summary: 'Lipids mildly raised; ECG and chest X-ray normal.',
  recommendations: 'Diet and 30 minutes of brisk walking daily; repeat lipid profile in 3 months.',
  itinerary: [
    stop('4a01', 1, 'Laboratory', 'Blood sample (CBC, lipid profile)', 'Done'),
    stop('4a02', 2, 'Cardiology', 'ECG (12-lead)', 'Done'),
    stop('4a03', 3, 'Radiology', 'X-ray chest PA view', 'Pending'),
    stop('4a04', 4, 'Laboratory', 'Pap smear', 'Pending'),
    stop('4a05', 5, 'General medicine', 'Physician review', 'Pending'),
  ],
});

const BOOKINGS: BookingDto[] = [
  ROUND,
  booking('4b02', 'HC26-00321', RAVI, 'Booked'),
  booking('4b03', 'HC26-00309', NEHA, 'Completed', {
    corporateBatchId: id('4900'),
    corporateName: 'Infosys Ltd, Pune',
    corporatePayerId: id('4901'),
    checkedInAt: minutesAgo(5 * 60),
    completedAt: minutesAgo(60),
    reviewedAt: minutesAgo(70),
    summary: 'All results within normal limits.',
  }),
];

const BATCH: CorporateBatchDto = {
  id: RECORD_ID,
  batchNumber: 'CB26-0042',
  payerId: id('4901'),
  corporateName: 'Infosys Ltd, Pune',
  contactPerson: 'Ms. Ritu Saxena (HR)',
  contactPhone: '+91 20 4011 2200',
  reference: 'PO 4500098812',
  packageId: RECORD_ID,
  scheduledDate: today,
  bookedCount: 18,
  skippedCount: 2,
  notes: 'Annual employee health check, batch 2 of 5.',
  createdAt: minutesAgo(9 * 24 * 60),
};

const BATCH_DETAIL: CorporateBatchDetail = {
  batch: BATCH,
  bookings: BOOKINGS.slice(2).map((b) => ({ ...b, corporateBatchId: RECORD_ID })),
};

const COMPANIES: CorporatePayer[] = [
  { id: id('4901'), name: 'Infosys Ltd, Pune', contactPerson: 'Ms. Ritu Saxena (HR)', phone: '+91 20 4011 2200' },
  { id: id('4902'), name: 'Tata Motors Ltd, Pimpri', contactPerson: 'Mr. Sameer Joshi', phone: '+91 20 6613 3000' },
];

const ELIGIBLE: PackageEligibility = { isEligible: true, reason: null, excludedItems: [] };

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/health-checkups/packages': PACKAGES,
  // A package by id, and whether a patient may have it (/packages/{id}/eligibility).
  'GET /api/v1/health-checkups/packages/*': (request: FakeRequest) =>
    request.path.endsWith('/eligibility') ? ELIGIBLE : PACKAGES.find((p) => request.path.endsWith(`/${p.id}`)),
  'GET /api/v1/health-checkups/bookings': paged(BOOKINGS),
  [`GET /api/v1/health-checkups/bookings/${RECORD_ID}`]: ROUND,
  'GET /api/v1/health-checkups/corporate-batches': paged([BATCH]),
  [`GET /api/v1/health-checkups/corporate-batches/${RECORD_ID}`]: BATCH_DETAIL,
  'GET /api/v1/health-checkups/corporate-payers': COMPANIES,
};
