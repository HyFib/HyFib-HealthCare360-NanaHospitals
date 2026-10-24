import type * as Core from '@hc360/core';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  fakeServer,
  openScreen,
  paged,
  SCREEN_TESTS,
  SLOW,
  STAFF_USER,
  stubBrowserApis,
  type FakeRequest,
} from '@/test/appScreen';
import type { EncounterListItem } from '@/modules/emr/api';
import type { PatientListItem } from '@/modules/patients/api';
import type { MedicalRecordDto, ProcedureCodeDto } from './api';
import { chartableEncounters, encounterOptionLabel } from './newChart';

// The MRD screens added for the procedure catalogue (MRD-01) and charts opened by hand (MRD-03), whole with the shell,
// against a fake server: what they list, what they send, and how the server's refusals read.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const id = (suffix: string) => `0192a000-0000-7000-8000-00000000b${suffix}`;

const CHOLE: ProcedureCodeDto = {
  id: id('001'),
  code: '0FT44ZZ',
  title: 'Resection of gallbladder, percutaneous endoscopic approach',
  system: 'ICD-10-PCS',
  category: 'General surgery',
  keywords: 'lap chole',
  source: 'Seed',
  isActive: true,
  version: 4,
};

const PATIENT: PatientListItem = {
  id: id('101'),
  uhid: 'NH-000123',
  fullName: 'Asha Kulkarni',
  gender: 'Female',
  dateOfBirth: '1980-01-01',
  age: '46 Y',
  phoneMasked: '98XXXXXX10',
  category: 'General',
  isVip: false,
  isDeceased: false,
  registeredAt: '2026-01-01T04:00:00Z',
  lastVisitAt: null,
} as PatientListItem;

const encounter = (overrides: Partial<EncounterListItem> & Pick<EncounterListItem, 'id'>): EncounterListItem => ({
  patientId: PATIENT.id,
  type: 'OutPatient',
  status: 'Signed',
  startedAt: '2026-10-20T05:00:00Z',
  signedAt: '2026-10-20T05:30:00Z',
  practitionerId: STAFF_USER.practitionerId!,
  practitionerName: 'Dr. Meera Iyer',
  departmentId: null,
  departmentName: 'Orthopaedics',
  chiefComplaint: 'Fall from a two-wheeler',
  diagnoses: [],
  ...overrides,
});

const OP = encounter({ id: id('201') });
const IP = encounter({ id: id('202'), type: 'InPatient', departmentName: 'Surgery' });

const CHART: MedicalRecordDto = {
  id: id('301'),
  patientId: PATIENT.id,
  uhid: PATIENT.uhid,
  patientName: PATIENT.fullName,
  encounterId: OP.id,
  admissionId: null,
  visitId: null,
  admittedAt: null,
  dischargedAt: null,
  status: 'Incomplete',
  assignedPractitionerId: OP.practitionerId,
  dueDate: null,
  isOverdue: false,
  isMlc: true,
  completedAt: null,
  archivedAt: null,
  archiveLocation: null,
  retainUntil: null,
  openDeficiencies: 0,
  deficiencies: [],
  version: 1,
  paper: null,
};

/** The server's refusal of a request, with its error code. */
const refusal = (status: number, detail: string, code: string) =>
  new Response(JSON.stringify({ title: 'Refused', status, detail, code }), {
    status,
    headers: { 'content-type': 'application/problem+json' },
  });

let server: ReturnType<typeof fakeServer>;
let chartExists: boolean;

beforeEach(() => {
  stubBrowserApis();
  localStorage.clear();
  chartExists = false;
  server = fakeServer({
    'GET /api/v1/mrd/procedure-codes': [CHOLE],
    'POST /api/v1/mrd/procedure-codes': (request: FakeRequest) => ({
      ...CHOLE,
      ...(request.body as object),
      id: id('002'),
      source: 'Manual',
      version: 1,
    }),
    'PUT /api/v1/mrd/procedure-codes/*': (request: FakeRequest) => ({ ...CHOLE, ...(request.body as object) }),
    'GET /api/v1/mrd/records': paged([]),
    'GET /api/v1/mrd/records/*': CHART,
    'POST /api/v1/mrd/records': () =>
      chartExists
        ? refusal(409, 'A chart already exists for this admission or encounter.', 'chart.exists')
        : { ...CHART },
    'GET /api/v1/patients': paged([PATIENT]),
    'GET /api/v1/emr/patients/*': paged([OP, IP]),
    'GET /api/v1/masters/lookups/practitioners': [],
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

/** What was sent to `method path`, in order. */
const sent = (method: string, path: string) =>
  server.requests.filter((request) => request.method === method && request.path === path).map((r) => r.body);

describe('opening a chart by hand', () => {
  it('offers the encounters a chart can be opened for: not an in-patient stay, which has its own', () => {
    expect(chartableEncounters([OP, IP])).toEqual([OP]);
    expect(encounterOptionLabel(encounter({ id: id('203'), status: 'InProgress', departmentName: null }))).toMatch(
      /^Out-patient \(OP\) · .+ · Dr\. Meera Iyer · not signed$/,
    );
  });
});

describe('MRD screens: procedure catalogue and new charts', SCREEN_TESTS, () => {
  it('lists the procedure catalogue and adds a code', async () => {
    const user = userEvent.setup();
    openScreen('/mrd/procedure-codes');
    const table = await screen.findByRole('table', { name: 'Procedure codes' }, SLOW);
    expect(await within(table).findByText('0FT44ZZ', {}, SLOW)).toBeInTheDocument();
    expect(within(table).getByText('lap chole')).toBeInTheDocument();
    expect(within(table).getByText('General surgery')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Add code' }));
    const drawer = await screen.findByRole('dialog', { name: 'Add procedure code' }, SLOW);
    // The system is ICD-10-PCS unless the coder says otherwise.
    expect(within(drawer).getByRole('textbox', { name: 'Code system' })).toHaveValue('ICD-10-PCS');
    await user.type(within(drawer).getByRole('textbox', { name: /^Code\s*\*?$/ }), 'x-1');
    await user.type(within(drawer).getByRole('textbox', { name: /^Title/ }), 'Hospital dressing');
    await user.click(within(drawer).getByRole('button', { name: 'Save' }));
    expect(await within(drawer).findByText('Use letters, digits and dots.')).toBeInTheDocument();
    expect(sent('POST', '/api/v1/mrd/procedure-codes')).toEqual([]);

    await user.clear(within(drawer).getByRole('textbox', { name: /^Code\s*\*?$/ }));
    await user.type(within(drawer).getByRole('textbox', { name: /^Code\s*\*?$/ }), 'HX.01');
    await user.type(within(drawer).getByRole('textbox', { name: 'Category' }), 'Minor procedures');
    await user.click(within(drawer).getByRole('button', { name: 'Save' }));
    await waitFor(() =>
      expect(sent('POST', '/api/v1/mrd/procedure-codes')).toEqual([
        {
          code: 'HX.01',
          title: 'Hospital dressing',
          system: 'ICD-10-PCS',
          category: 'Minor procedures',
          keywords: null,
          isActive: true,
        },
      ]),
    );
  });

  it('changes a code on the version it was read at, and says when the code is already in the catalogue', async () => {
    const user = userEvent.setup();
    openScreen('/mrd/procedure-codes');
    const table = await screen.findByRole('table', { name: 'Procedure codes' }, SLOW);
    await user.click(await within(table).findByText('0FT44ZZ', {}, SLOW));
    const drawer = await screen.findByRole('dialog', { name: '0FT44ZZ' }, SLOW);
    expect(within(drawer).getByText(/comes from the starter list/)).toBeInTheDocument();
    await user.type(within(drawer).getByRole('textbox', { name: 'Search words' }), ', laparoscopic cholecystectomy');
    await user.click(within(drawer).getByRole('button', { name: 'Save' }));
    await waitFor(() =>
      expect(sent('PUT', `/api/v1/mrd/procedure-codes/${CHOLE.id}`)).toEqual([
        expect.objectContaining({ keywords: 'lap chole, laparoscopic cholecystectomy', version: 4 }),
      ]),
    );
  });

  it('finds a switched-off code and switches it back on', async () => {
    const user = userEvent.setup();
    const retired: ProcedureCodeDto = {
      ...CHOLE,
      id: id('003'),
      code: '0FB44ZZ',
      title: 'Excision of gallbladder',
      isActive: false,
    };
    server = fakeServer({
      'GET /api/v1/mrd/procedure-codes': (request: FakeRequest) =>
        request.url.searchParams.get('includeInactive') === 'true' ? [CHOLE, retired] : [CHOLE],
      'PUT /api/v1/mrd/procedure-codes/*': (request: FakeRequest) => ({ ...retired, ...(request.body as object) }),
    });
    openScreen('/mrd/procedure-codes');
    const table = await screen.findByRole('table', { name: 'Procedure codes' }, SLOW);
    await within(table).findByText('0FT44ZZ', {}, SLOW);
    expect(within(table).queryByText('0FB44ZZ')).not.toBeInTheDocument();

    await user.click(screen.getByRole('switch', { name: 'Show switched-off codes' }));
    await user.click(await within(table).findByText('0FB44ZZ', {}, SLOW));
    expect(within(table).getByText('Switched off')).toBeInTheDocument();
    const drawer = await screen.findByRole('dialog', { name: '0FB44ZZ' }, SLOW);
    const active = within(drawer).getByRole('switch', { name: 'Active (offered in searches)' });
    expect(active).not.toBeChecked();
    await user.click(active);
    await user.click(within(drawer).getByRole('button', { name: 'Save' }));
    await waitFor(() =>
      expect(server.requests.filter((r) => r.method === 'PUT').map((r) => [r.path, r.body])).toEqual([
        [`/api/v1/mrd/procedure-codes/${retired.id}`, expect.objectContaining({ isActive: true, version: 4 })],
      ]),
    );
  });

  it('shows the server’s refusal of a duplicate code on the code field', async () => {
    const user = userEvent.setup();
    server = fakeServer({
      'GET /api/v1/mrd/procedure-codes': [CHOLE],
      'POST /api/v1/mrd/procedure-codes': refusal(
        409,
        '0FT44ZZ (ICD-10-PCS) is already in the catalogue.',
        'procedure.exists',
      ),
    });
    openScreen('/mrd/procedure-codes');
    await user.click(await screen.findByRole('button', { name: 'Add code' }, SLOW));
    const drawer = await screen.findByRole('dialog', { name: 'Add procedure code' }, SLOW);
    await user.type(within(drawer).getByRole('textbox', { name: /^Code\s*\*?$/ }), '0ft44zz');
    await user.type(within(drawer).getByRole('textbox', { name: /^Title/ }), 'Lap chole');
    await user.click(within(drawer).getByRole('button', { name: 'Save' }));
    expect(
      await within(drawer).findByText('0FT44ZZ (ICD-10-PCS) is already in the catalogue.', {}, SLOW),
    ).toBeInTheDocument();
  });

  it('opens a chart for a paper file: its number, the stay’s days and what it holds', async () => {
    const user = userEvent.setup();
    server = fakeServer({
      'GET /api/v1/mrd/records': paged([]),
      'GET /api/v1/mrd/records/*': {
        ...CHART,
        encounterId: null,
        paper: {
          fileNumber: 'IP/2016/431',
          isInpatient: true,
          episodeFrom: '2016-03-02',
          episodeTo: '2016-03-09',
          note: null,
        },
      },
      'POST /api/v1/mrd/records': (request: FakeRequest) => ({
        ...CHART,
        encounterId: null,
        paper: (request.body as { paper: unknown }).paper,
      }),
      'GET /api/v1/patients': paged([PATIENT]),
      'GET /api/v1/emr/patients/*': paged([OP, IP]),
      'GET /api/v1/masters/lookups/practitioners': [],
    });
    openScreen('/mrd/records');
    await user.click(await screen.findByRole('button', { name: 'New chart' }, SLOW));
    const dialog = await screen.findByRole('dialog', { name: 'New chart' }, SLOW);
    await user.type(within(dialog).getByRole('combobox', { name: /Patient/ }), 'Asha');
    await user.click(await screen.findByRole('option', { name: /Asha Kulkarni/ }, SLOW));
    await user.click(within(dialog).getByRole('radio', { name: 'A paper file' }));
    // No encounter to choose: a paper chart is of the file.
    expect(within(dialog).queryByRole('combobox', { name: /Encounter/ })).not.toBeInTheDocument();

    await user.click(within(dialog).getByRole('button', { name: 'Open chart' }));
    expect(await within(dialog).findByText('Enter the number on the file.')).toBeInTheDocument();
    expect(within(dialog).getByText('Enter the visit day.')).toBeInTheDocument();
    expect(sent('POST', '/api/v1/mrd/records')).toEqual([]);

    await user.type(within(dialog).getByRole('textbox', { name: /^File number/ }), 'IP/2016/431');
    await user.click(within(dialog).getByRole('radio', { name: 'In-patient stay' }));
    await user.type(within(dialog).getByLabelText(/^Admitted on/), '2016-03-02');
    await user.type(within(dialog).getByLabelText(/^Discharged on/), '2016-03-09');
    await user.click(within(dialog).getByRole('button', { name: 'Open chart' }));

    await waitFor(() =>
      expect(sent('POST', '/api/v1/mrd/records')).toEqual([
        {
          patientId: PATIENT.id,
          encounterId: null,
          admissionId: null,
          visitId: null,
          assignedPractitionerId: null,
          dueDate: null,
          isMlc: false,
          paper: {
            fileNumber: 'IP/2016/431',
            isInpatient: true,
            episodeFrom: '2016-03-02',
            episodeTo: '2016-03-09',
            note: null,
          },
        },
      ]),
    );
    // A paper chart has no record to scan for gaps.
    expect(await screen.findByText('IP/2016/431', {}, SLOW)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Check for gaps' })).not.toBeInTheDocument();
  });

  it('opens a chart for a patient’s out-patient encounter and goes to it', async () => {
    const user = userEvent.setup();
    openScreen('/mrd/records');
    await user.click(await screen.findByRole('button', { name: 'New chart' }, SLOW));
    const dialog = await screen.findByRole('dialog', { name: 'New chart' }, SLOW);

    await user.click(within(dialog).getByRole('button', { name: 'Open chart' }));
    expect(await within(dialog).findByText('Find the patient.')).toBeInTheDocument();

    await user.type(within(dialog).getByRole('combobox', { name: /Patient/ }), 'Asha');
    await user.click(await screen.findByRole('option', { name: /Asha Kulkarni/ }, SLOW));
    const select = await within(dialog).findByRole('combobox', { name: /Encounter/ }, SLOW);
    // The in-patient encounter is not offered: its chart follows the admission.
    expect(
      within(select)
        .getAllByRole('option')
        .map((o) => o.textContent),
    ).toEqual(['Choose the encounter', encounterOptionLabel(OP)]);
    await user.selectOptions(select, OP.id);
    await user.click(within(dialog).getByRole('switch', { name: 'Medico-legal case (MLC)' }));
    await user.click(within(dialog).getByRole('button', { name: 'Open chart' }));

    await waitFor(() =>
      expect(sent('POST', '/api/v1/mrd/records')).toEqual([
        {
          patientId: PATIENT.id,
          encounterId: OP.id,
          admissionId: null,
          visitId: null,
          assignedPractitionerId: OP.practitionerId,
          dueDate: null,
          isMlc: true,
        },
      ]),
    );
    expect(await screen.findByRole('heading', { name: 'Chart · Asha Kulkarni' }, SLOW)).toBeInTheDocument();
  });

  it('says so when the encounter already has a chart', async () => {
    chartExists = true;
    const user = userEvent.setup();
    openScreen('/mrd/records');
    await user.click(await screen.findByRole('button', { name: 'New chart' }, SLOW));
    const dialog = await screen.findByRole('dialog', { name: 'New chart' }, SLOW);
    await user.type(within(dialog).getByRole('combobox', { name: /Patient/ }), 'Asha');
    await user.click(await screen.findByRole('option', { name: /Asha Kulkarni/ }, SLOW));
    await user.selectOptions(await within(dialog).findByRole('combobox', { name: /Encounter/ }, SLOW), OP.id);
    await user.click(within(dialog).getByRole('button', { name: 'Open chart' }));
    expect(
      await within(dialog).findByText('A chart already exists for this admission or encounter.', {}, SLOW),
    ).toBeInTheDocument();
  });
});
