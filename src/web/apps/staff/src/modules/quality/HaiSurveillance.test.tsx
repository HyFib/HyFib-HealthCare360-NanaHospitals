import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import type { DeviceEpisode, HaiCaseDetail } from './haiApi';
import { DevicesTable, HaiCaseDialog, InsertDeviceDialog } from './HaiSurveillance';

// Infection surveillance: reviewing a case against its NHSN checklist, the devices in place, and recording a device put
// in. The server is replaced.

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

const device: DeviceEpisode = {
  id: 'd1',
  patientId: 'p1',
  uhid: 'NH26000611',
  admissionId: 'a1',
  admissionNumber: 'NHIP2600214',
  wardName: 'ICU',
  device: 'UrinaryCatheter',
  detail: 'Foley 14 Fr',
  insertedAt: '2026-11-01T04:00:00Z',
  insertedByName: 'Sr. Mary Thomas',
  removedAt: null,
  removalReason: null,
  removedByName: null,
  deviceDays: 6,
  notes: null,
  version: 3,
};

const cauti = (patch: Partial<HaiCaseDetail> = {}): HaiCaseDetail => ({
  id: 'c1',
  caseNumber: 'NHHAI260007',
  type: 'Cauti',
  origin: 'CultureAlert',
  status: 'Suspected',
  patientId: 'p1',
  uhid: 'NH26000611',
  patientName: 'Lakshmi Narayanan',
  admissionId: 'a1',
  admissionNumber: 'NHIP2600214',
  device,
  patientDevices: [device],
  cultureResultId: 'r1',
  organism: 'Escherichia coli',
  specimen: 'Urine culture and sensitivity',
  specimenCollectedAt: '2026-11-06T05:00:00Z',
  surgeryBookingId: null,
  surgeryReference: null,
  operatedOn: null,
  daysAfterOperation: null,
  eventOn: '2026-11-06',
  deviceEligible: true,
  dayOfDevice: 6,
  criteria: [
    { key: 'culture', group: 'culture', text: 'Urine culture with no more than two species', met: false },
    { key: 'fever', group: 'sign', text: 'Fever (> 38.0 °C)', met: false },
    { key: 'dysuria', group: 'after-removal', text: 'Dysuria (counts only once the catheter is out)', met: false },
  ],
  meetsDefinition: false,
  missing: ['A urine culture with no more than two species, one ≥ 10⁵ CFU/ml'],
  reviewedByName: null,
  reviewedAt: null,
  decisionNote: null,
  notes: null,
  version: 4,
  ...patch,
});

describe('reviewing an infection surveillance case', () => {
  it('saves the ticked checklist and confirms only once it meets the definition', async () => {
    const user = userEvent.setup();
    vi.mocked(api.get).mockResolvedValue(cauti());
    vi.mocked(api.put).mockResolvedValue(cauti({ meetsDefinition: true, missing: [], version: 5 }));
    render(wrap(<HaiCaseDialog caseId="c1" onClose={() => {}} />));

    expect(await screen.findByText(/Escherichia coli/)).toBeInTheDocument();
    expect(screen.getByText(/device day 6 on the date of event/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Confirm' })).toBeDisabled();
    await expectNoAxeViolations();

    await user.click(screen.getByRole('checkbox', { name: /Urine culture/ }));
    await user.click(screen.getByRole('checkbox', { name: /Fever/ }));
    expect(screen.getByRole('button', { name: 'Confirm' })).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Save checklist' }));

    expect(api.put).toHaveBeenCalledWith(
      '/api/v1/quality/hai/cases/c1',
      expect.objectContaining({
        criteriaMet: ['culture', 'fever'],
        deviceEpisodeId: 'd1',
        version: 4,
      }),
    );
  });

  it('rules a case out only with a reason', async () => {
    const user = userEvent.setup();
    vi.mocked(api.get).mockResolvedValue(cauti());
    vi.mocked(api.post).mockResolvedValue(cauti({ status: 'RuledOut' }));
    render(wrap(<HaiCaseDialog caseId="c1" onClose={() => {}} />));

    const ruleOut = await screen.findByRole('button', { name: 'Rule out' });
    expect(ruleOut).toBeDisabled();
    await user.type(screen.getByRole('textbox', { name: 'Decision note' }), 'Asymptomatic bacteriuria, no signs');
    await user.click(ruleOut);

    expect(api.post).toHaveBeenCalledWith('/api/v1/quality/hai/cases/c1/rule-out', {
      note: 'Asymptomatic bacteriuria, no signs',
      version: 4,
    });
  });
});

describe('the device register', () => {
  it('lists the devices in place with their device days', async () => {
    vi.mocked(api.get).mockResolvedValue([device]);
    render(wrap(<DevicesTable inPlace onRemove={() => {}} />));

    expect(await screen.findByText('Foley 14 Fr')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Remove' })).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('records a device put in by the admission number', async () => {
    const user = userEvent.setup();
    vi.mocked(api.post).mockResolvedValue(device);
    const onClose = vi.fn();
    render(wrap(<InsertDeviceDialog onClose={onClose} />));

    await user.type(screen.getByRole('textbox', { name: /Admission number/ }), 'NHIP2600214');
    await user.selectOptions(screen.getByRole('combobox', { name: /Device/ }), 'Ventilator');
    await expectNoAxeViolations();
    await user.click(screen.getByRole('button', { name: 'Record' }));

    expect(api.post).toHaveBeenCalledWith(
      '/api/v1/quality/hai/devices',
      expect.objectContaining({
        admissionNumber: 'NHIP2600214',
        device: 'Ventilator',
      }),
    );
    expect(onClose).toHaveBeenCalled();
  });
});
