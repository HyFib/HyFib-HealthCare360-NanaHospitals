import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Core from '@hc360/core';
import { saveFile } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import { AbdmRecordButton } from './AbdmRecordButton';

// The server, the switches, permissions, toasts and the browser's save dialog are outside the test.
const state = vi.hoisted(() => ({ switchedOn: true, canExport: true }));
vi.mock('@/lib/features', () => ({ useFeatureOn: (feature: string) => feature === 'abdm' && state.switchedOn }));
vi.mock('@/lib/permissions', () => ({
  usePermission: (permission: string) => permission === 'abdm.export' && state.canExport,
}));
vi.mock('@/lib/api', () => ({ api: { download: vi.fn() } }));
vi.mock('@hc360/core', async (importOriginal) => ({ ...(await importOriginal<typeof Core>()), saveFile: vi.fn() }));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const SUMMARY_ID = '0192a000-0000-7000-8000-00000000d501';

function renderButton() {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { mutations: { retry: false } } })}>
      <FluentProvider theme={webLightTheme}>
        <AbdmRecordButton summaryId={SUMMARY_ID} />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

describe('AbdmRecordButton', () => {
  beforeEach(() => {
    state.switchedOn = true;
    state.canExport = true;
    vi.mocked(api.download).mockReset();
    vi.mocked(saveFile).mockReset();
    Object.values(notify).forEach((fn) => fn.mockReset());
  });

  it('is not offered while ABDM health records are switched off', () => {
    state.switchedOn = false;
    renderButton();
    expect(screen.queryByRole('button', { name: /ABDM/ })).not.toBeInTheDocument();
  });

  it('is not offered to staff without the ABDM export permission', () => {
    state.canExport = false;
    renderButton();
    expect(screen.queryByRole('button', { name: /ABDM/ })).not.toBeInTheDocument();
  });

  it('downloads the summary as an ABDM record under the name the server gave it', async () => {
    const blob = new Blob(['{"resourceType":"Bundle","type":"document"}'], {
      type: 'application/fhir+json; charset=utf-8',
    });
    vi.mocked(api.download).mockResolvedValue({
      blob,
      fileName: 'discharge-summary-NH-000123-2026-09-25-abdm.json',
      headers: new Headers(),
    });
    renderButton();

    await userEvent.click(screen.getByRole('button', { name: 'Download ABDM record' }));

    await waitFor(() =>
      expect(saveFile).toHaveBeenCalledWith(blob, 'discharge-summary-NH-000123-2026-09-25-abdm.json'),
    );
    expect(api.download).toHaveBeenCalledWith(`/api/v1/abdm/bundles/discharge-summaries/${SUMMARY_ID}`);
    expect(notify.success).toHaveBeenCalledWith('ABDM record downloaded', 'It is in your Downloads folder.');
  });

  it('says why when the server refuses, for example a restricted record', async () => {
    const refusal = new Error('This is a restricted record.');
    vi.mocked(api.download).mockRejectedValue(refusal);
    renderButton();

    await userEvent.click(screen.getByRole('button', { name: 'Download ABDM record' }));

    await waitFor(() => expect(notify.error).toHaveBeenCalledWith(refusal, 'Could not download the ABDM record'));
    expect(saveFile).not.toHaveBeenCalled();
  });

  it('never saves a web page that answered instead of the server', async () => {
    vi.mocked(api.download).mockResolvedValue({
      blob: new Blob(['<html>Sign in to the WiFi</html>'], { type: 'text/html' }),
      fileName: 'login.html',
      headers: new Headers(),
    });
    renderButton();

    await userEvent.click(screen.getByRole('button', { name: 'Download ABDM record' }));

    await waitFor(() => expect(notify.error).toHaveBeenCalled());
    expect(saveFile).not.toHaveBeenCalled();
  });
});
