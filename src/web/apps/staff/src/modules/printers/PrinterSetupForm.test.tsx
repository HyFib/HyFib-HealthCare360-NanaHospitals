import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type { PrinterInfo, PrinterSetupInfo } from '@hc360/core';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeAll, describe, expect, it, vi } from 'vitest';
import { PrinterSetupForm } from './PrinterSetupForm';

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

const installed: PrinterInfo[] = [
  { name: 'HP LaserJet M404', isDefault: true },
  { name: 'TSC TE244', isDefault: false },
  { name: 'EPSON TM-T82 Receipt', isDefault: false },
];

const info: PrinterSetupInfo = {
  computerName: 'LAB-COLLECTION-1',
  lockedByPolicy: false,
  setup: {
    labels: { printer: 'ZDesigner GK420t', language: 'zpl', dpi: 203, widthMm: 50, heightMm: 25 },
    receipts: { printer: 'EPSON TM-T82 Receipt' },
  },
};

function renderForm(props: Partial<Parameters<typeof PrinterSetupForm>[0]> = {}) {
  const onSave = vi.fn();
  const onTest = vi.fn();
  render(
    <FluentProvider theme={webLightTheme}>
      <PrinterSetupForm
        info={info}
        installed={installed}
        canEdit
        saving={false}
        testing={null}
        onSave={onSave}
        onTest={onTest}
        {...props}
      />
    </FluentProvider>,
  );
  return { onSave, onTest };
}

const printerSelects = () => screen.getAllByRole('combobox', { name: 'Printer' });

describe('PrinterSetupForm', () => {
  it('shows a card for each kind of print with its printer, and warns about a printer no longer installed', () => {
    renderForm();

    expect(screen.getByRole('heading', { name: 'Labels' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Wristbands' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Receipts and token slips (80 mm)' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Documents (A4 and A5)' })).toBeInTheDocument();
    const [labels, wristbands, receipts, documents] = printerSelects();
    expect(labels).toHaveValue('ZDesigner GK420t');
    expect(wristbands).toHaveValue('');
    expect(receipts).toHaveValue('EPSON TM-T82 Receipt');
    expect(documents).toHaveValue('');
    expect(
      screen.getByText('"ZDesigner GK420t" is not installed on this computer. Choose another printer.'),
    ).toBeInTheDocument();
    expect(screen.getAllByRole('option', { name: 'HP LaserJet M404 (Windows default)' })).toHaveLength(4);
  });

  it('guesses the printer language and saves the printers chosen', async () => {
    const { onSave } = renderForm();
    const user = userEvent.setup();

    await user.selectOptions(printerSelects()[0]!, 'TSC TE244');
    expect(screen.getAllByRole('combobox', { name: 'How HC360 prints to it' })[0]).toHaveValue('tspl');
    await user.selectOptions(printerSelects()[3]!, 'HP LaserJet M404');
    await user.click(screen.getByRole('button', { name: 'Save printers' }));

    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(1));
    const values = onSave.mock.calls[0]![0];
    expect(values.labels).toMatchObject({
      printer: 'TSC TE244',
      language: 'tspl',
      dpi: '203',
      widthMm: 50,
      heightMm: 25,
    });
    expect(values.documents).toEqual({ printer: 'HP LaserJet M404' });
  });

  it('refuses a label size the printer cannot take before saving', async () => {
    const { onSave } = renderForm();
    const user = userEvent.setup();

    const width = screen.getByRole('spinbutton', { name: 'Label width (mm)' });
    await user.clear(width);
    await user.type(width, '120');
    await user.click(screen.getByRole('button', { name: 'Save printers' }));

    expect(await screen.findByText('Enter the label width: 25 to 110 mm.')).toBeInTheDocument();
    expect(onSave).not.toHaveBeenCalled();
  });

  it('prints a test with what is on the screen, saved or not', async () => {
    const { onTest } = renderForm();
    const user = userEvent.setup();

    await user.selectOptions(printerSelects()[0]!, 'TSC TE244');
    await user.click(screen.getAllByRole('button', { name: 'Print a test' })[0]!);

    expect(onTest).toHaveBeenCalledWith('labels', {
      printer: 'TSC TE244',
      language: 'tspl',
      dpi: 203,
      widthMm: 50,
      heightMm: 25,
    });
    expect(screen.getAllByRole('button', { name: 'Print a test' })[1]).toBeDisabled();
  });

  it('shows the printers without changing them for staff who may not set up printers', () => {
    renderForm({ canEdit: false });

    printerSelects().forEach((select) => expect(select).toBeDisabled());
    expect(screen.queryByRole('button', { name: 'Save printers' })).not.toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'Print a test' })[0]).toBeEnabled();
  });
});
