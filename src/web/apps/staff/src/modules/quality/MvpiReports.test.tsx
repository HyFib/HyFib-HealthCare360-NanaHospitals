import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type * as Mvpi from './mvpi';
import { blankDevice, mvpiApi, type MvpiPrefill, type MvpiReport, type MvpiReportListItem } from './mvpi';
import { IncidentMvpiPanel, MvpiFormDialog, MvpiRegister, MvpiReportDialog } from './MvpiReports';

// The MvPI register, the MDAE form as a draft (filled in from an incident and the implant on the patient's bill), and the
// incident's "Report to MvPI". The server is replaced.

const list = vi.hoisted(() => vi.fn());
const getReport = vi.hoisted(() => vi.fn());
vi.mock('./mvpi', async (importOriginal) => {
  const actual = await importOriginal<typeof Mvpi>();
  return {
    ...actual,
    mvpiApi: { raise: vi.fn(), correct: vi.fn(), submit: vi.fn(), correctReference: vi.fn(), followUp: vi.fn() },
    mvpiQueries: {
      ...actual.mvpiQueries,
      list: (filters: Mvpi.MvpiFilters) => ({ queryKey: ['mvpi', 'list', filters], queryFn: () => list(filters) }),
      prefill: () => ({ queryKey: ['mvpi', 'prefill'], queryFn: () => null }),
      report: (id: string) => ({ queryKey: ['mvpi', 'report', id], queryFn: () => getReport(id) }),
    },
  };
});

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
  vi.clearAllMocks();
});

function wrap(children: ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return (
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    </FluentProvider>
  );
}

const daysAgo = (days: number) => new Date(Date.now() + 5.5 * 3_600_000 - days * 86_400_000).toISOString().slice(0, 10);

const row = (patch: Partial<MvpiReportListItem>): MvpiReportListItem => ({
  id: 'r1',
  number: 'NHMDAE2600004',
  status: 'Draft',
  reportedOn: daysAgo(1),
  eventOn: daysAgo(2),
  patientInitials: 'L. N.',
  uhid: 'NH26000611',
  noPatient: false,
  deviceName: 'Attune',
  manufacturer: 'DePuy Synthes',
  serious: true,
  outcome: 'NotRecovered',
  incidentNumber: 'NHIR2600061',
  followUpOfNumber: null,
  submittedOn: null,
  reference: null,
  reporterName: 'Meena Iyer',
  ...patch,
});

/** Raised from an equipment failure of a patient with a knee implant on her bill. */
const PREFILL: MvpiPrefill = {
  incidentId: 'i1',
  incidentNumber: 'NHIR2600061',
  patientId: 'p1',
  patientInitials: 'L. N.',
  uhid: 'NH26000611',
  ageYears: 67,
  sex: 'Female',
  eventOn: daysAgo(2),
  awareOn: daysAgo(1),
  location: 'Ward 3, bed 12',
  description: 'The tibial component of the knee implant loosened six weeks after surgery.',
  seriousness: 'None',
  outcome: 'Unknown',
  device: {
    ...blankDevice(),
    name: 'Attune',
    genericName: 'Knee implant system',
    manufacturer: 'DePuy Synthes',
    lotNumber: 'KN2407',
    serialNumber: 'SN-A1',
    implantable: true,
    implantedOn: daysAgo(44),
    use: 'SingleUse',
  },
  devices: [
    {
      description: 'Implant: Knee implant system',
      brand: 'Attune',
      manufacturer: 'DePuy Synthes',
      specification: null,
      batch: 'KN2407',
      serial: 'SN-A1',
      isImplant: true,
      usedOn: daysAgo(44),
    },
  ],
};

/** The draft raised from PREFILL, as the server answers it. */
const draft = (missing: Record<string, string>): MvpiReport => ({
  id: 'r1',
  number: 'NHMDAE2600004',
  status: 'Draft',
  reportedOn: daysAgo(1),
  incidentId: 'i1',
  incidentNumber: 'NHIR2600061',
  followUpOfId: null,
  followUpOfNumber: null,
  followUpOfReference: null,
  followUps: [],
  patientId: 'p1',
  noPatient: false,
  patientInitials: 'L. N.',
  uhid: 'NH26000611',
  ageYears: 67,
  sex: 'Female',
  weightKg: null,
  eventOn: daysAgo(2),
  awareOn: daysAgo(1),
  location: 'Ward 3, bed 12',
  description: 'The tibial component of the knee implant loosened six weeks after surgery.',
  seriousness: 'None',
  deathOn: null,
  outcome: 'Unknown',
  device: PREFILL.device,
  reporterName: 'Meena Iyer',
  reporterDesignation: null,
  reporterDepartment: 'Quality',
  submittedOn: null,
  submittedByName: null,
  reference: null,
  referenceCorrectionReason: null,
  referenceCorrectedByName: null,
  referenceCorrectedAt: null,
  missing,
  version: 3,
});

describe('Device adverse events (MvPI)', () => {
  it('lists the reports with their device, patient by initials and state, and filters by state and date', async () => {
    list.mockResolvedValue([
      row({}),
      row({
        id: 'r2',
        number: 'NHMDAE2600002',
        status: 'Submitted',
        noPatient: true,
        patientInitials: null,
        uhid: null,
        deviceName: 'IntelliVue MX450',
        manufacturer: 'Philips',
        serious: false,
        submittedOn: daysAgo(29),
        reference: 'MDAE-TN-2026-0042',
      }),
    ]);
    const changed = vi.fn();
    const open = vi.fn();
    render(wrap(<MvpiRegister filters={{}} onFiltersChange={changed} onOpen={open} />));

    const draft = (await screen.findByText('Attune')).closest('tr')!;
    expect(within(draft).getByText('L. N.')).toBeInTheDocument();
    expect(within(draft).getByText('Draft')).toBeInTheDocument();
    expect(within(draft).getByText('Serious')).toBeInTheDocument();
    const sent = screen.getByText('IntelliVue MX450').closest('tr')!;
    expect(within(sent).getByText('No patient')).toBeInTheDocument();
    expect(within(sent).getByText('Sent to MvPI')).toBeInTheDocument();
    expect(within(sent).getByText(/MDAE-TN-2026-0042/)).toBeInTheDocument();

    await userEvent.click(sent);
    expect(open).toHaveBeenCalledWith('r2');

    await userEvent.selectOptions(screen.getByLabelText('State'), 'Submitted');
    expect(changed).toHaveBeenLastCalledWith({ status: 'Submitted' });
    expect(list).toHaveBeenCalledWith({});
  });

  it('fills the form from the incident and the implant on the bill, and says what MvPI still needs', async () => {
    const saved = vi.fn();
    vi.mocked(mvpiApi.raise).mockResolvedValue({ number: 'NHMDAE2600005', missing: {} } as unknown as MvpiReport);
    render(wrap(<MvpiFormDialog start={PREFILL} existing={null} onClose={vi.fn()} onSaved={saved} />));

    expect(screen.getByText(/from incident NHIR2600061/)).toBeInTheDocument();
    expect(screen.getByText(/L\. N\. · NH26000611 · 67 years · Female/)).toBeInTheDocument();
    expect(screen.getByLabelText(/Brand or trade name/)).toHaveValue('Attune');
    expect(screen.getByLabelText(/Serial no\./)).toHaveValue('SN-A1');

    // What MvPI needs is marked, and listed until it is filled in; a draft may be saved without it.
    for (const label of [/^Designation/, /^Kept at/, /^Manufacturer\s*\*?$/, /^Where it happened/, /^Implanted on/]) {
      expect(screen.getByLabelText(label)).toBeRequired();
    }
    expect(screen.getByText(/where the device is kept; the reporter's designation\./)).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText(/^Designation/), 'Quality manager');
    await userEvent.type(screen.getByLabelText(/^Kept at/), 'Biomedical quarantine shelf 2');
    expect(screen.queryByText('Still needed before it can be sent')).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('checkbox', { name: /Hospitalisation/ }));
    await userEvent.click(screen.getByRole('button', { name: 'Save draft' }));

    expect(mvpiApi.raise).toHaveBeenCalledWith(
      expect.objectContaining({
        incidentId: 'i1',
        patientId: 'p1',
        noPatient: false,
        seriousness: 'Hospitalisation',
        reporterDesignation: 'Quality manager',
        device: expect.objectContaining({
          name: 'Attune',
          lotNumber: 'KN2407',
          implantable: true,
          keptAt: 'Biomedical quarantine shelf 2',
        }),
      }),
    );
    expect(saved).toHaveBeenCalled();
  });

  it('does not save a draft whose facts do not fit together', async () => {
    render(wrap(<MvpiFormDialog start={PREFILL} existing={null} onClose={vi.fn()} onSaved={vi.fn()} />));

    await userEvent.click(screen.getByRole('checkbox', { name: 'Death' }));
    await userEvent.click(screen.getByRole('button', { name: 'Save draft' }));

    expect(screen.getByText('A fatal outcome and death as the seriousness go together.')).toBeInTheDocument();
    expect(mvpiApi.raise).not.toHaveBeenCalled();
  });

  it('offers to report an equipment failure or an incident that involved a device, and lists what was raised', async () => {
    const report = vi.fn();
    const open = vi.fn();
    const { rerender } = render(
      wrap(<IncidentMvpiPanel incident={{ isDeviceRelated: true, mvpiReports: [] }} onReport={report} onOpen={open} />),
    );
    await userEvent.click(screen.getByRole('button', { name: 'Report to MvPI' }));
    expect(report).toHaveBeenCalled();

    rerender(
      wrap(
        <IncidentMvpiPanel
          incident={{
            isDeviceRelated: true,
            mvpiReports: [
              { id: 'r1', number: 'NHMDAE2600004', status: 'Submitted', isFollowUp: false, reference: 'MDAE-1' },
            ],
          }}
          onReport={report}
          onOpen={open}
        />,
      ),
    );
    await userEvent.click(screen.getByRole('button', { name: /NHMDAE2600004 · sent \(MDAE-1\)/ }));
    expect(open).toHaveBeenCalledWith('r1');

    rerender(
      wrap(
        <IncidentMvpiPanel incident={{ isDeviceRelated: false, mvpiReports: [] }} onReport={report} onOpen={open} />,
      ),
    );
    expect(screen.queryByRole('button', { name: 'Report to MvPI' })).not.toBeInTheDocument();
  });
  it('offers "Record as sent" only once MvPI has all it needs', async () => {
    getReport.mockResolvedValue(draft({ reporterDesignation: "Give the reporter's designation." }));
    const first = render(wrap(<MvpiReportDialog id="r1" onClose={() => undefined} onOpen={() => undefined} />));
    const blocked = await screen.findByRole('button', { name: 'Record as sent' });
    expect(blocked).toHaveAttribute('aria-disabled', 'true');
    await userEvent.click(blocked);
    expect(screen.queryByText('Record NHMDAE2600004 as sent to MvPI')).not.toBeInTheDocument();
    first.unmount();

    getReport.mockResolvedValue(draft({}));
    render(wrap(<MvpiReportDialog id="r1" onClose={() => undefined} onOpen={() => undefined} />));
    const ready = await screen.findByRole('button', { name: 'Record as sent' });
    expect(ready).not.toHaveAttribute('aria-disabled', 'true');
    await userEvent.click(ready);
    expect(await screen.findByText('Record NHMDAE2600004 as sent to MvPI')).toBeInTheDocument();
  });
});
