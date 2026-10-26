import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { ApiError } from '@hc360/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { stubBrowserApis } from '@/test/appScreen';
import type { PatientDetail } from './api';
import { AbhaChecksPanel, type AbhaCheck } from './AbhaChecksPanel';

// Checking a patient's ABHA with ABDM from the Identity tab: the code is sent for the ABHA number on record, the
// patient reads it out, and what ABDM's ABHA says differently from the record is shown. The server is replaced.

const get = vi.hoisted(() => vi.fn());
const post = vi.hoisted(() => vi.fn());
vi.mock('@/lib/api', () => ({ api: { get, post } }));
const state = vi.hoisted(() => ({ verify: true, abdm: true }));
vi.mock('@/lib/permissions', () => ({
  usePermission: (permission: string) => permission !== 'patients.identity.verify' || state.verify,
}));
vi.mock('@/lib/features', () => ({ useFeatureOn: () => state.abdm }));

const PATIENT = {
  id: 'p1',
  fullName: 'Meera Krishnan',
  abhaNumberMasked: 'xx-xxxx-xxxx-0123',
  phone: '+919876543210',
  mergedIntoPatientId: null,
} as unknown as PatientDetail;

function check(patch: Partial<AbhaCheck> = {}): AbhaCheck {
  return {
    id: 'a1',
    patientId: 'p1',
    method: 'AbhaNumber',
    status: 'CodeSent',
    sentMessage: 'OTP sent to mobile number ending with ******3210',
    codeExpiresAt: '2026-10-26T10:10:00Z',
    attemptsLeft: 3,
    startedAt: '2026-10-26T10:00:00Z',
    completedAt: null,
    abhaNumberMasked: null,
    abhaAddress: null,
    name: null,
    gender: null,
    yearOfBirth: null,
    kycVerified: null,
    differences: [],
    error: null,
    ...patch,
  };
}

function renderPanel(ui: ReactNode = <AbhaChecksPanel patient={PATIENT} />) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <FluentProvider theme={webLightTheme}>{ui}</FluentProvider>
    </QueryClientProvider>,
  );
}

describe('AbhaChecksPanel', () => {
  beforeEach(() => {
    stubBrowserApis();
    state.verify = true;
    state.abdm = true;
    get.mockReset().mockResolvedValue([]);
    post.mockReset();
  });

  it('sends a code for the ABHA number on record and shows what differs once the patient reads it out', async () => {
    post.mockResolvedValueOnce(check()).mockResolvedValueOnce(
      check({
        status: 'Mismatch',
        completedAt: '2026-10-26T10:02:00Z',
        abhaNumberMasked: 'xx-xxxx-xxxx-0123',
        differences: ['The year of birth on the ABHA is 1983.'],
      }),
    );
    renderPanel();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Check ABHA with ABDM' }));
    expect(screen.getByRole('radio', { name: /ABHA number on record \(xx-xxxx-xxxx-0123\)/ })).toBeChecked();
    await user.click(screen.getByRole('button', { name: 'Send code' }));
    expect(post).toHaveBeenCalledWith('/api/v1/abdm/patients/p1/abha-checks', { method: 'AbhaNumber' });

    expect(await screen.findByText(/ending with \*{6}3210/)).toBeInTheDocument();
    const checkCode = screen.getByRole('button', { name: 'Check code' });
    expect(checkCode).toBeDisabled();
    await user.type(screen.getByRole('textbox', { name: /Code/ }), '24a6810');
    await user.click(checkCode);
    expect(post).toHaveBeenLastCalledWith('/api/v1/abdm/abha-checks/a1/code', { code: '246810' });
    expect(await screen.findByText(/The year of birth on the ABHA is 1983/)).toBeInTheDocument();
  });

  it("shows ABDM's refusal of a wrong code and keeps the code box", async () => {
    post
      .mockResolvedValueOnce(check())
      .mockRejectedValueOnce(
        new ApiError({ status: 422, title: 'ABDM did not accept the code (Entered OTP is incorrect.). 2 tries left.' }),
      );
    renderPanel();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Check ABHA with ABDM' }));
    await user.click(screen.getByRole('button', { name: 'Send code' }));
    await user.type(await screen.findByRole('textbox', { name: /Code/ }), '111111');
    await user.click(screen.getByRole('button', { name: 'Check code' }));
    expect(await screen.findByText(/2 tries left/)).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: /Code/ })).toBeInTheDocument();
  });

  it('lists earlier checks, and offers no check without the identity permission', async () => {
    state.verify = false;
    get.mockResolvedValue([
      check({
        status: 'Verified',
        completedAt: '2026-10-25T09:00:00Z',
        abhaNumberMasked: 'xx-xxxx-xxxx-0123',
        kycVerified: true,
      }),
    ]);
    renderPanel();
    expect(await screen.findByText('Matches the record')).toBeInTheDocument();
    expect(screen.getByText('KYC verified')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Check ABHA with ABDM' })).not.toBeInTheDocument();
  });

  it('is not shown while ABDM is switched off', async () => {
    state.abdm = false;
    renderPanel();
    await waitFor(() => expect(screen.queryByText('ABHA checks with ABDM')).not.toBeInTheDocument());
    expect(get).not.toHaveBeenCalled();
  });
});
