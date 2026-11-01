import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { useSession, type AuthUser } from '@hc360/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { useLanguageStore } from '@/i18n';
import { formattersFor } from '@/i18n/useFormat';
import { useProfileStore } from '@/lib/profileStore';
import type {
  DataSummary,
  NomineeView,
  PortalBreachNotice,
  PortalDataRequestDetail,
  PortalDataRequestItem,
  PortalPrivacyStatus,
  PortalProfile,
  PublicPrivacyInfo,
} from '@/lib/types';
import { createPortalRouter } from '@/router';

/**
 * "Your privacy" end to end in jsdom (router, signed-in layout, queries, i18n) against a fake server, with a patient
 * already signed in. Dialogs and drawers are left to the unit tests: they are slow and flaky in jsdom.
 */

const PATIENT_ID = '5f0c6d3e-8c1a-4a53-9a3f-2b1d6f7e9a10';
const CHILD_ID = '6a1d7e4f-9d2b-4b64-8b4f-3c2e7f8a0b21';
const REQUEST_ID = '0b9d2f4a-6c1e-4d8b-9f3a-7e5c1a2b3c4d';
const FAMILY_REQUEST_ID = '2d3e4f5a-6b7c-4d8e-9f0a-1b2c3d4e5f6a';
const ERASED_REQUEST_ID = '3e4f5a6b-7c8d-4e9f-8a1b-2c3d4e5f6a7b';
const NEW_REQUEST_ID = '1c2d3e4f-5a6b-4c7d-8e9f-0a1b2c3d4e5f';
const NOTICE_ID = '9e8d7c6b-5a49-4382-a1b0-c9d8e7f6a5b4';
const SLOW = { timeout: 8000 };
const en = formattersFor('en');

const PROFILE: PortalProfile = {
  id: PATIENT_ID,
  uhid: 'NH26000123',
  fullName: 'Asha Verma',
  gender: 'Female',
  dateOfBirth: '1987-03-14',
  isDateOfBirthEstimated: false,
  age: '39Y',
  phone: '+91******3210',
  bloodGroup: 'BPositive',
  category: 'General',
  isVip: false,
  isDeceased: false,
  photoFileId: null,
};

const CHILD: PortalProfile = {
  ...PROFILE,
  id: CHILD_ID,
  uhid: 'NH26000124',
  fullName: 'Meera Verma',
  dateOfBirth: '2016-05-02',
  age: '10Y',
};

const USER: AuthUser = {
  id: 'user-1',
  userName: '+919876543210',
  displayName: 'Asha Verma',
  type: 'patient',
  facilityId: null,
  facilityName: null,
  practitionerId: null,
  patientId: PATIENT_ID,
  roles: [],
  permissions: ['portal.access'],
  facilities: [],
  mfaEnabled: false,
  hasPin: false,
  mustChangePassword: false,
  restriction: null,
};

const CONTACT = {
  title: 'Grievance Officer',
  name: 'Anita Rao',
  email: 'privacy@nanahospitals.example',
  phone: '+919876543210',
  address: 'Nana Hospitals, Pune 411001',
};

const INFO: PublicPrivacyInfo = { hospital: 'Nana Hospitals', contact: CONTACT, responseDays: 30 };

const NOTICE: PortalBreachNotice = {
  id: NOTICE_ID,
  patientIds: [PATIENT_ID],
  reference: 'DPB260001',
  title: 'Unauthorised access to the laboratory system',
  notifiedAt: '2026-09-25T06:00:00Z',
  awareAt: '2026-09-22T04:30:00Z',
  occurredFrom: '2026-09-20T18:00:00Z',
  occurredTo: '2026-09-21T02:00:00Z',
  description: 'Someone used a stolen password to open the laboratory system.',
  location: 'Laboratory information system',
  dataCategories: ['Health', 'Contact'],
  consequences: 'Someone may know which tests you had.',
  mitigation: 'We locked the account and changed every password.',
  principalSteps: 'Be careful with calls that ask about your tests.',
  contact: CONTACT,
};

const STATUS: PortalPrivacyStatus = {
  hasNominee: true,
  nomineeName: 'Ravi Verma',
  openRequests: 1,
  maxOpenRequests: 5,
  notices: 1,
};

const REQUESTS: PortalDataRequestItem[] = [
  {
    id: REQUEST_ID,
    requestNumber: 'PUNDPR2600041',
    patientId: PATIENT_ID,
    type: 'Correction',
    status: 'InProgress',
    receivedAt: '2026-09-28T06:00:00Z',
    dueAt: '2099-10-28T06:00:00Z',
    closedAt: null,
  },
  {
    id: '7a6b5c4d-3e2f-4a1b-8c9d-0e1f2a3b4c5d',
    requestNumber: 'PUNDPR2600007',
    patientId: PATIENT_ID,
    type: 'Grievance',
    status: 'Completed',
    receivedAt: '2026-03-01T06:00:00Z',
    dueAt: '2026-03-31T06:00:00Z',
    closedAt: '2026-03-10T06:00:00Z',
  },
];

const DETAIL: PortalDataRequestDetail = {
  id: REQUEST_ID,
  requestNumber: 'PUNDPR2600041',
  patientId: PATIENT_ID,
  type: 'Correction',
  status: 'InProgress',
  requestedBy: 'Self',
  requesterName: null,
  identityConfirmed: true,
  details: 'My passport shows the right spelling.',
  corrections: [
    { field: 'name', currentValue: 'Asha Verma', requestedValue: 'Asha Varma', decision: 'Pending', note: null },
  ],
  receivedAt: '2026-09-28T06:00:00Z',
  dueAt: '2099-10-28T06:00:00Z',
  closedAt: null,
  response: null,
  erasedItems: [],
  retainedUntil: null,
  retainedPermanently: false,
  timeline: [
    { at: '2026-09-28T06:00:00Z', kind: 'Received', text: 'Request received through the patient portal.' },
    {
      at: '2026-09-28T06:00:00Z',
      kind: 'IdentityVerified',
      text: 'Identity confirmed by signing in with a code sent to the registered mobile number.',
    },
    { at: '2026-09-29T06:00:00Z', kind: 'Started', text: 'We are working on your request.' },
  ],
  canWithdraw: true,
  contact: CONTACT,
};

/** An erasure a family member asked for, not yet confirmed by the hospital. */
const FAMILY_REQUEST: PortalDataRequestDetail = {
  ...DETAIL,
  id: FAMILY_REQUEST_ID,
  requestNumber: 'PUNDPR2600043',
  type: 'Erasure',
  status: 'Submitted',
  requestedBy: 'FamilyMember',
  requesterName: 'Ravi Verma',
  identityConfirmed: false,
  details: 'Please stop all messages to my mother.',
  corrections: [],
  timeline: [{ at: '2026-09-28T06:00:00Z', kind: 'Received', text: 'Request received through the patient portal.' }],
};

const ERASED_TEXT =
  'Erased: optional messages stopped; portal account closed. Kept as the law requires: bills and payments until 01 Oct 2034.';
const REPLY = 'We stopped your messages and closed your portal account.';

/** A completed erasure: what went, and until when the rest is kept. */
const ERASED_REQUEST: PortalDataRequestDetail = {
  ...DETAIL,
  id: ERASED_REQUEST_ID,
  requestNumber: 'PUNDPR2600044',
  type: 'Erasure',
  status: 'PartlyCompleted',
  details: 'Stop all messages and close my account.',
  corrections: [],
  closedAt: '2026-09-30T06:00:00Z',
  response: REPLY,
  erasedItems: ['messages', 'portal'],
  retainedUntil: '2034-10-01',
  timeline: [
    { at: '2026-09-28T06:00:00Z', kind: 'Received', text: 'Request received through the patient portal.' },
    { at: '2026-09-30T05:00:00Z', kind: 'Erased', text: ERASED_TEXT },
    { at: '2026-09-30T06:00:00Z', kind: 'Closed', text: REPLY },
  ],
  canWithdraw: false,
};

const MEDICAL =
  'At least 3 years after an out-patient visit and 5 years after a hospital stay; medico-legal records permanently.';
const SERVER_SENTENCE = 'The retention sentence as the server wrote it.';

const SUMMARY: DataSummary = {
  generatedAt: '2026-09-30T06:00:00Z',
  hospital: 'Nana Hospitals',
  person: {
    uhid: 'NH26000123',
    fullName: 'Asha Verma',
    gender: 'Female',
    dateOfBirth: '1987-03-14',
    isDateOfBirthEstimated: false,
    phone: '+919876543210',
    alternatePhone: null,
    email: null,
    address: '12 MG Road, Pune',
    emergencyContact: null,
    abhaNumber: 'XX-XXXX-XXXX-5678',
    abhaAddress: null,
    aadhaarLast4: 'XXXX-XXXX-1234',
    hasPhoto: true,
    registeredAt: '2021-01-03T06:00:00Z',
    registeredAtFacility: 'Nana Hospitals, Pune',
  },
  categories: [
    {
      key: 'registration',
      title: 'Registration details',
      description: 'x',
      purpose: 'x',
      basis: 'x',
      retention: SERVER_SENTENCE,
      retentionKind: 'MedicalRecords',
      items: [],
    },
    {
      key: 'visits',
      title: 'Visits and appointments',
      description: 'x',
      purpose: 'x',
      basis: 'x',
      retention: SERVER_SENTENCE,
      retentionKind: 'MedicalRecords',
      items: [
        {
          key: 'patients.visits',
          label: 'Hospital visits',
          count: 3,
          first: '2021-01-03T06:00:00Z',
          last: '2026-09-28T06:00:00Z',
        },
      ],
    },
  ],
  consents: [{ type: 'Telehealth', granted: true, capturedAt: '2026-01-01T06:00:00Z', revokedAt: null }],
  sharedWith: [],
  processors: [{ name: 'MSG91', role: 'Sends SMS messages', data: 'Your mobile number and the text of each SMS' }],
  nominee: { name: 'Ravi Verma', relationship: 'Son' },
  contact: CONTACT,
  periods: { outpatientYears: 3, inpatientYears: 5, financeYears: 8, messageLogDays: 365, auditYears: 10 },
};

const NOMINEE: NomineeView = {
  nominee: {
    name: 'Ravi Verma',
    relationship: 'Son',
    phone: '+919876543211',
    email: null,
    recordedVia: 'Portal',
    updatedAt: '2026-09-01T06:00:00Z',
    version: 7,
  },
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
}

const calls: { method: string; path: string; body: unknown }[] = [];
let createAnswer: () => Response;
let profiles: PortalProfile[];
let summary: DataSummary;
let status: PortalPrivacyStatus;
let notice: PortalBreachNotice;

const fakeServer = vi.fn(async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
  const raw = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
  const method = (init?.method ?? 'GET').toUpperCase();
  const { pathname } = new URL(raw, 'https://portal.test');
  calls.push({ method, path: pathname, body: typeof init?.body === 'string' ? JSON.parse(init.body) : undefined });
  const privacy = '/api/v1/portal/privacy';
  switch (`${method} ${pathname}`) {
    case 'GET /api/v1/system/info':
      return json({ facilityName: 'Nana Hospitals' });
    case 'GET /api/v1/portal/profiles':
      return json(profiles);
    case 'GET /api/v1/public/privacy':
      return json(INFO);
    case `GET ${privacy}/status`:
      return json(status);
    case `GET ${privacy}/notices`:
      return json([notice]);
    case `GET ${privacy}/notices/${NOTICE_ID}`:
      return json(notice);
    case `GET ${privacy}/requests`:
      return json(REQUESTS);
    case `GET ${privacy}/requests/${REQUEST_ID}`:
      return json(DETAIL);
    case `GET ${privacy}/requests/${FAMILY_REQUEST_ID}`:
      return json(FAMILY_REQUEST);
    case `GET ${privacy}/requests/${ERASED_REQUEST_ID}`:
      return json(ERASED_REQUEST);
    case `POST ${privacy}/requests`:
      return createAnswer();
    case `GET ${privacy}/nominee`:
      return json(NOMINEE);
    case `GET ${privacy}/summary`:
      return json(summary);
    default:
      return json({ title: 'Not found', status: 404 }, 404);
  }
});

beforeAll(() => {
  window.matchMedia ??= ((query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
    addListener: () => undefined,
    removeListener: () => undefined,
    dispatchEvent: () => false,
  })) as typeof window.matchMedia;
  window.scrollTo = (() => undefined) as typeof window.scrollTo;
});

beforeEach(() => {
  calls.length = 0;
  createAnswer = () => json({ id: NEW_REQUEST_ID, requestNumber: 'PUNDPR2600042', dueAt: '2026-10-30T06:00:00Z' }, 201);
  profiles = [PROFILE];
  summary = SUMMARY;
  status = STATUS;
  notice = NOTICE;
  useProfileStore.getState().select(null);
  vi.stubGlobal('fetch', fakeServer);
  // As after a successful sign-in with a code: the access token lives in memory only.
  useSession.getState().setAuth({ accessToken: 'test-token', expiresAt: '2099-01-01T00:00:00Z', user: USER });
});

afterEach(() => {
  // The session is left as it is: ending it under a mounted page would start the sign-out redirect mid-teardown,
  // and every test signs in again (this file's modules, the session store included, are its own).
  useLanguageStore.getState().setLanguage('en');
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

function openAt(path: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const history = createMemoryHistory({ initialEntries: [path] });
  render(
    <QueryClientProvider client={queryClient}>
      <FluentProvider theme={webLightTheme}>
        <RouterProvider router={createPortalRouter(queryClient, history)} />
      </FluentProvider>
    </QueryClientProvider>,
  );
  return history;
}

const sent = (method: string, path: string) => calls.filter((call) => call.method === method && call.path === path);

describe('Your privacy', () => {
  it('shows notices first, then the rights, the requests, the nominee and whom to contact', async () => {
    openAt('/portal/my-privacy');
    expect(await screen.findByRole('heading', { name: 'Your privacy', level: 1 }, SLOW)).toBeInTheDocument();
    const link = await screen.findByRole('link', { name: /Unauthorised access to the laboratory system/ }, SLOW);
    expect(link).toHaveAttribute('href', `/portal/my-privacy/notices/${NOTICE_ID}`);
    expect(screen.getByRole('link', { name: /See the data we hold/ })).toHaveAttribute(
      'href',
      '/portal/my-privacy/data',
    );
    expect(screen.getByRole('link', { name: /Correct your details/ })).toHaveAttribute(
      'href',
      '/portal/my-privacy/requests/new?type=correction',
    );
    expect(screen.getByRole('link', { name: /Messages you get/ })).toHaveAttribute(
      'href',
      '/portal/profile/preferences',
    );
    expect(await screen.findByText('Waiting for our reply: 1', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText('Nominee: Ravi Verma')).toBeInTheDocument();
    expect(await screen.findByText('Anita Rao', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText('We reply within 30 days.')).toBeInTheDocument();
    // Counts and the nominee's name come from the status call, which the server does not record as record access.
    expect(sent('GET', '/api/v1/portal/privacy/status').length).toBeGreaterThan(0);
    expect(sent('GET', '/api/v1/portal/privacy/nominee')).toHaveLength(0);
    expect(sent('GET', '/api/v1/portal/privacy/requests')).toHaveLength(0);
    // Every request names the person being viewed; nothing personal travels in the address.
    for (const call of calls.filter((c) => c.path.startsWith('/api/v1/portal/privacy/'))) {
      expect(call.path).not.toContain('Asha');
    }
  });

  it('says so when no nominee is named', async () => {
    status = { ...STATUS, hasNominee: false, nomineeName: null, openRequests: 0 };
    openAt('/portal/my-privacy');
    expect(await screen.findByText('No nominee named yet.', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText('Follow your requests and read our replies.')).toBeInTheDocument();
  });

  it('speaks Hindi when the patient chooses it', async () => {
    useLanguageStore.getState().setLanguage('hi');
    openAt('/portal/my-privacy');
    expect(await screen.findByRole('heading', { name: 'आपकी गोपनीयता', level: 1 }, SLOW)).toBeInTheDocument();
    expect(await screen.findByText('नॉमिनी: Ravi Verma', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /अपना विवरण ठीक करवाएँ/ })).toBeInTheDocument();
  });

  it('shows the data summary with masked identifiers as held, translated groups and a print button', async () => {
    openAt('/portal/my-privacy/data');
    expect(await screen.findByRole('heading', { name: 'The data we hold', level: 1 }, SLOW)).toBeInTheDocument();
    expect(await screen.findByText('XXXX-XXXX-1234', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText('XX-XXXX-XXXX-5678')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Visits and appointments' })).toBeInTheDocument();
    expect(screen.getByText('Hospital visits')).toBeInTheDocument();
    expect(screen.getByText('Records: 3')).toBeInTheDocument();
    // "How long we keep it" is built from the kind and the periods in force, not from the server's English sentence.
    expect(screen.getAllByText(MEDICAL)).toHaveLength(2);
    expect(screen.queryByText(SERVER_SENTENCE)).not.toBeInTheDocument();
    expect(screen.getByText('Video consultations')).toBeInTheDocument();
    expect(
      screen.getByText('No insurer, TPA or other care provider has received your data from us.'),
    ).toBeInTheDocument();
    expect(screen.getByText('MSG91')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Print or save as PDF' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Request a copy of your records' })).toHaveAttribute(
      'href',
      '/portal/records/request',
    );
  });

  it('says a date of birth worked out from the age is an estimate, in the summary and on the profile', async () => {
    // Registered by her age: the record holds her registration day and month in the year of birth the age gives.
    const estimated = { dateOfBirth: '1974-10-31', isDateOfBirthEstimated: true };
    summary = { ...SUMMARY, person: { ...SUMMARY.person, ...estimated } };
    profiles = [{ ...PROFILE, ...estimated }];
    const text = `${en.calendarDate('1974-10-31')} (estimated from your age when you registered)`;

    const history = openAt('/portal/my-privacy/data');
    expect(await screen.findByText(text, {}, SLOW)).toBeInTheDocument();

    history.push('/portal/profile');
    expect(await screen.findByText(text, {}, SLOW)).toBeInTheDocument();
    expect(screen.queryByText(en.calendarDate('1974-10-31'))).not.toBeInTheDocument();
  });

  it('names each kind of record and how long it is kept in Hindi', async () => {
    useLanguageStore.getState().setLanguage('hi');
    openAt('/portal/my-privacy/data');
    expect(await screen.findByText('अस्पताल में मुलाक़ातें', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText('रिकॉर्ड: 3')).toBeInTheDocument();
    expect(
      screen.getAllByText(
        'ओपीडी मुलाक़ात के बाद कम से कम 3 साल और अस्पताल में भर्ती के बाद 5 साल; मेडिको-लीगल मामलों के रिकॉर्ड हमेशा के लिए।',
      ),
    ).toHaveLength(2);
  });

  it('lists requests with the reply date and the status in words', async () => {
    openAt('/portal/my-privacy/requests');
    const open = await screen.findByRole('link', { name: /Correction of your details/ }, SLOW);
    expect(open).toHaveAttribute('href', `/portal/my-privacy/requests/${REQUEST_ID}`);
    expect(within(open).getByText('Being worked on')).toBeInTheDocument();
    expect(within(open).getByText(`We will reply by ${en.date('2099-10-28T06:00:00Z')}`)).toBeInTheDocument();
    const closed = screen.getByRole('link', { name: /Privacy concern/ });
    expect(within(closed).getByText('Done')).toBeInTheDocument();
    expect(within(closed).getByText(`Closed on ${en.date('2026-03-10T06:00:00Z')}`)).toBeInTheDocument();
  });

  it('shows one request: what was asked, the history in words, and how to withdraw it', async () => {
    openAt(`/portal/my-privacy/requests/${REQUEST_ID}`);
    expect(
      await screen.findByRole('heading', { name: 'Correction of your details', level: 1 }, SLOW),
    ).toBeInTheDocument();
    expect(screen.getByText('No. PUNDPR2600041')).toBeInTheDocument();
    expect(screen.getByText('Being worked on')).toBeInTheDocument();
    expect(screen.getByText('The patient')).toBeInTheDocument();
    expect(screen.getByText('Was: Asha Verma')).toBeInTheDocument();
    expect(screen.getByText('Change to: Asha Varma')).toBeInTheDocument();
    expect(screen.getByText('My passport shows the right spelling.')).toBeInTheDocument();
    expect(screen.getByText('We received your request')).toBeInTheDocument();
    expect(screen.getByText('Identity confirmed')).toBeInTheDocument();
    expect(screen.getByText('We started working on it')).toBeInTheDocument();
    expect(screen.queryByText(/Before anything is changed or erased/)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Withdraw request' })).toBeInTheDocument();
    expect(screen.getByText('Anita Rao')).toBeInTheDocument();
  });

  it('names who asked for someone else, and says the hospital will confirm they may act first', async () => {
    openAt(`/portal/my-privacy/requests/${FAMILY_REQUEST_ID}`);
    expect(await screen.findByRole('heading', { name: 'Erasure of your data', level: 1 }, SLOW)).toBeInTheDocument();
    expect(screen.getByText('Ravi Verma, as a family member')).toBeInTheDocument();
    expect(
      screen.getByText(
        'Before anything is changed or erased, the hospital will contact Ravi Verma to confirm that they can act for Asha Verma.',
      ),
    ).toBeInTheDocument();
  });

  it('shows what an erasure removed and until when the law keeps the rest, without repeating it', async () => {
    openAt(`/portal/my-privacy/requests/${ERASED_REQUEST_ID}`);
    expect(await screen.findByRole('heading', { name: 'What was erased' }, SLOW)).toBeInTheDocument();
    expect(screen.getByText('Optional messages were stopped.')).toBeInTheDocument();
    expect(screen.getByText('Your portal account was closed.')).toBeInTheDocument();
    expect(
      screen.getByText(`Some records must be kept until ${en.calendarDate('2034-10-01')}, as the law requires.`),
    ).toBeInTheDocument();
    expect(screen.getByText(REPLY)).toBeInTheDocument();
    expect(screen.getByText('Data erased')).toBeInTheDocument();
    expect(screen.queryByText(ERASED_TEXT)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Withdraw request' })).not.toBeInTheDocument();
  });

  it('does not ask the server for an id that is not an id', async () => {
    openAt('/portal/my-privacy/requests/not-an-id');
    expect(await screen.findByText(/We could not find this/, {}, SLOW)).toBeInTheDocument();
    expect(calls.some((call) => call.path.includes('not-an-id'))).toBe(false);
  });

  it('shows the nominee on record', async () => {
    openAt('/portal/my-privacy/nominee');
    expect(await screen.findByRole('heading', { name: 'Ravi Verma' }, SLOW)).toBeInTheDocument();
    expect(screen.getByText('Son')).toBeInTheDocument();
    expect(screen.getByText('+91 98765 43211')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Change nominee' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Remove nominee' })).toBeInTheDocument();
  });

  it('shows a breach notice in full, calmly and in sections', async () => {
    openAt(`/portal/my-privacy/notices/${NOTICE_ID}`);
    expect(
      await screen.findByRole('heading', { name: 'Unauthorised access to the laboratory system', level: 1 }, SLOW),
    ).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'What data was involved' })).toBeInTheDocument();
    expect(screen.getByText('Health information')).toBeInTheDocument();
    expect(screen.getByText('Contact details')).toBeInTheDocument();
    expect(screen.getByText('Be careful with calls that ask about your tests.')).toBeInTheDocument();
    expect(screen.getByText(/never ask for your sign-in code/)).toBeInTheDocument();
  });

  it('does not show a family member notice as the person being viewed', async () => {
    profiles = [PROFILE, CHILD];
    notice = { ...NOTICE, patientIds: [CHILD_ID] };
    useProfileStore.getState().select(PATIENT_ID);
    openAt(`/portal/my-privacy/notices/${NOTICE_ID}`);
    expect(await screen.findByText('This belongs to Meera Verma', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Switch to Meera' })).toBeInTheDocument();
    expect(
      screen.queryByRole('heading', { name: 'Unauthorised access to the laboratory system' }),
    ).not.toBeInTheDocument();
  });
});

describe('making a request', () => {
  it('checks the form before sending, then sends a correction and says when we will reply', async () => {
    const user = userEvent.setup();
    openAt('/portal/my-privacy/requests/new?type=correction');
    expect(await screen.findByRole('heading', { name: 'Correct your details', level: 1 }, SLOW)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Send request' }));
    expect(await screen.findByText('Choose who the request is for.')).toBeInTheDocument();
    expect(screen.getByText('Choose at least one detail to correct.')).toBeInTheDocument();
    expect(sent('POST', '/api/v1/portal/privacy/requests')).toHaveLength(0);

    await user.click(screen.getByRole('radio', { name: /^Me/ }));
    await user.click(screen.getByRole('checkbox', { name: 'Name' }));
    expect(screen.getByText(/Now recorded: Asha Verma/)).toBeInTheDocument();
    await user.type(screen.getByRole('textbox', { name: /Your name as it should be/ }), 'Asha Varma');
    await user.click(screen.getByRole('button', { name: 'Send request' }));

    expect(await screen.findByRole('heading', { name: 'Request sent' }, SLOW)).toBeInTheDocument();
    expect(
      screen.getByText(`Your request number is PUNDPR2600042. We will reply by ${en.date('2026-10-30T06:00:00Z')}.`),
    ).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'See your request' })).toHaveAttribute(
      'href',
      `/portal/my-privacy/requests/${NEW_REQUEST_ID}`,
    );
    expect(sent('POST', '/api/v1/portal/privacy/requests').map((call) => call.body)).toEqual([
      {
        patientId: PATIENT_ID,
        type: 'Correction',
        requestedBy: 'Self',
        requesterName: null,
        details: null,
        corrections: [{ field: 'name', requestedValue: 'Asha Varma' }],
      },
    ]);
  });

  it('asks for the name of someone making a request for a family member', async () => {
    const user = userEvent.setup();
    openAt('/portal/my-privacy/requests/new?type=grievance');
    expect(await screen.findByRole('heading', { name: 'Raise a privacy concern', level: 1 }, SLOW)).toBeInTheDocument();
    await user.click(screen.getByRole('radio', { name: /^A family member/ }));
    await user.type(
      screen.getByRole('textbox', { name: /Tell us what went wrong/ }),
      'My report was sent to a stranger.',
    );
    await user.click(screen.getByRole('button', { name: 'Send request' }));
    expect(await screen.findByText('Enter your name.')).toBeInTheDocument();
    await user.type(screen.getByRole('textbox', { name: /Your name/ }), 'Ravi Verma');
    await user.click(screen.getByRole('button', { name: 'Send request' }));
    expect(await screen.findByRole('heading', { name: 'Request sent' }, SLOW)).toBeInTheDocument();
    expect(screen.getByText(/the hospital may contact you to confirm that you can act for them/)).toBeInTheDocument();
    expect(sent('POST', '/api/v1/portal/privacy/requests')[0]?.body).toMatchObject({
      type: 'Grievance',
      requestedBy: 'FamilyMember',
      requesterName: 'Ravi Verma',
      details: 'My report was sent to a stranger.',
      corrections: null,
    });
  });

  it('explains kindly when the server says there are already too many open requests', async () => {
    const user = userEvent.setup();
    createAnswer = () =>
      json(
        {
          title: 'Business rule',
          status: 422,
          code: 'privacy.too_many_open',
          detail: 'There are already 5 open requests for this patient. Please wait for our replies.',
        },
        422,
      );
    openAt('/portal/my-privacy/requests/new?type=grievance');
    expect(await screen.findByRole('heading', { name: 'Raise a privacy concern', level: 1 }, SLOW)).toBeInTheDocument();
    await user.click(screen.getByRole('radio', { name: /^Me/ }));
    await user.type(screen.getByRole('textbox', { name: /Tell us what went wrong/ }), 'Please look into this.');
    await user.click(screen.getByRole('button', { name: 'Send request' }));
    expect(await screen.findByText('Please wait for our replies', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText(/You already have 5 requests waiting for our reply/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'See your requests' })).toHaveAttribute(
      'href',
      '/portal/my-privacy/requests',
    );
    expect(screen.queryByRole('heading', { name: 'Request sent' })).not.toBeInTheDocument();
  });

  it('says before anything is typed when the portal limit of open requests is reached', async () => {
    status = { ...STATUS, openRequests: 3, maxOpenRequests: 3 };
    openAt('/portal/my-privacy/requests/new?type=correction');
    expect(await screen.findByRole('heading', { name: 'Correct your details', level: 1 }, SLOW)).toBeInTheDocument();
    expect(await screen.findByText('Please wait for our replies', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText(/You already have 3 requests waiting for our reply/)).toBeInTheDocument();
    // The server has the last word: the form stays, in case a request was answered a moment ago.
    expect(screen.getByRole('button', { name: 'Send request' })).toBeEnabled();
  });

  it('explains what can be erased and what the law keeps, and needs the confirmation', async () => {
    const user = userEvent.setup();
    openAt('/portal/my-privacy/requests/new?type=erasure');
    expect(
      await screen.findByRole('heading', { name: 'Ask us to erase your data', level: 1 }, SLOW),
    ).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'What we can erase' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'What the law requires us to keep' })).toBeInTheDocument();
    expect(screen.getByText(/you will be signed out on every phone and computer/)).toBeInTheDocument();
    await user.click(screen.getByRole('radio', { name: /^Me/ }));
    await user.type(screen.getByRole('textbox', { name: /What would you like us to erase/ }), 'Stop all messages.');
    await user.click(screen.getByRole('button', { name: 'Send request' }));
    expect(await screen.findByText('Please confirm that you have read this.')).toBeInTheDocument();
    expect(sent('POST', '/api/v1/portal/privacy/requests')).toHaveLength(0);
  });
});
