import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type * as MrdApi from './api';
import { MedicalRecordsWidget } from './widgets';

// The server, the router and the signed-in user are outside the test.
const server = vi.hoisted(() => ({ codings: vi.fn(), records: vi.fn(), requests: vi.fn() }));
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof MrdApi>();
  return {
    ...actual,
    mrdQueries: {
      ...actual.mrdQueries,
      codings: () => ({ queryKey: ['codings'], queryFn: server.codings }),
      records: () => ({ queryKey: ['records'], queryFn: server.records }),
      requests: () => ({ queryKey: ['requests'], queryFn: server.requests }),
    },
  };
});
vi.mock('@tanstack/react-router', () => ({ useNavigate: () => vi.fn() }));
vi.mock('@/lib/permissions', () => ({ usePermission: () => true }));

// Fluent's MessageBar measures itself; jsdom has no ResizeObserver.
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

describe('MedicalRecordsWidget', () => {
  beforeEach(() => {
    server.codings.mockReset().mockResolvedValue({ totalCount: 3, items: [] });
    server.records.mockReset().mockRejectedValue(new Error('offline'));
    server.requests.mockReset().mockResolvedValue({ totalCount: 0, items: [] });
  });

  it('says a count could not be loaded, rather than showing it as loading forever, and retries it', async () => {
    const user = userEvent.setup();
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <FluentProvider theme={webLightTheme}>
          <MedicalRecordsWidget />
        </FluentProvider>
      </QueryClientProvider>,
    );

    expect(await screen.findByText('Could not load')).toBeInTheDocument();
    expect(screen.getByText('—')).toBeInTheDocument();
    expect(screen.getByText('3')).toBeInTheDocument();

    server.records.mockResolvedValue({ totalCount: 7, items: [] });
    await user.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText('7')).toBeInTheDocument();
    expect(screen.queryByText('Could not load')).not.toBeInTheDocument();
  });
});
