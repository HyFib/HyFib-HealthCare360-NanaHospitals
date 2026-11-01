import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ApprovalRequestDto } from '@/modules/approvals/api';
import { AmendReportDialog } from './AmendReportDialog';
import type * as LabApi from './api';
import { labQueries, type LabReportView } from './api';
import { LabReportPreview } from './LabReportPreview';
import { labReportNotes } from './reportBody';

// The server and the toasts are outside the test: what is tested is what the pathologist is told once the correction is
// sent, and what every reader of the report sees while one waits for approval.
const amendReport = vi.hoisted(() => vi.fn());
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof LabApi>();
  return { ...actual, labApi: { ...actual.labApi, amendReport } };
});
const notify = vi.hoisted(() => ({ success: vi.fn(), warning: vi.fn(), error: vi.fn(), info: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const view: LabReportView = {
  reportId: 'rep1',
  labOrderId: 'o1',
  reportNumber: 'NHLR260000045',
  status: 'Released',
  isPartial: false,
  revision: 0,
  letterhead: {
    facilityName: 'Nana Hospitals',
    address: null,
    phone: null,
    email: null,
    nabhNumber: null,
    letterheadNote: null,
    logoFileId: null,
  },
  patient: { id: 'p1', uhid: 'NH26000001', name: 'Asha Rao', gender: 'Female', age: '31Y', dateOfBirth: null },
  patientClass: 'External',
  referredBy: 'Dr. Kumar',
  clinicalNotes: null,
  orderedAt: '2026-09-26T03:00:00Z',
  firstReleasedAt: '2026-09-26T06:00:00Z',
  releasedAt: '2026-09-26T06:00:00Z',
  sections: [
    {
      section: 'Biochemistry',
      tests: [
        {
          orderItemId: 'i1',
          code: 'LAB-ELECTROLYTES',
          name: 'Serum electrolytes',
          status: 'Released',
          specimenType: 'Serum',
          method: null,
          interpretation: null,
          referralLabName: null,
          collectedAt: '2026-09-26T03:30:00Z',
          receivedAt: '2026-09-26T03:50:00Z',
          verifiedAt: '2026-09-26T05:50:00Z',
          releasedAt: '2026-09-26T06:00:00Z',
          results: [
            {
              resultId: 'k1',
              name: 'Potassium',
              groupName: null,
              value: '4.2',
              unit: 'mmol/L',
              flag: 'Normal',
              referenceText: '3.5 - 5.1',
              status: 'Verified',
              autoValidated: false,
              remarks: null,
              organism: null,
              colonyCount: null,
              susceptibilities: [],
              previousValue: null,
              previousResultAt: null,
            },
          ],
        },
      ],
    },
  ],
  signatories: [],
  hasAutoValidatedResults: false,
  amendments: [],
  verificationCode: null,
  verificationPath: null,
  verificationUrl: null,
  amendmentWaitingSince: null,
};

const asked = {
  id: 'a1',
  kind: 'diagnostics.lab_amendment',
  title: 'Lab report correction',
  summary: 'Correction of lab report NHLR260000045',
  status: 'Pending',
} as ApprovalRequestDto;

function renderWithProviders(node: ReactNode) {
  return render(
    <QueryClientProvider client={new QueryClient()}>
      <FluentProvider theme={webLightTheme}>{node}</FluentProvider>
    </QueryClientProvider>,
  );
}

async function correctPotassium() {
  const onClose = vi.fn();
  renderWithProviders(<AmendReportDialog view={view} onClose={onClose} />);
  await userEvent.type(screen.getByRole('textbox', { name: 'Corrected value for Potassium' }), '5.8');
  await userEvent.type(
    screen.getByRole('textbox', { name: /Reason for the amendment/ }),
    'Wrong tube read: the analyser printout says otherwise',
  );
  await userEvent.click(screen.getByRole('button', { name: 'Amend 1 result' }));
  await waitFor(() => expect(onClose).toHaveBeenCalled());
}

describe('a correction of a released lab report', () => {
  // Fluent's MessageBar measures itself; jsdom has no ResizeObserver.
  beforeAll(() => {
    vi.stubGlobal(
      'ResizeObserver',
      class {
        observe() {}
        unobserve() {}
        disconnect() {}
      },
    );
  });

  beforeEach(() => {
    amendReport.mockReset();
    Object.values(notify).forEach((spy) => spy.mockClear());
  });

  it('is made at once in a lab with one pathologist', async () => {
    amendReport.mockResolvedValue(undefined);
    await correctPotassium();
    expect(notify.success).toHaveBeenCalledWith(
      'Report amended (revision 1)',
      'The doctor and the patient are informed of the corrected report.',
    );
  });

  it('waits for another pathologist where the lab requires a separate verifier, and says so', async () => {
    amendReport.mockResolvedValue(asked);
    await correctPotassium();
    expect(notify.success).toHaveBeenCalledWith(
      'Asked for approval',
      'Another pathologist must approve this correction. Until then the report says a correction is waiting.',
    );
    expect(notify.success).not.toHaveBeenCalledWith('Report amended (revision 1)', expect.anything());
  });

  it('shows on the report while it waits, and names who asked for it once made', () => {
    const { unmount } = renderWithProviders(
      <LabReportPreview view={{ ...view, amendmentWaitingSince: '2026-09-26T08:00:00Z' }} />,
    );
    // At the top of the screen, with when it was asked for; and in the footnotes, as on the printed report.
    expect(
      screen.getByText(/^A correction of this report, asked for .+, is waiting for a second pathologist/),
    ).toBeVisible();
    expect(screen.getByText(/^An amendment of this report is waiting for a second pathologist/)).toBeVisible();
    unmount();

    renderWithProviders(
      <LabReportPreview
        view={{
          ...view,
          status: 'Amended',
          revision: 1,
          amendments: [
            {
              revision: 1,
              reason: 'Wrong tube read',
              changes: 'Potassium: 4.2 mmol/L -> 5.8 mmol/L',
              amendedAt: '2026-09-26T09:00:00Z',
              amendedBy: 'Dr. Ravi Menon',
              requestedBy: 'Dr. Meena Iyer',
            },
          ],
        }}
      />,
    );
    expect(screen.queryByText(/is waiting for a second pathologist/)).not.toBeInTheDocument();
    expect(screen.getByText(/Dr\. Ravi Menon \(asked for by Dr\. Meena Iyer\)/)).toBeVisible();
  });

  it('is announced first in the report notes, as the printed report says it', () => {
    const notes = labReportNotes({ ...view, amendmentWaitingSince: '2026-09-26T08:00:00Z' });
    expect(notes[0]).toBe(
      "An amendment of this report is waiting for a second pathologist's approval. Results on it may change.",
    );
    expect(labReportNotes(view)).toEqual([]);
  });
});

describe('a report with a correction waiting for approval', () => {
  // A rejection, a withdrawal or the request lapsing, on any workstation, sends the report page no lab event: while one
  // waits, the page asks for the report again, so Amend comes back once a correction may be asked for.
  const refetchInterval = labQueries.report('rep1').refetchInterval as unknown as (query: {
    state: { data?: LabReportView };
  }) => number | false;

  it('is asked for again every 30 seconds while the correction waits, and not once none does', () => {
    expect(refetchInterval({ state: { data: { ...view, amendmentWaitingSince: '2026-09-26T08:00:00Z' } } })).toBe(
      30_000,
    );
    expect(refetchInterval({ state: { data: view } })).toBe(false);
    expect(refetchInterval({ state: { data: undefined } })).toBe(false);
  });
});
