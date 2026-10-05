import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { ApiError } from '@hc360/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import type { ImageSeriesDetail, StudyImages as StudyImagesDto } from './api';
import { ImageViewerContent } from './ImageViewer';
import { StudyImages, StudyThumbnails } from './StudyImages';

vi.mock('@/lib/api', () => ({ api: { get: vi.fn(), blob: vi.fn() } }));

const STUDY = '0192c0de-0000-7000-8000-000000000001';

const images: StudyImagesDto = {
  studyId: STUDY,
  accessionNumber: 'NHRD260000042',
  studyInstanceUid: '2.25.1',
  series: [
    {
      seriesInstanceUid: '2.25.1.1',
      seriesNumber: 1,
      modality: 'CT',
      description: 'Scout',
      bodyPart: null,
      imageCount: 1,
    },
    {
      seriesInstanceUid: '2.25.1.2',
      seriesNumber: 2,
      modality: 'CT',
      description: 'Axial',
      bodyPart: null,
      imageCount: 3,
    },
    {
      seriesInstanceUid: '2.25.1.3',
      seriesNumber: 3,
      modality: 'US',
      description: 'Cine',
      bodyPart: null,
      imageCount: 1,
    },
  ],
};

const axial: ImageSeriesDetail = {
  studyId: STUDY,
  series: images.series[1]!,
  instances: [
    { sopInstanceUid: '2.25.1.2.1', instanceNumber: 1, frames: 1 },
    { sopInstanceUid: '2.25.1.2.2', instanceNumber: 2, frames: 1 },
    { sopInstanceUid: '2.25.1.2.3', instanceNumber: 3, frames: 1 },
  ],
};

const cine: ImageSeriesDetail = {
  studyId: STUDY,
  series: images.series[2]!,
  instances: [{ sopInstanceUid: '2.25.1.3.1', instanceNumber: 1, frames: 12 }],
};

const study = (linked: boolean) => ({
  id: STUDY,
  studyName: 'CT brain (plain)',
  accessionNumber: 'NHRD260000042',
  pacsLinkedAt: linked ? '2026-10-02T05:00:00Z' : null,
  status: 'Acquired' as const,
});

beforeAll(() => {
  // jsdom has neither object URLs nor ResizeObserver (Fluent's MessageBar measures itself).
  URL.createObjectURL = vi.fn(() => 'blob:image');
  URL.revokeObjectURL = vi.fn();
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  );
});

afterEach(() => {
  vi.mocked(api.get).mockReset();
  vi.mocked(api.blob).mockReset();
});

function wrap(children: ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    </FluentProvider>,
  );
}

function answer(paths: Record<string, unknown>) {
  vi.mocked(api.get).mockImplementation((path: string) => {
    const found = Object.entries(paths).find(([key]) => path.endsWith(key));
    return found
      ? found[1] instanceof Error
        ? Promise.reject(found[1])
        : Promise.resolve(found[1])
      : Promise.reject(new Error(`No answer for ${path}`));
  });
  vi.mocked(api.blob).mockResolvedValue(new Blob([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], { type: 'image/png' }));
}

describe('StudyImages', () => {
  it('says the images are not linked yet and lets radiology staff link them', async () => {
    const onLink = vi.fn();
    wrap(<StudyImages study={study(false)} canLink linking={false} onLink={onLink} onOpenPacsViewer={vi.fn()} />);

    expect(screen.getByText('The images are not linked yet')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Link images from PACS' }));
    expect(onLink).toHaveBeenCalledTimes(1);
    expect(api.get).not.toHaveBeenCalled();
  });

  it('offers no link button to people who may not link studies', () => {
    wrap(
      <StudyImages study={study(false)} canLink={false} linking={false} onLink={vi.fn()} onOpenPacsViewer={vi.fn()} />,
    );

    expect(screen.queryByRole('button', { name: 'Link images from PACS' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Open in PACS viewer' })).toBeInTheDocument();
  });

  it('shows each series with its thumbnail', async () => {
    answer({ '/images': images });
    wrap(<StudyImages study={study(true)} canLink linking={false} onLink={vi.fn()} onOpenPacsViewer={vi.fn()} />);

    expect(await screen.findByRole('button', { name: 'Open Series 1 · Scout · 1 image' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Open Series 2 · Axial · 3 images' })).toBeInTheDocument();
    await waitFor(() => expect(api.blob).toHaveBeenCalledTimes(3));
    expect(vi.mocked(api.blob).mock.calls[0]![0]).toBe(
      `/api/v1/radiology/studies/${STUDY}/images/series/2.25.1.1/thumbnail`,
    );
  });

  it('explains that PACS is not set up', async () => {
    answer({
      '/images': new ApiError({
        status: 422,
        title: 'Cannot complete this action',
        code: 'pacs.not_configured',
        detail: 'PACS is not configured.',
      }),
    });
    wrap(<StudyImages study={study(true)} canLink linking={false} onLink={vi.fn()} onOpenPacsViewer={vi.fn()} />);

    expect(await screen.findByText('PACS is not set up')).toBeInTheDocument();
  });
});

describe('StudyThumbnails', () => {
  it('shows a few thumbnails that open the study', async () => {
    answer({ '/images': images });
    const onOpen = vi.fn();
    wrap(<StudyThumbnails studyId={STUDY} onOpen={onOpen} />);

    await userEvent.click(await screen.findByRole('button', { name: 'Open Series 2 · Axial · 3 images' }));
    expect(onOpen).toHaveBeenCalledTimes(1);
  });

  it('says only a line when the study is not linked', async () => {
    answer({ '/images': new ApiError({ status: 422, title: 'Cannot complete this action', code: 'pacs.not_linked' }) });
    wrap(<StudyThumbnails studyId={STUDY} onOpen={vi.fn()} />);

    expect(await screen.findByText('The images are not in HC360 yet.')).toBeInTheDocument();
  });
});

describe('ImageViewerContent', () => {
  it('moves through the images with the keys and the buttons', async () => {
    answer({ '/series/2.25.1.2': axial });
    wrap(
      <ImageViewerContent studyId={STUDY} series={images.series} initialSeries="2.25.1.2" onOpenPacsViewer={vi.fn()} />,
    );

    expect(await screen.findByText('Image 1 of 3')).toBeInTheDocument();
    fireEvent.keyDown(window, { key: 'ArrowRight' });
    expect(await screen.findByText('Image 2 of 3')).toBeInTheDocument();
    fireEvent.keyDown(window, { key: 'End' });
    expect(await screen.findByText('Image 3 of 3')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Next image' })).toBeDisabled();
    await userEvent.click(screen.getByRole('button', { name: 'Previous image' }));
    expect(await screen.findByText('Image 2 of 3')).toBeInTheDocument();
    await waitFor(() =>
      expect(vi.mocked(api.blob).mock.calls.map((call) => call[0])).toContain(
        `/api/v1/radiology/studies/${STUDY}/images/series/2.25.1.2/instances/2.25.1.2.2/rendered`,
      ),
    );
    expect(await screen.findByRole('img', { name: 'Image 2 of 3, Series 2 · Axial · 3 images' })).toBeInTheDocument();
  });

  it('steps through the frames of a cine loop', async () => {
    answer({ '/series/2.25.1.3': cine });
    wrap(
      <ImageViewerContent studyId={STUDY} series={images.series} initialSeries="2.25.1.3" onOpenPacsViewer={vi.fn()} />,
    );

    expect(await screen.findByText('Frame 1 of 12')).toBeInTheDocument();
    fireEvent.keyDown(window, { key: 'PageDown' });
    expect(await screen.findByText('Frame 2 of 12')).toBeInTheDocument();
    await waitFor(() =>
      expect(
        vi.mocked(api.blob).mock.calls.some((call) => (call[1] as { query?: { frame?: number } })?.query?.frame === 2),
      ).toBe(true),
    );
  });

  it('zooms in and fits the picture back to the screen', async () => {
    answer({ '/series/2.25.1.2': axial });
    wrap(
      <ImageViewerContent studyId={STUDY} series={images.series} initialSeries="2.25.1.2" onOpenPacsViewer={vi.fn()} />,
    );

    await screen.findByText('Image 1 of 3');
    expect(screen.getByText('100%')).toBeInTheDocument();
    fireEvent.keyDown(window, { key: '+' });
    expect(await screen.findByText('125%')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Fit to the screen' }));
    expect(await screen.findByText('100%')).toBeInTheDocument();
  });
});
