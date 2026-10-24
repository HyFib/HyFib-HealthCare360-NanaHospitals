import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { ApiError, todayIso } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import type { PatientDocumentDto } from './api';
import { DocumentsPanel } from './DocumentsPanel';

// The server, the toasts and the signed-in user are outside the test; what the panel sends and shows is what is tested.
vi.mock('@/lib/api', () => ({ api: { get: vi.fn(), post: vi.fn(), delete: vi.fn(), blob: vi.fn() } }));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));
// Every permission unless a test takes one away.
const denied = vi.hoisted(() => new Set<string>());
vi.mock('@/lib/permissions', () => ({ usePermission: (permission: string) => !denied.has(permission) }));

const discharge: PatientDocumentDto = {
  id: 'd1',
  fileId: 'f1',
  type: 'DischargeSummary',
  title: 'Discharge summary after knee surgery',
  documentDate: '2026-08-14',
  source: 'Ruby Hall Clinic, Pune',
  notes: null,
  createdAt: '2026-10-20T05:00:00Z',
  fileName: 'discharge.pdf',
  contentType: 'application/pdf',
  sizeBytes: 48_000,
};

const report = () => new File(['%PDF-1.7'], 'discharge.pdf', { type: 'application/pdf' });

function renderPanel() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <FluentProvider theme={webLightTheme}>
        <DocumentsPanel patientId="p1" />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

describe('DocumentsPanel', () => {
  beforeEach(() => {
    vi.mocked(api.get).mockReset().mockResolvedValue([]);
    vi.mocked(api.post).mockReset();
    vi.mocked(api.delete).mockReset();
    denied.clear();
    Object.values(notify).forEach((spy) => spy.mockClear());
  });

  it('labels every field of the upload, the file chooser included', async () => {
    renderPanel();
    expect(await screen.findByText('No documents')).toBeInTheDocument();

    expect(screen.getByLabelText(/^File/)).toHaveAttribute('type', 'file');
    await expectNoAxeViolations();
  });

  it('uploads the file, then attaches it with its type, the date on it and where it came from', async () => {
    const user = userEvent.setup();
    vi.mocked(api.get).mockResolvedValueOnce([]).mockResolvedValue([discharge]);
    vi.mocked(api.post).mockResolvedValueOnce({ id: 'f1' }).mockResolvedValueOnce({ id: 'd1' });
    renderPanel();
    expect(await screen.findByText('No documents')).toBeInTheDocument();

    const file = report();
    await user.upload(screen.getByLabelText(/^File/), file);
    await user.selectOptions(screen.getByRole('combobox', { name: /^Type/ }), 'DischargeSummary');
    await user.type(screen.getByRole('textbox', { name: /^Title/ }), 'Discharge summary after knee surgery');
    await user.type(screen.getByLabelText(/^Document date/), '2026-08-14');
    await user.type(screen.getByRole('textbox', { name: /^Source/ }), 'Ruby Hall Clinic, Pune');
    await user.click(screen.getByRole('button', { name: 'Attach' }));

    await waitFor(() => expect(notify.success).toHaveBeenCalledWith('Document attached'));
    expect(api.post).toHaveBeenCalledTimes(2);
    const [uploadPath, upload] = vi.mocked(api.post).mock.calls[0]!;
    expect(uploadPath).toBe('/api/v1/files');
    expect((upload as FormData).get('patientId')).toBe('p1');
    expect(((upload as FormData).get('file') as File).name).toBe('discharge.pdf');
    expect(api.post).toHaveBeenLastCalledWith('/api/v1/patients/p1/documents', {
      fileId: 'f1',
      type: 'DischargeSummary',
      title: 'Discharge summary after knee surgery',
      documentDate: '2026-08-14',
      source: 'Ruby Hall Clinic, Pune',
      notes: null,
    });

    // The list shows them back, and the form is ready for the next document.
    const row = (await screen.findByText('Ruby Hall Clinic, Pune')).closest('tr')!;
    expect(within(row).getByText('14 Aug 2026')).toBeInTheDocument();
    expect(within(row).getByText('Discharge summary after knee surgery')).toBeInTheDocument();
    expect(within(row).getByText('Discharge summary')).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: /^Source/ })).toHaveValue('');
    expect(screen.getByRole('button', { name: 'Attach' })).toBeDisabled();
  });

  it('says a document cannot be dated after today, and sends nothing', async () => {
    const user = userEvent.setup();
    renderPanel();
    expect(await screen.findByText('No documents')).toBeInTheDocument();

    await user.upload(screen.getByLabelText(/^File/), report());
    await user.type(screen.getByLabelText(/^Document date/), todayIso(1));
    await user.click(screen.getByRole('button', { name: 'Attach' }));

    expect(await screen.findByText('The document date cannot be in the future.')).toBeInTheDocument();
    expect(api.post).not.toHaveBeenCalled();
  });

  it('shows what the server refused on its field, not in a toast', async () => {
    const user = userEvent.setup();
    vi.mocked(api.post)
      .mockResolvedValueOnce({ id: 'f1' })
      .mockRejectedValueOnce(
        new ApiError({
          status: 400,
          title: 'Please correct the highlighted fields.',
          errors: { source: ['Contains characters that are not allowed.'] },
        }),
      );
    renderPanel();
    expect(await screen.findByText('No documents')).toBeInTheDocument();

    await user.upload(screen.getByLabelText(/^File/), report());
    await user.type(screen.getByRole('textbox', { name: /^Source/ }), 'Ruby Hall');
    await user.click(screen.getByRole('button', { name: 'Attach' }));

    expect(await screen.findByText('Contains characters that are not allowed.')).toBeInTheDocument();
    expect(notify.error).not.toHaveBeenCalled();
    expect(notify.success).not.toHaveBeenCalled();
  });

  it('removes a document once confirmed, and lists the rest', async () => {
    const user = userEvent.setup();
    vi.mocked(api.get).mockResolvedValueOnce([discharge]).mockResolvedValue([]);
    vi.mocked(api.delete).mockResolvedValueOnce(undefined);
    renderPanel();

    await user.click(
      await screen.findByRole('button', { name: 'Remove document: Discharge summary after knee surgery' }),
    );
    const dialog = await screen.findByRole('alertdialog', { name: 'Remove document' });
    expect(within(dialog).getByText('Discharge summary after knee surgery')).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Remove' }));

    await waitFor(() => expect(notify.success).toHaveBeenCalledWith('Document removed'));
    expect(api.delete).toHaveBeenCalledWith('/api/v1/patients/documents/d1');
    expect(await screen.findByText('No documents')).toBeInTheDocument();
  });

  it('says why a document could not be removed, and keeps it listed', async () => {
    const user = userEvent.setup();
    vi.mocked(api.get).mockResolvedValue([discharge]);
    const refusal = new ApiError({ status: 403, title: 'This is a restricted record.' });
    vi.mocked(api.delete).mockRejectedValueOnce(refusal);
    renderPanel();

    await user.click(
      await screen.findByRole('button', { name: 'Remove document: Discharge summary after knee surgery' }),
    );
    await user.click(
      within(await screen.findByRole('alertdialog', { name: 'Remove document' })).getByRole('button', {
        name: 'Remove',
      }),
    );

    await waitFor(() => expect(notify.error).toHaveBeenCalledWith(refusal, 'Could not remove the document'));
    expect(notify.success).not.toHaveBeenCalled();
    expect(screen.getByText('Ruby Hall Clinic, Pune')).toBeInTheDocument();
  });

  it('offers no removal without patients.documents', async () => {
    denied.add('patients.documents');
    vi.mocked(api.get).mockResolvedValue([discharge]);
    renderPanel();

    expect(await screen.findByText('Ruby Hall Clinic, Pune')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Remove document/ })).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/^File/)).not.toBeInTheDocument();
  });
});
