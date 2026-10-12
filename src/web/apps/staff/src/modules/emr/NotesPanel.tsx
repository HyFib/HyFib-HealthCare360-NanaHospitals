import {
  Badge,
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  makeStyles,
  mergeClasses,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import {
  DocumentArrowLeftRegular,
  EditRegular,
  NoteAddRegular,
  SaveRegular,
  SignatureRegular,
  TextBulletListLtrRegular,
} from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateTime, formatRelative, isApiError, useSession } from '@hc360/core';
import {
  applyServerErrors,
  CloseRequestScope,
  EmptyState,
  FormGrid,
  LoadingState,
  Pagination,
  SelectField,
  TextField,
  useCloseRequests,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { z } from 'zod';
import { usePermission } from '@/lib/permissions';
import { useFormDraft } from '@/lib/useFormDraft';
import {
  emrApi,
  emrKeys,
  emrQueries,
  NOTE_TYPES,
  noteTypeLabel,
  type ClinicalNoteDto,
  type ClinicalNoteType,
} from './api';
import { appendText } from './encounterForm';
import { DictateButton } from './ui';

const useStyles = makeStyles({
  composer: { display: 'flex', flexDirection: 'column', gap: '12px' },
  bodyLabel: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  actions: { display: 'flex', gap: '8px', justifyContent: 'flex-end', alignItems: 'center', flexWrap: 'wrap' },
  list: { display: 'flex', flexDirection: 'column', gap: '12px' },
  note: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    padding: '12px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  addendum: { marginLeft: '24px', borderLeft: `3px solid ${tokens.colorBrandStroke1}` },
  noteHeader: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap', justifyContent: 'space-between' },
  noteTitle: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  body: { whiteSpace: 'pre-wrap', wordBreak: 'break-word' },
  muted: { color: tokens.colorNeutralForeground3 },
  savedAt: { color: tokens.colorNeutralForeground3 },
});

/** Starters for common notes (plain text so they print and export cleanly). */
const NOTE_STARTERS: { label: string; type: ClinicalNoteType; title: string; body: string }[] = [
  { label: 'Progress note (SOAP)', type: 'ProgressNote', title: 'Progress note', body: 'S: \nO: \nA: \nP: ' },
  {
    label: 'Procedure note',
    type: 'Procedure',
    title: 'Procedure note',
    body: 'Procedure: \nIndication: \nConsent: written consent obtained\nAnaesthesia: \nFindings: \nComplications: none\nPost-procedure plan: ',
  },
  {
    label: 'Counselling note',
    type: 'Counselling',
    title: 'Counselling',
    body: 'Counselled: patient / attendant (name, relation)\nTopics: diagnosis, treatment options, risks and benefits\nQuestions answered: yes\nUnderstanding confirmed: yes',
  },
  {
    label: 'Referral letter',
    type: 'Referral',
    title: 'Referral letter',
    body: 'Dear Doctor,\n\nI am referring this patient for your opinion and management of: \n\nRelevant history: \nCurrent medicines: \nInvestigations: \n\nThank you.',
  },
];

const noteSchema = z.object({
  type: z.enum(['ProgressNote', 'Procedure', 'Referral', 'Counselling', 'Other']),
  title: z.string().max(200, 'Keep the title under 200 characters.'),
  body: z.string().trim().min(1, 'Write the note.').max(20000, 'Keep the note under 20,000 characters.'),
});

type NoteValues = z.infer<typeof noteSchema>;

const emptyNote: NoteValues = { type: 'ProgressNote', title: '', body: '' };

/**
 * Writes a clinical note. Typing is saved on this computer every few seconds, so a WiFi drop or a closed window never
 * loses it; the note is stored on the server when you press Save note.
 */
export function NoteComposer({
  patientId,
  encounterId,
  admissionId = null,
  onSaved,
}: {
  patientId: string;
  encounterId: string | null;
  admissionId?: string | null;
  onSaved?: (note: ClinicalNoteDto) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<NoteValues>({ resolver: zodResolver(noteSchema), defaultValues: emptyNote });
  const draft = useFormDraft(form, `emr-note:${encounterId ?? patientId}`);

  const save = useMutation({
    mutationFn: (values: NoteValues) =>
      emrApi.writeNote(patientId, {
        encounterId,
        admissionId,
        type: values.type,
        title: values.title.trim() || null,
        body: values.body.trim(),
      }),
    onSuccess: (note) => {
      draft.discard();
      form.reset(emptyNote);
      notify.success('Note saved', 'Sign it when it is final.');
      void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });
      onSaved?.(note);
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the note');
      }
    },
  });

  const insertStarter = (starter: (typeof NOTE_STARTERS)[number]) => {
    const current = form.getValues();
    form.setValue('type', starter.type, { shouldDirty: true });
    if (!current.title.trim()) {
      form.setValue('title', starter.title, { shouldDirty: true });
    }
    form.setValue('body', appendText(current.body, starter.body), { shouldDirty: true });
    form.setFocus('body');
  };

  return (
    <form className={styles.composer} onSubmit={form.handleSubmit((values) => save.mutate(values))} noValidate>
      {draft.saved && draft.saved.value.body.trim() && (
        <MessageBar intent="info" icon={<DocumentArrowLeftRegular />}>
          <MessageBarBody>
            An unsaved note from {formatRelative(new Date(draft.saved.savedAt).toISOString())} was found on this
            computer.
          </MessageBarBody>
          <MessageBarActions>
            <Button onClick={draft.restore}>Restore it</Button>
            <Button appearance="subtle" onClick={draft.discard}>
              Discard
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}
      <FormGrid columns={2}>
        <SelectField
          control={form.control}
          name="type"
          label="Note type"
          options={NOTE_TYPES.map((t) => ({ value: t, label: noteTypeLabel[t] }))}
        />
        <TextField control={form.control} name="title" label="Title" maxLength={200} placeholder="Optional" />
      </FormGrid>
      <Controller
        control={form.control}
        name="body"
        render={({ field, fieldState }) => (
          <div>
            <div className={styles.bodyLabel}>
              <span />
              <span>
                <Menu>
                  <MenuTrigger disableButtonEnhancement>
                    <Button size="small" appearance="subtle" icon={<TextBulletListLtrRegular />}>
                      Insert template
                    </Button>
                  </MenuTrigger>
                  <MenuPopover>
                    <MenuList>
                      {NOTE_STARTERS.map((starter) => (
                        <MenuItem key={starter.label} onClick={() => insertStarter(starter)}>
                          {starter.label}
                        </MenuItem>
                      ))}
                    </MenuList>
                  </MenuPopover>
                </Menu>
                <DictateButton onText={(text) => field.onChange(appendText(field.value, text))} />
              </span>
            </div>
            <Field
              label="Note"
              required
              validationMessage={fieldState.error?.message}
              validationState={fieldState.error ? 'error' : 'none'}
            >
              <Textarea
                {...field}
                rows={8}
                resize="vertical"
                maxLength={20000}
                placeholder="Type the note. It is kept on this computer until you save it."
              />
            </Field>
          </div>
        )}
      />
      <div className={styles.actions}>
        <Caption1 className={styles.savedAt}>Drafts are kept on this computer while you type.</Caption1>
        <Button
          type="submit"
          appearance="primary"
          icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : <SaveRegular />}
          disabled={save.isPending}
        >
          Save note
        </Button>
      </div>
    </form>
  );
}

const editSchema = noteSchema;

function EditNoteDialog({
  note,
  patientId,
  onClose,
}: {
  note: ClinicalNoteDto | null;
  patientId: string;
  onClose: () => void;
}) {
  const closing = useCloseRequests({ onClose });
  return (
    <Dialog open={note !== null} onOpenChange={closing.onOpenChange}>
      <DialogSurface style={{ maxWidth: 760 }}>
        <CloseRequestScope value={closing}>
          {note && <EditNoteForm key={note.id} note={note} patientId={patientId} onClose={onClose} />}
        </CloseRequestScope>
      </DialogSurface>
    </Dialog>
  );
}

function EditNoteForm({ note, patientId, onClose }: { note: ClinicalNoteDto; patientId: string; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<NoteValues>({
    resolver: zodResolver(editSchema),
    defaultValues: { type: note.type, title: note.title ?? '', body: note.body },
  });
  const draft = useFormDraft(form, `emr-note-edit:${note.id}`);
  const save = useMutation({
    mutationFn: (values: NoteValues) =>
      emrApi.editNote(note.id, {
        type: values.type,
        title: values.title.trim() || null,
        body: values.body.trim(),
        version: note.version,
      }),
    onSuccess: () => {
      draft.discard();
      notify.success('Note updated');
      void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });
      onClose();
    },
    onError: (error) => {
      if (isApiError(error) && error.isConflict) {
        notify.error(error, 'The note was changed elsewhere');
        void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not update the note');
      }
    },
  });

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <form onSubmit={form.handleSubmit((values) => save.mutate(values))} noValidate>
      <DialogBody>
        <DialogTitle>Edit note</DialogTitle>
        <DialogContent className={styles.composer}>
          {draft.saved && draft.saved.value.body !== note.body && (
            <MessageBar intent="info">
              <MessageBarBody>Unsaved changes to this note were found on this computer.</MessageBarBody>
              <MessageBarActions>
                <Button onClick={draft.restore}>Restore them</Button>
                <Button appearance="subtle" onClick={draft.discard}>
                  Discard
                </Button>
              </MessageBarActions>
            </MessageBar>
          )}
          <FormGrid columns={2}>
            <SelectField
              control={form.control}
              name="type"
              label="Note type"
              options={NOTE_TYPES.map((t) => ({ value: t, label: noteTypeLabel[t] }))}
            />
            <TextField control={form.control} name="title" label="Title" maxLength={200} />
          </FormGrid>
          <Controller
            control={form.control}
            name="body"
            render={({ field, fieldState }) => (
              <Field
                label="Note"
                required
                validationMessage={fieldState.error?.message}
                validationState={fieldState.error ? 'error' : 'none'}
              >
                <Textarea {...field} rows={12} resize="vertical" maxLength={20000} autoFocus />
              </Field>
            )}
          />
        </DialogContent>
        <DialogActions>
          <Button onClick={guard.requestClose} disabled={save.isPending}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={save.isPending}
            icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            Save changes
          </Button>
        </DialogActions>
      </DialogBody>
      {guard.dialogs}
    </form>
  );
}

function AddendumDialog({
  note,
  patientId,
  onClose,
}: {
  note: ClinicalNoteDto | null;
  patientId: string;
  onClose: () => void;
}) {
  const closing = useCloseRequests({ onClose });
  return (
    <Dialog open={note !== null} onOpenChange={closing.onOpenChange}>
      <DialogSurface style={{ maxWidth: 680 }}>
        <CloseRequestScope value={closing}>
          {note && <AddendumForm note={note} patientId={patientId} onClose={onClose} />}
        </CloseRequestScope>
      </DialogSurface>
    </Dialog>
  );
}

function AddendumForm({ note, patientId, onClose }: { note: ClinicalNoteDto; patientId: string; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [body, setBody] = useState('');
  const save = useMutation({
    mutationFn: () => emrApi.addendum(note.id, body.trim()),
    onSuccess: () => {
      notify.success('Addendum added', 'Sign it when it is final.');
      void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });
      onClose();
    },
    onError: (error) => notify.error(error, 'Could not add the addendum'),
  });
  const guard = useUnsavedChanges({ dirty: body !== '', busy: save.isPending, onClose });

  return (
    <DialogBody>
      <DialogTitle>Add an addendum</DialogTitle>
      <DialogContent className={styles.composer}>
        <Text>
          Signed notes cannot be changed. An addendum is a new note linked to the original, with your name and the time.
        </Text>
        <Field label="Addendum" required>
          <Textarea
            autoFocus
            value={body}
            onChange={(_, d) => setBody(d.value)}
            rows={6}
            resize="vertical"
            maxLength={20000}
          />
        </Field>
      </DialogContent>
      <DialogActions>
        <Button onClick={guard.requestClose} disabled={save.isPending}>
          Cancel
        </Button>
        <Button appearance="primary" disabled={!body.trim() || save.isPending} onClick={() => save.mutate()}>
          Add addendum
        </Button>
      </DialogActions>
      {guard.dialogs}
    </DialogBody>
  );
}

/** Notes with their signing state. Authors edit and sign their own unsigned notes; signed notes take addenda. */
export function NotesList({
  patientId,
  encounterId,
  emptyHint,
}: {
  patientId: string;
  encounterId: string | null;
  emptyHint?: string;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const userId = useSession((s) => s.user?.id);
  const canSign = usePermission('clinical.encounters.sign');
  const canWrite = usePermission('clinical.emr.write');
  const [page, setPage] = useState(1);
  const [editing, setEditing] = useState<ClinicalNoteDto | null>(null);
  const [addendumFor, setAddendumFor] = useState<ClinicalNoteDto | null>(null);
  const notes = useQuery(emrQueries.notes(patientId, encounterId, page));

  const sign = useMutation({
    mutationFn: (note: ClinicalNoteDto) => emrApi.signNote(note.id, note.version),
    onSuccess: () => {
      notify.success('Note signed');
      void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });
    },
    onError: (error) => {
      notify.error(error, 'Could not sign the note');
      void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });
    },
  });

  if (notes.isLoading) {
    return <LoadingState />;
  }
  const items = notes.data?.items ?? [];
  if (items.length === 0) {
    return (
      <EmptyState
        title="No notes yet"
        description={emptyHint ?? 'Progress, procedure and counselling notes appear here.'}
      />
    );
  }

  return (
    <div className={styles.list}>
      {items.map((note) => {
        const mine = note.authorUserId === userId;
        return (
          <article
            key={note.id}
            className={mergeClasses(styles.note, note.amendsNoteId ? styles.addendum : undefined)}
            aria-label={note.title ?? noteTypeLabel[note.type]}
          >
            <div className={styles.noteHeader}>
              <span className={styles.noteTitle}>
                <Text weight="semibold">{note.title ?? noteTypeLabel[note.type]}</Text>
                <Badge appearance="outline" size="small">
                  {noteTypeLabel[note.type]}
                </Badge>
                {note.isSigned ? (
                  <Badge appearance="tint" color="success" size="small">
                    Signed {note.signedAt ? formatDateTime(note.signedAt) : ''}
                  </Badge>
                ) : (
                  <Badge appearance="tint" color="warning" size="small">
                    Not signed
                  </Badge>
                )}
              </span>
              <span className={styles.noteTitle}>
                {!note.isSigned && mine && canWrite && (
                  <Button size="small" icon={<EditRegular />} onClick={() => setEditing(note)}>
                    Edit
                  </Button>
                )}
                {!note.isSigned && mine && canSign && (
                  <Button
                    size="small"
                    appearance="primary"
                    icon={<SignatureRegular />}
                    disabled={sign.isPending}
                    onClick={() => sign.mutate(note)}
                  >
                    Sign
                  </Button>
                )}
                {note.isSigned && canWrite && (
                  <Button size="small" icon={<NoteAddRegular />} onClick={() => setAddendumFor(note)}>
                    Addendum
                  </Button>
                )}
              </span>
            </div>
            <Caption1 className={styles.muted}>
              {note.authorName ?? 'Staff'} · {formatDateTime(note.createdAt)}
            </Caption1>
            <Text className={styles.body}>{note.body}</Text>
          </article>
        );
      })}
      {(notes.data?.totalCount ?? 0) > 20 && (
        <Pagination page={page} pageSize={20} totalCount={notes.data?.totalCount ?? 0} onPageChange={setPage} />
      )}
      <EditNoteDialog note={editing} patientId={patientId} onClose={() => setEditing(null)} />
      <AddendumDialog note={addendumFor} patientId={patientId} onClose={() => setAddendumFor(null)} />
    </div>
  );
}

/** Chart tab: every note of the patient, with a composer for doctors. */
export function NotesTab({ patientId }: { patientId: string }) {
  const canWrite = usePermission('clinical.emr.write');
  const [writing, setWriting] = useState(false);
  return (
    <>
      {canWrite && (
        <div style={{ marginBottom: 12 }}>
          <Button appearance="primary" icon={<NoteAddRegular />} onClick={() => setWriting(true)}>
            Write note
          </Button>
        </div>
      )}
      <NotesList patientId={patientId} encounterId={null} />
      <Dialog open={writing} onOpenChange={(_, d) => !d.open && setWriting(false)}>
        <DialogSurface style={{ maxWidth: 800 }}>
          <DialogBody>
            <DialogTitle>Write note</DialogTitle>
            <DialogContent>
              {writing && <NoteComposer patientId={patientId} encounterId={null} onSaved={() => setWriting(false)} />}
            </DialogContent>
            <DialogActions>
              <Button onClick={() => setWriting(false)}>Close</Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </>
  );
}
