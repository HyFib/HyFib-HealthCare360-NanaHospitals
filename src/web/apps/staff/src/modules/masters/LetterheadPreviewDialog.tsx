import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { ErrorState, LoadingState } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { mastersApi, type LetterheadPreviewRequest } from './api';

const useStyles = makeStyles({
  surface: { maxWidth: 'min(900px, 96vw)', width: '900px' },
  frame: {
    width: '100%',
    height: 'min(70vh, 720px)',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    // The page itself is white, as printed.
    backgroundColor: '#ffffff',
  },
});

/**
 * The letterhead as documents will print it, from the details being edited (not yet saved). The server renders the real
 * report letterhead; the page is shown in a sandbox that runs no scripts.
 */
export function LetterheadPreviewDialog({
  request,
  onClose,
}: {
  request: LetterheadPreviewRequest;
  onClose: () => void;
}) {
  const styles = useStyles();
  const preview = useQuery({
    queryKey: ['masters', 'letterhead-preview', request],
    queryFn: () => mastersApi.previewLetterhead(request),
    staleTime: Infinity,
    gcTime: 0,
  });

  return (
    <Dialog open onOpenChange={(_, data) => !data.open && onClose()}>
      <DialogSurface className={styles.surface}>
        <DialogBody>
          <DialogTitle>Letterhead preview</DialogTitle>
          <DialogContent>
            {preview.data ? (
              <iframe className={styles.frame} title="Letterhead preview" sandbox="" srcDoc={preview.data.html} />
            ) : preview.error ? (
              <ErrorState
                error={preview.error}
                onRetry={() => void preview.refetch()}
                title="The preview could not be made"
              />
            ) : (
              <LoadingState label="Preparing the preview…" />
            )}
          </DialogContent>
          <DialogActions>
            <Button appearance="primary" onClick={onClose}>
              Close
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
