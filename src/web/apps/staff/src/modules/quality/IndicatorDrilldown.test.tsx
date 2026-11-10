import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import type { IndicatorDto } from './api';
import type { IndicatorDrilldown } from './computedApi';
import { IndicatorDrilldownPanel } from './IndicatorDrilldown';

// The records behind a computed indicator's value, and recomputing a month. The server is replaced.

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

const indicator: IndicatorDto = {
  id: 'i1',
  code: 'FALL-RATE',
  name: 'Patient fall rate',
  category: 'Patient safety',
  numeratorDefinition: 'Falls of in-patients recorded on the nursing chart',
  denominatorDefinition: 'In-patient days (midnight census)',
  unit: 'PerThousand',
  multiplier: 1000,
  target: 1,
  direction: 'LowerIsBetter',
  frequency: 'Monthly',
  nabhReference: 'PSQ',
  sortOrder: 130,
  isActive: true,
  latest: null,
  version: 3,
  isComputed: true,
};

const drill: IndicatorDrilldown = {
  indicatorId: 'i1',
  code: 'FALL-RATE',
  name: 'Patient fall rate',
  numeratorDefinition: indicator.numeratorDefinition,
  denominatorDefinition: indicator.denominatorDefinition,
  unit: 'PerThousand',
  periodStart: '2026-10-01',
  periodEnd: '2026-10-31',
  periodLabel: 'Oct 2026',
  numerator: 2,
  denominator: 1250,
  value: 1.6,
  meetsTarget: false,
  stored: null,
  note: 'Denominator: 1250 in-patient days (midnight census) from 96 stays.',
  unavailable: false,
  recordCount: 2,
  records: [
    {
      role: 'Numerator',
      kind: 'Fall',
      reference: 'Admission NHIP2600214',
      uhid: 'NH26000611',
      at: '2026-10-12T22:10:00Z',
      minutes: null,
      detail: 'Injury: Minor; incident NHIR2600118',
    },
    {
      role: 'Excluded',
      kind: 'Pressure injury',
      reference: 'Admission NHIP2600199',
      uhid: 'Restricted record',
      at: '2026-10-03T06:00:00Z',
      minutes: null,
      detail: 'Stage1 at Sacrum: present on admission',
    },
  ],
};

describe('the records behind a computed indicator', () => {
  it('lists each record with how it counts, and recomputes the month', async () => {
    const user = userEvent.setup();
    vi.mocked(api.get).mockResolvedValue(drill);
    vi.mocked(api.post).mockResolvedValue([
      {
        indicatorId: 'i1',
        code: 'FALL-RATE',
        name: 'Patient fall rate',
        periodStart: '2026-10-01',
        outcome: 'Stored',
        numerator: 2,
        denominator: 1250,
        value: 1.6,
        meetsTarget: false,
      },
    ]);
    render(wrap(<IndicatorDrilldownPanel indicator={indicator} />));

    expect(await screen.findByText('Admission NHIP2600214')).toBeInTheDocument();
    expect(screen.getByText('Counted')).toBeInTheDocument();
    expect(screen.getByText('Left out')).toBeInTheDocument();
    expect(screen.getByText(/1250 in-patient days/)).toBeInTheDocument();
    await expectNoAxeViolations();

    await user.click(screen.getByRole('button', { name: /^Recompute/ }));
    expect(api.post).toHaveBeenCalledWith('/api/v1/quality/indicators/i1/compute', {
      period: expect.stringMatching(/^\d{4}-\d{2}-01$/),
    });
    expect(await screen.findByText(/Computed\./)).toBeInTheDocument();
  });

  it('says so when the records it needs are not in this installation', async () => {
    vi.mocked(api.get).mockResolvedValue({
      ...drill,
      unavailable: true,
      numerator: 0,
      denominator: 0,
      records: [],
      note: 'HC360 has no records from the operation theatre here, so this indicator cannot be computed.',
    });
    render(wrap(<IndicatorDrilldownPanel indicator={indicator} />));
    expect(await screen.findByText(/cannot be computed/)).toBeInTheDocument();
  });
});
