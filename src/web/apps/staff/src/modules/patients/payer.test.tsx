import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as InsuranceApi from '@/modules/insurance/api';
import type * as PatientsApi from './api';
import type { VisitListItem } from './api';
import { payerText, payerVisit, usePatientPayer } from './payer';

// The server and the signed-in user are outside the test.
const server = vi.hoisted(() => ({ visits: vi.fn(), visit: vi.fn(), payer: vi.fn() }));
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof PatientsApi>();
  return {
    ...actual,
    patientQueries: {
      ...actual.patientQueries,
      visits: (id: string) => ({ queryKey: ['visits', id], queryFn: () => server.visits(id) }),
      visit: (id: string) => ({ queryKey: ['visit', id], queryFn: () => server.visit(id) }),
    },
  };
});
vi.mock('@/modules/insurance/api', async (importOriginal) => {
  const actual = await importOriginal<typeof InsuranceApi>();
  return {
    ...actual,
    insuranceQueries: {
      ...actual.insuranceQueries,
      payer: (id: string) => ({ queryKey: ['payer', id], queryFn: () => server.payer(id) }),
    },
  };
});
const permissions = vi.hoisted(() => ({ granted: new Set<string>(['visits.read', 'billing.op.create']) }));
vi.mock('@/lib/permissions', () => ({
  usePermission: (p: string | readonly string[]) =>
    (typeof p === 'string' ? [p] : p).some((x) => permissions.granted.has(x)),
}));

function visit(patch: Partial<VisitListItem>): VisitListItem {
  return {
    id: 'v1',
    visitNumber: 'OP-1',
    patientId: 'p1',
    uhid: 'NHP-1',
    patientName: 'Ravi Kumar',
    gender: 'Male',
    age: '40 y',
    type: 'OutPatient',
    status: 'Completed',
    departmentId: null,
    departmentName: null,
    practitionerId: null,
    practitionerName: null,
    visitedAt: '2026-10-01T05:00:00Z',
    completedAt: null,
    isFollowUp: false,
    isFreeFollowUp: false,
    payerType: 'Self',
    isMlc: false,
    tokenLabel: null,
    tokenStatus: null,
    ...patch,
  };
}

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

describe('payerVisit', () => {
  it('prefers the open visit, then the latest one that was not cancelled', () => {
    const done = visit({ id: 'done' });
    const open = visit({ id: 'open', status: 'InProgress' });
    const cancelled = visit({ id: 'x', status: 'Cancelled' });
    expect(payerVisit([cancelled, done, open])).toEqual({ visit: open, isOpen: true });
    expect(payerVisit([cancelled, done])).toEqual({ visit: done, isOpen: false });
    expect(payerVisit([cancelled])).toBeNull();
  });
});

describe('payerText', () => {
  it('names the payer type and, when known, the payer', () => {
    expect(payerText('Insurance', 'Star Health')).toBe('Payer: Insurance · Star Health');
    expect(payerText('Self', null)).toBe('Payer: Self-pay');
    expect(payerText('GovernmentScheme', null, false)).toBe('Last payer: Government scheme');
  });
});

describe('usePatientPayer', () => {
  beforeEach(() => {
    Object.values(server).forEach((spy) => spy.mockReset());
    permissions.granted = new Set(['visits.read', 'billing.op.create']);
  });

  it("shows the open visit's insurer by name", async () => {
    server.visits.mockResolvedValue({ items: [visit({ status: 'Open', payerType: 'Insurance' })] });
    server.visit.mockResolvedValue({ id: 'v1', payerId: 'payer-1' });
    server.payer.mockResolvedValue({ id: 'payer-1', name: 'Star Health' });
    const { result } = renderHook(() => usePatientPayer('p1'), { wrapper });
    await waitFor(() => expect(result.current).toBe('Payer: Insurance · Star Health'));
  });

  it('shows the payer type alone to staff who may not look up payers', async () => {
    permissions.granted = new Set(['visits.read']);
    server.visits.mockResolvedValue({ items: [visit({ status: 'Open', payerType: 'Corporate' })] });
    const { result } = renderHook(() => usePatientPayer('p1'), { wrapper });
    await waitFor(() => expect(result.current).toBe('Payer: Corporate'));
    expect(server.visit).not.toHaveBeenCalled();
  });

  it('asks nothing when the screen supplies the payer, or the user may not read visits', () => {
    const { result } = renderHook(() => usePatientPayer('p1', false), { wrapper });
    expect(result.current).toBeNull();
    permissions.granted = new Set();
    renderHook(() => usePatientPayer('p1'), { wrapper });
    expect(server.visits).not.toHaveBeenCalled();
  });
});
