import type * as Ui from '@hc360/ui';
import { Hc360ThemeProvider } from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { labDevicesApi, type LabDeviceDto } from '../api';
import { describeDevice, LabDevicesPanel } from './LabDevicesPanel';

const notify = { success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() };
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const device = (overrides: Partial<LabDeviceDto>): LabDeviceDto => ({
  id: 'd1',
  connectionCode: 'XN1000',
  name: null,
  method: 'SharedSecret',
  identifier: 'dev-unknown',
  subject: null,
  status: 'Seen',
  firstSeenAt: '2026-11-08T05:00:00Z',
  lastSeenAt: '2026-11-08T05:00:00Z',
  lastAddress: '10.0.4.21',
  refusedAttempts: 2,
  addedByName: null,
  addedAt: null,
  approvedByName: null,
  approvedAt: null,
  pendingApprovalId: null,
  version: 3,
  ...overrides,
});

function renderPanel() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <Hc360ThemeProvider>
      <QueryClientProvider client={client}>
        <LabDevicesPanel connectionCode="XN1000" />
      </QueryClientProvider>
    </Hc360ThemeProvider>,
  );
}

describe('LabDevicesPanel', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    vi.spyOn(labDevicesApi, 'list').mockResolvedValue([
      device({}),
      device({
        id: 'd2',
        name: 'Biochemistry analyser',
        method: 'MutualTls',
        identifier: 'AB'.repeat(32),
        subject: 'CN=analyser',
        status: 'Approved',
        addedByName: 'Lab manager',
        approvedByName: 'Hospital admin',
        refusedAttempts: 0,
      }),
    ]);
  });

  it('lists the devices refused at the door and adds one for someone else to approve, showing its secret once', async () => {
    const add = vi.spyOn(labDevicesApi, 'add').mockResolvedValue({
      device: device({ status: 'PendingApproval' }),
      approval: { id: 'a1' },
      keyId: 'dev-unknown',
      secret: 'shown-once',
    });
    const { baseElement } = renderPanel();
    expect(await screen.findByText(/Refused: not known here/)).toBeInTheDocument();
    expect(screen.getByText(/approved by Hospital admin/)).toBeInTheDocument();
    await userEvent.type(screen.getByRole('textbox', { name: /Name for the device/ }), 'XN-1000 middleware');
    await userEvent.click(screen.getByRole('button', { name: 'Add this device' }));
    await waitFor(() =>
      expect(add).toHaveBeenCalledWith(
        expect.objectContaining({ seenDeviceId: 'd1', name: 'XN-1000 middleware', method: 'SharedSecret' }),
      ),
    );
    expect(await screen.findByText('shown-once')).toBeInTheDocument();
    await expectNoAxeViolations(baseElement);
  });

  it('revokes an approved device at once', async () => {
    const revoke = vi.spyOn(labDevicesApi, 'revoke').mockResolvedValue(undefined);
    renderPanel();
    await userEvent.click(await screen.findByRole('button', { name: 'Revoke' }));
    await waitFor(() => expect(revoke).toHaveBeenCalledWith('d2', 3, 'Revoked in Lab setup.'));
  });

  it('says how each device proves itself', () => {
    expect(describeDevice(device({}))).toContain('key id dev-unknown');
    expect(describeDevice(device({ method: 'LabAgent' }))).toContain('lab agent');
    expect(describeDevice(device({}))).toContain('refused 2 times');
  });
});
