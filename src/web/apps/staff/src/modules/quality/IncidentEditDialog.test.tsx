import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Ui from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as QualityApi from './api';
import { qualityApi, type IncidentDetail } from './api';
import { IncidentEditDialog } from './IncidentEditDialog';

// The server, the toasts and the lookups are outside the test; the dialog's own behaviour is what is tested.
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof QualityApi>();
  return { ...actual, qualityApi: { ...actual.qualityApi, updateIncident: vi.fn() } };
});
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));
vi.mock('@/lib/permissions', () => ({ usePermission: () => false }));
vi.mock('@/lib/lookups', () => ({ useDepartments: () => ({ data: [] }), toOptions: () => [] }));
vi.mock('@/modules/hr/shared/UserPicker', () => ({ UserPicker: () => null }));

const sentinel: IncidentDetail = {
  id: 'i1',
  incidentNumber: 'NHIR2600042',
  type: 'Clinical',
  severity: 'Death',
  isSentinel: true,
  sentinelKind: null,
  sentinelReason: 'Death',
  status: 'UnderReview',
  nextStatuses: ['Investigation', 'CapaInProgress', 'Closed'],
  occurredAt: '2026-11-07T04:00:00Z',
  reportedAt: '2026-11-07T05:00:00Z',
  location: 'Ward 4',
  departmentId: null,
  departmentName: null,
  patientId: null,
  involvedStaff: [],
  description: 'Recorded as a death after a fall from the bed.',
  immediateAction: null,
  witnesses: null,
  isAnonymous: false,
  reporterName: 'Asha Nair',
  assignedTo: null,
  closedAt: null,
  closureNote: null,
  lastStatusNote: null,
  reopenCount: 0,
  investigationStatus: null,
  capas: [],
  involvesDevice: false,
  isDeviceRelated: false,
  mvpiReports: [],
  version: 7,
};

function open() {
  const onClose = vi.fn();
  render(
    <QueryClientProvider client={new QueryClient()}>
      <FluentProvider theme={webLightTheme}>
        <IncidentEditDialog open incident={sentinel} onClose={onClose} />
      </FluentProvider>
    </QueryClientProvider>,
  );
  return onClose;
}

describe('re-grading a sentinel event out of sentinel (MAKER-CHECKER.md, slice 14)', () => {
  beforeEach(() => {
    vi.mocked(qualityApi.updateIncident).mockReset();
    Object.values(notify).forEach((spy) => spy.mockClear());
  });

  it('asks for a reason and for approval, and says nothing is saved until the quality manager agrees', async () => {
    vi.mocked(qualityApi.updateIncident).mockResolvedValue({
      id: 'a1',
      kind: 'quality.sentinel_downgrade',
    } as unknown as Awaited<ReturnType<typeof qualityApi.updateIncident>>);
    const user = userEvent.setup();
    const onClose = open();
    expect(screen.getByRole('button', { name: 'Save' })).toBeInTheDocument();

    await user.selectOptions(screen.getByRole('combobox', { name: /^Harm grade/ }), 'Mild');
    expect(
      await screen.findByText(/This takes it out of sentinel, so no root-cause analysis is due/),
    ).toBeInTheDocument();
    const ask = screen.getByRole('button', { name: 'Ask for approval' });
    await expectNoAxeViolations(document.body);

    await user.click(ask);
    expect(await screen.findByText('Say why this is no longer a sentinel event.')).toBeInTheDocument();
    expect(qualityApi.updateIncident).not.toHaveBeenCalled();

    await user.type(
      screen.getByRole('textbox', { name: /^Why it is no longer a sentinel event/ }),
      'The patient survived.',
    );
    await user.click(ask);

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(qualityApi.updateIncident).toHaveBeenCalledWith(
      'i1',
      expect.objectContaining({ severity: 'Mild', reason: 'The patient survived.', version: 7 }),
    );
    expect(notify.success).toHaveBeenCalledWith(
      'Asked for approval',
      expect.stringContaining('stays a sentinel event'),
    );
  });

  it('saves at once a re-grading that keeps it sentinel, with no reason asked', async () => {
    vi.mocked(qualityApi.updateIncident).mockResolvedValue(undefined);
    const user = userEvent.setup();
    const onClose = open();
    await user.selectOptions(screen.getByRole('combobox', { name: /^Harm grade/ }), 'Severe');
    expect(screen.queryByRole('textbox', { name: /^Why it is no longer a sentinel event/ })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(qualityApi.updateIncident).toHaveBeenCalledWith(
      'i1',
      expect.objectContaining({ severity: 'Severe', reason: null }),
    );
    expect(notify.success).toHaveBeenCalledWith('Incident updated', undefined);
  });
});
