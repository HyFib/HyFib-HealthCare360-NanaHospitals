import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { saveFile } from '@hc360/core';
import type * as Core from '@hc360/core';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import { useFileViewer } from './FileViewerDialog';

vi.mock('@/lib/api', () => ({ api: { blob: vi.fn() } }));
vi.mock('@hc360/core', async (importOriginal) => ({ ...(await importOriginal<typeof Core>()), saveFile: vi.fn() }));

function Viewer({ title, fileName }: { title: string; fileName?: string }) {
  const viewer = useFileViewer();
  return (
    <>
      <button type="button" onClick={() => void viewer.open('file-1', title, fileName)}>
        Open
      </button>
      {viewer.dialog}
    </>
  );
}

function renderViewer(title: string, fileName?: string) {
  render(
    <FluentProvider theme={webLightTheme}>
      <Viewer title={title} fileName={fileName} />
    </FluentProvider>,
  );
}

describe('useFileViewer', () => {
  beforeEach(() => {
    URL.createObjectURL = vi.fn(() => 'blob:file');
    URL.revokeObjectURL = vi.fn();
  });

  afterEach(() => {
    vi.mocked(api.blob).mockReset();
    vi.mocked(saveFile).mockReset();
  });

  it('offers to save an ECG file that it cannot show', async () => {
    const ecg = new Blob([new Uint8Array(64)], { type: 'application/x-scp-ecg' });
    vi.mocked(api.blob).mockResolvedValue(ecg);
    renderViewer('ECG 12-lead', 'ecg-0412.scp');

    await userEvent.click(screen.getByRole('button', { name: 'Open' }));

    // The dialog can take a while to open when the whole suite runs at once.
    expect(await screen.findByText(/HC360 cannot show this file/, {}, { timeout: 10_000 })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Save file' }));
    expect(saveFile).toHaveBeenCalledWith(ecg, 'ecg-0412.scp');
  });

  it('shows a PDF tracing in the app', async () => {
    vi.mocked(api.blob).mockResolvedValue(new Blob(['%PDF-'], { type: 'application/pdf' }));
    renderViewer('tracing.pdf');

    await userEvent.click(screen.getByRole('button', { name: 'Open' }));

    expect(await screen.findByTitle('tracing.pdf', {}, { timeout: 10_000 })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save file' })).not.toBeInTheDocument();
  });
});
