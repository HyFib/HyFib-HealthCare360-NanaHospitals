import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { ApiError } from '@hc360/core';
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

function renderDialog(collectTarget: CollectTarget = target) {
  render(
    <QueryClientProvider client={new QueryClient()}>
      <FluentProvider theme={webLightTheme}>
        <CollectDialog target={collectTarget} onClose={vi.fn()} />
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
    // The spinner shows while the request runs (hidden from screen readers, which hear the button is unavailable). Not
    // aria-disabled: a button that went back to disabled would then time out here instead of failing on focus below.
    await within(submit).findByRole('progressbar', { hidden: true });
    dropFocusIfDisabled(submit);

    expect(submit).toHaveFocus();
    expect(submit).toHaveAttribute('aria-disabled', 'true');
    await userEvent.keyboard('{Enter}');
    expect(collect).toHaveBeenCalledTimes(1);

    request.resolve(labels);
    expect(await within(dialog).findByText('1 tube collected for Meena Kumari')).toBeInTheDocument();
    await waitFor(() => expect(printSampleLabels).toHaveBeenCalledWith(labels));
  });

  it('asks for the consent to an HIV test the server holds back, and sends it with the collection', async () => {
    collect.mockRejectedValueOnce(
      new ApiError({
        status: 422,
        title: 'The request breaks a rule',
        detail: "HIV 1 & 2 antibodies: an HIV test needs the patient's informed consent before its sample is drawn.",
        code: 'lab.consent_required',
        data: { tests: [{ itemId: 'i1', name: 'HIV 1 & 2 antibodies', sampleId: 's1' }] },
      }),
    );
    collect.mockResolvedValueOnce(labels);
    printSampleLabels.mockResolvedValue(undefined);
    const dialog = renderDialog();
    const submit = within(dialog).getByRole('button', { name: 'Collect and print labels' });
    await userEvent.click(submit);

    // The test is named, and the samples are not collected until counselling is confirmed and the form numbered.
    const consent = await within(dialog).findByRole('group', { name: 'Record the consent' });
    expect(consent).toHaveTextContent('HIV 1 & 2 antibodies');
    expect(submit).toBeDisabled();
    await userEvent.type(within(consent).getByRole('textbox', { name: 'Consent form number' }), 'ICTC/42');
    await userEvent.click(within(consent).getByRole('checkbox', { name: /Pre-test counselling was given/ }));
    await userEvent.click(submit);

    expect(await within(dialog).findByText('1 tube collected for Meena Kumari')).toBeInTheDocument();
    expect(collect).toHaveBeenLastCalledWith(
      'o1',
      expect.objectContaining({
        consents: [{ itemId: 'i1', basis: 'Patient', givenBy: null, relation: null, reference: 'ICTC/42' }],
      }),
    );
  });

  it('draws the other tubes without the consent once the held HIV tube is left out', async () => {
    collect.mockRejectedValueOnce(
      new ApiError({
        status: 422,
        title: 'The request breaks a rule',
        detail: "HIV 1 & 2 antibodies: an HIV test needs the patient's informed consent before its sample is drawn.",
        code: 'lab.consent_required',
        data: { tests: [{ itemId: 'i2', name: 'HIV 1 & 2 antibodies', sampleId: 's2' }] },
      }),
    );
    collect.mockResolvedValueOnce(labels);
    printSampleLabels.mockResolvedValue(undefined);
    const dialog = renderDialog({
      ...target,
      tubes: [
        ...target.tubes,
        {
          sampleId: 's2',
          barcode: 'NHS26000002',
          container: 'Plain',
          specimenType: 'Serum',
          section: 'Serology',
          tests: ['HIV 1 & 2 antibodies'],
        },
      ],
    });
    const submit = within(dialog).getByRole('button', { name: 'Collect and print labels' });
    await userEvent.click(submit);
    await within(dialog).findByRole('group', { name: 'Record the consent' });
    expect(submit).toBeDisabled();

    // The patient is not ready for the HIV test: the collector leaves its tube out and draws the blood count.
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Collect HIV 1 & 2 antibodies' }));
    expect(within(dialog).queryByRole('group', { name: 'Record the consent' })).not.toBeInTheDocument();
    expect(submit).toBeEnabled();
    await userEvent.click(submit);

    expect(await within(dialog).findByText('1 tube collected for Meena Kumari')).toBeInTheDocument();
    expect(collect).toHaveBeenLastCalledWith('o1', expect.objectContaining({ sampleIds: ['s1'], consents: null }));
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
