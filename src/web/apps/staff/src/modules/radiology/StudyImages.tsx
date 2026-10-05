import { Button, Caption1, makeStyles, mergeClasses, Spinner, tokens } from '@fluentui/react-components';
import { ImageMultipleRegular, ImageOffRegular, LinkRegular, OpenRegular } from '@fluentui/react-icons';
import { isApiError } from '@hc360/core';
import { EmptyState, ErrorState, LoadingState, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { radiologyQueries, type ImageSeries, type ImagingStudyDetail } from './api';
import { BlobImage } from './BlobImage';
import { ImageViewer } from './ImageViewer';
import { seriesLabel } from './images';

const useStyles = makeStyles({
  grid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(168px, 1fr))', gap: '12px' },
  strip: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'flex-start' },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    padding: '6px',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorNeutralForeground1,
    cursor: 'pointer',
    textAlign: 'left',
    font: 'inherit',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '2px' },
  },
  small: { width: '104px' },
  frame: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    aspectRatio: '1',
    backgroundColor: tokens.colorNeutralBackgroundInverted,
    color: tokens.colorNeutralForegroundInverted2,
    borderRadius: tokens.borderRadiusSmall,
    overflow: 'hidden',
    fontSize: '28px',
  },
  thumb: { width: '100%', height: '100%', objectFit: 'contain' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** One series as a card: its thumbnail and what it is. */
export function SeriesThumbnail({
  studyId,
  series,
  onOpen,
  small = false,
}: {
  studyId: string;
  series: ImageSeries;
  onOpen: () => void;
  small?: boolean;
}) {
  const styles = useStyles();
  const thumbnail = useQuery(radiologyQueries.thumbnail(studyId, series.seriesInstanceUid));
  const label = seriesLabel(series);
  return (
    <button
      type="button"
      className={mergeClasses(styles.card, small && styles.small)}
      onClick={onOpen}
      aria-label={`Open ${label}`}
      title={label}
    >
      <span className={styles.frame}>
        {thumbnail.isPending ? (
          <Spinner size="tiny" aria-label="Loading the picture" />
        ) : thumbnail.data ? (
          <BlobImage blob={thumbnail.data} alt="" className={styles.thumb} />
        ) : (
          <ImageOffRegular aria-label="No picture" />
        )}
      </span>
      {!small && <Caption1>{label}</Caption1>}
    </button>
  );
}

const NOT_LINKED = 'HC360 links a study with its images a few minutes after they reach PACS.';

/**
 * The study's images from PACS on the study page (IMG-02): its series with thumbnails, the image viewer, and the PACS
 * viewer. Shown to people allowed to see images (`radiology.images.view`).
 */
export function StudyImages({
  study,
  canLink,
  linking,
  onLink,
  onOpenPacsViewer,
}: {
  study: Pick<ImagingStudyDetail, 'id' | 'studyName' | 'accessionNumber' | 'pacsLinkedAt' | 'status'>;
  /** The person may link the study with PACS now (radiology worklist permission). */
  canLink: boolean;
  linking: boolean;
  onLink: () => void;
  onOpenPacsViewer: () => void;
}) {
  const styles = useStyles();
  const linked = study.pacsLinkedAt !== null;
  const images = useQuery({ ...radiologyQueries.images(study.id), enabled: linked });
  const [viewing, setViewing] = useState<string | null>(null);

  let content;
  if (!linked) {
    content = (
      <EmptyState
        icon={<ImageMultipleRegular />}
        title="The images are not linked yet"
        description={study.status === 'Cancelled' ? 'The study was cancelled.' : NOT_LINKED}
        action={
          canLink && study.status !== 'Cancelled' ? (
            <Button icon={<LinkRegular />} disabled={linking} onClick={onLink}>
              Link images from PACS
            </Button>
          ) : undefined
        }
      />
    );
  } else if (images.isPending) {
    content = <LoadingState label="Finding the images in PACS…" />;
  } else if (images.error) {
    content =
      isApiError(images.error) && images.error.code === 'pacs.not_configured' ? (
        <EmptyState
          title="PACS is not set up"
          description="Images appear here once an administrator sets up the PACS integration."
        />
      ) : (
        <ErrorState error={images.error} onRetry={() => void images.refetch()} title="The images could not be loaded" />
      );
  } else if (images.data.series.length === 0) {
    content = (
      <EmptyState
        icon={<ImageMultipleRegular />}
        title="No pictures in PACS"
        description="The study is linked, but PACS lists no pictures for it yet."
      />
    );
  } else {
    content = (
      <div className={styles.grid}>
        {images.data.series.map((series) => (
          <SeriesThumbnail
            key={series.seriesInstanceUid}
            studyId={study.id}
            series={series}
            onOpen={() => setViewing(series.seriesInstanceUid)}
          />
        ))}
      </div>
    );
  }

  return (
    <Section
      title="Images"
      description={linked && images.data ? 'Choose a series to view its images.' : undefined}
      actions={
        <Button icon={<OpenRegular />} onClick={onOpenPacsViewer}>
          Open in PACS viewer
        </Button>
      }
    >
      {content}
      {viewing && images.data && (
        <ImageViewer
          studyId={study.id}
          title={`${study.studyName} · ${study.accessionNumber}`}
          series={images.data.series}
          initialSeries={viewing}
          onClose={() => setViewing(null)}
          onOpenPacsViewer={onOpenPacsViewer}
        />
      )}
    </Section>
  );
}

/**
 * A few thumbnails of a study's images for the doctor's result review; choosing one opens the study. Says nothing more
 * than a line when the images cannot be shown.
 */
export function StudyThumbnails({ studyId, onOpen }: { studyId: string; onOpen: () => void }) {
  const styles = useStyles();
  const images = useQuery(radiologyQueries.images(studyId));
  if (images.isPending) {
    return <Spinner size="tiny" label="Finding the images…" labelPosition="after" />;
  }
  if (images.error || images.data.series.length === 0) {
    const notThere =
      isApiError(images.error) &&
      (images.error.code === 'pacs.not_linked' || images.error.code === 'pacs.not_configured');
    return (
      <Caption1 className={styles.muted}>
        {images.error && !notThere ? 'The images could not be loaded here.' : 'The images are not in HC360 yet.'}
      </Caption1>
    );
  }
  return (
    <div className={styles.strip} aria-label="Images">
      {images.data.series.slice(0, 4).map((series) => (
        <SeriesThumbnail key={series.seriesInstanceUid} studyId={studyId} series={series} onOpen={onOpen} small />
      ))}
      {images.data.series.length > 4 && (
        <Caption1 className={styles.muted}>+{images.data.series.length - 4} more</Caption1>
      )}
    </div>
  );
}
