import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { deferred, dropFocusIfDisabled } from '@/test/busyButtons';
import type * as LabApi from './api';
import type { SampleLabel } from './api';
import { CollectDialog, type CollectTarget } from './CollectDialog';

// The server, the label printer and the toasts are outside the test: what is tested is where focus stays while the
// dialog waits for them.
const collect = vi.hoisted(() => vi.fn());
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof LabApi>();
  return { ...actual, labApi: { ...actual.labApi, collect } };
});
const printSampleLabels = vi.hoisted(() => vi.fn());
vi.mock('./print', () => ({ printSampleLabels }));
vi.mock('@/modules/emr/PatientFlagsStrip', () => ({ PatientFlagsStrip: () => null }));
const notify = vi.hoisted(() => ({ success: vi.fn(), warning: vi.fn(), error: vi.fn(), info: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const target: CollectTarget = {
  orderId: 'o1',
  accessionNumber: 'NHLB26000001',
  patientId: 'p1',
  patientName: 'Meena Kumari',
  uhid: 'NH26000001',
  ageSex: '42 Y / F',
  tubes: [
    {
      sampleId: 's1',
      barcode: 'NHS26000001',
      container: 'Edta',
      specimenType: 'WholeBlood',
      section: 'Hematology',
      tests: ['Complete blood count'],
    },
  ],
};

const labels: SampleLabel[] = [
  {
    sampleId: 's1',
    barcode: 'NHS26000001',
    accessionNumber: 'NHLB26000001',
    uhid: 'NH26000001',
    patientName: 'Meena Kumari',
    ageSex: '42 Y / F',
    container: 'Edta',
    specimenType: 'WholeBlood',
    section: 'Hematology',
    priority: 'Routine',
    tests: ['Complete blood count'],
    collectedAt: '2026-10-07T04:30:00Z',
  },
];

function renderDialog() {
  render(
    <QueryClientProvider client={new QueryClient()}>
      <FluentProvider theme={webLightTheme}>
        <CollectDialog target={target} onClose={vi.fn()} />
      </FluentProvider>
    </QueryClientProvider>,
  );
  return screen.getByRole('dialog');
}

describe('CollectDialog', () => {
  beforeEach(() => {
    collect.mockReset();
    printSampleLabels.mockReset();
    Object.values(notify).forEach((spy) => spy.mockClear());
  });

  it('keeps focus on the collect button while the samples are recorded, and records them once', async () => {
    const request = deferred<SampleLabel[]>();
    collect.mockReturnValue(request.promise);
    printSampleLabels.mockResolvedValue(undefined);
    const dialog = renderDialog();
    const submit = within(dialog).getByRole('button', { name: 'Collect and print labels' });

    submit.focus();
    await userEvent.keyboard('{Enter}');
    // The spinner shows while the request runs.
    await within(submit).findByRole('progressbar');
    dropFocusIfDisabled(submit);

    expect(submit).toHaveFocus();
    expect(submit).toHaveAttribute('aria-disabled', 'true');
    await userEvent.keyboard('{Enter}');
    expect(collect).toHaveBeenCalledTimes(1);

    request.resolve(labels);
    expect(await within(dialog).findByText('1 tube collected for Meena Kumari')).toBeInTheDocument();
    await waitFor(() => expect(printSampleLabels).toHaveBeenCalledWith(labels));
  });

  it('keeps focus on Print labels again, inside the dialog, while the labels print', async () => {
    collect.mockResolvedValue(labels);
    printSampleLabels.mockResolvedValueOnce(undefined);
    const dialog = renderDialog();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Collect and print labels' }));
    const again = await within(dialog).findByRole('button', { name: 'Print labels again' });
    await waitFor(() => expect(printSampleLabels).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(again).not.toHaveAttribute('aria-disabled'));

    const printing = deferred();
    printSampleLabels.mockReturnValueOnce(printing.promise);
    again.focus();
    await userEvent.keyboard('{Enter}');
    expect(printSampleLabels).toHaveBeenCalledTimes(2);
    dropFocusIfDisabled(again);

    expect(again).toHaveFocus();
    expect(again).toHaveAttribute('aria-disabled', 'true');
    await userEvent.keyboard('{Enter}');
    expect(printSampleLabels).toHaveBeenCalledTimes(2);

    printing.resolve();
    await waitFor(() => expect(again).not.toHaveAttribute('aria-disabled'));
    expect(again).toHaveFocus();
  });
});
