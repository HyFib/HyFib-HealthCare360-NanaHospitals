import { Avatar, Button, Caption1, makeStyles, Spinner, tokens } from '@fluentui/react-components';
import { ArrowUploadRegular, CameraRegular, DeleteRegular } from '@fluentui/react-icons';
import { isApiError } from '@hc360/core';
import { useRef, useState, type ChangeEvent } from 'react';
import { PhotoCaptureDialog } from './PhotoCaptureDialog';
import { PHOTO_ACCEPT, photoSourceProblem, preparePhoto, uploadPhoto } from './photo';
import { usePatientPhotoUrl } from './usePatientPhotoUrl';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '12px' },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap', justifyContent: 'center' },
  hint: { color: tokens.colorNeutralForeground3, textAlign: 'center' },
  error: { color: tokens.colorPaletteRedForeground1, textAlign: 'center' },
});

export interface PatientPhotoFieldProps {
  /** The stored photo's file id, or null. */
  value: string | null;
  onChange: (fileId: string | null) => void;
  /** The patient's name as typed so far, for the initials shown while there is no photo. */
  name: string;
  /** A server message about the photo (for example that it belongs to another patient). */
  error?: string;
  disabled?: boolean;
}

/**
 * The patient's photo on the registration and edit forms (SRS FO-01): take one with the camera, upload a file, or remove
 * it. A new photo is uploaded at once as a small JPEG; the patient record only refers to it after the form is saved.
 */
export function PatientPhotoField({ value, onChange, name, error, disabled }: PatientPhotoFieldProps) {
  const styles = useStyles();
  const photoUrl = usePatientPhotoUrl(value);
  const fileInput = useRef<HTMLInputElement>(null);
  const [cameraOpen, setCameraOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);

  const store = async (source: Blob | ImageBitmap) => {
    setBusy(true);
    setProblem(null);
    try {
      onChange(await uploadPhoto(await preparePhoto(source)));
    } catch (failure) {
      setProblem(isApiError(failure) ? failure.userMessage : 'The photo could not be used. Try another photo.');
    } finally {
      setBusy(false);
    }
  };

  const chosen = (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    // Clearing the input lets the same file be chosen again.
    event.target.value = '';
    if (!file) {
      return;
    }
    const issue = photoSourceProblem(file);
    if (issue) {
      setProblem(issue);
      return;
    }
    void store(file);
  };

  const shown = problem ?? error ?? null;
  const locked = busy || disabled;

  return (
    <div className={styles.root} role="group" aria-label="Patient photo">
      <Avatar
        size={96}
        color="colorful"
        name={name.trim() || undefined}
        image={photoUrl ? { src: photoUrl, alt: '' } : undefined}
        // Says whether there is a photo; Fluent's own label is the name, and there is none before one is typed.
        aria-label={photoUrl ? 'Photo' : 'No photo yet'}
      />
      <div className={styles.actions}>
        <Button icon={<CameraRegular />} disabled={locked} onClick={() => setCameraOpen(true)}>
          Take photo
        </Button>
        <Button icon={<ArrowUploadRegular />} disabled={locked} onClick={() => fileInput.current?.click()}>
          Upload
        </Button>
        {value && (
          <Button appearance="subtle" icon={<DeleteRegular />} disabled={locked} onClick={() => onChange(null)}>
            Remove
          </Button>
        )}
      </div>
      {busy && <Spinner size="tiny" label="Saving the photo…" labelPosition="after" />}
      {shown ? (
        <Caption1 className={styles.error} role="alert">
          {shown}
        </Caption1>
      ) : (
        <Caption1 className={styles.hint}>
          Optional. Helps staff recognise the patient. It is saved when you save the patient.
        </Caption1>
      )}
      <input
        ref={fileInput}
        type="file"
        accept={PHOTO_ACCEPT}
        hidden
        tabIndex={-1}
        aria-label="Choose a photo file"
        onChange={chosen}
      />
      <PhotoCaptureDialog
        open={cameraOpen}
        onClose={() => setCameraOpen(false)}
        onCaptured={(frame) => void store(frame)}
      />
    </div>
  );
}
