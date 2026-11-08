import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { ApiError } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type * as CrmApi from './api';
import { crmApi, type CampaignDetail } from './api';
import { ScheduleCampaignDialog } from './ScheduleCampaignDialog';

// The server and the toasts are outside the test; the dialog's own behaviour is what is tested.
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof CrmApi>();
  return {
    ...actual,
    crmApi: { ...actual.crmApi, scheduleCampaign: vi.fn(), previewAudience: vi.fn() },
  };
});
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));
vi.mock('@/lib/permissions', () => ({ usePermission: () => true }));

// The error bar reflows with the dialog's width; jsdom has no ResizeObserver.
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

const campaign: CampaignDetail = {
  id: 'cmp1',
  name: 'Monsoon health check',
  channel: 'Sms',
  templateKey: 'campaign.monsoon',
  message: 'Free health check-up this Sunday.',
  status: 'Draft',
  segment: {
    minAge: null,
    maxAge: null,
    gender: null,
    city: null,
    lastVisitFrom: null,
    lastVisitTo: null,
    departmentIds: [],
  },
  scheduledAt: null,
  startedAt: null,
  completedAt: null,
  cancelReason: null,
  audienceCount: 0,
  queuedCount: 0,
  skippedCount: 0,
  version: 3,
};

function open() {
  const onClose = vi.fn();
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <FluentProvider theme={webLightTheme}>
        <ScheduleCampaignDialog open campaign={campaign} onClose={onClose} />
      </FluentProvider>
    </QueryClientProvider>,
  );
  return onClose;
}

describe('asking for a campaign to be scheduled (MAKER-CHECKER.md, slice 14)', () => {
  beforeEach(() => {
    vi.mocked(crmApi.previewAudience).mockReset().mockResolvedValue({ count: 120, sample: [] });
    vi.mocked(crmApi.scheduleCampaign).mockReset();
    Object.values(notify).forEach((spy) => spy.mockClear());
  });

  it('says someone other than the author approves it, and that it stays a draft until then', async () => {
    vi.mocked(crmApi.scheduleCampaign).mockResolvedValue({
      id: 'a1',
      kind: 'engagement.campaign_schedule',
      status: 'Pending',
    } as unknown as Awaited<ReturnType<typeof crmApi.scheduleCampaign>>);
    const onClose = open();
    expect(screen.getByText(/Someone other than the campaign.s author approves it/)).toBeInTheDocument();
    await screen.findByText(/120 patients with a marketing consent/);
    await expectNoAxeViolations(document.body);

    await userEvent.click(screen.getByRole('button', { name: 'Ask for approval' }));

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(crmApi.scheduleCampaign).toHaveBeenCalledWith('cmp1', expect.stringMatching(/\+05:30$/));
    expect(notify.success).toHaveBeenCalledWith(
      'Asked for approval',
      expect.stringContaining('it stays a draft until then'),
    );
    expect(notify.success).not.toHaveBeenCalledWith('Campaign scheduled', expect.anything());
  });

  it('explains a request already waiting', async () => {
    vi.mocked(crmApi.scheduleCampaign).mockRejectedValue(
      new ApiError({
        status: 409,
        title: 'Conflict',
        code: 'approval.already_requested',
        detail: 'A request of this kind for this campaign is already waiting for approval.',
      }),
    );
    const onClose = open();
    await userEvent.click(screen.getByRole('button', { name: 'Ask for approval' }));

    expect(await screen.findByText(/has been asked for already and waits for approval/)).toBeInTheDocument();
    expect(onClose).not.toHaveBeenCalled();
  });
});
