import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { ApiError } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as CardiologyApi from './api';
import type { CardiologyStudyDetail, DeviceFileDto } from './api';
import { PacsButtons } from './PacsButtons';

// The server, the toasts and the browser window are outside the test; what the buttons show and do is what is tested.
const sendToPacs = vi.hoisted(() => vi.fn());
const viewer = vi.hoisted(() => vi.fn());
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof CardiologyApi>();
  return { ...actual, cardiologyApi: { ...actual.cardiologyApi, sendToPacs, viewer } };
});
const openViewerUrl = vi.hoisted(() => vi.fn());
vi.mock('@/modules/radiology/viewer', () => ({ openViewerUrl }));
const notify = vi.hoisted(() => ({ success: vi.fn(), warning: vi.fn(), error: vi.fn(), info: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const loop = (id: string, pacs: DeviceFileDto['pacs'] = null): DeviceFileDto => ({
  id,
  fileName: `${id}.dcm`,
  contentType: 'application/dicom',
  sizeBytes: 4096,
  pacs,
});

const storedAt = { status: 'Stored', message: null, sentAt: '2026-10-08T04:30:00Z' } as const;

function study(files: DeviceFileDto[], status: CardiologyStudyDetail['status'] = 'Acquired'): CardiologyStudyDetail {
  return { id: 'st1', status, files } as CardiologyStudyDetail;
}

function renderButtons(detail: CardiologyStudyDetail, canSend = true) {
  render(
    <QueryClientProvider client={new QueryClient()}>
      <FluentProvider theme={webLightTheme}>
        <PacsButtons study={detail} canSend={canSend} />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

describe('PacsButtons', () => {
  beforeEach(() => {
    sendToPacs.mockReset();
    viewer.mockReset();
    openViewerUrl.mockReset();
    Object.values(notify).forEach((spy) => spy.mockClear());
  });

  it('offers sending until the PACS holds every DICOM file, and the viewer once it holds one', () => {
    renderButtons(study([loop('a', storedAt), loop('b')]));

    expect(screen.getByRole('button', { name: 'Send to PACS' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Open in PACS viewer' })).toBeEnabled();
  });

  it('offers neither without DICOM files, nor sending to someone who may not send', () => {
    renderButtons(study([{ ...loop('pdf'), contentType: 'application/pdf' }]));
    expect(screen.queryByRole('button', { name: /PACS/ })).not.toBeInTheDocument();
  });

  it('leaves sending to the technician', () => {
    renderButtons(study([loop('a')]), false);
    expect(screen.queryByRole('button', { name: 'Send to PACS' })).not.toBeInTheDocument();
  });

  it('says when the PACS holds every file after sending', async () => {
    sendToPacs.mockResolvedValue(study([loop('a', storedAt), loop('b', storedAt)]));
    renderButtons(study([loop('a'), loop('b')]));

    await userEvent.click(screen.getByRole('button', { name: 'Send to PACS' }));

    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith(
        'Sent to the PACS',
        'The PACS holds all 2 DICOM files. Open them in its viewer.',
      ),
    );
    expect(sendToPacs).toHaveBeenCalledWith('st1');
  });

  it('warns when some files did not reach the PACS', async () => {
    sendToPacs.mockResolvedValue(
      study([
        loop('a', storedAt),
        loop('b', { status: 'Failed', message: 'The PACS returned HTTP 503.', sentAt: storedAt.sentAt }),
      ]),
    );
    renderButtons(study([loop('a'), loop('b')]));

    await userEvent.click(screen.getByRole('button', { name: 'Send to PACS' }));

    await waitFor(() =>
      expect(notify.warning).toHaveBeenCalledWith(
        '1 of 2 DICOM files are not in the PACS',
        'The reason is under each file. Send them again once it is put right.',
      ),
    );
  });

  it('opens the viewer link the server gives, and explains a viewer that is not set up', async () => {
    viewer.mockResolvedValueOnce({
      studyId: 'st1',
      url: 'https://pacs.local/viewer?study=2.25.1',
      studyInstanceUid: '2.25.1',
    });
    renderButtons(study([loop('a', storedAt)]));

    await userEvent.click(screen.getByRole('button', { name: 'Open in PACS viewer' }));
    await waitFor(() => expect(openViewerUrl).toHaveBeenCalledWith('https://pacs.local/viewer?study=2.25.1'));

    viewer.mockRejectedValueOnce(
      new ApiError({
        status: 422,
        code: 'pacs.viewer_not_configured',
        detail: 'No image viewer is configured for the PACS integration.',
      }),
    );
    await userEvent.click(screen.getByRole('button', { name: 'Open in PACS viewer' }));
    await waitFor(() =>
      expect(notify.info).toHaveBeenCalledWith(
        'Images are not available',
        'No image viewer is configured for the PACS integration.',
      ),
    );
  });
});
