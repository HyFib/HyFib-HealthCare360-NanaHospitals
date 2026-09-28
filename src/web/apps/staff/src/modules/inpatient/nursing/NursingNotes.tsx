import {
  Badge,
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Select,
  Spinner,
  Switch,
  Text,
  tokens,
} from '@fluentui/react-components';
import { DocumentArrowLeftRegular, NoteRegular, SendRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateOnly, formatDateTime, formatRelative, formatTime, useHotkey } from '@hc360/core';
import { applyServerErrors, EmptyState, ErrorState, TextAreaField, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { useFormDraft } from '@/lib/useFormDraft';
import { NOTE_CATEGORIES, NOTE_CATEGORY_LABEL, nursingApi, nursingKeys, nursingQueries, type NursingNoteCategory } from '../nursingApi';
import { dutyDate, fromIstInput, SHIFT_LABEL, toIstInput } from '../time';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '10px', padding: '12px', borderRadius: tokens.borderRadiusLarge, backgroundColor: tokens.colorNeutralBackground2 },
  categories: { display: 'flex', gap: '6px', flexWrap: 'wrap' },
  row: { display: 'flex', gap: '12px', alignItems: 'flex-end', flexWrap: 'wrap', justifyContent: 'space-between' },
  list: { display: 'flex', flexDirection: 'column', gap: '8px', marginTop: '16px' },
  note: { padding: '10px 12px', borderRadius: tokens.borderRadiusMedium, border: `1px solid ${tokens.colorNeutralStroke2}`, backgroundColor: tokens.colorNeutralBackground1 },
  incident: { borderLeft: `4px solid ${tokens.colorPaletteRedBorderActive}` },
  noteHead: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap', marginBottom: '4px' },
  text: { whiteSpace: 'pre-wrap', wordBreak: 'break-word' },
  muted: { color: tokens.colorNeutralForeground3 },
});

const schema = z
  .object({
    category: z.enum(NOTE_CATEGORIES),
    note: z.string().trim().min(3, 'Write the note.').max(4000),
    earlier: z.boolean(),
    noteFor: z.string(),
  })
  .superRefine((v, ctx) => {
    if (v.earlier && !fromIstInput(v.noteFor)) {
      ctx.addIssue({ code: 'custom', path: ['noteFor'], message: 'Enter the time the note is about.' });
    }
  });

type NoteValues = z.infer<typeof schema>;

/**
 * Nursing progress notes (medico-legal record: never edited; corrections are new notes). The note is saved on this
 * workstation while typing, so a WiFi drop never loses it. Ctrl+Enter saves.
 */
export function NursingNotes({ admissionId, canWrite }: { admissionId: string; canWrite: boolean }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [scope, setScope] = useState<'shift' | 'all'>('shift');
  const [limits] = useState(() => ({ now: toIstInput(new Date()), duty: dutyDate(new Date()) }));
  const notes = useQuery(nursingQueries.notes(admissionId, scope === 'shift' ? limits.duty : null));
  const form = useForm<NoteValues>({
    resolver: zodResolver(schema),
    defaultValues: { category: 'General', note: '', earlier: false, noteFor: limits.now },
  });
  const draft = useFormDraft(canWrite ? form : null, `nursing-note:${admissionId}`);
  const earlier = useWatch({ control: form.control, name: 'earlier' });

  const add = useMutation({
    mutationFn: (v: NoteValues) => nursingApi.addNote(admissionId, { category: v.category, note: v.note, noteFor: v.earlier ? fromIstInput(v.noteFor) : null }),
    onSuccess: (note) => {
      draft.clear();
      form.reset({ category: 'General', note: '', earlier: false, noteFor: toIstInput(new Date()) });
      notify.success(note.isLateEntry ? 'Note saved as a late entry' : 'Note saved');
      void queryClient.invalidateQueries({ queryKey: nursingKeys.admission(admissionId) });
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the note');
      }
    },
  });
  const submit = form.handleSubmit((v) => add.mutate(v));
  useHotkey('mod+enter', () => void submit(), { allowInInputs: true, enabled: canWrite && !add.isPending });

  return (
    <div>
      {canWrite && (
        <form onSubmit={submit} noValidate className={styles.form}>
          {draft.saved && (
            <MessageBar intent="info" icon={<DocumentArrowLeftRegular />}>
              <MessageBarBody>An unsaved note from {formatRelative(new Date(draft.saved.savedAt).toISOString())} was kept on this computer.</MessageBarBody>
              <MessageBarActions>
                <Button size="small" onClick={draft.restore}>
                  Restore
                </Button>
                <Button size="small" appearance="subtle" onClick={draft.discard}>
                  Discard
                </Button>
              </MessageBarActions>
            </MessageBar>
          )}
          <Controller
            control={form.control}
            name="category"
            render={({ field }) => (
              <div className={styles.categories} role="radiogroup" aria-label="Note type">
                {NOTE_CATEGORIES.map((c) => (
                  <Button
                    key={c}
                    size="small"
                    shape="circular"
                    role="radio"
                    aria-checked={field.value === c}
                    appearance={field.value === c ? 'primary' : 'outline'}
                    onClick={() => field.onChange(c)}
                  >
                    {NOTE_CATEGORY_LABEL[c as NursingNoteCategory]}
                  </Button>
                ))}
              </div>
            )}
          />
          <TextAreaField control={form.control} name="note" label="Nursing note" rows={4} maxLength={4000} placeholder="Observation, care given, response, plan…" />
          <div className={styles.row}>
            <div>
              <Controller
                control={form.control}
                name="earlier"
                render={({ field }) => <Switch checked={field.value} onChange={(_, d) => field.onChange(d.checked)} label="About an earlier time (last 24 hours)" />}
              />
              {earlier && (
                <Controller
                  control={form.control}
                  name="noteFor"
                  render={({ field, fieldState }) => (
                    <Field label="The note is about" validationMessage={fieldState.error?.message} validationState={fieldState.error ? 'error' : 'none'}>
                      <Input type="datetime-local" value={field.value} max={limits.now} onChange={(_, d) => field.onChange(d.value)} />
                    </Field>
                  )}
                />
              )}
            </div>
            <Button type="submit" appearance="primary" icon={add.isPending ? <Spinner size="tiny" /> : <SendRegular />} disabled={add.isPending} aria-keyshortcuts="Control+Enter">
              Save note (Ctrl+Enter)
            </Button>
          </div>
        </form>
      )}

      <div className={styles.row} style={{ marginTop: 16 }}>
        <Text weight="semibold">{scope === 'shift' ? `Notes for duty date ${formatDateOnly(limits.duty)}` : 'All notes'}</Text>
        <Select value={scope} onChange={(_, d) => setScope(d.value as 'shift' | 'all')} aria-label="Which notes">
          <option value="shift">This duty date</option>
          <option value="all">Whole stay</option>
        </Select>
      </div>
      {notes.error ? (
        <ErrorState error={notes.error} onRetry={() => void notes.refetch()} />
      ) : notes.isLoading ? (
        <Spinner size="small" label="Loading notes…" />
      ) : !notes.data || notes.data.length === 0 ? (
        <EmptyState icon={<NoteRegular />} title="No notes yet" description={canWrite ? 'Write the first note of the shift above.' : undefined} />
      ) : (
        <div className={styles.list}>
          {notes.data.map((n) => (
            <article key={n.id} className={n.category === 'Incident' ? `${styles.note} ${styles.incident}` : styles.note}>
              <div className={styles.noteHead}>
                <Badge appearance="tint" color={n.category === 'Incident' ? 'danger' : 'brand'}>
                  {NOTE_CATEGORY_LABEL[n.category] ?? n.category}
                </Badge>
                <Text weight="semibold">{formatTime(n.noteFor)}</Text>
                <Caption1 className={styles.muted}>
                  {SHIFT_LABEL[n.shift]} · {n.recordedByName ?? 'Unknown'} · written {formatDateTime(n.recordedAt)}
                </Caption1>
                {n.isLateEntry && (
                  <Badge appearance="outline" color="warning">
                    Late entry
                  </Badge>
                )}
              </div>
              <Text className={styles.text}>{n.note}</Text>
            </article>
          ))}
        </div>
      )}
    </div>
  );
}
