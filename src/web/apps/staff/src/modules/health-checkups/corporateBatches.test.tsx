import type * as Core from '@hc360/core';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { PatientListItem } from '@/modules/patients/api';
import { fakeServer, openScreen, paged, SCREEN_TESTS, SLOW, stubBrowserApis } from '@/test/appScreen';
import type { CorporateBatchResult, CorporatePayer, PackageDto } from './api';

// The corporate batches screen, whole, with a fake server: the company of a drive is a corporate payer of the payer
// master (MHC-05), and its id is what the batch is booked with, so every employee's check-up is billed to it.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const acme: CorporatePayer = {
  id: 'payer-acme',
  name: 'Acme Steel Ltd',
  contactPerson: 'Meena Iyer',
  phone: '9876543210',
};

const executive: PackageDto = {
  id: 'package-1',
  code: 'HC-EXEC',
  name: 'Executive Health Checkup',
  description: null,
  gender: 'Any',
  minAgeYears: null,
  maxAgeYears: null,
  price: 2400,
  serviceItemId: 'service-1',
  isActive: true,
  items: [],
  version: 1,
};

const employee: PatientListItem = {
  id: 'patient-1',
  uhid: 'NHP26000042',
  fullName: 'Ravi Kumar',
  gender: 'Male',
  dateOfBirth: '1980-04-01',
  age: '46Y',
  phoneMasked: null,
  category: 'General',
  isVip: false,
  isDeceased: false,
  registeredAt: '2026-10-01T05:00:00Z',
  lastVisitAt: null,
};

const booked: CorporateBatchResult = { batchId: 'batch-1', batchNumber: 'NHPHCB260001', booked: 1, skipped: [] };

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  stubBrowserApis();
  server = fakeServer({
    'GET /api/v1/health-checkups/corporate-batches': paged([]),
    'GET /api/v1/health-checkups/packages': [executive],
    'GET /api/v1/health-checkups/corporate-payers': [acme],
    'GET /api/v1/patients': paged([employee]),
    'POST /api/v1/health-checkups/corporate-batches': booked,
  });
});

afterEach(() => {
  expect(server.unanswered).toEqual([]);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('corporate batches', SCREEN_TESTS, () => {
  it('books the employees for a company of the payer master, which their check-ups are billed to', async () => {
    const user = userEvent.setup();
    openScreen('/health-checkups/corporate');
    await user.click((await screen.findAllByRole('button', { name: 'New batch' }, SLOW))[0]!);

    // The companies are the payer master's corporates; choosing one brings its contact for the drive.
    await user.selectOptions(await screen.findByRole('combobox', { name: /^Company/ }, SLOW), 'payer-acme');
    expect(screen.getByRole('textbox', { name: 'Contact person' })).toHaveValue('Meena Iyer');
    expect(screen.getByRole('textbox', { name: 'Contact phone' })).toHaveValue('9876543210');
    await user.selectOptions(screen.getByRole('combobox', { name: /^Package/ }), 'package-1');
    await user.type(screen.getByRole('combobox', { name: /^Add employee/ }), 'Ravi');
    await user.click(await screen.findByRole('option', { name: /Ravi Kumar/ }, SLOW));
    expect(screen.getByText(/billed to Acme Steel Ltd as each employee/)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Book 1 employee' }));

    expect(await screen.findByText('Batch NHPHCB260001 booked', {}, SLOW)).toBeInTheDocument();
    // From the check-up desk's own lookup: the insurance screens' payer list needs a permission the desk may not hold.
    expect(server.requests.some((r) => r.path === '/api/v1/insurance/payers')).toBe(false);
    const batch = server.requests.find(
      (r) => r.method === 'POST' && r.path === '/api/v1/health-checkups/corporate-batches',
    );
    expect(batch?.body).toMatchObject({
      payerId: 'payer-acme',
      contactPerson: 'Meena Iyer',
      contactPhone: '9876543210',
      packageId: 'package-1',
      patientIds: ['patient-1'],
    });
    expect(batch?.body).not.toHaveProperty('corporateName');
  });
});
