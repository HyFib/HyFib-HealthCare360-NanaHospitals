import { Button, Caption1, Label, makeStyles, mergeClasses, Spinner, tokens } from '@fluentui/react-components';
import { ArrowUploadRegular, DeleteRegular } from '@fluentui/react-icons';
import { isApiError } from '@hc360/core';
import { useQuery } from '@tanstack/react-query';
import { useId, useRef, useState, type ChangeEvent } from 'react';
import { Controller, type Control, type FieldPath, type FieldValues } from 'react-hook-form';
import {
  IMAGE_ACCEPT,
  IMAGE_SPECS,
  imageDataUrl,
  imageSourceProblem,
  prepareImage,
  uploadImage,
  type ImageKind,
} from './images';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '6px' },
  row: { display: 'flex', gap: '12px', alignItems: 'center', flexWrap: 'wrap' },
  frame: {
    width: '160px',
    height: '72px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    // White as on paper, so a transparent logo or signature shows as it will print.
    backgroundColor: '#ffffff',
    overflow: 'hidden',
  },
  photoFrame: { width: '96px', height: '96px', borderRadius: '50%' },
  image: { maxWidth: '100%', maxHeight: '100%', objectFit: 'contain' },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
  empty: { color: tokens.colorNeutralForeground3, textAlign: 'center', padding: '0 8px' },
  hint: { color: tokens.colorNeutralForeground3 },
  error: { color: tokens.colorPaletteRedForeground1 },
});

export interface ImagePickerProps {
  label: string;
  hint?: string;
  kind: ImageKind;
  /** The stored image's file id, or null. */
  value: string | null;
  onChange: (fileId: string | null) => void;
  /** A message from the form or the server about the image. */
  error?: string;
  disabled?: boolean;
}

/**
 * Upload, replace or remove a logo, photo or signature. The image is shrunk and stored at once; the record refers to it
 * only when the form is saved. It is shown on white, as it will print.
 */
export function ImagePicker({ label, hint, kind, value, onChange, error, disabled }: ImagePickerProps) {
  const styles = useStyles();
  const labelId = useId();
  const spec = IMAGE_SPECS[kind];
  const fileInput = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const preview = useQuery({
    queryKey: ['masters', 'image', value],
    queryFn: () => imageDataUrl(value ?? ''),
    enabled: value !== null,
    staleTime: Infinity,
  });

  const chosen = async (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    // Clearing the input lets the same file be chosen again.
    event.target.value = '';
    if (!file) {
      return;
    }
    const issue = imageSourceProblem(file);
    if (issue) {
      setProblem(issue);
      return;
    }
    setBusy(true);
    setProblem(null);
    try {
      onChange(await uploadImage(await prepareImage(file, kind), kind));
    } catch (failure) {
      setProblem(
        isApiError(failure)
          ? failure.userMessage
          : failure instanceof Error
            ? failure.message
            : `The ${spec.noun} could not be uploaded. Try again.`,
      );
    } finally {
      setBusy(false);
    }
  };

  const shown = problem ?? error ?? null;
  const locked = busy || disabled;

  return (
    <div className={styles.root} role="group" aria-labelledby={labelId}>
      <Label id={labelId}>{label}</Label>
      <div className={styles.row}>
        <div className={mergeClasses(styles.frame, kind === 'photo' && styles.photoFrame)}>
          {value === null ? (
            <Caption1 className={styles.empty}>No {spec.noun}</Caption1>
          ) : preview.data ? (
            <img src={preview.data} alt={`The ${spec.noun} as uploaded`} className={styles.image} />
          ) : preview.isLoading ? (
            <Spinner size="tiny" />
          ) : (
            <Caption1 className={styles.empty}>Can't show the {spec.noun}</Caption1>
          )}
        </div>
        <div className={styles.actions}>
          <Button icon={<ArrowUploadRegular />} disabled={locked} onClick={() => fileInput.current?.click()}>
            {value ? `Replace ${spec.noun}` : `Upload ${spec.noun}`}
          </Button>
          {value && (
            <Button appearance="subtle" icon={<DeleteRegular />} disabled={locked} onClick={() => onChange(null)}>
              Remove
            </Button>
          )}
        </div>
      </div>
      {busy && <Spinner size="tiny" label={`Uploading the ${spec.noun}…`} labelPosition="after" />}
      {shown ? (
        <Caption1 className={styles.error} role="alert">
          {shown}
        </Caption1>
      ) : (
        hint && <Caption1 className={styles.hint}>{hint}</Caption1>
      )}
      <input
        ref={fileInput}
        type="file"
        accept={IMAGE_ACCEPT}
        hidden
        tabIndex={-1}
        aria-label={`Choose the ${spec.noun} file`}
        onChange={(event) => void chosen(event)}
      />
    </div>
  );
}

/** {@link ImagePicker} bound to a react-hook-form field holding the file id. */
export function ImageField<T extends FieldValues>({
  control,
  name,
  ...props
}: Omit<ImagePickerProps, 'value' | 'onChange' | 'error'> & { control: Control<T>; name: FieldPath<T> }) {
  return (
    <Controller
      control={control}
      name={name}
      render={({ field, fieldState }) => (
        <ImagePicker
          {...props}
          value={(field.value as string | null | undefined) ?? null}
          onChange={field.onChange}
          error={fieldState.error?.message}
        />
      )}
    />
  );
}
