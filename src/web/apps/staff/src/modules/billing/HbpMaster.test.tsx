import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ApprovalRequestDto } from '@/modules/approvals/api';
import type * as HbpApi from './hbpApi';
import { hbpApi, type HbpImport, type HbpProcedure } from './hbpApi';
import { HbpImports, HbpProcedures, ImportHbpDialog } from './HbpMaster';

// AB PM-JAY's HBP master: importing NHA's file for a second person's approval, the master's imports, and looking
// procedures up. The server is replaced.

const search = vi.hoisted(() => vi.fn());
const imports = vi.hoisted(() => vi.fn());
vi.mock('./hbpApi', async (importOriginal) => {
  const actual = await importOriginal<typeof HbpApi>();
  return {
    ...actual,
    hbpApi: { importMaster: vi.fn() },
    hbpQueries: {
      ...actual.hbpQueries,
      search: (term: string, includeRetired = false) => ({
        queryKey: ['hbp', term, includeRetired],
        queryFn: () => search(term, includeRetired),
      }),
      imports: () => ({ queryKey: ['hbp', 'imports'], queryFn: () => imports() }),
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

const procedure = (patch: Partial<HbpProcedure> = {}): HbpProcedure => ({
  code: 'BM001A',
  packageCode: 'BM001',
  specialtyCode: 'BM',
  specialty: 'Burns Management',
  packageName: 'Thermal burns',
  procedureName: 'Thermal burns, TBSA less than 20%',
  rate: 7700,
  lengthOfStayDays: 3,
  reservedForPublic: false,
  stratification: 'TBSA < 20%',
  masterVersion: 'HBP 2022',
  isActive: true,
  ...patch,
});

const SHA = '9f2c1e4b7a0d3c5e8f1a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f70';

const staged = (patch: Partial<HbpImport> = {}): HbpImport => ({
  id: 'import-1',
  masterVersion: 'HBP 2022',
  fileName: 'hbp-2022.xlsx',
  fileSha256: SHA,
  read: 1949,
  added: 1949,
  updated: 0,
  retired: 0,
  problemCount: 0,
  problems: [],
  status: 'Waiting',
  askedBy: 'Billing Desk',
  askedAt: '2026-11-02T10:00:00Z',
  decidedBy: null,
  decidedAt: null,
  note: null,
  approvalId: 'approval-1',
  ...patch,
});

describe('HBP master', () => {
  it('stages the master with the reason for the approver, and says nothing changes until it is approved', async () => {
    const problem =
      'Row 7: "S.01.0001" is not an HBP procedure code (two letters, three digits, then a letter for a stratified procedure).';
    vi.mocked(hbpApi.importMaster).mockResolvedValue({
      import: staged({ problemCount: 4, problems: [problem] }),
      approval: { id: 'approval-1' } as ApprovalRequestDto,
    });
    const user = userEvent.setup();
    render(wrap(<ImportHbpDialog open onClose={vi.fn()} />));

    expect(screen.getByRole('button', { name: 'Import' })).toBeDisabled();
    const file = new File(['Procedure Code,Procedure Name\n'], 'hbp-2022.xlsx');
    await user.upload(screen.getByLabelText('HBP master file'), file);
    await user.type(screen.getByRole('textbox', { name: 'Reason, for the approver' }), 'NHA circular 12/2022');
    await user.click(screen.getByRole('button', { name: 'Import' }));

    expect(hbpApi.importMaster).toHaveBeenCalledTimes(1);
    expect(hbpApi.importMaster).toHaveBeenCalledWith(file, 'HBP 2022', 'NHA circular 12/2022');
    expect(await screen.findByText('Asked for approval')).toBeInTheDocument();
    expect(screen.getByText(/HBP 2022: 1949 procedures read: 1949 new, 0 changed, 0 retired\./)).toBeInTheDocument();
    expect(
      screen.getByText(/Nothing in the master changes until another person who approves prices approves it/),
    ).toBeInTheDocument();
    expect(screen.getByText(`File SHA-256 ${SHA}`)).toBeInTheDocument();
    const unread = within(screen.getByRole('list', { name: 'Rows not read' }));
    expect(unread.getByText(problem)).toBeInTheDocument();
    expect(unread.getByText('…and 3 more rows not read.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Import' })).not.toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it("lists the master's imports: waiting, applied and refused, with the file's hash and who asked and decided", async () => {
    imports.mockResolvedValue([
      staged({
        id: 'import-3',
        masterVersion: 'HBP 2023',
        fileName: 'hbp-2023.csv',
        read: 2010,
        added: 61,
        updated: 300,
        retired: 12,
      }),
      staged({
        id: 'import-2',
        masterVersion: 'HBP 2022 (state copy)',
        status: 'Rejected',
        decidedBy: 'Accounts Officer',
        decidedAt: '2026-10-20T09:00:00Z',
        note: 'Wait for the state tier rates.',
      }),
      staged({ status: 'Applied', decidedBy: 'Accounts Officer', decidedAt: '2026-10-01T09:00:00Z' }),
    ]);
    render(wrap(<HbpImports />));

    expect(await screen.findByText('HBP 2023')).toBeInTheDocument();
    expect(screen.getByText('2010 procedures read: 61 new, 300 changed, 12 retired')).toBeInTheDocument();
    expect(screen.getByText('Waiting for approval')).toBeInTheDocument();
    expect(screen.getByText('Refused')).toBeInTheDocument();
    expect(screen.getByText('Wait for the state tier rates.')).toBeInTheDocument();
    expect(screen.getByText('Applied')).toBeInTheDocument();
    expect(screen.getByText(`hbp-2023.csv · SHA-256 ${SHA.slice(0, 16)}…`)).toBeInTheDocument();
    expect(screen.getAllByText('Accounts Officer')).toHaveLength(2);
    await expectNoAxeViolations();
  });

  it('lists procedures with their rate, stay, reservation and whether a later master retired them', async () => {
    search.mockResolvedValue([
      procedure(),
      procedure({
        code: 'SG039A',
        specialty: 'General Surgery',
        packageName: 'Appendicectomy',
        stratification: null,
        procedureName: 'Appendicectomy (laparoscopic)',
        lengthOfStayDays: null,
        reservedForPublic: true,
        isActive: false,
      }),
    ]);
    render(wrap(<HbpProcedures search="" includeRetired />));

    expect(await screen.findByText('Thermal burns, TBSA less than 20%')).toBeInTheDocument();
    expect(screen.getByText('Burns Management · Thermal burns · TBSA < 20%')).toBeInTheDocument();
    expect(screen.getByText('3 days')).toBeInTheDocument();
    expect(screen.getByText('Reserved for public hospitals')).toBeInTheDocument();
    expect(screen.getByText('Retired')).toBeInTheDocument();
    expect(search).toHaveBeenCalledWith('', true);
  });
});
