import type * as Core from '@hc360/core';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  fakeServer,
  openScreen,
  SCREEN_TESTS,
  SLOW,
  STAFF_USER,
  stubBrowserApis,
  type FakeRequest,
} from '@/test/appScreen';
import { problem } from '@/test/fakeApi';
import type { TemplateDto, TemplateListItem } from './api';
import type { OrderSetDto, ServiceItemDto } from './ordersApi';

// The template and order-set screens (EMR-02, EMR-05), whole with the shell, against a fake server: what each list
// shows and filters, the request a new record and an edit send (an edit on the version it was opened at, REL-04), who
// may change them, and how the server's refusals read.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const id = (suffix: string) => `0192a000-0000-7000-8000-00000000e${suffix}`;

const SPECIALTIES = [
  { id: id('a01'), code: 'GM', name: 'General Medicine' },
  { id: id('a02'), code: 'PED', name: 'Paediatrics' },
];

const service = (
  overrides: Partial<ServiceItemDto> & Pick<ServiceItemDto, 'id' | 'code' | 'name'>,
): ServiceItemDto => ({
  shortName: null,
  category: 'Laboratory',
  departmentId: null,
  basePrice: 300,
  taxRate: 0,
  sacCode: null,
  isDiscountAllowed: true,
  isPriceEditable: false,
  description: null,
  isActive: true,
  version: 1,
  ...overrides,
});
const HBA1C = service({ id: id('b01'), code: 'LAB-HBA1C', name: 'HbA1c' });
const FBS = service({ id: id('b02'), code: 'LAB-FBS', name: 'Fasting blood sugar' });
const ECG = service({ id: id('b03'), code: 'CAR-ECG', name: 'ECG 12-lead', category: 'Cardiology' });
const asOrdered = (s: ServiceItemDto) => ({ serviceItemId: s.id, code: s.code, name: s.name, category: s.category });

const DM: TemplateDto = {
  id: id('c01'),
  code: 'GM-DM',
  name: 'General Medicine — Diabetes review',
  specialtyCode: 'GM',
  ownerPractitionerId: null,
  chiefComplaint: 'Diabetes follow-up',
  historyOfPresentIllness: null,
  examination: 'BP, weight, foot examination',
  assessment: null,
  plan: 'Continue medicines',
  followUpAdvice: 'Review with reports',
  followUpDays: 30,
  commonComplaints: ['Polyuria', 'Tingling feet'],
  defaultOrders: [asOrdered(HBA1C)],
  medications: [
    {
      id: id('c11'),
      drugItemId: null,
      drugName: 'Metformin 500',
      genericName: 'metformin',
      strength: '500 mg',
      dosageForm: 'Tablet',
      route: 'Oral',
      dose: '1 tablet',
      frequency: 'BD',
      customFrequency: null,
      durationDays: 30,
      instructions: 'After food',
    },
  ],
  displayOrder: 30,
  isActive: true,
  version: 3,
};

const listItem = (t: TemplateDto): TemplateListItem => ({
  id: t.id,
  code: t.code,
  name: t.name,
  specialtyCode: t.specialtyCode,
  isPersonal: t.ownerPractitionerId !== null,
  displayOrder: t.displayOrder,
  isActive: t.isActive,
});
const PED: TemplateListItem = {
  id: id('c02'),
  code: 'PED-OPD',
  name: 'Paediatrics — OP consultation',
  specialtyCode: 'PED',
  isPersonal: false,
  displayOrder: 10,
  isActive: true,
};
const MINE: TemplateListItem = {
  id: id('c03'),
  code: 'MI-ASTHMA',
  name: 'My asthma review',
  specialtyCode: null,
  isPersonal: true,
  displayOrder: 0,
  isActive: false,
};

const DIABETES_SET: OrderSetDto = {
  id: id('d01'),
  code: 'DM-WORKUP',
  name: 'Diabetes work-up',
  specialtyCode: 'GM',
  description: 'Baseline for a new diabetic',
  services: [asOrdered(HBA1C), asOrdered(FBS)],
  isActive: true,
  version: 7,
};
const CARDIAC_SET: OrderSetDto = {
  id: id('d02'),
  code: 'CARDIAC',
  name: 'Chest pain panel',
  specialtyCode: null,
  description: null,
  services: [asOrdered(ECG)],
  isActive: false,
  version: 2,
};

/** The server's answer to a save on a version that is no longer the record's. */
const concurrency = () =>
  new Response(JSON.stringify({ title: 'Changed meanwhile', status: 409, code: 'concurrency' }), {
    status: 409,
    headers: { 'content-type': 'application/problem+json' },
  });

let server: ReturnType<typeof fakeServer>;
/** The diabetes template as the server has it now. */
let dm: TemplateDto;
let orderSets: OrderSetDto[];

function listTemplates(request: FakeRequest): TemplateListItem[] {
  const specialty = request.url.searchParams.get('specialtyCode');
  const inactive = request.url.searchParams.get('includeInactive') === 'true';
  return [listItem(dm), PED, MINE].filter(
    (t) => (inactive || t.isActive) && (!specialty || t.specialtyCode === specialty),
  );
}

function saveTemplate(request: FakeRequest) {
  const body = request.body as { version: number; name: string };
  if (body.version !== dm.version) {
    return concurrency();
  }
  dm = { ...dm, name: body.name, version: dm.version + 1 };
  return dm;
}

function listOrderSets(request: FakeRequest): OrderSetDto[] {
  const specialty = request.url.searchParams.get('specialtyCode');
  const inactive = request.url.searchParams.get('includeInactive') === 'true';
  return orderSets.filter(
    (s) => (inactive || s.isActive) && (!specialty || s.specialtyCode === null || s.specialtyCode === specialty),
  );
}

function saveOrderSet(request: FakeRequest) {
  const setId = request.path.split('/').at(-1);
  const body = request.body as { version: number; name: string };
  const current = orderSets.find((s) => s.id === setId);
  if (!current || current.version !== body.version) {
    return concurrency();
  }
  const saved = { ...current, name: body.name, version: current.version + 1 };
  orderSets = orderSets.map((s) => (s.id === setId ? saved : s));
  return saved;
}

beforeEach(() => {
  stubBrowserApis();
  localStorage.clear();
  dm = DM;
  orderSets = [DIABETES_SET, CARDIAC_SET];
  server = fakeServer({
    'GET /api/v1/masters/lookups/specialties': SPECIALTIES,
    'GET /api/v1/masters/lookups/services': (request: FakeRequest) => {
      const term = (request.url.searchParams.get('search') ?? '').toLowerCase();
      return [HBA1C, FBS, ECG].filter((s) => `${s.name} ${s.code}`.toLowerCase().includes(term));
    },
    'GET /api/v1/emr/favourites': [],
    'GET /api/v1/emr/drugs': [],
    'GET /api/v1/emr/templates': listTemplates,
    'GET /api/v1/emr/templates/*': () => dm,
    'POST /api/v1/emr/templates': (request: FakeRequest) => ({
      ...DM,
      id: id('c09'),
      ...(request.body as object),
      version: 1,
    }),
    'PUT /api/v1/emr/templates/*': saveTemplate,
    'GET /api/v1/clinical/order-sets': listOrderSets,
    'POST /api/v1/clinical/order-sets': (request: FakeRequest) => ({
      ...DIABETES_SET,
      id: id('d09'),
      ...(request.body as object),
      version: 1,
    }),
    'PUT /api/v1/clinical/order-sets/*': saveOrderSet,
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

/** What was sent to `method path`, in order. */
const sent = (method: string, path: string) =>
  server.requests.filter((request) => request.method === method && request.path === path).map((r) => r.body);

/** The open drawer, by its title. */
const drawerTitled = async (title: string | RegExp) =>
  (await screen.findByRole('dialog', { name: title }, SLOW)) as HTMLElement;

/** Picks a service from the catalogue search inside `within`. */
async function pickService(
  user: ReturnType<typeof userEvent.setup>,
  container: HTMLElement,
  label: RegExp,
  term: string,
  name: string,
) {
  await user.type(within(container).getByRole('combobox', { name: label }), term);
  await user.click(await screen.findByRole('option', { name: new RegExp(name) }, SLOW));
}

/** Clinical staff who may read templates but not maintain them. */
const NURSE = { ...STAFF_USER, roles: ['Nurse'], permissions: ['clinical.emr.read', 'masters.read'] };

describe('clinical note templates (EMR-02)', SCREEN_TESTS, () => {
  it('lists the templates by speciality, filters by speciality on the server, and shows the inactive on request', async () => {
    const user = userEvent.setup();
    openScreen('/emr/templates');
    const table = await screen.findByRole('table', { name: 'Clinical note templates' }, SLOW);
    expect(await within(table).findByText('General Medicine — Diabetes review', {}, SLOW)).toBeInTheDocument();
    expect(within(table).getByText('GM-DM')).toBeInTheDocument();
    expect(await within(table).findByText('General Medicine')).toBeInTheDocument();
    expect(within(table).getByText('Paediatrics — OP consultation')).toBeInTheDocument();
    expect(within(table).queryByText('My asthma review')).not.toBeInTheDocument();

    await user.selectOptions(screen.getByRole('combobox', { name: 'Speciality' }), 'PED');
    await waitFor(() =>
      expect(within(table).queryByText('General Medicine — Diabetes review')).not.toBeInTheDocument(),
    );
    expect(within(table).getByText('Paediatrics — OP consultation')).toBeInTheDocument();
    expect(server.requests.some((r) => r.url.searchParams.get('specialtyCode') === 'PED')).toBe(true);

    await user.selectOptions(screen.getByRole('combobox', { name: 'Speciality' }), '');
    await user.click(screen.getByRole('switch', { name: 'Show inactive' }));
    expect(await within(table).findByText('My asthma review', {}, SLOW)).toBeInTheDocument();
    expect(within(table).getByText('Only me')).toBeInTheDocument();
    expect(within(table).getByText('Inactive')).toBeInTheDocument();
  });

  it('a new template sends the server contract: code in capitals, blanks as null, complaints, orders and medicines', async () => {
    const user = userEvent.setup();
    openScreen('/emr/templates');
    await screen.findByText('General Medicine — Diabetes review', {}, SLOW);
    await user.click(screen.getByRole('button', { name: 'New template' }));
    const drawer = await drawerTitled('New template');

    await user.type(within(drawer).getByRole('textbox', { name: /^Code/ }), 'gm-cough');
    await user.type(within(drawer).getByRole('textbox', { name: /^Name/ }), 'Cough and cold');
    await user.selectOptions(within(drawer).getByRole('combobox', { name: 'Speciality' }), 'GM');
    await user.type(within(drawer).getByRole('textbox', { name: 'Chief complaint' }), 'Cough for 3 days');
    await user.type(within(drawer).getByRole('spinbutton', { name: 'Follow up in (days)' }), '5');
    await user.type(
      within(drawer).getByRole('textbox', { name: /^Common complaints/ }),
      'Cough{Enter}Fever{Enter}Cough',
    );
    await pickService(user, drawer, /^Tests and services/, 'fasting', 'Fasting blood sugar');
    expect(await within(drawer).findByText('LAB-FBS')).toBeInTheDocument();

    await user.click(within(drawer).getByRole('button', { name: 'Add medicine' }));
    const medicine = within(drawer).getByRole('group', { name: 'Medicine 1' });
    await user.type(within(medicine).getByRole('combobox', { name: 'Medicine 1 name' }), 'Paracetamol 500');
    await user.type(within(medicine).getByRole('textbox', { name: /^Dose/ }), '1 tablet');
    await user.selectOptions(within(medicine).getByRole('combobox', { name: /^Frequency/ }), 'TDS');
    const days = within(medicine).getByRole('spinbutton', { name: /^Days/ });
    await user.clear(days);
    await user.type(days, '3');
    await user.click(within(drawer).getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(sent('POST', '/api/v1/emr/templates')).toHaveLength(1));
    expect(sent('POST', '/api/v1/emr/templates')[0]).toEqual({
      code: 'GM-COUGH',
      name: 'Cough and cold',
      specialtyCode: 'GM',
      personal: false,
      chiefComplaint: 'Cough for 3 days',
      historyOfPresentIllness: null,
      examination: null,
      assessment: null,
      plan: null,
      followUpAdvice: null,
      followUpDays: 5,
      commonComplaints: ['Cough', 'Fever'],
      defaultServiceItemIds: [FBS.id],
      medications: [
        {
          drugItemId: null,
          drugName: 'Paracetamol 500',
          genericName: null,
          strength: null,
          dosageForm: null,
          route: null,
          dose: '1 tablet',
          frequency: 'TDS',
          customFrequency: null,
          durationDays: 3,
          instructions: null,
        },
      ],
      displayOrder: 0,
      isActive: true,
      version: null,
    });
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'New template' })).not.toBeInTheDocument());
  });

  it('a template is checked as the server checks it before anything is sent', async () => {
    const user = userEvent.setup();
    openScreen('/emr/templates');
    await screen.findByText('General Medicine — Diabetes review', {}, SLOW);
    await user.click(screen.getByRole('button', { name: 'New template' }));
    const drawer = await drawerTitled('New template');
    await user.type(within(drawer).getByRole('textbox', { name: /^Code/ }), 'GM DM');
    await user.click(within(drawer).getByRole('button', { name: 'Add medicine' }));
    await user.selectOptions(within(drawer).getByRole('combobox', { name: /^Frequency/ }), 'Custom');
    await user.click(within(drawer).getByRole('button', { name: 'Save' }));

    expect(
      await within(drawer).findByText('Use letters, digits and hyphens only, without spaces.'),
    ).toBeInTheDocument();
    expect(within(drawer).getByText('Enter the name doctors pick it by.')).toBeInTheDocument();
    expect(within(drawer).getByText('Enter the medicine.')).toBeInTheDocument();
    expect(within(drawer).getByText('Enter the dose, e.g. 1 tablet.')).toBeInTheDocument();
    expect(within(drawer).getByText('Describe the frequency, e.g. 1-0-1.')).toBeInTheDocument();
    expect(sent('POST', '/api/v1/emr/templates')).toHaveLength(0);
  });

  it('an edit loads the template and is saved on the version it was loaded at', async () => {
    const user = userEvent.setup();
    openScreen('/emr/templates');
    await user.click(await screen.findByText('General Medicine — Diabetes review', {}, SLOW));
    const drawer = await drawerTitled('General Medicine — Diabetes review');
    const name = await within(drawer).findByRole('textbox', { name: /^Name/ }, SLOW);
    expect(within(drawer).getByRole('textbox', { name: 'Examination' })).toHaveValue('BP, weight, foot examination');
    expect(within(drawer).getByText('HbA1c')).toBeInTheDocument();
    expect(within(drawer).getByRole('combobox', { name: 'Medicine 1 name' })).toHaveValue('Metformin 500');
    await user.clear(name);
    await user.type(name, 'Diabetes review');
    await user.click(within(drawer).getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(sent('PUT', `/api/v1/emr/templates/${DM.id}`)).toHaveLength(1));
    expect(sent('PUT', `/api/v1/emr/templates/${DM.id}`)[0]).toEqual({
      code: 'GM-DM',
      name: 'Diabetes review',
      specialtyCode: 'GM',
      personal: false,
      chiefComplaint: 'Diabetes follow-up',
      historyOfPresentIllness: null,
      examination: 'BP, weight, foot examination',
      assessment: null,
      plan: 'Continue medicines',
      followUpAdvice: 'Review with reports',
      followUpDays: 30,
      commonComplaints: ['Polyuria', 'Tingling feet'],
      defaultServiceItemIds: [HBA1C.id],
      medications: [
        {
          drugItemId: null,
          drugName: 'Metformin 500',
          genericName: 'metformin',
          strength: '500 mg',
          dosageForm: 'Tablet',
          route: 'Oral',
          dose: '1 tablet',
          frequency: 'BD',
          customFrequency: null,
          durationDays: 30,
          instructions: 'After food',
        },
      ],
      displayOrder: 30,
      isActive: true,
      version: 3,
    });
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    // The list shows the new name, read again.
    const table = await screen.findByRole('table', { name: 'Clinical note templates' }, SLOW);
    expect(await within(table).findByText('Diabetes review', {}, SLOW)).toBeInTheDocument();
  });

  it('a template someone else saved meanwhile is not overwritten: the refusal is shown and Reload brings their version', async () => {
    const user = userEvent.setup();
    openScreen('/emr/templates');
    await user.click(await screen.findByText('General Medicine — Diabetes review', {}, SLOW));
    const drawer = await drawerTitled('General Medicine — Diabetes review');
    const name = await within(drawer).findByRole('textbox', { name: /^Name/ }, SLOW);
    // Another doctor renames it while this drawer is open.
    dm = { ...dm, name: 'Diabetes (type 2) review', version: 4 };
    await user.clear(name);
    await user.type(name, 'Diabetes review');
    await user.click(within(drawer).getByRole('button', { name: 'Save' }));

    expect(await within(drawer).findByText('Someone else changed this template', {}, SLOW)).toBeInTheDocument();
    expect((sent('PUT', `/api/v1/emr/templates/${DM.id}`)[0] as { version: number }).version).toBe(3);
    expect(dm.name).toBe('Diabetes (type 2) review');
    expect(within(drawer).getByRole('button', { name: 'Save' })).toBeDisabled();

    await user.click(within(drawer).getByRole('button', { name: 'Reload' }));
    await waitFor(() =>
      expect(within(drawer).getByRole('textbox', { name: /^Name/ })).toHaveValue('Diabetes (type 2) review'),
    );
    expect(within(drawer).queryByText('Someone else changed this template')).not.toBeInTheDocument();
  });

  it('a code another template has is shown on the code', async () => {
    const user = userEvent.setup();
    server = fakeServer({
      'GET /api/v1/masters/lookups/specialties': SPECIALTIES,
      'GET /api/v1/emr/templates': listTemplates,
      'POST /api/v1/emr/templates': new Response(
        JSON.stringify({
          title: 'Conflict',
          status: 409,
          code: 'template.code_in_use',
          detail: 'Template code GM-DM is already used.',
        }),
        { status: 409, headers: { 'content-type': 'application/problem+json' } },
      ),
    });
    openScreen('/emr/templates');
    await screen.findByText('General Medicine — Diabetes review', {}, SLOW);
    await user.click(screen.getByRole('button', { name: 'New template' }));
    const drawer = await drawerTitled('New template');
    await user.type(within(drawer).getByRole('textbox', { name: /^Code/ }), 'GM-DM');
    await user.type(within(drawer).getByRole('textbox', { name: /^Name/ }), 'Another diabetes template');
    await user.click(within(drawer).getByRole('button', { name: 'Save' }));
    expect(await within(drawer).findByText('Template code GM-DM is already used.', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByRole('dialog', { name: 'New template' })).toBeInTheDocument();
  });

  it('staff who may not maintain templates see the list but cannot add or open one', async () => {
    const user = userEvent.setup();
    openScreen('/emr/templates', NURSE);
    const table = await screen.findByRole('table', { name: 'Clinical note templates' }, SLOW);
    await user.click(await within(table).findByText('General Medicine — Diabetes review', {}, SLOW));
    expect(screen.queryByRole('button', { name: 'New template' })).not.toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(sent('GET', `/api/v1/emr/templates/${DM.id}`)).toHaveLength(0);
  });

  it('a list the server cannot load says so and offers to retry', async () => {
    server = fakeServer({
      'GET /api/v1/masters/lookups/specialties': SPECIALTIES,
      'GET /api/v1/emr/templates': problem(500, 'The templates could not be read'),
    });
    openScreen('/emr/templates');
    expect((await screen.findAllByText('The templates could not be read', {}, SLOW)).length).toBeGreaterThan(0);
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument();
  });
});

describe('order sets (EMR-05)', SCREEN_TESTS, () => {
  it('lists the order sets with their services; a speciality also lists the sets for every speciality', async () => {
    const user = userEvent.setup();
    openScreen('/emr/order-sets');
    const table = await screen.findByRole('table', { name: 'Order sets' }, SLOW);
    expect(await within(table).findByText('Diabetes work-up', {}, SLOW)).toBeInTheDocument();
    expect(within(table).getByText('2 services')).toBeInTheDocument();
    expect(within(table).getByText('HbA1c, Fasting blood sugar')).toBeInTheDocument();
    expect(within(table).queryByText('Chest pain panel')).not.toBeInTheDocument();

    await user.click(screen.getByRole('switch', { name: 'Show inactive' }));
    expect(await within(table).findByText('Chest pain panel', {}, SLOW)).toBeInTheDocument();
    expect(within(table).getByText('Every speciality')).toBeInTheDocument();

    await user.selectOptions(screen.getByRole('combobox', { name: /^Speciality/ }), 'PED');
    await waitFor(() => expect(within(table).queryByText('Diabetes work-up')).not.toBeInTheDocument());
    expect(within(table).getByText('Chest pain panel')).toBeInTheDocument();
    expect(server.requests.some((r) => r.url.searchParams.get('specialtyCode') === 'PED')).toBe(true);
  });

  it('a new order set needs a service, and sends the server contract', async () => {
    const user = userEvent.setup();
    openScreen('/emr/order-sets');
    await screen.findByText('Diabetes work-up', {}, SLOW);
    await user.click(screen.getByRole('button', { name: 'New order set' }));
    const drawer = await drawerTitled('New order set');
    await user.type(within(drawer).getByRole('textbox', { name: /^Code/ }), 'cardiac-2');
    await user.type(within(drawer).getByRole('textbox', { name: /^Name/ }), 'Cardiac screen');
    await user.click(within(drawer).getByRole('button', { name: 'Save' }));
    expect(await within(drawer).findByText('Add at least one service.')).toBeInTheDocument();
    expect(sent('POST', '/api/v1/clinical/order-sets')).toHaveLength(0);

    await pickService(user, drawer, /^Services/, 'ecg', 'ECG 12-lead');
    await pickService(user, drawer, /^Services/, 'fasting', 'Fasting blood sugar');
    await user.click(within(drawer).getByRole('button', { name: 'Remove ECG 12-lead' }));
    await pickService(user, drawer, /^Services/, 'hba1c', 'HbA1c');
    await user.click(within(drawer).getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(sent('POST', '/api/v1/clinical/order-sets')).toHaveLength(1));
    expect(sent('POST', '/api/v1/clinical/order-sets')[0]).toEqual({
      code: 'CARDIAC-2',
      name: 'Cardiac screen',
      specialtyCode: null,
      description: null,
      serviceItemIds: [FBS.id, HBA1C.id],
      isActive: true,
      version: null,
    });
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'New order set' })).not.toBeInTheDocument());
  });

  it('an order set is edited, and deactivated, on the version it was opened at', async () => {
    const user = userEvent.setup();
    openScreen('/emr/order-sets');
    await user.click(await screen.findByText('Diabetes work-up', {}, SLOW));
    const drawer = await drawerTitled('Diabetes work-up');
    expect(within(drawer).getByRole('textbox', { name: 'Description' })).toHaveValue('Baseline for a new diabetic');
    await user.click(within(drawer).getByRole('switch', { name: 'Active (offered when ordering)' }));
    await user.click(within(drawer).getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(sent('PUT', `/api/v1/clinical/order-sets/${DIABETES_SET.id}`)).toHaveLength(1));
    expect(sent('PUT', `/api/v1/clinical/order-sets/${DIABETES_SET.id}`)[0]).toEqual({
      code: 'DM-WORKUP',
      name: 'Diabetes work-up',
      specialtyCode: 'GM',
      description: 'Baseline for a new diabetic',
      serviceItemIds: [HBA1C.id, FBS.id],
      isActive: false,
      version: 7,
    });
  });

  it('an order set someone else saved meanwhile is refused, and Reload opens their version', async () => {
    const user = userEvent.setup();
    openScreen('/emr/order-sets');
    await user.click(await screen.findByText('Diabetes work-up', {}, SLOW));
    const drawer = await drawerTitled('Diabetes work-up');
    orderSets = orderSets.map((s) => (s.id === DIABETES_SET.id ? { ...s, name: 'Diabetes baseline', version: 8 } : s));
    const name = within(drawer).getByRole('textbox', { name: /^Name/ });
    await user.clear(name);
    await user.type(name, 'Diabetes panel');
    await user.click(within(drawer).getByRole('button', { name: 'Save' }));

    expect(await within(drawer).findByText('Someone else changed this order set', {}, SLOW)).toBeInTheDocument();
    expect((sent('PUT', `/api/v1/clinical/order-sets/${DIABETES_SET.id}`)[0] as { version: number }).version).toBe(7);
    await user.click(within(drawer).getByRole('button', { name: 'Reload' }));
    const reloaded = await drawerTitled('Diabetes baseline');
    expect(within(reloaded).getByRole('textbox', { name: /^Name/ })).toHaveValue('Diabetes baseline');
    expect(within(reloaded).queryByText('Someone else changed this order set')).not.toBeInTheDocument();
  });

  it('staff who may not maintain order sets see the list but cannot add or open one', async () => {
    const user = userEvent.setup();
    openScreen('/emr/order-sets', NURSE);
    await user.click(await screen.findByText('Diabetes work-up', {}, SLOW));
    expect(screen.queryByRole('button', { name: 'New order set' })).not.toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('a list the server cannot load says so and offers to retry', async () => {
    server = fakeServer({
      'GET /api/v1/masters/lookups/specialties': SPECIALTIES,
      'GET /api/v1/clinical/order-sets': problem(503, 'The order sets could not be read'),
    });
    openScreen('/emr/order-sets');
    expect((await screen.findAllByText('The order sets could not be read', {}, SLOW)).length).toBeGreaterThan(0);
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument();
  });
});
