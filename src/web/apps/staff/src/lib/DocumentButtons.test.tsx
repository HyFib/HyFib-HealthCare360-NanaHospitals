import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { DocumentButtons } from './DocumentButtons';
import { useServerDocuments } from './documents';

// The fetching, printing and toasts are tested in documents.test.ts; here only the buttons' behaviour.
vi.mock('./documents', () => ({ useServerDocuments: vi.fn() }));

const print = { mutate: vi.fn(), isPending: false };
const downloadPdf = { mutate: vi.fn(), isPending: false };

function renderButtons(props: Partial<Parameters<typeof DocumentButtons>[0]> = {}) {
  return render(
    <FluentProvider theme={webLightTheme}>
      <DocumentButtons path="/api/v1/clinical/prescriptions/p1" {...props} />
    </FluentProvider>,
  );
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

  it('waits for a print before allowing another, without blocking the download', () => {
    print.isPending = true;
    renderButtons();

    expect(screen.getByRole('button', { name: 'Print' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Download PDF' })).toBeEnabled();
  });

  it('waits for a download before allowing another, without blocking printing', () => {
    downloadPdf.isPending = true;
    renderButtons();

    expect(screen.getByRole('button', { name: 'Download PDF' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Print' })).toBeEnabled();
  });
});
