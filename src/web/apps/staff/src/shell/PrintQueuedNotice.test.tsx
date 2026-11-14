import { Toaster, FluentProvider, webLightTheme } from '@fluentui/react-components';
import { bridge, printHtml } from '@hc360/core';
import { TOASTER_ID } from '@hc360/ui';
import { render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { PrintQueuedNotice } from './PrintQueuedNotice';

function renderShell() {
  render(
    <FluentProvider theme={webLightTheme}>
      <PrintQueuedNotice />
      <Toaster toasterId={TOASTER_ID} />
    </FluentProvider>,
  );
}

describe('PrintQueuedNotice', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('warns "Not printed yet" with the outcome message when a print is queued behind a paused printer, whichever screen printed', async () => {
    vi.spyOn(bridge, 'isDesktop', 'get').mockReturnValue(true);
    vi.spyOn(bridge, 'printHtml').mockResolvedValue({
      status: 'queued_printer_paused',
      printer: 'HP LaserJet M404',
      message: "The document is queued on 'HP LaserJet M404', but the printer is paused in Windows.",
    });
    renderShell();

    // A caller that ignores the result, as most screens do.
    await printHtml('<p>bill</p>', { printerName: 'HP LaserJet M404', silent: true });

    expect(await screen.findByText('Not printed yet')).toBeInTheDocument();
    expect(await screen.findByText(/printer is paused in Windows/)).toBeInTheDocument();
  });

  it('says nothing when the printer took the job', async () => {
    vi.spyOn(bridge, 'isDesktop', 'get').mockReturnValue(true);
    vi.spyOn(bridge, 'printHtml').mockResolvedValue({ status: 'printed', printer: 'HP LaserJet M404', message: null });
    renderShell();

    await printHtml('<p>bill</p>', { printerName: 'HP LaserJet M404', silent: true });

    expect(screen.queryByText('Not printed yet')).not.toBeInTheDocument();
  });
});
