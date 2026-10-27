import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type * as HbpApi from './hbpApi';
import { hbpApi, type HbpImportResult, type HbpProcedure } from './hbpApi';
import { HbpProcedures, ImportHbpDialog } from './HbpMaster';

// AB PM-JAY's HBP master: importing NHA's file and looking procedures up. The server is replaced.

const search = vi.hoisted(() => vi.fn());
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

describe('HBP master', () => {
  it('imports the master under its version and shows what changed and the rows not read', async () => {
    const result: HbpImportResult = {
      masterVersion: 'HBP 2022',
      read: 1949,
      added: 1949,
      updated: 0,
      retired: 0,
      problems: [
        'Row 7: "S.01.0001" is not an HBP procedure code (two letters, three digits, then a letter for a stratified procedure).',
      ],
    };
    vi.mocked(hbpApi.importMaster).mockResolvedValue(result);
    const user = userEvent.setup();
    render(wrap(<ImportHbpDialog open onClose={vi.fn()} />));

    expect(screen.getByRole('button', { name: 'Import' })).toBeDisabled();
    const file = new File(['Procedure Code,Procedure Name\n'], 'hbp-2022.xlsx');
    await user.upload(screen.getByLabelText('HBP master file'), file);
    await user.click(screen.getByRole('button', { name: 'Import' }));

    expect(hbpApi.importMaster).toHaveBeenCalledWith(file, 'HBP 2022');
    expect(await screen.findByText(/1949 procedures read: 1949 new, 0 changed, 0 retired/)).toBeInTheDocument();
    expect(
      within(screen.getByRole('list', { name: 'Rows not read' })).getByText(result.problems[0]!),
    ).toBeInTheDocument();
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
