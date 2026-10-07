import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Core from '@hc360/core';
import { ApiError, saveFile } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as AnalyticsApi from './api';
import { analyticsApi, type ReportResult } from './api';
import { ExportMenu } from './ExportMenu';

// The export call, the browser's download and the toasts are outside the test; the menu's behaviour is what is tested.
vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof AnalyticsApi>()),
  analyticsApi: { exportReport: vi.fn() },
}));
vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  saveFile: vi.fn(),
  todayIso: () => '2026-10-02',
}));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const NOTICE =
  'The PDF lists the first 2,000 of 2,345 rows; the Excel export has every row. The totals are for all rows.';

const result = {
  reportId: 'bed-occupancy',
  from: '2026-09-01',
  to: '2026-09-30',
  parameters: { period: 'lastMonth' },
  sort: null,
  desc: false,
  containsPatientIdentifiers: false,
  pdfNotice: null,
} as unknown as ReportResult;

function renderMenu(overrides: Partial<ReportResult> = {}) {
  render(
    <QueryClientProvider client={new QueryClient()}>
      <FluentProvider theme={webLightTheme}>
        <ExportMenu result={{ ...result, ...overrides }} />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

async function choosePdf() {
  await userEvent.click(screen.getByRole('button', { name: 'Export' }));
  await userEvent.click(await screen.findByRole('menuitem', { name: /PDF \(print-ready\)/ }));
}

describe('ExportMenu', () => {
  const pdf = new Blob(['%PDF-1.7'], { type: 'application/pdf' });

  beforeEach(() => {
    vi.mocked(analyticsApi.exportReport).mockReset();
    vi.mocked(saveFile).mockClear();
    Object.values(notify).forEach((spy) => spy.mockClear());
  });

  it('says before and after the download what a long PDF leaves out', async () => {
    vi.mocked(analyticsApi.exportReport).mockResolvedValue({
      blob: pdf,
      fileName: 'bed-occupancy_20260901-20260930.pdf',
      headers: new Headers({ 'X-Report-Notice': NOTICE }),
    });
    renderMenu({ pdfNotice: NOTICE });

    await userEvent.click(screen.getByRole('button', { name: 'Export' }));
    const item = await screen.findByRole('menuitem', { name: /PDF \(print-ready\)/ });
    expect(item).toHaveTextContent('the Excel export has every row');
    await userEvent.click(item);

    await waitFor(() => expect(notify.info).toHaveBeenCalledWith('PDF downloaded', NOTICE));
    expect(analyticsApi.exportReport).toHaveBeenCalledWith(
      'bed-occupancy',
      { period: 'lastMonth' },
      'Pdf',
      undefined,
      false,
    );
    expect(saveFile).toHaveBeenCalledWith(pdf, 'bed-occupancy_20260901-20260930.pdf');
  });

  it('names the file like the server when it sent no name', async () => {
    vi.mocked(analyticsApi.exportReport).mockResolvedValue({ blob: pdf, fileName: null, headers: new Headers() });
    renderMenu();

    await choosePdf();

    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith('PDF downloaded', 'It is in your Downloads folder.'),
    );
    expect(saveFile).toHaveBeenCalledWith(pdf, 'bed-occupancy_20260901-20260930.pdf');
  });

  it("shows the server's reason when it cannot make the PDF", async () => {
    vi.mocked(analyticsApi.exportReport).mockRejectedValue(
      new ApiError({
        status: 503,
        title: 'The PDF could not be made',
        detail: 'PDF generation is not set up on this server. Export to Excel or CSV instead.',
        code: 'pdf.unavailable',
      }),
    );
    renderMenu();

    await choosePdf();

    await waitFor(() =>
      expect(notify.warning).toHaveBeenCalledWith(
        'The PDF could not be made',
        'PDF generation is not set up on this server. Export to Excel or CSV instead.',
      ),
    );
    expect(saveFile).not.toHaveBeenCalled();
  });

  it('keeps focus on Export while the file is prepared', async () => {
    vi.mocked(analyticsApi.exportReport).mockReturnValue(new Promise<never>(() => {}));
    renderMenu();
    const exportButton = screen.getByRole('button', { name: 'Export' });

    exportButton.focus();
    await userEvent.keyboard('{Enter}');
    await waitFor(() => expect(screen.getByRole('menuitem', { name: /Excel workbook/ })).toHaveFocus());
    await userEvent.keyboard('{Enter}');

    // A disabled button loses focus in the browser; Export stays focusable while it works.
    const preparing = await screen.findByRole('button', { name: 'Preparing file…' });
    expect(preparing).toHaveAttribute('aria-disabled', 'true');
    expect(preparing).not.toBeDisabled();
    expect(preparing).toHaveFocus();
  });

  it('returns focus to Export when the patient-list confirmation is closed from the keyboard', async () => {
    renderMenu({ containsPatientIdentifiers: true });
    const exportButton = screen.getByRole('button', { name: 'Export' });

    exportButton.focus();
    await userEvent.keyboard('{Enter}');
    await waitFor(() => expect(screen.getByRole('menuitem', { name: /Excel workbook/ })).toHaveFocus());
    await userEvent.keyboard('{Enter}');
    const dialog = await screen.findByRole('alertdialog', { name: 'Download a list of patients?' });
    await waitFor(() => expect(dialog).toContainElement(document.activeElement as HTMLElement));
    await userEvent.keyboard('{Escape}');

    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
    await waitFor(() => expect(exportButton).toHaveFocus());
    expect(analyticsApi.exportReport).not.toHaveBeenCalled();
  });
});
