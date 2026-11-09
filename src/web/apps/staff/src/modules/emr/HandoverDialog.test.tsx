import { FluentProvider } from '@fluentui/react-components';
import type * as Ui from '@hc360/ui';
import { hc360LightTheme } from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { HandoverDialog, type DoctorHandover } from './HandoverDialog';

// Handing the doctor's patients over to a covering colleague (maker-checker, slice 15): who covers, until when, and the
// handovers now, which either doctor ends.

const get = vi.hoisted(() => vi.fn());
const post = vi.hoisted(() => vi.fn());
vi.mock('@/lib/api', () => ({ api: { get, post } }));
vi.mock('@/lib/lookups', () => ({ searchPractitioners: vi.fn(() => Promise.resolve([])) }));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const NOW: DoctorHandover = {
  id: 'h1',
  fromPractitionerId: 'd1',
  fromName: 'Dr. Meera Iyer',
  toPractitionerId: 'd2',
  toName: 'Dr. Ravi Kumar',
  patientId: null,
  startsAt: '2026-11-08T12:00:00Z',
  until: '2026-11-09T02:00:00Z',
  endedAt: null,
  note: 'Night cover',
  version: 3,
};

function show() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <FluentProvider theme={hc360LightTheme}>
      <QueryClientProvider client={client}>
        <HandoverDialog onClose={vi.fn()} />
      </QueryClientProvider>
    </FluentProvider>,
  );
}

describe('HandoverDialog', () => {
  it('asks for the covering doctor before handing over, lists the handovers now and ends one, with no accessibility problems', async () => {
    get.mockResolvedValue([NOW]);
    post.mockResolvedValue({ ...NOW, endedAt: '2026-11-08T13:00:00Z' });
    show();

    expect(await screen.findByRole('dialog', { name: 'Hand over my patients' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Hand over' })).toBeDisabled();
    expect(await screen.findByText(/Dr. Meera Iyer → Dr. Ravi Kumar/)).toBeInTheDocument();
    await expectNoAxeViolations();

    await userEvent.click(screen.getByRole('button', { name: 'End' }));
    await waitFor(() => expect(post).toHaveBeenCalledWith('/api/v1/emr/handovers/h1/end', { version: 3 }));
  });
});
