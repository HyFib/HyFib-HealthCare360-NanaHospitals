import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import { AbdmLinkButton } from './AbdmLinkButton';

// The server, the switches, permissions and toasts are outside the test.
const state = vi.hoisted(() => ({ switchedOn: true, canExport: true }));
vi.mock('@/lib/features', () => ({ useFeatureOn: (feature: string) => feature === 'abdm' && state.switchedOn }));
vi.mock('@/lib/permissions', () => ({
  usePermission: (permission: string) => permission === 'abdm.export' && state.canExport,
}));
vi.mock('@/lib/api', () => ({ api: { get: vi.fn(), post: vi.fn() } }));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const REPORT_ID = '0192a000-0000-7000-8000-00000000e901';

function renderButton(disabled = false) {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { mutations: { retry: false } } })}>
      <FluentProvider theme={webLightTheme}>
        <AbdmLinkButton
          record={{ type: 'RadiologyReport', id: REPORT_ID }}
          what="radiology report"
          disabled={disabled}
        />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

describe('AbdmLinkButton', () => {
  beforeEach(() => {
    state.switchedOn = true;
    state.canExport = true;
    vi.mocked(api.get).mockReset();
    vi.mocked(api.post).mockReset();
    Object.values(notify).forEach((fn) => fn.mockReset());
  });

  it('is not offered while ABDM health records are switched off', () => {
    state.switchedOn = false;
    renderButton();
    expect(screen.queryByRole('button', { name: /ABHA/ })).not.toBeInTheDocument();
  });

  it('is not offered to staff without the ABDM export permission', () => {
    state.canExport = false;
    renderButton();
    expect(screen.queryByRole('button', { name: /ABHA/ })).not.toBeInTheDocument();
  });

  it('cannot be used while the record has unsaved changes', () => {
    renderButton(true);
    expect(screen.getByRole('button', { name: "Link to patient's ABHA" })).toBeDisabled();
  });

  it("asks first, then links the report to the patient's ABHA", async () => {
    vi.mocked(api.post).mockResolvedValue({
      id: '0192a000-0000-7000-8000-00000000ea01',
      patientId: '0192a000-0000-7000-8000-00000000eb01',
      abhaAddress: 'meera.iyer@sbx',
      status: 'Failed',
      records: [],
      createdAt: '2026-10-28T10:00:00Z',
      sentAt: '2026-10-28T10:00:00Z',
      answeredAt: '2026-10-28T10:00:01Z',
      errorCode: 'abdm.unreachable',
      error: 'The ABDM gateway could not be reached.',
    });
    renderButton();

    await userEvent.click(screen.getByRole('button', { name: "Link to patient's ABHA" }));
    const dialog = await screen.findByRole('alertdialog', { name: "Link to the patient's ABHA?" });
    expect(dialog).toHaveTextContent('The radiology report goes to the Ayushman Bharat Digital Mission (ABDM)');
    expect(api.post).not.toHaveBeenCalled();

    await userEvent.click(screen.getByRole('button', { name: 'Link' }));

    await waitFor(() =>
      expect(notify.warning).toHaveBeenCalledWith(
        'ABDM did not link the record',
        'The ABDM gateway could not be reached.',
      ),
    );
    expect(api.post).toHaveBeenCalledWith('/api/v1/abdm/links', {
      records: [{ type: 'RadiologyReport', id: REPORT_ID }],
    });
    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
  });

  it('sends nothing when the user cancels', async () => {
    renderButton();

    await userEvent.click(screen.getByRole('button', { name: "Link to patient's ABHA" }));
    await userEvent.click(await screen.findByRole('button', { name: 'Cancel' }));

    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
    expect(api.post).not.toHaveBeenCalled();
  });
});
