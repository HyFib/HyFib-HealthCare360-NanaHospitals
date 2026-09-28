import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, makeStyles, tokens } from '@fluentui/react-components';
import { useNotify } from '@hc360/ui';
import { useEffect, useState, type ReactNode } from 'react';
import { openFile } from './files';

const useStyles = makeStyles({
  frame: { width: '100%', height: '70vh', border: 'none', backgroundColor: tokens.colorNeutralBackground3 },
  image: { maxWidth: '100%', maxHeight: '70vh', display: 'block', margin: '0 auto' },
});

interface ViewedFile {
  url: string;
  contentType: string;
  title: string;
}

/**
 * Shows a decrypted document (tracing, external report) inside the app. The object URL is released as soon as the
 * viewer closes, so no PHI stays in memory or on disk.
 */
export function useFileViewer(): { open: (fileId: string, title: string) => Promise<void>; opening: boolean; dialog: ReactNode } {
  const styles = useStyles();
  const notify = useNotify();
  const [file, setFile] = useState<ViewedFile | null>(null);
  const [opening, setOpening] = useState(false);

  useEffect(
    () => () => {
      if (file) {
        URL.revokeObjectURL(file.url);
      }
    },
    [file],
  );

  const open = async (fileId: string, title: string) => {
    setOpening(true);
    try {
      const opened = await openFile(fileId);
      setFile({ ...opened, title });
    } catch (error) {
      notify.error(error, 'Could not open the file');
    } finally {
      setOpening(false);
    }
  };

  const dialog = (
    <Dialog open={file !== null} onOpenChange={(_, data) => !data.open && setFile(null)}>
      <DialogSurface style={{ maxWidth: '92vw', width: 1000 }}>
        <DialogBody>
          <DialogTitle>{file?.title}</DialogTitle>
          <DialogContent>
            {file?.contentType.startsWith('image/') ? (
              <img src={file.url} alt={file.title} className={styles.image} />
            ) : (
              file && <iframe src={file.url} title={file.title} className={styles.frame} />
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={() => setFile(null)}>Close</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );

  return { open, opening, dialog };
}
