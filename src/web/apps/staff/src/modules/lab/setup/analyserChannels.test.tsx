import type * as Core from '@hc360/core';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import type { AnalyserChannelDto, LabInstrumentDto } from '../api';
import { lastReading } from './channels';

// Lab setup › Analysers, whole with the shell against a fake server: each analyser shows how many channels it sends
// that no test maps; the list says what each last read and how to write its mapping, and whoever manages the masters
// can ignore one with why, or take that back. A technician sees the list but cannot decide.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const id = (suffix: string) => `0192a000-0000-7000-8000-00000000e${suffix}`;
const H500 = id('001');
const C150 = id('002');

const analyser = (instrumentId: string, code: string, name: string): LabInstrumentDto => ({
  id: instrumentId,
  code,
  name,
  manufacturer: 'HORIBA',
  model: null,
  section: 'Hematology',
  protocol: 'Astm',
  isBidirectional: false,
  autoValidationEnabled: true,
  location: null,
  isActive: true,
  version: 1,
});

const channel = (
  overrides: Partial<AnalyserChannelDto> & Pick<AnalyserChannelDto, 'id' | 'channelCode'>,
): AnalyserChannelDto => ({
  instrumentId: H500,
  instrumentCode: 'H500',
  firstSeenAt: '2026-11-06T04:00:00Z',
  lastSeenAt: '2026-11-06T09:30:00Z',
  timesSeen: 12,
  lastValue: '13.2',
  lastUnit: '%',
  mapping: `H500:${overrides.channelCode}`,
  ignored: null,
  ...overrides,
});

const RDW = channel({ id: id('101'), channelCode: 'RDW-CV' });
const RATIO = channel({ id: id('102'), channelCode: 'NA/K', mapping: null, lastValue: '33.6', lastUnit: null });
const LIC = channel({
  id: id('103'),
  channelCode: 'LIC',
  ignored: {
    reason: 'Research parameter, not reported',
    ignoredBy: 'Dr. Kavya Rao',
    ignoredAt: '2026-11-06T06:00:00Z',
  },
});

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  stubBrowserApis();
  localStorage.clear();
  server = fakeServer({
    'GET /api/v1/lab/instruments': [analyser(H500, 'H500', 'Yumizen H500'), analyser(C150, 'C150', 'Yumizen C150')],
    'GET /api/v1/lab/instruments/channels': [RDW, RATIO, LIC],
    [`POST /api/v1/lab/instruments/channels/${RDW.id}/ignore`]: null,
    [`DELETE /api/v1/lab/instruments/channels/${LIC.id}/ignore`]: null,
    'GET /api/v1/integrations/lab/connections': { gatewayEnabled: true, listVersion: 1, connections: [], agents: [] },
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const sent = (method: string, path: string) => server.requests.filter((r) => r.method === method && r.path === path);

async function openChannels(user = STAFF_USER) {
  openScreen('/lab/setup?tab=analysers', user);
  const table = await screen.findByRole('table', { name: 'Analysers' }, SLOW);
  const button = await within(table).findByRole('button', { name: 'Channels H500 sends that no test maps' }, SLOW);
  return { table, button };
}

describe('the channels analysers send that no test maps', SCREEN_TESTS, () => {
  it('counts those waiting to be mapped on each analyser, not the ignored ones', async () => {
    const { table, button } = await openChannels();

    expect(button).toHaveTextContent('2 to map');
    const c150 = within(table).getByText('Yumizen C150').closest('tr')!;
    expect(within(c150).getByText('None')).toBeInTheDocument();
  });

  it('lists what each channel last read and how to write its mapping, or why it cannot be mapped', async () => {
    const user = userEvent.setup();
    const { button } = await openChannels();

    await user.click(button);
    const list = await screen.findByRole('table', { name: 'Channels H500 sends that no test maps' });
    const rdw = within(list).getByText('RDW-CV').closest('tr')!;
    expect(within(rdw).getByText('13.2 %')).toBeInTheDocument();
    expect(within(rdw).getByText('12')).toBeInTheDocument();
    expect(within(rdw).getByText('H500:RDW-CV')).toBeInTheDocument();
    const ratio = within(list).getByText('NA/K').closest('tr')!;
    expect(within(ratio).getByText('33.6')).toBeInTheDocument();
    expect(within(ratio).getByText(/Cannot be mapped/)).toBeInTheDocument();
    const lic = within(list).getByText('LIC').closest('tr')!;
    expect(within(lic).getByText('Ignored: Research parameter, not reported')).toBeInTheDocument();
    expect(within(lic).getByText(/Dr\. Kavya Rao/)).toBeInTheDocument();
  });

  it('ignores a channel only with a reason, and takes an ignored one back', async () => {
    const user = userEvent.setup();
    const { button } = await openChannels();
    await user.click(button);
    const list = await screen.findByRole('table', { name: 'Channels H500 sends that no test maps' });

    await user.click(within(within(list).getByText('RDW-CV').closest('tr')!).getByRole('button', { name: 'Ignore…' }));
    const asked = await screen.findByRole('alertdialog', { name: "Ignore H500's channel RDW-CV?" });
    const confirm = within(asked).getByRole('button', { name: 'Ignore channel' });
    expect(confirm).toBeDisabled();
    await user.type(within(asked).getByRole('textbox'), 'Shown as RDW-SD on the report');
    await user.click(confirm);
    await waitFor(() => expect(sent('POST', `/api/v1/lab/instruments/channels/${RDW.id}/ignore`)).toHaveLength(1));
    expect(sent('POST', `/api/v1/lab/instruments/channels/${RDW.id}/ignore`)[0]!.body).toEqual({
      reason: 'Shown as RDW-SD on the report',
    });

    const reopened = await screen.findByRole('table', { name: 'Channels H500 sends that no test maps' }, SLOW);
    await user.click(
      within(within(reopened).getByText('LIC').closest('tr')!).getByRole('button', { name: 'Stop ignoring' }),
    );
    await waitFor(() => expect(sent('DELETE', `/api/v1/lab/instruments/channels/${LIC.id}/ignore`)).toHaveLength(1));
  });

  it('shows a technician the list without the decisions', async () => {
    const user = userEvent.setup();
    const technician = { ...STAFF_USER, roles: ['LabTechnician'], permissions: ['lab.qc.manage', 'lab.orders.read'] };
    const { button } = await openChannels(technician);

    await user.click(button);
    const list = await screen.findByRole('table', { name: 'Channels H500 sends that no test maps' });
    expect(within(list).getByText('H500:RDW-CV')).toBeInTheDocument();
    expect(within(list).queryByRole('button', { name: 'Ignore…' })).not.toBeInTheDocument();
    expect(within(list).queryByRole('button', { name: 'Stop ignoring' })).not.toBeInTheDocument();
  });
});

describe('lastReading', () => {
  it('writes the value with its unit, the value alone, or a dash', () => {
    expect(lastReading(RDW)).toBe('13.2 %');
    expect(lastReading(RATIO)).toBe('33.6');
    expect(lastReading({ ...RDW, lastValue: null })).toBe('—');
  });
});
