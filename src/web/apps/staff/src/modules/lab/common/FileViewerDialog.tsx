import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  Text,
  tokens,
} from '@fluentui/react-components';
import { ArrowDownloadRegular } from '@fluentui/react-icons';
import { saveFile } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useEffect, useState, type ReactNode } from 'react';
import { openFile } from './files';

/** Files the browser shows: images, and PDFs in its own viewer. */
function canShow(contentType: string) {
  return contentType.startsWith('image/') || contentType === 'application/pdf';
}

const useStyles = makeStyles({
  frame: { width: '100%', height: '70vh', border: 'none', backgroundColor: tokens.colorNeutralBackground3 },
  image: { maxWidth: '100%', maxHeight: '70vh', display: 'block', margin: '0 auto' },
});

interface ViewedFile {
  url: string;
  contentType: string;
  blob: Blob;
  title: string;
  fileName: string;
}

/**
 * Shows a decrypted document (tracing, external report) inside the app. A file the browser cannot show, such as a DICOM
 * or SCP-ECG file from a machine, is offered for saving instead. The object URL is released as soon as the viewer
 * closes, so no PHI stays in memory, and nothing reaches the disk unless the user saves it.
 */
export function useFileViewer(): {
  open: (fileId: string, title: string, fileName?: string) => Promise<void>;
  opening: boolean;
  dialog: ReactNode;
} {
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

  const open = async (fileId: string, title: string, fileName?: string) => {
    setOpening(true);
    try {
      const opened = await openFile(fileId);
      setFile({ ...opened, title, fileName: fileName ?? title });
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
            {file &&
              (file.contentType.startsWith('image/') ? (
                <img src={file.url} alt={file.title} className={styles.image} />
              ) : file.contentType === 'application/pdf' ? (
                <iframe src={file.url} title={file.title} className={styles.frame} />
              ) : (
                <Text as="p">
                  HC360 cannot show this file. Save it, then open it in a program that reads the machine’s files, such
                  as an ECG or DICOM viewer.
                </Text>
              ))}
          </DialogContent>
          <DialogActions>
            {file && !canShow(file.contentType) && (
              <Button
                appearance="primary"
                icon={<ArrowDownloadRegular />}
                onClick={() => saveFile(file.blob, file.fileName)}
              >
                Save file
              </Button>
            )}
            <Button onClick={() => setFile(null)}>Close</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );

  return { open, opening, dialog };
}
