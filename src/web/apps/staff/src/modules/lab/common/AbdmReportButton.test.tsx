import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Core from '@hc360/core';
import { saveFile } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import { abdmCardiologyReportBundle } from '@/modules/cardiology/api';
import { abdmLabReportBundle } from '@/modules/lab/api';
import { abdmRadiologyReportBundle } from '@/modules/radiology/api';
import { imagingReportExportable, labReportExportable } from './abdm';
import { AbdmReportButton } from './AbdmReportButton';

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

const REPORT_ID = '0192a000-0000-7000-8000-00000000e501';

function renderButton(path = abdmLabReportBundle(REPORT_ID)) {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { mutations: { retry: false } } })}>
      <FluentProvider theme={webLightTheme}>
        <AbdmReportButton path={path} what="lab report" />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

describe('which reports the server exports as ABDM records', () => {
  it('a lab report once released, an amended one too, never a draft or one waiting for release', () => {
    expect(labReportExportable('Released')).toBe(true);
    expect(labReportExportable('Amended')).toBe(true);
    expect(labReportExportable('Verified')).toBe(false);
    expect(labReportExportable('Draft')).toBe(false);
  });

  it('a radiology or cardiology report only once released', () => {
    expect(imagingReportExportable('Released')).toBe(true);
    expect(imagingReportExportable('Verified')).toBe(false);
    expect(imagingReportExportable('Reported')).toBe(false);
    expect(imagingReportExportable('Draft')).toBe(false);
    expect(imagingReportExportable(null)).toBe(false);
  });

  it('asks for each report by its own id', () => {
    expect(abdmLabReportBundle(REPORT_ID)).toBe(`/api/v1/abdm/bundles/lab-reports/${REPORT_ID}`);
    expect(abdmRadiologyReportBundle(REPORT_ID)).toBe(`/api/v1/abdm/bundles/radiology-reports/${REPORT_ID}`);
    expect(abdmCardiologyReportBundle(REPORT_ID)).toBe(`/api/v1/abdm/bundles/cardiology-reports/${REPORT_ID}`);
  });
});

describe('AbdmReportButton', () => {
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

  it('downloads the report as an ABDM record under the name the server gave it', async () => {
    const blob = new Blob(['{"resourceType":"Bundle","type":"document"}'], { type: 'application/fhir+json' });
    vi.mocked(api.download).mockResolvedValue({
      blob,
      fileName: 'lab-report-LR-0042-abdm.json',
      headers: new Headers(),
    });
    renderButton();

    await userEvent.click(screen.getByRole('button', { name: 'Download ABDM record' }));

    await waitFor(() => expect(saveFile).toHaveBeenCalledWith(blob, 'lab-report-LR-0042-abdm.json'));
    expect(api.download).toHaveBeenCalledWith(`/api/v1/abdm/bundles/lab-reports/${REPORT_ID}`);
    expect(notify.success).toHaveBeenCalledWith('ABDM record downloaded', 'It is in your Downloads folder.');
  });

  it('says why when the server refuses, for example a report not released yet', async () => {
    const refusal = new Error('Only a released radiology report can be exported.');
    vi.mocked(api.download).mockRejectedValue(refusal);
    renderButton(abdmRadiologyReportBundle(REPORT_ID));

    await userEvent.click(screen.getByRole('button', { name: 'Download ABDM record' }));

    await waitFor(() => expect(notify.error).toHaveBeenCalledWith(refusal, 'Could not download the ABDM record'));
    expect(saveFile).not.toHaveBeenCalled();
  });
});
