import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { dropFocusIfDisabled } from '@/test/busyButtons';
import { DocumentButtons } from './DocumentButtons';
import { useServerDocuments } from './documents';

// The fetching, printing and toasts are tested in documents.test.ts; here only the buttons' behaviour.
vi.mock('./documents', () => ({ useServerDocuments: vi.fn() }));

const print = { mutate: vi.fn(), isPending: false };
const downloadPdf = { mutate: vi.fn(), isPending: false };

function renderButtons(props: Partial<Parameters<typeof DocumentButtons>[0]> = {}) {
  const ui = () => (
    <FluentProvider theme={webLightTheme}>
      <DocumentButtons path="/api/v1/clinical/prescriptions/p1" {...props} />
    </FluentProvider>
  );
  const view = render(ui());
  // The hook is a stub: after changing isPending, render again to show it.
  return { ...view, update: () => view.rerender(ui()) };
}

describe('DocumentButtons', () => {
  beforeEach(() => {
    print.mutate.mockReset();
    downloadPdf.mutate.mockReset();
    print.isPending = false;
    downloadPdf.isPending = false;
    vi.mocked(useServerDocuments).mockReturnValue({ print, downloadPdf } as unknown as ReturnType<
      typeof useServerDocuments
    >);
  });

  it('prints the server document', async () => {
    renderButtons({ printLabel: 'Print (A5)' });

    await userEvent.click(screen.getByRole('button', { name: 'Print (A5)' }));

    expect(print.mutate).toHaveBeenCalledWith('/api/v1/clinical/prescriptions/p1');
    expect(downloadPdf.mutate).not.toHaveBeenCalled();
  });

  it('downloads the same document as a PDF', async () => {
    renderButtons();

    await userEvent.click(screen.getByRole('button', { name: 'Download PDF' }));

    expect(downloadPdf.mutate).toHaveBeenCalledWith('/api/v1/clinical/prescriptions/p1');
    expect(print.mutate).not.toHaveBeenCalled();
  });

  it('can be switched off, for example while there are unsaved changes', () => {
    renderButtons({ printLabel: 'Print (A4)', disabled: true });

    expect(screen.getByRole('button', { name: 'Print (A4)' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Download PDF' })).toBeDisabled();
  });

  it('waits for a print before allowing another, without blocking the download', async () => {
    print.isPending = true;
    renderButtons();
    const printButton = screen.getByRole('button', { name: 'Print' });

    expect(printButton).toHaveAttribute('aria-disabled', 'true');
    printButton.focus();
    await userEvent.keyboard('{Enter}');
    expect(print.mutate).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Download PDF' })).not.toHaveAttribute('aria-disabled');
  });

  it('waits for a download before allowing another, without blocking printing', async () => {
    downloadPdf.isPending = true;
    renderButtons();
    const download = screen.getByRole('button', { name: 'Download PDF' });

    expect(download).toHaveAttribute('aria-disabled', 'true');
    download.focus();
    await userEvent.keyboard('{Enter}');
    expect(downloadPdf.mutate).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Print' })).not.toHaveAttribute('aria-disabled');
  });

  it.each([
    { name: 'Print', mutation: print },
    { name: 'Download PDF', mutation: downloadPdf },
  ])('keeps focus on $name while it waits, so keyboard users carry on from there', async ({ name, mutation }) => {
    const view = renderButtons();
    const button = screen.getByRole('button', { name });
    button.focus();
    await userEvent.keyboard('{Enter}');
    expect(mutation.mutate).toHaveBeenCalledTimes(1);

    mutation.isPending = true;
    view.update();
    dropFocusIfDisabled(button);

    expect(button).toHaveFocus();
    expect(button).toHaveAttribute('aria-disabled', 'true');

    mutation.isPending = false;
    view.update();

    expect(button).toHaveFocus();
    expect(button).not.toHaveAttribute('aria-disabled');
  });
});
