import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Core from '@hc360/core';
import { ApiError, bridge, BridgeError, trainingCapture, type AadhaarCaptureSetup } from '@hc360/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as PatientsApi from './api';
import { identityCheckApi, type IdentityCheckDto } from './api';
import { IdentityCheckForm, IdentityChecksPanel } from './IdentityChecksPanel';

// The server and HC360 Desktop are outside the test: the server calls are replaced, and the fingerprint reader can be
// made to capture (or fail).
vi.mock('@hc360/core', async (importOriginal) => {
  const core = await importOriginal<typeof Core>();
  return { ...core, bridge: { ...core.bridge, isDesktop: true, captureFingerprint: vi.fn() } };
});
const readChecks = vi.hoisted(() => vi.fn());
const readSetup = vi.hoisted(() => vi.fn());
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof PatientsApi>();
  return {
    ...actual,
    identityCheckApi: { verify: vi.fn() },
    patientQueries: {
      ...actual.patientQueries,
      identityChecks: (id: string) => ({
        queryKey: actual.patientKeys.identityChecks(id),
        queryFn: () => readChecks(id),
      }),
      identitySetup: () => ({ queryKey: actual.patientKeys.identitySetup, queryFn: () => readSetup() }),
    },
  };
});
const permissions = vi.hoisted(() => ({ verify: true }));
vi.mock('@/lib/permissions', () => ({
  usePermission: (permission: string) => permission !== 'patients.identity.verify' || permissions.verify,
}));

const AADHAAR = '999941057058';
const PATIENT = { id: 'p1', fullName: 'Asha Nair', aadhaarLast4: null, mergedIntoPatientId: null };

function setup(patch: Partial<AadhaarCaptureSetup> = {}): AadhaarCaptureSetup {
  return {
    provider: 'uidai',
    problem: null,
    environment: 'P',
    fingerDataType: 2,
    fingerCount: 1,
    timeoutSeconds: 15,
    training: false,
    ...patch,
  };
}

function check(patch: Partial<IdentityCheckDto> = {}): IdentityCheckDto {
  return {
    id: 'c1',
    method: 'AadhaarFingerprint',
    outcome: 'Verified',
    message: 'UIDAI confirmed the fingerprint is that of the Aadhaar number holder.',
    aadhaarLast4: '7058',
    usedVirtualId: false,
    linkedAadhaar: true,
    provider: 'uidai',
    errorCode: null,
    transactionId: 'HC360ABC',
    responseCode: 'f3a9c1',
    deviceProviderId: 'MANTRA.AND',
    deviceModelId: 'MFS110',
    checkedAt: '2026-10-20T05:00:00Z',
    checkedByName: 'Asha Desk',
    ...patch,
  };
}

function renderWithProviders(ui: ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <FluentProvider theme={webLightTheme}>{ui}</FluentProvider>
    </QueryClientProvider>,
  );
}

describe('IdentityChecksPanel', () => {
  beforeEach(() => {
    permissions.verify = true;
    readChecks.mockReset().mockResolvedValue([check()]);
    readSetup.mockReset().mockResolvedValue(setup());
  });

  it('lists the checks UIDAI answered, with only the number’s last 4 digits', async () => {
    renderWithProviders(<IdentityChecksPanel patient={PATIENT} />);

    expect(await screen.findByText('Aadhaar number ending 7058, linked to the record')).toBeInTheDocument();
    expect(screen.getByText('Verified')).toBeInTheDocument();
    expect(screen.getByText('UIDAI f3a9c1')).toBeInTheDocument();
    expect(screen.getByText('MANTRA.AND MFS110')).toBeInTheDocument();
    expect(await screen.findByRole('button', { name: 'Check identity with fingerprint' })).toBeInTheDocument();
  });

  it('says why identity cannot be checked until it is set up', async () => {
    readSetup.mockResolvedValue(
      setup({
        provider: null,
        problem: 'Aadhaar authentication is not configured. Set it up in Administration → Integrations.',
      }),
    );
    renderWithProviders(<IdentityChecksPanel patient={PATIENT} />);

    expect(await screen.findByText(/Aadhaar authentication is not configured/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Check identity with fingerprint' })).not.toBeInTheDocument();
  });

  it('warns that the simulator answers in training', async () => {
    readSetup.mockResolvedValue(setup({ provider: 'simulator', training: true }));
    renderWithProviders(<IdentityChecksPanel patient={PATIENT} />);

    expect(await screen.findByText(/A simulator answers instead of UIDAI/)).toBeInTheDocument();
  });

  it('offers no check to staff who may not make one, and does not ask for the setup', async () => {
    permissions.verify = false;
    renderWithProviders(<IdentityChecksPanel patient={PATIENT} />);

    expect(await screen.findByText('Verified')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Check identity with fingerprint' })).not.toBeInTheDocument();
    expect(readSetup).not.toHaveBeenCalled();
  });

  it('offers no check on a record merged into another', async () => {
    renderWithProviders(<IdentityChecksPanel patient={{ ...PATIENT, mergedIntoPatientId: 'p2' }} />);

    expect(await screen.findByText('Verified')).toBeInTheDocument();
    await waitFor(() => expect(readSetup).toHaveBeenCalled());
    expect(screen.queryByRole('button', { name: 'Check identity with fingerprint' })).not.toBeInTheDocument();
  });
});

describe('IdentityCheckForm', () => {
  beforeEach(() => {
    vi.mocked(identityCheckApi.verify).mockReset().mockResolvedValue(check());
    vi.mocked(bridge.captureFingerprint).mockReset();
  });

  function renderForm(options: { training?: boolean; aadhaarLast4?: string | null } = {}) {
    const onClose = vi.fn();
    renderWithProviders(
      <IdentityCheckForm
        patient={{ ...PATIENT, aadhaarLast4: options.aadhaarLast4 ?? null }}
        setup={setup(options.training ? { provider: 'simulator', training: true } : {})}
        onClose={onClose}
      />,
    );
    return { onClose, submit: () => screen.getByRole('button', { name: /^Check (with UIDAI|\(training\))$/ }) };
  }

  async function fillNumberAndConsent(user: ReturnType<typeof userEvent.setup>, number = '9999 4105 7058') {
    await user.type(screen.getByRole('textbox', { name: /Aadhaar number or Virtual ID/ }), number);
    await user.click(screen.getByRole('checkbox', { name: /The patient agreed to Aadhaar authentication/ }));
  }

  it('captures a finger with the reader, checks it, and shows UIDAI’s answer', async () => {
    const user = userEvent.setup();
    vi.mocked(bridge.captureFingerprint).mockResolvedValue({
      pidData: '<PidData>encrypted</PidData>',
      quality: 72,
      device: { providerId: 'MANTRA.AND', modelId: 'MFS110', serialNumber: '123', serviceId: 'M', serviceVersion: '1' },
    });
    const { submit } = renderForm();
    await fillNumberAndConsent(user);
    expect(submit()).toBeDisabled();

    await user.click(screen.getByRole('button', { name: 'Capture fingerprint' }));
    expect(await screen.findByText('Fingerprint captured: MANTRA.AND MFS110, quality 72')).toBeInTheDocument();
    expect(bridge.captureFingerprint).toHaveBeenCalledWith(
      expect.objectContaining({ timeoutSeconds: 15, environment: 'P' }),
    );
    await user.click(submit());

    expect(
      await screen.findByText('UIDAI confirmed the fingerprint is that of the Aadhaar number holder.'),
    ).toBeInTheDocument();
    expect(identityCheckApi.verify).toHaveBeenCalledWith('p1', {
      aadhaarNumber: AADHAAR,
      pidData: '<PidData>encrypted</PidData>',
      consent: true,
    });
  });

  it('needs the patient’s consent before checking', async () => {
    const user = userEvent.setup();
    const { submit } = renderForm({ training: true });
    await user.type(screen.getByRole('textbox', { name: /Aadhaar number or Virtual ID/ }), AADHAAR);
    await user.click(screen.getByRole('button', { name: 'Training: a finger that matches' }));

    expect(submit()).toBeDisabled();
    expect(submit()).toHaveAttribute('title', 'Ask for the patient’s consent to Aadhaar authentication first.');
  });

  it('uses a training capture where the simulator answers', async () => {
    const user = userEvent.setup();
    vi.mocked(identityCheckApi.verify).mockResolvedValue(
      check({
        outcome: 'NotVerified',
        message: 'Training: the fingerprint did not match.',
        provider: 'simulator',
        errorCode: '300',
      }),
    );
    const { submit } = renderForm({ training: true });
    await fillNumberAndConsent(user);
    await user.click(screen.getByRole('button', { name: 'Training: one that does not' }));
    await user.click(submit());

    expect(await screen.findByText('Training: the fingerprint did not match.')).toBeInTheDocument();
    expect(identityCheckApi.verify).toHaveBeenCalledWith('p1', {
      aadhaarNumber: AADHAAR,
      pidData: trainingCapture(false),
      consent: true,
    });
    expect(screen.getByRole('button', { name: 'Check again' })).toBeInTheDocument();
  });

  it('says why the reader could not capture, in HC360 Desktop’s words', async () => {
    const user = userEvent.setup();
    vi.mocked(bridge.captureFingerprint).mockRejectedValue(
      new BridgeError('no_reader', 'No fingerprint reader was found on this computer. Check that it is plugged in.'),
    );
    const { submit } = renderForm();
    await fillNumberAndConsent(user);
    await user.click(screen.getByRole('button', { name: 'Capture fingerprint' }));

    expect(
      await screen.findByText('No fingerprint reader was found on this computer. Check that it is plugged in.'),
    ).toBeInTheDocument();
    expect(submit()).toBeDisabled();
  });

  it('warns before checking another number than the record’s, and shows the server’s refusal', async () => {
    const user = userEvent.setup();
    vi.mocked(identityCheckApi.verify).mockRejectedValue(
      new ApiError({
        status: 422,
        title: 'Unprocessable',
        code: 'patient.aadhaar_mismatch',
        detail:
          'This is not the Aadhaar number on Asha Nair’s record (it ends in 1234). Check the number with the patient.',
      }),
    );
    const { submit } = renderForm({ training: true, aadhaarLast4: '1234' });
    await fillNumberAndConsent(user);

    expect(
      screen.getByText('The record has an Aadhaar number ending in 1234. Check the number with the patient.'),
    ).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Training: a finger that matches' }));
    await user.click(submit());

    expect(await screen.findByText(/This is not the Aadhaar number on Asha Nair’s record/)).toBeInTheDocument();
    // UIDAI was not asked: the capture is kept for the corrected number.
    expect(screen.getByText('Training capture: a finger that matches')).toBeInTheDocument();
  });

  it('refuses a number that is not an Aadhaar number or a Virtual ID', async () => {
    const user = userEvent.setup();
    renderForm();
    await user.type(screen.getByRole('textbox', { name: /Aadhaar number or Virtual ID/ }), '999941057059');

    expect(screen.getByText('Enter the 12-digit Aadhaar number or the 16-digit Virtual ID.')).toBeInTheDocument();
  });
});
