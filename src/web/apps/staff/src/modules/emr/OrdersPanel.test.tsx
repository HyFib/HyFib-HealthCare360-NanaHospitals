import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeAll, describe, expect, it, vi } from 'vitest';
import type * as EmrApi from './api';
import { emrApi, type FavouriteDto } from './api';
import type * as OrdersApi from './ordersApi';
import { OrderComposer } from './OrdersPanel';

// The server, the toasts, the leave guard (a router's) and the signed-in user are outside the test.
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
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof EmrApi>();
  return {
    ...actual,
    emrApi: { ...actual.emrApi, removeFavourite: vi.fn(() => Promise.resolve()) },
    emrQueries: {
      ...actual.emrQueries,
      favourites: () => ({ queryKey: ['favourites'], queryFn: () => Promise.resolve([CBC]) }),
    },
  };
});
vi.mock('./ordersApi', async (importOriginal) => {
  const actual = await importOriginal<typeof OrdersApi>();
  return {
    ...actual,
    orderQueries: { ...actual.orderQueries, orderSets: () => ({ queryKey: ['sets'], queryFn: () => [] }) },
  };
});
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

describe('OrderComposer', () => {
  it('asks before removing a favourite', async () => {
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <FluentProvider theme={webLightTheme}>
          <OrderComposer patientId="p1" encounterId="e1" />
        </FluentProvider>
      </QueryClientProvider>,
    );

    await userEvent.click(await screen.findByRole('button', { name: 'Remove Complete blood count from favourites' }));
    const confirm = await screen.findByRole('alertdialog', { name: 'Remove Complete blood count from favourites?' });
    expect(emrApi.removeFavourite).not.toHaveBeenCalled();

    await userEvent.click(within(confirm).getByRole('button', { name: 'Remove' }));
    await waitFor(() => expect(emrApi.removeFavourite).toHaveBeenCalledWith('fav1'));
    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
  });
});
