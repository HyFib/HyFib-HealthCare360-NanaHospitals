import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { ArrowUploadRegular, DismissRegular, DocumentRegular } from '@fluentui/react-icons';
import { useNotify } from '@hc360/ui';
import { useEffect, useRef, useState } from 'react';
import { api } from '@/lib/api';
import { usePermission } from '@/lib/permissions';
import { uploadInsuranceDocument } from './api';

const useStyles = makeStyles({
  list: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'center' },
  viewer: { width: '100%', height: '70vh', border: 'none', backgroundColor: tokens.colorNeutralBackground3 },
  image: { maxWidth: '100%', maxHeight: '70vh', display: 'block', margin: '0 auto' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/**
 * Supporting documents of a pre-authorisation or claim (discharge summary, investigation reports, ID and policy
 * card): open them in place (decrypted in memory only) and, when editing, upload more (PDF or images, 25 MB).
 */
export function Attachments({
  fileIds,
  patientId,
  onChange,
  disabled,
}: {
  fileIds: string[];
  patientId: string;
  onChange?: (ids: string[]) => void;
  disabled?: boolean;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const canUpload = usePermission('files.write');
  const inputRef = useRef<HTMLInputElement>(null);
  const [uploading, setUploading] = useState(false);
  const [viewer, setViewer] = useState<{ url: string; type: string; index: number } | null>(null);

  // Release the decrypted document from memory when the viewer closes or shows another file.
  useEffect(
    () => () => {
      if (viewer) {
        URL.revokeObjectURL(viewer.url);
      }
    },
    [viewer],
  );

  const open = async (id: string, index: number) => {
    try {
      const blob = await api.blob(`/api/v1/files/${id}`);
      setViewer({ url: URL.createObjectURL(blob), type: blob.type, index });
    } catch (error) {
      notify.error(error, 'Could not open the document');
    }
  };

  const upload = async (files: FileList | null) => {
    if (!files || files.length === 0 || !onChange) {
      return;
    }
    setUploading(true);
    try {
      const ids: string[] = [];
      for (const file of Array.from(files)) {
        if (file.size > 25 * 1024 * 1024) {
          notify.warning('File too large', `${file.name} is over 25 MB.`);
          continue;
        }
        ids.push(await uploadInsuranceDocument(file, patientId));
      }
      if (ids.length) {
        onChange([...fileIds, ...ids].slice(0, 50));
        notify.success(`${ids.length} document${ids.length === 1 ? '' : 's'} attached`);
      }
    } catch (error) {
      notify.error(error, 'Upload failed');
    } finally {
      setUploading(false);
      if (inputRef.current) {
        inputRef.current.value = '';
      }
    }
  };

  return (
    <div className={styles.list}>
      {fileIds.length === 0 && <Text className={styles.muted}>No documents attached.</Text>}
      {fileIds.map((id, index) => (
        <span key={id} style={{ display: 'inline-flex', alignItems: 'center' }}>
          <Button size="small" icon={<DocumentRegular />} onClick={() => void open(id, index)}>
            Document {index + 1}
          </Button>
          {onChange && !disabled && (
            <Button
              size="small"
              appearance="subtle"
              icon={<DismissRegular />}
              aria-label={`Remove document ${index + 1}`}
              onClick={() => onChange(fileIds.filter((f) => f !== id))}
            />
          )}
        </span>
      ))}
      {onChange && !disabled && canUpload && (
        <>
          <input
            ref={inputRef}
            type="file"
            accept="application/pdf,image/png,image/jpeg,image/webp"
            multiple
            hidden
            onChange={(event) => void upload(event.target.files)}
          />
          <Button
            size="small"
            icon={uploading ? <Spinner size="tiny" /> : <ArrowUploadRegular />}
            disabled={uploading}
            onClick={() => inputRef.current?.click()}
          >
            Attach documents
          </Button>
        </>
      )}
      {onChange && !disabled && !canUpload && (
        <Caption1 className={styles.muted}>Your role cannot upload files.</Caption1>
      )}
      <Dialog open={viewer !== null} onOpenChange={(_, d) => !d.open && setViewer(null)}>
        <DialogSurface style={{ maxWidth: '90vw', width: 1000 }}>
          <DialogBody>
            <DialogTitle>Document {viewer ? viewer.index + 1 : ''}</DialogTitle>
            <DialogContent>
              {viewer?.type.startsWith('image/') ? (
                <img src={viewer.url} alt={`Document ${viewer.index + 1}`} className={styles.image} />
              ) : (
                viewer && <iframe src={viewer.url} title={`Document ${viewer.index + 1}`} className={styles.viewer} />
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={() => setViewer(null)}>Close</Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </div>
  );
}
