import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import type { AuditDetail, AuditFinding } from './auditApi';
import { AuditDialog } from './Audits';

// An internal audit: findings recorded item by item, a non-compliance needing what was found, completion held until every
// item is answered and every gap has a CAPA, and closure once the CAPAs are verified. The server is replaced.

vi.mock('@/lib/api', () => ({ api: { get: vi.fn(), post: vi.fn(), put: vi.fn() } }));

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

beforeEach(() => vi.clearAllMocks());

function wrap(children: ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return (
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    </FluentProvider>
  );
}

const finding = (order: number, patch: Partial<AuditFinding> = {}): AuditFinding => ({
  id: `f${order}`,
  order,
  itemKey: `I0${order}`,
  itemText:
    order === 1
      ? 'Hand hygiene facilities are available at the point of care'
      : 'Biomedical waste is segregated at source',
  reference: 'HIC',
  result: null,
  observation: null,
  capaId: null,
  capaNumber: null,
  capaStatus: null,
  capaOwnerName: null,
  capaDueDate: null,
  ...patch,
});

const audit = (patch: Partial<AuditDetail> = {}): AuditDetail => ({
  id: 'a1',
  auditNumber: 'NHAUD260003',
  templateId: 't1',
  title: 'Hospital infection control',
  chapter: 'Hic',
  departmentId: null,
  area: 'ICU',
  scheduledOn: '2026-11-06',
  leadAuditorUserId: 'u1',
  leadAuditorName: 'Sr. Mary Thomas',
  recurrence: 'Monthly',
  status: 'InProgress',
  startedAt: '2026-11-06T04:00:00Z',
  completedAt: null,
  closedAt: null,
  summary: null,
  cancelReason: null,
  compliancePercent: null,
  completionGaps: ['2 items are not answered'],
  findings: [finding(1), finding(2)],
  nextAuditId: null,
  canRecord: true,
  version: 7,
  ...patch,
});

describe('an internal audit', () => {
  it('records a non-compliance only with what was found, and holds completion while items are open', async () => {
    const user = userEvent.setup();
    vi.mocked(api.get).mockResolvedValue(audit());
    vi.mocked(api.post).mockResolvedValue(audit());
    render(wrap(<AuditDialog auditId="a1" canPlanCapa onClose={() => {}} />));

    expect(await screen.findByText(/2 items are not answered/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Complete the audit' })).toBeDisabled();
    await expectNoAxeViolations();

    const item1 = screen.getByRole('radiogroup', { name: 'Finding for item 1' });
    await user.click(item1.querySelector('input[value="NonCompliant"]')!);
    expect(screen.getByRole('button', { name: 'Save item 1' })).toBeDisabled();
    await user.type(
      screen.getByRole('textbox', { name: /What was found \(item 1\)/ }),
      'Dispensers empty at 3 of 12 beds',
    );
    await user.click(screen.getByRole('button', { name: 'Save item 1' }));

    expect(api.post).toHaveBeenCalledWith('/api/v1/quality/audits/a1/findings', {
      itemKey: 'I01',
      result: 'NonCompliant',
      observation: 'Dispensers empty at 3 of 12 beds',
      version: 7,
    });
  });

  it('offers a CAPA for a non-compliance to the quality team, and closes once the audit is completed', async () => {
    const user = userEvent.setup();
    vi.mocked(api.get).mockResolvedValue(
      audit({
        status: 'Completed',
        completionGaps: [],
        compliancePercent: 50,
        findings: [
          finding(1, {
            result: 'NonCompliant',
            observation: 'Dispensers empty',
            capaId: 'c1',
            capaNumber: 'NHCAPA260012',
            capaStatus: 'Verified',
            capaOwnerName: 'Staff Nurse Kavita Pawar',
            capaDueDate: '2026-11-20',
          }),
          finding(2, { result: 'Compliant' }),
        ],
      }),
    );
    vi.mocked(api.post).mockResolvedValue(audit({ status: 'Closed' }));
    render(wrap(<AuditDialog auditId="a1" canPlanCapa onClose={() => {}} />));

    expect(await screen.findByText(/CAPA NHCAPA260012/)).toBeInTheDocument();
    await expectNoAxeViolations();
    await user.click(screen.getByRole('button', { name: 'Close the audit' }));
    expect(api.post).toHaveBeenCalledWith('/api/v1/quality/audits/a1/close', { version: 7 });
  });

  it('shows a non-compliance without a CAPA the raise button, and none to an auditor who may not plan CAPAs', async () => {
    const answered = audit({
      completionGaps: ['1 non-compliance has no CAPA'],
      findings: [
        finding(1, { result: 'NonCompliant', observation: 'Dispensers empty' }),
        finding(2, { result: 'Compliant' }),
      ],
    });
    vi.mocked(api.get).mockResolvedValue(answered);
    const { unmount } = render(wrap(<AuditDialog auditId="a1" canPlanCapa onClose={() => {}} />));
    expect(await screen.findByRole('button', { name: 'Raise a CAPA for item 1' })).toBeInTheDocument();
    unmount();

    render(wrap(<AuditDialog auditId="a1" canPlanCapa={false} onClose={() => {}} />));
    expect(await screen.findByText(/1 non-compliance has no CAPA/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Raise a CAPA for item 1' })).not.toBeInTheDocument();
  });
});
