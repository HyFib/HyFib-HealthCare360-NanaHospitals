import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type * as CssdApi from './api';
import type { WashCycleDto } from './api';
import { CompleteWashDialog } from './WashDialogs';

// The server and the toasts are outside the test; the dialog's own checks and what it sends are what is tested.
const completeWash = vi.hoisted(() => vi.fn());
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof CssdApi>();
  return { ...actual, cssdApi: { ...actual.cssdApi, completeWash } };
});
const notify = vi.hoisted(() => ({ success: vi.fn(), warning: vi.fn(), error: vi.fn(), info: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

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

const wash: WashCycleDto = {
  id: 'w1',
  cycleNumber: 'NHCW26000001',
  washerName: 'Washer-disinfector 1',
  program: null,
  status: 'Running',
  startedAt: '2026-10-07T04:30:00Z',
  completedAt: null,
  disinfectionTemperatureC: null,
  holdMinutes: null,
  a0: null,
  requiredA0: 3000,
  inspectedClean: null,
  failureReason: null,
  operatorName: 'Lakshmi',
  notes: null,
  trays: [{ instrumentSetId: 's1', setName: 'Major surgical tray', quantity: 2 }],
  packs: [],
  version: 1,
};

function renderDialog() {
  const onClose = vi.fn();
  render(
    <QueryClientProvider client={new QueryClient()}>
      <FluentProvider theme={webLightTheme}>
        <CompleteWashDialog wash={wash} onClose={onClose} />
      </FluentProvider>
    </QueryClientProvider>,
  );
  return onClose;
}

describe('CompleteWashDialog', () => {
  beforeEach(() => {
    completeWash.mockReset();
    Object.values(notify).forEach((spy) => spy.mockClear());
  });

  it('warns that a cycle below A0 3000 will fail', async () => {
    renderDialog();
    const temperature = screen.getByRole('spinbutton', { name: /Disinfection temperature/ });
    await userEvent.clear(temperature);
    await userEvent.type(temperature, '85');
    await userEvent.type(screen.getByRole('spinbutton', { name: /Held for/ }), '5');
    await userEvent.click(screen.getByRole('radio', { name: 'Clean' }));

    expect(screen.getByText('A0 948 is below 3000')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Record as failed' })).toBeEnabled();
  });

  it('records a cycle that reaches A0 3000 with clean trays', async () => {
    completeWash.mockResolvedValue({ ...wash, status: 'Passed', a0: 3000 });
    const onClose = renderDialog();
    await userEvent.type(screen.getByRole('spinbutton', { name: /Held for/ }), '5');
    await userEvent.click(screen.getByRole('radio', { name: 'Clean' }));
    expect(screen.getByText('A0 3000 reaches 3000.')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Record result' }));

    await waitFor(() =>
      expect(completeWash).toHaveBeenCalledWith('w1', { temperatureC: 90, holdMinutes: 5, inspectedClean: true }),
    );
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(notify.success).toHaveBeenCalledWith(
      'Wash cycle NHCW26000001 passed',
      'The trays can be packed and loaded.',
    );
  });
});
