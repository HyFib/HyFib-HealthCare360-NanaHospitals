import {
  Button,
  Caption1,
  Dialog,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  mergeClasses,
  Select,
  Spinner,
  Text,
  Toolbar,
  ToolbarButton,
  ToolbarDivider,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowLeftRegular,
  ArrowRightRegular,
  DismissRegular,
  OpenRegular,
  ZoomFitRegular,
  ZoomInRegular,
  ZoomOutRegular,
} from '@fluentui/react-icons';
import { ErrorState } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useState, type Dispatch, type SetStateAction } from 'react';
import { radiologyQueries, type ImageSeries } from './api';
import { BlobImage } from './BlobImage';
import {
  framePosition,
  framesOf,
  imageSize,
  MAX_ZOOM,
  MIN_ZOOM,
  seriesLabel,
  stepIndex,
  viewerAction,
  zoomIn,
  zoomOut,
  type ViewerAction,
} from './images';

const useStyles = makeStyles({
  surface: { width: '96vw', maxWidth: '96vw', height: '92vh', maxHeight: '92vh', display: 'flex' },
  body: { display: 'flex', flexDirection: 'column', gap: '8px', flex: 1, minHeight: 0, width: '100%' },
  content: { display: 'flex', flexDirection: 'column', gap: '8px', flex: 1, minHeight: 0, overflow: 'hidden' },
  bar: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  position: { minWidth: '9em', textAlign: 'center' },
  stage: {
    flex: 1,
    minHeight: '200px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: '#000',
    borderRadius: tokens.borderRadiusMedium,
    overflow: 'hidden',
  },
  scroll: { overflow: 'auto', display: 'block' },
  fitted: { maxWidth: '100%', maxHeight: '100%', objectFit: 'contain' },
  zoomed: { display: 'block', height: 'auto', maxWidth: 'none' },
  light: { color: '#fff' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** Applies one of the viewer's actions to the picture shown and the zoom. */
function perform(
  action: ViewerAction,
  count: number,
  setIndex: Dispatch<SetStateAction<number>>,
  setZoom: Dispatch<SetStateAction<number>>,
) {
  switch (action) {
    case 'next':
      setIndex((i) => stepIndex(i, count, 1));
      break;
    case 'previous':
      setIndex((i) => stepIndex(i, count, -1));
      break;
    case 'first':
      setIndex(0);
      break;
    case 'last':
      setIndex(Math.max(0, count - 1));
      break;
    case 'zoomIn':
      setZoom(zoomIn);
      break;
    case 'zoomOut':
      setZoom(zoomOut);
      break;
    case 'fit':
      setZoom(MIN_ZOOM);
      break;
  }
}

export interface ImageViewerProps {
  studyId: string;
  title: string;
  series: ImageSeries[];
  initialSeries: string;
  onClose: () => void;
  onOpenPacsViewer: () => void;
}

/** The image viewer over the study page: one series at a time, picture by picture, fitted or zoomed. */
export function ImageViewer(props: ImageViewerProps) {
  const styles = useStyles();
  return (
    <Dialog open onOpenChange={(_, data) => !data.open && props.onClose()}>
      <DialogSurface className={styles.surface} aria-label={`Images of ${props.title}`}>
        <DialogBody className={styles.body}>
          <DialogTitle
            action={
              <Button
                appearance="subtle"
                aria-label="Close the viewer"
                icon={<DismissRegular />}
                onClick={props.onClose}
              />
            }
          >
            {props.title}
          </DialogTitle>
          <DialogContent className={styles.content}>
            <ImageViewerContent {...props} />
          </DialogContent>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

/**
 * The viewer itself (rendered on its own in tests). While it is open, arrow keys and Page Up/Down move through the
 * pictures (each frame of a cine loop is a picture), Home and End jump to the first and last, + and − zoom, and 0 fits
 * the picture to the screen; Esc closes the dialog. The next picture is fetched ahead, so moving through a series stays
 * smooth on WiFi.
 */
export function ImageViewerContent({
  studyId,
  series,
  initialSeries,
  onOpenPacsViewer,
}: Omit<ImageViewerProps, 'title' | 'onClose'>) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const [seriesUid, setSeriesUid] = useState(initialSeries);
  const [index, setIndex] = useState(0);
  const [zoom, setZoom] = useState(MIN_ZOOM);
  const detail = useQuery(radiologyQueries.seriesImages(studyId, seriesUid));
  const frames = useMemo(() => framesOf(detail.data?.instances ?? []), [detail.data]);
  const current = frames[index];
  const size = imageSize(window.innerWidth, window.innerHeight, window.devicePixelRatio, zoom);
  const image = useQuery({
    ...radiologyQueries.image(studyId, seriesUid, current?.instanceUid ?? '', current?.frame ?? null, size),
    enabled: current !== undefined,
  });
  const next = frames[index + 1];
  const count = frames.length;

  useEffect(() => {
    if (next) {
      void queryClient.prefetchQuery(radiologyQueries.image(studyId, seriesUid, next.instanceUid, next.frame, size));
    }
  }, [queryClient, studyId, seriesUid, next, size]);

  const act = (action: ViewerAction) => perform(action, count, setIndex, setZoom);

  // The viewer is modal: while it is open, its keys work wherever the focus is, except in the series list.
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.target instanceof HTMLElement && event.target.closest('select, input, textarea')) {
        return;
      }
      const action = viewerAction(event.key);
      if (action) {
        event.preventDefault();
        perform(action, count, setIndex, setZoom);
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [count]);

  const showSeries = (uid: string) => {
    setSeriesUid(uid);
    setIndex(0);
    setZoom(MIN_ZOOM);
  };

  const position = framePosition(frames, index);
  const shown = series.find((item) => item.seriesInstanceUid === seriesUid);
  let stage;
  if (detail.isPending || (current && image.isPending)) {
    stage = <Spinner className={styles.light} label="Loading the image…" />;
  } else if (detail.error || image.error) {
    stage = (
      <ErrorState
        error={detail.error ?? image.error}
        title="The image could not be loaded"
        onRetry={() => void (detail.error ? detail.refetch() : image.refetch())}
      />
    );
  } else if (!current) {
    stage = <Text className={styles.light}>This series has no pictures to show.</Text>;
  } else {
    stage = (
      <BlobImage
        blob={image.data}
        alt={shown ? `${position}, ${seriesLabel(shown)}` : position}
        className={zoom > MIN_ZOOM ? styles.zoomed : styles.fitted}
        style={zoom > MIN_ZOOM ? { width: `${zoom * 100}%` } : undefined}
      />
    );
  }

  return (
    <>
      <div className={styles.bar}>
        <Select aria-label="Series" value={seriesUid} onChange={(_, data) => showSeries(data.value)}>
          {series.map((item) => (
            <option key={item.seriesInstanceUid} value={item.seriesInstanceUid}>
              {seriesLabel(item)}
            </option>
          ))}
        </Select>
        <Toolbar aria-label="Image tools">
          <ToolbarButton
            aria-label="Previous image"
            icon={<ArrowLeftRegular />}
            disabled={index === 0}
            onClick={() => act('previous')}
          />
          <Text className={styles.position} aria-live="polite">
            {position}
          </Text>
          <ToolbarButton
            aria-label="Next image"
            icon={<ArrowRightRegular />}
            disabled={index >= count - 1}
            onClick={() => act('next')}
          />
          <ToolbarDivider />
          <ToolbarButton
            aria-label="Zoom out"
            icon={<ZoomOutRegular />}
            disabled={zoom <= MIN_ZOOM}
            onClick={() => act('zoomOut')}
          />
          <ToolbarButton
            aria-label="Fit to the screen"
            icon={<ZoomFitRegular />}
            disabled={zoom === MIN_ZOOM}
            onClick={() => act('fit')}
          />
          <ToolbarButton
            aria-label="Zoom in"
            icon={<ZoomInRegular />}
            disabled={zoom >= MAX_ZOOM}
            onClick={() => act('zoomIn')}
          />
          <Text className={styles.muted}>{Math.round(zoom * 100)}%</Text>
          <ToolbarDivider />
          <ToolbarButton icon={<OpenRegular />} onClick={onOpenPacsViewer}>
            Open in PACS viewer
          </ToolbarButton>
        </Toolbar>
      </div>
      <div className={mergeClasses(styles.stage, zoom > MIN_ZOOM && styles.scroll)}>{stage}</div>
      <Caption1 className={styles.muted}>
        Arrow keys or Page Up and Page Down: previous and next image. Home and End: first and last. + and −: zoom. 0:
        fit to the screen. Esc: close. For measurements and windowing, use the PACS viewer.
      </Caption1>
    </>
  );
}
