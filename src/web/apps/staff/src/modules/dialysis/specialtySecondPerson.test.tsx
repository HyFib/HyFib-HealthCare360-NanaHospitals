import type * as Core from '@hc360/core';
import { useSession } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { Hc360ThemeProvider } from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, json, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import type { TransfusionReactionDto } from '@/modules/blood-bank/api';
import { ClassifyReactionDialog } from '@/modules/blood-bank/ReactionDialogs';
import type { DialysisProfileDto } from './api';
import { InfectionStatusDialog } from './InfectionStatusDialog';

// Slice 15 on the specialty screens, against a fake server: lowering a transfusion reaction says it waits for the blood
// bank medical officer and asks why; a dialysis patient taken off a positive status says it waits for a second person, and
// is asked for on the lab result chosen. The toasts are outside the test.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const REACTION_ID = '0192a000-0000-7000-8000-00000000d101';
const PATIENT_ID = '0192a000-0000-7000-8000-00000000d201';
const RESULT_ID = '0192a000-0000-7000-8000-00000000d301';

const reaction = {
  id: REACTION_ID,
  patientName: 'Asha Kulkarni',
  unitNumber: 'NHBB26-00366-PR',
  severity: 'Severe',
  imputability: 'Probable',
  type: 'FebrileNonHaemolytic',
  status: 'UnderInvestigation',
  version: 5,
} as unknown as TransfusionReactionDto;

const profile = {
  id: '0192a000-0000-7000-8000-00000000d202',
  patientId: PATIENT_ID,
  seroStatus: 'HBsAg',
  version: 9,
} as unknown as DialysisProfileDto;

beforeEach(() => {
  vi.clearAllMocks();
  stubBrowserApis();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function show(node: ReactNode) {
  useSession.setState({
    status: 'authenticated',
    accessToken: 'test-token',
    expiresAt: Date.now() + 3600_000,
    user: STAFF_USER,
    locked: false,
  });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <Hc360ThemeProvider>{node}</Hc360ThemeProvider>
    </QueryClientProvider>,
  );
}

describe('slice 15 on the specialty screens', SCREEN_TESTS, () => {
  it('says lowering a transfusion reaction waits for the medical officer, asks why, and sends it', async () => {
    const server = fakeServer({
      [`PUT /api/v1/blood-bank/reactions/${REACTION_ID}/classification`]: json(
        {
          id: '0192a000-0000-7000-8000-00000000d1a1',
          kind: 'bloodbank.reaction_lower',
          summary: 'Lower the transfusion reaction',
          canDecide: false,
        },
        202,
      ),
    });
    const user = userEvent.setup();
    show(<ClassifyReactionDialog reaction={reaction} onClose={() => undefined} />);

    const dialog = await screen.findByRole('dialog', {}, SLOW);
    await user.selectOptions(within(dialog).getByRole('combobox', { name: 'Severity' }), 'Mild');
    expect(await within(dialog).findByText('Waits for the blood bank medical officer')).toBeInTheDocument();
    expect(within(dialog).getByText(/severity Severe → Mild/)).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Ask the medical officer' }));
    expect(await within(dialog).findByText(/Say why the reaction is lowered/)).toBeInTheDocument();
    expect(server.requests.some((r) => r.method === 'PUT')).toBe(false);

    await user.type(
      within(dialog).getByRole('textbox', { name: /Why it is lowered/ }),
      'Bag culture sterile, fever settled',
    );
    await expectNoAxeViolations();
    await user.click(within(dialog).getByRole('button', { name: 'Ask the medical officer' }));
    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith('Lowering asked for', expect.stringContaining('medical officer')),
    );
    expect(server.requests.find((r) => r.method === 'PUT')?.body).toMatchObject({
      severity: 'Mild',
      imputability: 'Probable',
      reason: 'Bag culture sterile, fever settled',
      version: 5,
    });
  });

  it('asks for a patient to be taken off a positive status on the lab result chosen, waiting for a second person', async () => {
    const server = fakeServer({
      [`GET /api/v1/dialysis/patients/${PATIENT_ID}/infection-results`]: [
        {
          id: RESULT_ID,
          infection: 'HBsAg',
          testName: 'Hepatitis B surface antigen (HBsAg)',
          value: 'Non-reactive',
          flag: null,
          verifiedOn: '2026-11-05',
        },
        {
          id: 'other',
          infection: 'HCV',
          testName: 'Anti-HCV antibodies',
          value: 'Non-reactive',
          flag: null,
          verifiedOn: '2026-11-05',
        },
      ],
      [`POST /api/v1/dialysis/patients/${PATIENT_ID}/infection-status`]: json(
        {
          id: '0192a000-0000-7000-8000-00000000d2a1',
          kind: 'dialysis.infection_status',
          summary: 'Change infection status',
          canDecide: false,
        },
        202,
      ),
    });
    const user = userEvent.setup();
    show(<InfectionStatusDialog profile={profile} onClose={() => undefined} />);

    const dialog = await screen.findByRole('dialog', { name: 'Change infection status' }, SLOW);
    await user.click(within(dialog).getByRole('radio', { name: /^Sero-negative|^Negative/ }));
    expect(await within(dialog).findByText('Waits for a second person')).toBeInTheDocument();

    // Only the HBsAg result supports going back to negative.
    const result = await within(dialog).findByRole('radio', {
      name: /Hepatitis B surface antigen \(HBsAg\): Non-reactive/,
    });
    expect(within(dialog).queryByRole('radio', { name: /Anti-HCV/ })).not.toBeInTheDocument();
    await user.click(result);
    await user.type(within(dialog).getByRole('textbox', { name: /Reason/ }), 'Repeat HBsAg non-reactive');
    await expectNoAxeViolations();
    await user.click(within(dialog).getByRole('button', { name: 'Ask for the change' }));

    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith('Change asked for', expect.stringContaining('second person')),
    );
    expect(server.requests.find((r) => r.method === 'POST')?.body).toEqual({
      seroStatus: 'Negative',
      labResultId: RESULT_ID,
      reason: 'Repeat HBsAg non-reactive',
      version: 9,
    });
  });
});
