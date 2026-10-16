import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { ApiError } from '@hc360/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as CrmApi from './api';
import type { CommunicationEntry } from './communications';
import { CommunicationsHistory } from './CommunicationsHistory';

// The server is outside the test: the section's own states and filter are what is tested.
const listCommunications = vi.hoisted(() => vi.fn());
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof CrmApi>();
  return {
    ...actual,
    crmQueries: {
      ...actual.crmQueries,
      communications: (patientId: string, filters: { kind?: string; page?: number }) => ({
        queryKey: ['crm', 'patient360', patientId, 'communications', filters],
        queryFn: () => listCommunications(patientId, filters),
      }),
    },
  };
});

function renderHistory() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <FluentProvider theme={webLightTheme}>
        <CommunicationsHistory patientId="p1" />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

const reminder: CommunicationEntry = {
  id: 'e1',
  kind: 'Message',
  occurredAt: '2026-10-16T04:30:00Z',
  channel: 'Sms',
  templateKey: 'appointment.reminder',
  purpose: 'Appointment reminder',
  recipient: '+91******3210',
  status: 'Delivered',
  sentAt: null,
  deliveredAt: null,
  campaignId: null,
  campaignName: null,
  skipReason: null,
  followUpId: null,
  followUpType: null,
  followUpStatus: null,
  callOutcome: null,
  callAttempts: null,
};

const page = (items: CommunicationEntry[]) => ({ items, page: 1, pageSize: 10, totalCount: items.length });

describe('CommunicationsHistory', () => {
  beforeEach(() => listCommunications.mockReset());

  it('lists what was sent with the masked number and how far it got', async () => {
    listCommunications.mockResolvedValue(page([reminder]));
    renderHistory();

    expect(await screen.findByText('Appointment reminder')).toBeInTheDocument();
    expect(screen.getByText('SMS · +91******3210')).toBeInTheDocument();
    expect(screen.getByText('Delivered')).toBeInTheDocument();
    expect(listCommunications).toHaveBeenCalledWith('p1', { kind: undefined, page: 1 });
  });

  it('says when nothing has been sent, and filters by kind', async () => {
    listCommunications.mockResolvedValue(page([]));
    const user = userEvent.setup();
    renderHistory();

    expect(await screen.findByText('Nothing sent yet')).toBeInTheDocument();
    await user.selectOptions(screen.getByRole('combobox', { name: 'Show' }), 'FollowUpCall');
    await waitFor(() => expect(listCommunications).toHaveBeenLastCalledWith('p1', { kind: 'FollowUpCall', page: 1 }));
    expect(await screen.findByText('No follow-up calls')).toBeInTheDocument();
  });

  it('shows the error with a way to try again', async () => {
    listCommunications.mockRejectedValueOnce(
      new ApiError({
        status: 500,
        title: 'Could not load the history',
        detail: 'The server is busy.',
        code: 'server.error',
      }),
    );
    listCommunications.mockResolvedValue(page([reminder]));
    const user = userEvent.setup();
    renderHistory();

    expect(await screen.findByText('Could not load the history')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText('Appointment reminder')).toBeInTheDocument();
  });
});
