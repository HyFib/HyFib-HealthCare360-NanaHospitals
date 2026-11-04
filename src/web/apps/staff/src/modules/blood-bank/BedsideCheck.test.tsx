import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { ApiError } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as WitnessApi from '@/modules/witness/api';
import type * as BedsideApi from './bedsideApi';
import { BedsideCheckDialog, PatientBlood } from './PatientBlood';

// A unit of blood is started only after two staff check it at the bedside: the wristband and the bag are read, the
// observations taken, and the second member of staff confirms at this screen with their own user name and password.

const confirm = vi.hoisted(() => vi.fn());
vi.mock('@/modules/witness/api', async (importOriginal) => {
  const actual = await importOriginal<typeof WitnessApi>();
  return { ...actual, witnessApi: { ...actual.witnessApi, confirm } };
});
const start = vi.hoisted(() => vi.fn());
const wardUnits = vi.hoisted(() => ({ current: null as unknown }));
vi.mock('./bedsideApi', async (importOriginal) => {
  const actual = await importOriginal<typeof BedsideApi>();
  const { queryOptions } = await import('@tanstack/react-query');
  return {
    ...actual,
    bedsideApi: { ...actual.bedsideApi, start },
    bedsideQueries: {
      units: (patientId: string) =>
        queryOptions({
          queryKey: ['blood-bank', 'patient-units', patientId],
          queryFn: () => Promise.resolve(wardUnits.current as BedsideApi.PatientBloodUnits),
        }),
    },
  };
});
const notify = vi.hoisted(() => ({ success: vi.fn(), warning: vi.fn(), error: vi.fn(), info: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const PASSWORD = 'Witness-pass-1';
const CONFIRMED: WitnessApi.WitnessConfirmed = {
  confirmationId: 'c0000000-0000-4000-8000-000000000001',
  userId: 'u0000000-0000-4000-8000-000000000002',
  name: 'Sister Rani Thomas',
  expiresAt: '2026-11-04T10:05:00Z',
};

const inAnHour = () => new Date(Date.now() + 3_600_000).toISOString();

const unit = (over: Partial<BedsideApi.WardBloodUnit> = {}): BedsideApi.WardBloodUnit => ({
  issueId: 'i1',
  requestNumber: 'BR-26-000041',
  unitNumber: 'NH26000123-R',
  componentType: 'PRBC',
  unitBloodGroup: 'BPositive',
  volumeMl: 280,
  expiresAt: '2026-12-01T00:00:00Z',
  isEmergencyRelease: false,
  crossMatchValidUntil: inAnHour(),
  issuedAt: new Date().toISOString(),
  issuedToName: 'Staff nurse Asha',
  startBy: inAnHour(),
  returnedAt: null,
  consentCapturedAt: '2026-11-03T09:00:00Z',
  transfusion: null,
  ...over,
});

function open(u: BedsideApi.WardBloodUnit = unit()) {
  render(
    <QueryClientProvider client={new QueryClient()}>
      <FluentProvider theme={webLightTheme}>
        <BedsideCheckDialog unit={u} patientId="p1" patientBloodGroup="BPositive" onClose={vi.fn()} />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

async function read() {
  await userEvent.type(screen.getByLabelText(/Patient's wristband/), 'UH-000123');
  // Read from the bag as it comes (scanned, or typed in lower case): what is sent is what was read, not the unit on screen.
  await userEvent.type(screen.getByLabelText(/Unit number on the bag/), ' nh26000123-r ');
}

async function observe() {
  await userEvent.type(screen.getByLabelText(/Temperature/), '36.8');
  await userEvent.type(screen.getByLabelText(/Pulse/), '84');
  await userEvent.type(screen.getByLabelText(/Respiratory rate/), '16');
  await userEvent.type(screen.getByLabelText(/BP systolic/), '118');
  await userEvent.type(screen.getByLabelText(/BP diastolic/), '76');
}

async function readAndObserve() {
  await read();
  await observe();
}

async function witnessConfirms() {
  await userEvent.type(screen.getByLabelText(/Witness's user name/), 'nurse2');
  await userEvent.type(screen.getByLabelText(/Witness's password/), PASSWORD);
  await userEvent.click(screen.getByRole('button', { name: 'Confirm witness' }));
  expect(await screen.findByText(CONFIRMED.name)).toBeInTheDocument();
}

beforeEach(() => {
  confirm.mockReset();
  start.mockReset();
});

describe('the bedside check of a unit of blood', () => {
  it('starts the unit only once read, observed and witnessed, and sends what was read with the confirmation, never the password', async () => {
    confirm.mockResolvedValue(CONFIRMED);
    start.mockResolvedValue({});
    open();

    const startButton = screen.getByRole('button', { name: 'Start transfusion' });
    await readAndObserve();
    expect(startButton).toBeDisabled();

    await witnessConfirms();
    expect(confirm).toHaveBeenCalledWith(expect.objectContaining({ purpose: 'blood.bedside_check', patientId: 'p1' }));
    expect(startButton).toBeEnabled();

    // Without the wristband, the bag or an observation, the unit is not started.
    for (const label of [/Patient's wristband/, /Unit number on the bag/, /Pulse/]) {
      const field = screen.getByLabelText(label);
      const value = (field as HTMLInputElement).value;
      await userEvent.clear(field);
      expect(startButton).toBeDisabled();
      await userEvent.type(field, value);
      expect(startButton).toBeEnabled();
    }
    await userEvent.click(startButton);

    await waitFor(() => expect(start).toHaveBeenCalledTimes(1));
    const [issueId, body] = start.mock.calls[0] as [string, BedsideApi.StartTransfusionRequest];
    expect(issueId).toBe('i1');
    expect(body).toMatchObject({
      wristbandUhid: 'UH-000123',
      bagUnitNumber: 'nh26000123-r',
      witnessConfirmationId: CONFIRMED.confirmationId,
      baseline: { temperatureC: 36.8, pulsePerMinute: 84, systolicMmHg: 118, diastolicMmHg: 76, respiratoryRate: 16 },
      consentDeferredReason: null,
    });
    expect(JSON.stringify(body)).not.toContain(PASSWORD);
  });

  it('does not start without the consent for this stay', async () => {
    confirm.mockResolvedValue(CONFIRMED);
    open(unit({ consentCapturedAt: null }));

    expect(screen.getByText('No consent for this stay')).toBeInTheDocument();
    await readAndObserve();
    await witnessConfirms();

    expect(screen.getByRole('button', { name: 'Start transfusion' })).toBeDisabled();
  });

  it('starts a unit released in an emergency before consent only with the reason, and sends it', async () => {
    confirm.mockResolvedValue(CONFIRMED);
    start.mockResolvedValue({});
    open(unit({ consentCapturedAt: null, isEmergencyRelease: true, crossMatchValidUntil: null }));

    await readAndObserve();
    await witnessConfirms();
    const startButton = screen.getByRole('button', { name: 'Start transfusion' });
    expect(startButton).toBeDisabled();

    await userEvent.type(screen.getByLabelText(/Why consent cannot be taken now/), 'Unconscious, no relative present');
    await userEvent.click(startButton);

    await waitFor(() => expect(start).toHaveBeenCalledTimes(1));
    expect((start.mock.calls[0] as [string, BedsideApi.StartTransfusionRequest])[1].consentDeferredReason).toBe(
      'Unconscious, no relative present',
    );
  });

  it('says a unit out for more than 4 hours is too late, and does not start it', () => {
    open(unit({ startBy: new Date(Date.now() - 60_000).toISOString() }));

    expect(screen.getByText('Too late to start')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Start transfusion' })).toBeDisabled();
  });

  it('asks the witness again when the confirmation ran out before the start was saved', async () => {
    confirm.mockResolvedValue(CONFIRMED);
    start.mockRejectedValue(
      new ApiError({ status: 422, title: 'Cannot complete this action', code: 'witness.confirmation_unusable' }),
    );
    open();

    await readAndObserve();
    await witnessConfirms();
    await userEvent.click(screen.getByRole('button', { name: 'Start transfusion' }));

    expect(await screen.findByLabelText(/Witness's user name/)).toBeInTheDocument();
    expect(screen.queryByText(CONFIRMED.name)).not.toBeInTheDocument();
  });
});

describe("the ward's list of a patient's blood units", () => {
  const transfusion = (status: BedsideApi.TransfusionStatus): BedsideApi.TransfusionDto => ({
    id: 't1',
    issueId: 'i2',
    startedAt: '2026-11-04T09:00:00Z',
    startedByName: 'Sister Mary',
    witnessName: 'Sister Rani Thomas',
    consentId: 'c1',
    consentDeferredReason: null,
    status,
    endedAt: null,
    endedByName: null,
    volumeMl: null,
    stopReason: null,
    observations: [],
    version: 3,
  });

  function list(canTransfuse: boolean) {
    wardUnits.current = {
      patientId: 'p1',
      uhid: 'UH-000123',
      patientName: 'Ravi Kumar',
      patientBloodGroup: 'BPositive',
      units: [
        unit({ issueId: 'i1', unitNumber: 'NH26000123-R' }),
        unit({ issueId: 'i2', unitNumber: 'NH26000124-R', transfusion: transfusion('InProgress') }),
        unit({ issueId: 'i3', unitNumber: 'NH26000125-R', returnedAt: '2026-11-04T08:20:00Z' }),
      ],
    } satisfies BedsideApi.PatientBloodUnits;
    render(
      <QueryClientProvider client={new QueryClient()}>
        <FluentProvider theme={webLightTheme}>
          <PatientBlood patientId="p1" canTransfuse={canTransfuse} />
        </FluentProvider>
      </QueryClientProvider>,
    );
  }

  it('says what became of each unit and offers the check only for one not started, and the end for one running', async () => {
    list(true);

    expect(await screen.findByText('NH26000123-R')).toBeInTheDocument();
    expect(screen.getByText('Not started')).toBeInTheDocument();
    expect(screen.getByText('Running')).toBeInTheDocument();
    expect(screen.getByText('Returned')).toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'Bedside check' })).toHaveLength(1);
    expect(screen.getAllByRole('button', { name: 'End transfusion' })).toHaveLength(1);
    expect(screen.getByText(/checked with Sister Rani Thomas/)).toBeInTheDocument();
  });

  it('offers nothing to someone who may not transfuse', async () => {
    list(false);

    expect(await screen.findByText('NH26000123-R')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Bedside check' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'End transfusion' })).not.toBeInTheDocument();
  });
});
