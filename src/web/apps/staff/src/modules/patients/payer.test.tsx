import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as PatientsApi from './api';
import type { PatientPayer } from './api';
import { payerText, usePatientPayer } from './payer';

// The server (which chooses the payer, PatientPayerRule) and the signed-in user are outside the test.
const server = vi.hoisted(() => ({ payer: vi.fn() }));
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof PatientsApi>();
  return {
    ...actual,
    patientQueries: {
      ...actual.patientQueries,
      payer: (id: string) => ({ queryKey: ['payer', id], queryFn: () => server.payer(id) }),
    },
  };
});
const permissions = vi.hoisted(() => ({ granted: new Set<string>(['visits.read']) }));
vi.mock('@/lib/permissions', () => ({
  usePermission: (p: string | readonly string[]) =>
    (typeof p === 'string' ? [p] : p).some((x) => permissions.granted.has(x)),
}));

function payer(patch: Partial<PatientPayer>): PatientPayer {
  return {
    visitId: 'v1',
    isCurrentVisit: true,
    payerType: 'Self',
    payerId: null,
    payerName: null,
    policyNumber: null,
    validUntil: null,
    ...patch,
  };
}

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

describe('payerText', () => {
  it('names the payer type and, when known, the payer', () => {
    expect(payerText('Insurance', 'Star Health')).toBe('Payer: Insurance · Star Health');
    expect(payerText('Self', null)).toBe('Payer: Self-pay');
    expect(payerText('GovernmentScheme', null, false)).toBe('Last payer: Government scheme');
  });
});

describe('usePatientPayer', () => {
  beforeEach(() => {
    server.payer.mockReset();
    permissions.granted = new Set(['visits.read']);
  });

  it("shows the open visit's insurer by name", async () => {
    server.payer.mockResolvedValue({
      current: payer({ payerType: 'Insurance', payerId: 'payer-1', payerName: 'Star Health' }),
    });
    const { result } = renderHook(() => usePatientPayer('p1'), { wrapper });
    await waitFor(() => expect(result.current).toBe('Payer: Insurance · Star Health'));
    expect(server.payer).toHaveBeenCalledWith('p1');
  });

  it('shows the payer type alone when the server sends no name (staff who may not look payers up)', async () => {
    server.payer.mockResolvedValue({ current: payer({ payerType: 'Corporate', payerId: 'payer-2' }) });
    const { result } = renderHook(() => usePatientPayer('p1'), { wrapper });
    await waitFor(() => expect(result.current).toBe('Payer: Corporate'));
  });

  it("shows the last visit's payer as the last payer", async () => {
    server.payer.mockResolvedValue({ current: payer({ isCurrentVisit: false }) });
    const { result } = renderHook(() => usePatientPayer('p1'), { wrapper });
    await waitFor(() => expect(result.current).toBe('Last payer: Self-pay'));
  });

  it('shows nothing for a patient who has had no visit', async () => {
    server.payer.mockResolvedValue({ current: null });
    const { result } = renderHook(() => usePatientPayer('p1'), { wrapper });
    await waitFor(() => expect(server.payer).toHaveBeenCalled());
    expect(result.current).toBeNull();
  });

  it('asks nothing when the screen supplies the payer, or the user may not read visits', () => {
    const { result } = renderHook(() => usePatientPayer('p1', false), { wrapper });
    expect(result.current).toBeNull();
    permissions.granted = new Set();
    renderHook(() => usePatientPayer('p1'), { wrapper });
    expect(server.payer).not.toHaveBeenCalled();
  });
});
