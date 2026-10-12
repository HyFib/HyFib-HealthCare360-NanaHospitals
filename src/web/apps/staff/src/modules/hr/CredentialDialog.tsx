import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Spinner,
} from '@fluentui/react-components';
import { zodResolver } from '@hookform/resolvers/zod';
import {
  applyServerErrors,
  CloseRequestScope,
  DateField,
  FormGrid,
  SelectField,
  TextAreaField,
  TextField,
  useCloseRequests,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { ignoreEscapeWhile } from '@/lib/dialogFocus';
import { hrApi, hrKeys, type CredentialDto } from './api';
import { CREDENTIAL_TYPES, credentialTypeLabel } from './labels';
import { DOCUMENT_ACCEPT, MAX_UPLOAD_BYTES, uploadFile } from './shared/files';

const schema = z
  .object({
    type: z.enum(CREDENTIAL_TYPES),
    name: z.string().trim().min(1, 'Enter the name, e.g. Tamil Nadu Nurses and Midwives Council.').max(150),
    number: z.string().trim().min(1, 'Enter the registration or certificate number.').max(64),
    issuingAuthority: z.string().trim().max(150),
    issuedOn: z.string().nullable(),
    expiresOn: z.string().nullable(),
    notes: z.string().trim().max(1000),
  })
  .superRefine((v, ctx) => {
    if (v.expiresOn && v.issuedOn && v.expiresOn <= v.issuedOn) {
      ctx.addIssue({ code: 'custom', path: ['expiresOn'], message: 'The expiry date must be after the issue date.' });
    }
    if (!v.expiresOn && (v.type === 'BLS' || v.type === 'ACLS')) {
      ctx.addIssue({
        code: 'custom',
        path: ['expiresOn'],
        message: 'BLS and ACLS certificates expire; enter the expiry date.',
      });
    }
  });

type Values = z.infer<typeof schema>;

/** Add or edit a licence / certification. Changing the expiry date restarts the 30-day, 7-day and expiry reminders. */
export function CredentialDialog({
  open,
  onClose,
  employeeId,
  employeeName,
  credential,
}: {
  open: boolean;
  onClose: () => void;
  employeeId: string;
  employeeName: string;
  credential: CredentialDto | null;
}) {
  const closing = useCloseRequests({ onClose });
  return (
    <Dialog open={open} onOpenChange={closing.onOpenChange}>
      <DialogSurface style={{ maxWidth: 680 }}>
        <CloseRequestScope value={closing}>
          {open && (
            <Body onClose={onClose} employeeId={employeeId} employeeName={employeeName} credential={credential} />
          )}
        </CloseRequestScope>
      </DialogSurface>
    </Dialog>
  );
}

function Body({
  onClose,
  employeeId,
  employeeName,
  credential,
}: {
  onClose: () => void;
  employeeId: string;
  employeeName: string;
  credential: CredentialDto | null;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [file, setFile] = useState<File | null>(null);
  const [fileError, setFileError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: {
      type: credential?.type ?? 'NursingCouncil',
      name: credential?.name ?? '',
      number: credential?.number ?? '',
      issuingAuthority: credential?.issuingAuthority ?? '',
      issuedOn: credential?.issuedOn ?? null,
      expiresOn: credential?.expiresOn ?? null,
      notes: credential?.notes ?? '',
    },
  });
  const expiresOn = useWatch({ control: form.control, name: 'expiresOn' });

  const onValid = async (values: Values) => {
    setBusy(true);
    try {
      const uploaded = file ? await uploadFile(file, 'hr-credential') : null;
      await hrApi.saveCredential(employeeId, credential?.id ?? null, {
        type: values.type,
        name: values.name.trim(),
        number: values.number.trim(),
        issuingAuthority: values.issuingAuthority.trim() || null,
        issuedOn: values.issuedOn || null,
        expiresOn: values.expiresOn || null,
        fileId: uploaded?.id ?? credential?.fileId ?? null,
        notes: values.notes.trim() || null,
        version: credential?.version ?? null,
      });
      notify.success(
        credential ? 'Credential updated' : 'Credential added',
        credential && credential.expiresOn !== (values.expiresOn || null)
          ? 'Expiry reminders start again from the new date.'
          : undefined,
      );
      void queryClient.invalidateQueries({ queryKey: hrKeys.credentials });
      void queryClient.invalidateQueries({ queryKey: hrKeys.me });
      onClose();
    } catch (error) {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the credential');
      }
    } finally {
      setBusy(false);
    }
  };

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty || file !== null, busy, onClose });

  return (
    <form
      onSubmit={form.handleSubmit((values) => !busy && onValid(values))}
      onKeyDown={ignoreEscapeWhile(busy)}
      noValidate
    >
      <DialogBody>
        <DialogTitle>
          {credential ? 'Edit credential' : 'Add credential'}
          <Caption1 block>{employeeName}</Caption1>
        </DialogTitle>
        <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          <FormGrid columns={2} minColumnWidth={220}>
            <SelectField
              control={form.control}
              name="type"
              label="Type"
              required
              options={CREDENTIAL_TYPES.map((t) => ({ value: t, label: credentialTypeLabel[t] }))}
            />
            <TextField control={form.control} name="name" label="Name" required autoFocus />
            <TextField control={form.control} name="number" label="Registration / certificate number" required />
            <TextField control={form.control} name="issuingAuthority" label="Issued by" />
            <DateField control={form.control} name="issuedOn" label="Issued on" />
            <DateField
              control={form.control}
              name="expiresOn"
              label="Valid until"
              hint={
                expiresOn
                  ? 'Reminders go out 30 and 7 days before, and on the day.'
                  : 'Leave blank for lifetime registrations.'
              }
            />
          </FormGrid>
          <Field
            label={credential?.fileId ? 'Replace the scanned certificate' : 'Scanned certificate'}
            hint="PDF or photo, up to 10 MB."
            validationMessage={fileError ?? undefined}
            validationState={fileError ? 'error' : 'none'}
          >
            <input
              type="file"
              accept={DOCUMENT_ACCEPT}
              aria-label="Scanned certificate"
              onChange={(event) => {
                const chosen = event.target.files?.[0] ?? null;
                if (chosen && chosen.size > MAX_UPLOAD_BYTES) {
                  setFileError('The file is larger than 10 MB.');
                  setFile(null);
                } else {
                  setFileError(null);
                  setFile(chosen);
                }
              }}
            />
          </Field>
          <TextAreaField control={form.control} name="notes" label="Notes" rows={2} maxLength={1000} />
        </DialogContent>
        <DialogActions>
          <Button onClick={guard.requestClose} disabled={busy}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={busy}
            // Keeps focus while busy (a disabled button drops it); Enter can still submit, which is ignored.
            disabledFocusable={busy}
            icon={busy ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            Save
          </Button>
        </DialogActions>
      </DialogBody>
      {guard.dialogs}
    </form>
  );
}
