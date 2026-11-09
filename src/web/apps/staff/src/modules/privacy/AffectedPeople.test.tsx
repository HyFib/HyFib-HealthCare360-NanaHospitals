import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Ui from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as PrivacyApi from './api';
import { privacyApi, type AffectedPatientItem, type BreachDetail } from './api';
import { AffectedPeople } from './AffectedPeople';
import { useBreachCommand } from './useBreachCommand';

// The server and the toasts are outside the test; the list's own behaviour is what is tested.
const listAffected = vi.hoisted(() => vi.fn());
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof PrivacyApi>();
  return {
    ...actual,
    privacyApi: { ...actual.privacyApi, removeAffected: vi.fn() },
    privacyQueries: {
      ...actual.privacyQueries,
      affected: (id: string, page: number) => ({
        queryKey: [...actual.privacyKeys.affected(id), page],
        queryFn: () => listAffected(id, page),
      }),
    },
  };
});
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));
vi.mock('@/lib/permissions', () => ({ usePermission: () => true }));

const person: AffectedPatientItem = {
  patientId: 'p1',
  uhid: 'NH2600011',
  fullName: 'Meera Pillai',
  phone: '+91******3210',
  hasEmail: false,
  addedAt: '2026-11-07T05:00:00Z',
  notifiedAt: null,
  noticeChannels: null,
};

function breach(patch: Partial<BreachDetail> = {}): BreachDetail {
  return {
    id: 'b1',
    breachNumber: 'DPB260004',
    title: 'Discharge summaries e-mailed to the wrong address',
    description: 'Two summaries went to another patient.',
    status: 'BoardInformed',
    awareAt: '2026-11-07T04:00:00Z',
    occurredFrom: null,
    occurredTo: null,
    nature: ['Confidentiality'],
    location: null,
    dataCategories: ['Health'],
    estimatedAffected: 2,
    consequences: null,
    mitigation: null,
    principalSteps: null,
    cause: null,
    findings: null,
    remediation: null,
    principalNotice: null,
    boardInformedAt: '2026-11-07T05:00:00Z',
    reportDueAt: '2026-11-10T04:00:00Z',
    extendedUntil: null,
    reportSubmittedAt: null,
    boardReference: null,
    closedAt: null,
    closingNote: null,
    notifiedAt: null,
    reportOverdue: false,
    affectedListed: 1,
    notified: 0,
    withoutContact: 0,
    missingForReport: [],
    missingForNotice: [],
    version: 4,
    certInType: null,
    certInReportDueAt: null,
    certInReportedAt: null,
    certInReference: null,
    certInReportOverdue: false,
    ...patch,
  } as BreachDetail;
}

function Harness({ detail }: { detail: BreachDetail }) {
  const command = useBreachCommand();
  return <AffectedPeople breach={detail} page={1} onPageChange={() => undefined} command={command} />;
}

function renderList(detail: BreachDetail) {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <FluentProvider theme={webLightTheme}>
        <Harness detail={detail} />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

describe('taking a patient off a breach’s notification list (MAKER-CHECKER.md, slice 14)', () => {
  beforeEach(() => {
    listAffected.mockReset().mockResolvedValue({ items: [person], page: 1, pageSize: 25, totalCount: 1 });
    vi.mocked(privacyApi.removeAffected).mockReset();
    Object.values(notify).forEach((spy) => spy.mockClear());
  });

  it('asks why, and says the Data Protection Officer decides while the patient stays on the list', async () => {
    vi.mocked(privacyApi.removeAffected).mockResolvedValue({ id: 'a1' } as unknown as Awaited<
      ReturnType<typeof privacyApi.removeAffected>
    >);
    const user = userEvent.setup();
    renderList(breach());
    await user.click(await screen.findByRole('button', { name: 'Ask to take Meera Pillai off the list' }));

    const dialog = await screen.findByRole('dialog', { name: 'Take Meera Pillai off the list' });
    expect(within(dialog).getByText(/The Data Protection Officer decides, not you/)).toBeInTheDocument();
    await expectNoAxeViolations(document.body);
    await user.type(within(dialog).getByRole('textbox', { name: /^Why they are taken off/ }), 'Not in the e-mail');
    await user.click(within(dialog).getByRole('button', { name: 'Ask for approval' }));

    await waitFor(() => expect(privacyApi.removeAffected).toHaveBeenCalledWith('b1', ['p1'], 'Not in the e-mail'));
    expect(notify.success).toHaveBeenCalledWith('Asked for approval', expect.stringContaining('stays on the list'));
  });

  it('shows a removal already waiting and offers no other meanwhile', async () => {
    renderList(breach({ removalAskedAt: '2026-11-08T05:00:00Z' }));
    expect(await screen.findByText(/waits for the Data\s+Protection Officer/)).toBeInTheDocument();
    expect(await screen.findByRole('button', { name: 'Ask to take Meera Pillai off the list' })).toBeDisabled();
  });
});
