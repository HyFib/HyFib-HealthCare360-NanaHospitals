import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import { RecordSafetyEventDialog } from './SafetyEvents';

// Recording a fall or a pressure injury on the nursing chart. The server is replaced.

vi.mock('@/lib/api', () => ({ api: { get: vi.fn(), post: vi.fn() } }));

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

describe('recording a fall or pressure injury', () => {
  it('records a pressure injury present on admission with its stage and site', async () => {
    const user = userEvent.setup();
    vi.mocked(api.post).mockResolvedValue({});
    const onClose = vi.fn();
    render(wrap(<RecordSafetyEventDialog admissionId="a1" onClose={onClose} />));

    expect(screen.getByText(/also reported to the quality team/)).toBeInTheDocument();
    await user.selectOptions(screen.getByRole('combobox', { name: /What/ }), 'PressureInjury');
    await user.selectOptions(screen.getByRole('combobox', { name: /Stage/ }), 'Stage2');
    await user.type(screen.getByRole('textbox', { name: /Where on the body/ }), 'Sacrum');
    await user.click(screen.getByRole('switch', { name: /Present on admission/ }));
    expect(screen.queryByText(/also reported to the quality team/)).not.toBeInTheDocument();
    await user.type(screen.getByRole('textbox', { name: /^Where/ }), 'Bed 4');
    await user.type(screen.getByRole('textbox', { name: /What happened/ }), 'Blister over the sacrum at the admission skin check.');
    await expectNoAxeViolations();
    await user.click(screen.getByRole('button', { name: 'Record' }));

    expect(api.post).toHaveBeenCalledWith(
      '/api/v1/nursing/admissions/a1/safety-events',
      expect.objectContaining({
        kind: 'PressureInjury',
        stage: 'Stage2',
        bodySite: 'Sacrum',
        presentOnAdmission: true,
        fallInjury: null,
      }),
    );
    expect(onClose).toHaveBeenCalled();
  });
});
