import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { ApiError } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as WitnessApi from '@/modules/witness/api';
import type * as NursingApi from '../nursingApi';
import { AdHocDoseDialog } from './PatientMar';
import { RecordDoseDialog, type DoseToRecord } from './RecordDoseDialog';

// A controlled drug given is witnessed by a second nurse at this screen, with her own user name and password. The
// password goes to the witness check alone; the dose carries only the confirmation it gave.

const confirm = vi.hoisted(() => vi.fn());
vi.mock('@/modules/witness/api', async (importOriginal) => {
  const actual = await importOriginal<typeof WitnessApi>();
  return { ...actual, witnessApi: { ...actual.witnessApi, confirm } };
});
const recordDose = vi.hoisted(() => vi.fn());
const addAdHocDose = vi.hoisted(() => vi.fn());
vi.mock('../nursingApi', async (importOriginal) => {
  const actual = await importOriginal<typeof NursingApi>();
  return { ...actual, nursingApi: { ...actual.nursingApi, recordDose, addAdHocDose } };
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

const morphine: DoseToRecord = {
  id: 'd1',
  drugName: 'Morphine 10 mg/ml Injection',
  dose: '2 mg',
  route: 'IV',
  scheduledAt: '2026-11-04T10:00:00Z',
  version: 7,
  admissionId: 'a1',
  patientId: 'p1',
  patientName: 'Ravi Kumar',
  isControlledDrug: true,
};

const morphineOrder: NursingApi.MedicationOrderDto = {
  id: 'o1',
  prescriptionId: null,
  drugItemId: null,
  drugName: 'Morphine 10 mg/ml Injection',
  genericName: 'Morphine',
  strength: '10 mg/ml',
  dose: '2 mg',
  route: 'IV',
  frequency: 'STAT',
  durationDays: 1,
  startAt: '2026-11-04T10:00:00Z',
  endAt: null,
  instructions: null,
  isControlledDrug: true,
  isAsNeeded: false,
  source: 'Manual',
  status: 'Active',
  lastDispensedAt: null,
  stoppedAt: null,
  stopReason: null,
  roundTimes: [],
};

function openExtra(order: NursingApi.MedicationOrderDto = morphineOrder) {
  render(
    <QueryClientProvider client={new QueryClient()}>
      <FluentProvider theme={webLightTheme}>
        <AdHocDoseDialog admissionId="a1" patientId="p1" orders={[order]} onClose={vi.fn()} />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

function open(dose: DoseToRecord = morphine) {
  render(
    <QueryClientProvider client={new QueryClient()}>
      <FluentProvider theme={webLightTheme}>
        <RecordDoseDialog dose={dose} onClose={vi.fn()} />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

async function witnessTypes(userName: string, password: string) {
  await userEvent.type(screen.getByLabelText(/Witness's user name/), userName);
  await userEvent.type(screen.getByLabelText(/Witness's password/), password);
}

beforeEach(() => {
  confirm.mockReset();
  recordDose.mockReset();
  addAdHocDose.mockReset();
});

describe('a controlled dose witnessed at the screen', () => {
  it('is recorded only once a second nurse has confirmed, and carries her confirmation, never her password', async () => {
    confirm.mockResolvedValue(CONFIRMED);
    recordDose.mockResolvedValue({});
    open();

    const record = screen.getByRole('button', { name: 'Record given' });
    expect(record).toBeDisabled();

    await witnessTypes('nurse2', PASSWORD);
    await userEvent.click(screen.getByRole('button', { name: 'Confirm witness' }));
    expect(confirm).toHaveBeenCalledWith({
      userName: 'nurse2',
      password: PASSWORD,
      code: null,
      purpose: 'mar.dose',
      patientId: 'p1',
    });
    expect(await screen.findByText(CONFIRMED.name)).toBeInTheDocument();
    expect(screen.queryByLabelText(/Witness's password/)).not.toBeInTheDocument();

    await userEvent.click(record);
    await waitFor(() => expect(recordDose).toHaveBeenCalledTimes(1));
    const [doseId, body] = recordDose.mock.calls[0] as [string, NursingApi.RecordAdministrationRequest];
    expect(doseId).toBe('d1');
    expect(body.witnessConfirmationId).toBe(CONFIRMED.confirmationId);
    expect(JSON.stringify(body)).not.toContain(PASSWORD);
  });

  it("asks for the witness's authenticator code when her account has one, keeping the password she typed", async () => {
    confirm
      .mockRejectedValueOnce(
        new ApiError({ status: 422, title: 'Cannot complete this action', code: 'witness.code_required' }),
      )
      .mockResolvedValueOnce(CONFIRMED);
    open();

    await witnessTypes('nurse3', PASSWORD);
    await userEvent.click(screen.getByRole('button', { name: 'Confirm witness' }));
    const code = await screen.findByLabelText(/Witness's authenticator code/);
    expect(screen.getByRole('button', { name: 'Confirm witness' })).toBeDisabled();
    await userEvent.type(code, '123456');
    await userEvent.click(screen.getByRole('button', { name: 'Confirm witness' }));

    expect(confirm).toHaveBeenLastCalledWith(
      expect.objectContaining({ userName: 'nurse3', password: PASSWORD, code: '123456' }),
    );
    expect(await screen.findByText(CONFIRMED.name)).toBeInTheDocument();
  });

  it('clears a password the server refused, and says why', async () => {
    confirm.mockRejectedValue(
      new ApiError({
        status: 422,
        title: 'Cannot complete this action',
        code: 'witness.invalid',
        detail: "The witness's user name, password or authenticator code is not right.",
      }),
    );
    open();

    await witnessTypes('nurse2', 'wrong-password');
    await userEvent.click(screen.getByRole('button', { name: 'Confirm witness' }));

    expect(await screen.findByText(/password or authenticator code is not right/)).toBeInTheDocument();
    expect(screen.getByLabelText(/Witness's password/)).toHaveValue('');
    expect(screen.getByRole('button', { name: 'Record given' })).toBeDisabled();
  });

  it('asks the witness again when her confirmation ran out before the dose was saved', async () => {
    confirm.mockResolvedValue(CONFIRMED);
    recordDose.mockRejectedValue(
      new ApiError({ status: 422, title: 'Cannot complete this action', code: 'witness.confirmation_unusable' }),
    );
    open();

    await witnessTypes('nurse2', PASSWORD);
    await userEvent.click(screen.getByRole('button', { name: 'Confirm witness' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Record given' }));

    expect(await screen.findByLabelText(/Witness's user name/)).toBeInTheDocument();
    expect(screen.queryByText(CONFIRMED.name)).not.toBeInTheDocument();
  });

  it('is not asked for when the drug is not controlled', () => {
    open({ ...morphine, drugName: 'Paracetamol 1 g Injection', isControlledDrug: false });

    expect(screen.queryByLabelText(/Witness's user name/)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Record given' })).toBeEnabled();
  });
});

describe('an extra dose of a controlled drug given now', () => {
  it('is witnessed at the screen as well, and carries the confirmation, never the password', async () => {
    confirm.mockResolvedValue(CONFIRMED);
    addAdHocDose.mockResolvedValue({});
    openExtra();

    const record = screen.getByRole('button', { name: 'Record as given' });
    expect(record).toBeDisabled();

    await witnessTypes('nurse2', PASSWORD);
    await userEvent.click(screen.getByRole('button', { name: 'Confirm witness' }));
    expect(confirm).toHaveBeenCalledWith(expect.objectContaining({ purpose: 'mar.dose', patientId: 'p1' }));
    expect(await screen.findByText(CONFIRMED.name)).toBeInTheDocument();

    await userEvent.click(record);
    await waitFor(() => expect(addAdHocDose).toHaveBeenCalledTimes(1));
    const [admissionId, body] = addAdHocDose.mock.calls[0] as [string, NursingApi.AddAdHocDoseRequest];
    expect(admissionId).toBe('a1');
    expect(body.witnessConfirmationId).toBe(CONFIRMED.confirmationId);
    expect(JSON.stringify(body)).not.toContain(PASSWORD);
  });

  it('asks the witness again when her confirmation ran out before the dose was saved', async () => {
    confirm.mockResolvedValue(CONFIRMED);
    addAdHocDose.mockRejectedValue(
      new ApiError({ status: 422, title: 'Cannot complete this action', code: 'witness.confirmation_unusable' }),
    );
    openExtra();

    await witnessTypes('nurse2', PASSWORD);
    await userEvent.click(screen.getByRole('button', { name: 'Confirm witness' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Record as given' }));

    expect(await screen.findByLabelText(/Witness's user name/)).toBeInTheDocument();
    expect(screen.queryByText(CONFIRMED.name)).not.toBeInTheDocument();
  });

  it('is not asked for when the drug is not controlled', () => {
    openExtra({ ...morphineOrder, drugName: 'Paracetamol 1 g Injection', isControlledDrug: false });

    expect(screen.queryByLabelText(/Witness's user name/)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Record as given' })).toBeEnabled();
  });
});
