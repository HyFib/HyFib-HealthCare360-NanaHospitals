import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  makeStyles,
  Spinner,
  tokens,
} from '@fluentui/react-components';
import { ArrowUploadRegular, DocumentRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDate, formatDateOnly, humanize, todayIso } from '@hc360/core';
import {
  applyServerErrors,
  DataTable,
  DateField,
  EmptyState,
  FormGrid,
  SelectField,
  TextField,
  useLeaveGuard,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { api } from '@/lib/api';
import { usePermission } from '@/lib/permissions';
import { documentApi, PATIENT_DOCUMENT_TYPES, patientKeys, patientQueries } from './api';
import {
  DOCUMENT_LIMITS,
  documentFormDefaults,
  documentFormSchema,
  toAttachRequest,
  type DocumentFormValues,
} from './documentForm';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px', marginBottom: '16px' },
  // Titles and sources run to 200 characters: they wrap rather than widen the table.
  wrap: { whiteSpace: 'normal', overflowWrap: 'anywhere' },
  viewer: { width: '100%', height: '70vh', border: 'none', backgroundColor: tokens.colorNeutralBackground3 },
  image: { maxWidth: '100%', maxHeight: '70vh', display: 'block', margin: '0 auto' },
});

const TYPE_OPTIONS = PATIENT_DOCUMENT_TYPES.map((type) => ({ value: type, label: humanize(type) }));

/**
 * The documents attached to the patient record (EHR-04), on the patient profile: scanned IDs, insurance cards and
 * outside reports, each with its type, the date on it and where it came from. Staff who may attach documents and
 * upload files attach them here; a row opens the document.
 */
export function DocumentsPanel({ patientId }: { patientId: string }) {
  const styles = useStyles();
  const notify = useNotify();
  const canManageDocuments = usePermission('patients.documents');
  const canWriteFiles = usePermission('files.write');
  const documents = useQuery(patientQueries.documents(patientId));
  const [viewer, setViewer] = useState<{ url: string; contentType: string; title: string } | null>(null);

  // Release the decrypted document from memory when the viewer closes or shows another file.
  useEffect(
    () => () => {
      if (viewer) {
        URL.revokeObjectURL(viewer.url);
      }
    },
    [viewer],
  );

  const open = async (fileId: string, contentType: string | null, title: string) => {
    try {
      const blob = await api.blob(`/api/v1/files/${fileId}`);
      setViewer({ url: URL.createObjectURL(blob), contentType: contentType ?? blob.type, title });
    } catch (error) {
      notify.error(error, 'Could not open the document');
    }
  };

  return (
    <>
      {canManageDocuments && canWriteFiles && <AttachDocumentForm patientId={patientId} />}
      {documents.data && documents.data.length === 0 ? (
        <EmptyState
          title="No documents"
          description="Scanned IDs, insurance cards and outside reports appear here."
          icon={<DocumentRegular />}
        />
      ) : (
        <DataTable
          ariaLabel="Documents"
          rows={documents.data}
          loading={documents.isLoading}
          error={documents.error}
          onRetry={() => void documents.refetch()}
          getRowId={(d) => d.id}
          onRowClick={(d) => void open(d.fileId, d.contentType, d.title)}
          columns={[
            {
              key: 'title',
              header: 'Title',
              minWidth: 160,
              render: (d) => <span className={styles.wrap}>{d.title}</span>,
            },
            { key: 'type', header: 'Type', render: (d) => humanize(d.type) },
            {
              key: 'date',
              header: 'Document date',
              width: 130,
              priority: 'low',
              render: (d) => (d.documentDate ? formatDateOnly(d.documentDate) : '—'),
            },
            { key: 'added', header: 'Added', width: 130, render: (d) => formatDate(d.createdAt) },
            {
              key: 'source',
              header: 'Source',
              priority: 'low',
              render: (d) => (d.source ? <span className={styles.wrap}>{d.source}</span> : '—'),
            },
            {
              key: 'size',
              header: 'Size',
              width: 90,
              render: (d) => (d.sizeBytes ? `${Math.ceil(d.sizeBytes / 1024)} KB` : '—'),
              priority: 'low',
            },
          ]}
          size="small"
        />
      )}
      <Dialog open={viewer !== null} onOpenChange={(_, d) => !d.open && setViewer(null)}>
        <DialogSurface style={{ maxWidth: '90vw', width: 1000 }}>
          <DialogBody>
            <DialogTitle>{viewer?.title}</DialogTitle>
            <DialogContent>
              {viewer?.contentType.startsWith('image/') ? (
                <img src={viewer.url} alt={viewer.title} className={styles.image} />
              ) : (
                viewer && <iframe src={viewer.url} title={viewer.title} className={styles.viewer} />
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={() => setViewer(null)}>Close</Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </>
  );
}

/** Uploads the file, encrypted, then attaches it to the record with its type, date and source. */
function AttachDocumentForm({ patientId }: { patientId: string }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  // The browser shows the chosen file's name until the input is replaced: a new one after each document attached.
  const [attachedCount, setAttachedCount] = useState(0);
  const form = useForm<DocumentFormValues>({
    resolver: zodResolver(documentFormSchema),
    mode: 'onTouched',
    defaultValues: documentFormDefaults(),
  });
  const file = useWatch({ control: form.control, name: 'file' });

  const attach = useMutation({
    mutationFn: async (values: DocumentFormValues) => {
      const stored = await documentApi.upload(patientId, values.file!);
      await documentApi.attach(patientId, toAttachRequest(values, stored.id));
    },
    onSuccess: () => {
      form.reset(documentFormDefaults());
      setAttachedCount((count) => count + 1);
      notify.success('Document attached');
      void queryClient.invalidateQueries({ queryKey: patientKeys.documents(patientId) });
    },
    // The server's messages about a field go on it; anything else (a restricted record, a lost connection) in a toast.
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not attach the document');
      }
    },
  });

  // Leaving the screen or closing the window with unsaved changes asks first (SRS UX-08).
  useLeaveGuard({ dirty: form.formState.isDirty, busy: attach.isPending });

  return (
    <form className={styles.form} onSubmit={form.handleSubmit((values) => attach.mutate(values))} noValidate>
      <FormGrid>
        <Controller
          control={form.control}
          name="file"
          render={({ field, fieldState }) => (
            <Field
              label="File (PDF, JPG, PNG, max 25 MB)"
              required
              validationMessage={fieldState.error?.message}
              validationState={fieldState.error ? 'error' : 'none'}
            >
              {(fieldProps) => (
                <input
                  key={attachedCount}
                  {...fieldProps}
                  ref={field.ref}
                  name={field.name}
                  type="file"
                  accept="application/pdf,image/png,image/jpeg,image/webp"
                  onChange={(e) => field.onChange(e.target.files?.[0] ?? null)}
                />
              )}
            </Field>
          )}
        />
        <SelectField control={form.control} name="type" label="Type" required options={TYPE_OPTIONS} />
        <TextField
          control={form.control}
          name="title"
          label="Title"
          placeholder={file?.name ?? 'For example: MRI report from Apollo'}
          maxLength={DOCUMENT_LIMITS.title}
        />
        <DateField
          control={form.control}
          name="documentDate"
          label="Document date"
          hint="The date on the document, such as the day of the test"
          max={todayIso()}
        />
        <TextField
          control={form.control}
          name="source"
          label="Source"
          hint="Where it came from, such as an outside lab or hospital"
          maxLength={DOCUMENT_LIMITS.source}
        />
      </FormGrid>
      <div>
        <Button
          type="submit"
          appearance="primary"
          icon={attach.isPending ? <Spinner size="tiny" aria-hidden /> : <ArrowUploadRegular />}
          disabled={!file || attach.isPending}
        >
          Attach
        </Button>
      </div>
    </form>
  );
}
