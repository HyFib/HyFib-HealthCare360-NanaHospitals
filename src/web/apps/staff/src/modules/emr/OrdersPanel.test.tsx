import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type { PagedResult } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type * as Router from '@tanstack/react-router';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeAll, describe, expect, it, vi } from 'vitest';
import type * as EmrApi from './api';
import { emrApi, type FavouriteDto, type LabReportView } from './api';
import type * as OrdersApi from './ordersApi';
import type { OrderDto, OrderItemDto } from './ordersApi';
import { OrderComposer, OrdersList } from './OrdersPanel';

// The server, the toasts, the leave guard (a router's), the router and the signed-in user are outside the test.
const CBC: FavouriteDto = {
  id: 'fav1',
  kind: 'Order',
  label: 'Complete blood count',
  serviceItemId: 'svc1',
  drugItemId: null,
  genericName: null,
  strength: null,
  dosageForm: null,
  route: null,
  dose: null,
  frequency: null,
  customFrequency: null,
  durationDays: null,
  instructions: null,
  isControlledDrug: false,
  displayOrder: 1,
};
const LAB_REPORT = '0192c0de-0000-7000-8000-00000000a002';
const line = (patch: Partial<OrderItemDto>): OrderItemDto => ({
  id: 'i1',
  serviceItemId: 'svc1',
  serviceCode: 'LAB-CBC',
  serviceName: 'Complete blood count',
  category: 'Laboratory',
  quantity: 1,
  status: 'Completed',
  notes: null,
  statusChangedAt: '2026-10-02T05:30:00Z',
  completedAt: '2026-10-02T05:30:00Z',
  resultReference: LAB_REPORT,
  cancelReason: null,
  ...patch,
});
const ORDERS: PagedResult<OrderDto> = {
  items: [
    {
      id: 'o1',
      orderNumber: 'NHOR260000101',
      patientId: 'p1',
      encounterId: null,
      visitId: null,
      admissionId: null,
      orderingPractitionerId: null,
      orderingPractitionerName: 'Dr. Meera Iyer',
      priority: 'Routine',
      status: 'Active',
      source: 'Direct',
      orderedAt: '2026-10-02T04:00:00Z',
      clinicalNotes: null,
      items: [
        line({}),
        line({
          id: 'i2',
          serviceItemId: 'svc2',
          serviceCode: 'LAB-LIPID',
          serviceName: 'Lipid profile',
          status: 'Ordered',
          completedAt: null,
          resultReference: null,
        }),
      ],
    },
  ],
  page: 1,
  pageSize: 20,
  totalCount: 1,
  totalPages: 1,
};
const LAB: LabReportView = {
  reportId: LAB_REPORT,
  reportNumber: 'NHLB260000077',
  status: 'Released',
  isPartial: true,
  releasedAt: '2026-10-02T05:30:00Z',
  sections: [
    {
      section: 'Haematology',
      tests: [
        {
          orderItemId: 'i1',
          name: 'Complete blood count',
          interpretation: null,
          results: [
            {
              resultId: 'r1',
              name: 'Haemoglobin',
              groupName: null,
              value: '13.2',
              unit: 'g/dL',
              flag: 'Normal',
              referenceText: '12.0 - 15.0',
              remarks: null,
            },
          ],
        },
      ],
    },
  ],
};
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof EmrApi>();
  return {
    ...actual,
    emrApi: { ...actual.emrApi, removeFavourite: vi.fn(() => Promise.resolve()) },
    emrQueries: {
      ...actual.emrQueries,
      favourites: () => ({ queryKey: ['favourites'], queryFn: () => Promise.resolve([CBC]) }),
      labReport: (id: string) => ({ queryKey: ['lab-report', id], queryFn: () => Promise.resolve(LAB) }),
    },
  };
});
vi.mock('./ordersApi', async (importOriginal) => {
  const actual = await importOriginal<typeof OrdersApi>();
  return {
    ...actual,
    orderQueries: {
      ...actual.orderQueries,
      orderSets: () => ({ queryKey: ['sets'], queryFn: () => [] }),
      list: () => ({ queryKey: ['orders'], queryFn: () => Promise.resolve(ORDERS) }),
    },
  };
});
vi.mock('@tanstack/react-router', async (importOriginal) => ({
  ...(await importOriginal<typeof Router>()),
  useNavigate: () => vi.fn(),
}));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({
  ...(await importOriginal<typeof Ui>()),
  useNotify: () => notify,
  useLeaveGuard: () => undefined,
}));
vi.mock('@/lib/permissions', () => ({ usePermission: () => true }));

// Fluent's dialogs measure themselves; jsdom has no ResizeObserver.
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

function wrap(children: ReactNode) {
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <FluentProvider theme={webLightTheme}>{children}</FluentProvider>
    </QueryClientProvider>,
  );
}

describe('OrderComposer', () => {
  it('asks before removing a favourite', async () => {
    wrap(<OrderComposer patientId="p1" encounterId="e1" />);

    await userEvent.click(await screen.findByRole('button', { name: 'Remove Complete blood count from favourites' }));
    const confirm = await screen.findByRole('alertdialog', { name: 'Remove Complete blood count from favourites?' });
    expect(emrApi.removeFavourite).not.toHaveBeenCalled();

    await userEvent.click(within(confirm).getByRole('button', { name: 'Remove' }));
    await waitFor(() => expect(emrApi.removeFavourite).toHaveBeenCalledWith('fav1'));
    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
  });
});

describe('OrdersList', () => {
  it('opens the report of a line whose result is released', async () => {
    wrap(<OrdersList patientId="p1" encounterId={null} />);

    expect(await screen.findByText('Lipid profile')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'View report of Lipid profile' })).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'View report of Complete blood count' }));

    const dialog = await screen.findByRole('dialog', { name: 'Complete blood count' });
    expect(await within(dialog).findByText('Haemoglobin')).toBeInTheDocument();
    expect(within(dialog).getByText('13.2 g/dL')).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Download PDF' })).toBeInTheDocument();
  });
});
