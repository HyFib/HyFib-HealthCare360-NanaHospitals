import {
  Button,
  Caption1,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  AddRegular,
  DeleteRegular,
  DocumentArrowLeftRegular,
  LockClosedRegular,
  PrintRegular,
  SaveRegular,
  SignatureRegular,
  TextBulletListLtrRegular,
} from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatRelative, useHotkey, useSession } from '@hc360/core';
import {
  ConfirmDialog,
  ErrorState,
  FormGrid,
  FullRow,
  LoadingState,
  NumberField,
  Section,
  SelectField,
  TextAreaField,
  TextField,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useFieldArray, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { usePermission } from '@/lib/permissions';
import { useFormDraft } from '@/lib/useFormDraft';
import { otApi, otKeys, otQueries, SPECIMEN_DESTINATIONS, type OperationNoteDto, type SurgeryDetail } from './api';
import { specimenLabel } from './labels';
import { operationNoteGate } from './logic';
import { printOperationNote } from './print';
import { applyFormErrors } from './shared/errors';
import { ActionError, Detail, DetailGrid, WhoWhen } from './shared/feedback';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '16px' },
  rows: { display: 'flex', flexDirection: 'column', gap: '8px' },
  specimen: {
    display: 'grid',
    gridTemplateColumns: 'minmax(200px, 3fr) minmax(150px, 2fr) minmax(110px, 1fr) auto',
    gap: '8px',
    alignItems: 'start',
  },
  removeButton: { marginTop: '24px' },
  footer: { display: 'flex', gap: '8px', justifyContent: 'flex-end', alignItems: 'center', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  pre: { whiteSpace: 'pre-wrap' },
});

const POST_OP_TEMPLATE = [
  'Monitoring: pulse, BP, SpO₂ every 15 min for 1 h, then hourly for 4 h',
  'Position: ',
  'IV fluids: ',
  'Analgesia: ',
  'Antibiotics: ',
  'Diet: nil by mouth until ',
  'Drains / catheter: ',
  'Investigations: ',
  'Call the surgeon if: ',
].join('\n');

const noteSchema = z
  .object({
    preOperativeDiagnosis: z.string().trim().max(500),
    postOperativeDiagnosis: z.string().trim().max(500),
    procedurePerformed: z.string().trim().min(1, 'Enter the procedure performed.').max(500),
    findings: z.string().trim().max(4000),
    procedureDetails: z.string().trim().max(8000),
    specimens: z
      .array(
        z.object({
          description: z.string().trim().min(1, 'Describe the specimen.').max(200),
          sentTo: z.enum(SPECIMEN_DESTINATIONS),
          label: z.string().trim().max(64),
        }),
      )
      .max(30, 'At most 30 specimens.'),
    drains: z.string().trim().max(500),
    estimatedBloodLossMl: z.number().nullable(),
    complications: z.string().trim().max(2000),
    postOpOrders: z.string().trim().max(4000),
  })
  .superRefine((v, ctx) => {
    if (v.estimatedBloodLossMl !== null && (v.estimatedBloodLossMl < 0 || v.estimatedBloodLossMl > 20_000)) {
      ctx.addIssue({ code: 'custom', path: ['estimatedBloodLossMl'], message: 'Enter between 0 and 20,000 ml.' });
    }
  });

type NoteValues = z.infer<typeof noteSchema>;

function toValues(note: OperationNoteDto | null, booking: SurgeryDetail): NoteValues {
  const side =
    booking.laterality === 'NotApplicable'
      ? ''
      : ` (${booking.laterality === 'Bilateral' ? 'bilateral' : booking.laterality.toLowerCase()})`;
  return {
    preOperativeDiagnosis: note?.preOperativeDiagnosis ?? booking.diagnosis ?? '',
    postOperativeDiagnosis: note?.postOperativeDiagnosis ?? '',
    procedurePerformed: note?.procedurePerformed ?? `${booking.procedureName}${side}`,
    findings: note?.findings ?? '',
    procedureDetails: note?.procedureDetails ?? '',
    specimens: (note?.specimens ?? []).map((s) => ({
      description: s.description,
      sentTo: s.sentTo,
      label: s.label ?? '',
    })),
    drains: note?.drains ?? '',
    estimatedBloodLossMl: note?.estimatedBloodLossMl ?? null,
    complications: note?.complications ?? '',
    postOpOrders: note?.postOpOrders ?? '',
  };
}

const orNull = (value: string) => (value.trim() ? value.trim() : null);

/** Operation note and post-operative orders (OT-05), written by the operating surgeon once surgery has started. */
export function OperationNotePanel({ booking }: { booking: SurgeryDetail }) {
  const note = useQuery(otQueries.operationNote(booking.id));
  const canWrite = usePermission('ot.surgery.perform');
  const gate = operationNoteGate(booking);

  if (note.isLoading) {
    return <LoadingState label="Loading the operation note…" />;
  }
  if (note.error) {
    return <ErrorState error={note.error} onRetry={() => void note.refetch()} />;
  }
  const data = note.data ?? null;
  if (data?.signedAt) {
    return <NoteView booking={booking} note={data} />;
  }
  if (!gate.allowed) {
    return (
      <MessageBar intent="info">
        <MessageBarBody>{gate.reason}</MessageBarBody>
      </MessageBar>
    );
  }
  if (!canWrite) {
    return data ? (
      <NoteView booking={booking} note={data} />
    ) : (
      <MessageBar intent="info">
        <MessageBarBody>The operating surgeon has not written the operation note yet.</MessageBarBody>
      </MessageBar>
    );
  }
  return <NoteForm booking={booking} note={data} />;
}

function NoteForm({ booking, note }: { booking: SurgeryDetail; note: OperationNoteDto | null }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const facilityName = useSession((s) => s.user?.facilityName) ?? 'Nana Hospitals';
  const [confirmSign, setConfirmSign] = useState(false);
  const form = useForm<NoteValues>({ resolver: zodResolver(noteSchema), defaultValues: toValues(note, booking) });
  const draft = useFormDraft(form, `ot-operation-note-${booking.id}`);
  const specimens = useFieldArray({ control: form.control, name: 'specimens' });
  const [postOpOrders] = useWatch({ control: form.control, name: ['postOpOrders'] });

  const save = useMutation({
    mutationFn: ({ values, sign }: { values: NoteValues; sign: boolean }) => {
      const latest = queryClient.getQueryData<OperationNoteDto | null>(otKeys.operationNote(booking.id)) ?? note;
      return otApi.saveOperationNote(booking.id, {
        preOperativeDiagnosis: orNull(values.preOperativeDiagnosis),
        postOperativeDiagnosis: orNull(values.postOperativeDiagnosis),
        procedurePerformed: values.procedurePerformed,
        findings: orNull(values.findings),
        procedureDetails: orNull(values.procedureDetails),
        specimens: values.specimens.map((s) => ({
          description: s.description,
          sentTo: s.sentTo,
          label: orNull(s.label),
        })),
        drains: orNull(values.drains),
        estimatedBloodLossMl: values.estimatedBloodLossMl,
        complications: orNull(values.complications),
        postOpOrders: orNull(values.postOpOrders),
        sign,
        version: latest?.version ?? null,
      });
    },
    onSuccess: (saved, { sign }) => {
      queryClient.setQueryData(otKeys.operationNote(booking.id), saved);
      draft.clear();
      setConfirmSign(false);
      notify.success(sign ? 'Operation note signed and locked' : 'Operation note saved');
    },
    onError: (error) => {
      setConfirmSign(false);
      applyFormErrors(error, form.setError);
    },
  });

  const submit = (sign: boolean) => form.handleSubmit((values) => save.mutate({ values, sign }))();
  useHotkey('mod+s', () => void submit(false), { allowInInputs: true, enabled: !save.isPending });

  const reload = async () => {
    const latest = await queryClient.fetchQuery(otQueries.operationNote(booking.id));
    form.reset(toValues(latest, booking));
    save.reset();
  };

  const insertTemplate = () =>
    form.setValue(
      'postOpOrders',
      postOpOrders.trim() ? `${postOpOrders.trimEnd()}\n${POST_OP_TEMPLATE}` : POST_OP_TEMPLATE,
      { shouldDirty: true },
    );

  return (
    <Section
      title="Operation note"
      description={note ? 'Draft — press Ctrl+S to save' : 'Written by the operating surgeon. Press Ctrl+S to save.'}
      actions={
        note && (
          <Button
            icon={<PrintRegular />}
            onClick={() =>
              void printOperationNote(facilityName, booking, note).catch((error: unknown) =>
                notify.error(error, 'Printing failed'),
              )
            }
          >
            Print draft
          </Button>
        )
      }
    >
      <form onSubmit={(event) => event.preventDefault()} noValidate className={styles.stack}>
        {draft.saved && (
          <MessageBar intent="info" icon={<DocumentArrowLeftRegular />}>
            <MessageBarBody>
              Unsaved changes from {formatRelative(new Date(draft.saved.savedAt).toISOString())} were found on this
              computer.
            </MessageBarBody>
            <MessageBarActions>
              <Button onClick={draft.restore}>Restore them</Button>
              <Button appearance="subtle" onClick={draft.discard}>
                Discard
              </Button>
            </MessageBarActions>
          </MessageBar>
        )}
        <FormGrid columns={2}>
          <TextField
            control={form.control}
            name="preOperativeDiagnosis"
            label="Pre-operative diagnosis"
            maxLength={500}
          />
          <TextField
            control={form.control}
            name="postOperativeDiagnosis"
            label="Post-operative diagnosis"
            maxLength={500}
          />
          <FullRow>
            <TextField
              control={form.control}
              name="procedurePerformed"
              label="Procedure performed"
              required
              maxLength={500}
            />
          </FullRow>
          <FullRow>
            <TextAreaField control={form.control} name="findings" label="Findings" rows={3} maxLength={4000} />
          </FullRow>
          <FullRow>
            <TextAreaField
              control={form.control}
              name="procedureDetails"
              label="Procedure details"
              rows={6}
              maxLength={8000}
              placeholder="Position, incision, steps, closure"
            />
          </FullRow>
        </FormGrid>

        <div className={styles.rows}>
          <Text weight="semibold">Specimens</Text>
          {specimens.fields.length === 0 && <Caption1 className={styles.muted}>No specimens sent.</Caption1>}
          {specimens.fields.map((row, index) => (
            <div key={row.id} className={styles.specimen}>
              <TextField
                control={form.control}
                name={`specimens.${index}.description`}
                label="Specimen"
                required
                maxLength={200}
              />
              <SelectField
                control={form.control}
                name={`specimens.${index}.sentTo`}
                label="Sent to"
                options={SPECIMEN_DESTINATIONS.map((d) => ({ value: d, label: specimenLabel[d] }))}
              />
              <TextField control={form.control} name={`specimens.${index}.label`} label="Label" maxLength={64} />
              <Button
                className={styles.removeButton}
                appearance="subtle"
                icon={<DeleteRegular />}
                aria-label={`Remove specimen ${index + 1}`}
                onClick={() => specimens.remove(index)}
              />
            </div>
          ))}
          <div>
            <Button
              icon={<AddRegular />}
              onClick={() => specimens.append({ description: '', sentTo: 'Histopathology', label: '' })}
            >
              Add specimen
            </Button>
          </div>
        </div>

        <FormGrid columns={3}>
          <TextField control={form.control} name="drains" label="Drains" maxLength={500} />
          <NumberField
            control={form.control}
            name="estimatedBloodLossMl"
            label="Estimated blood loss (ml)"
            min={0}
            max={20000}
          />
          <TextField control={form.control} name="complications" label="Complications" maxLength={2000} />
        </FormGrid>

        <div className={styles.rows}>
          <div className={styles.footer} style={{ justifyContent: 'space-between' }}>
            <Text weight="semibold">Post-operative orders</Text>
            <Button size="small" icon={<TextBulletListLtrRegular />} onClick={insertTemplate}>
              Insert order headings
            </Button>
          </div>
          <TextAreaField
            control={form.control}
            name="postOpOrders"
            label="Orders for recovery and the ward"
            rows={8}
            maxLength={4000}
          />
        </div>

        <ActionError error={save.error} onReload={() => void reload()} />

        <div className={styles.footer}>
          <Caption1 className={styles.muted}>
            Signing locks the note. The ward sees the post-op orders as soon as it is saved.
          </Caption1>
          <Button
            icon={save.isPending ? <Spinner size="tiny" /> : <SaveRegular />}
            disabled={save.isPending}
            onClick={() => void submit(false)}
          >
            Save draft
          </Button>
          <Button
            appearance="primary"
            icon={<SignatureRegular />}
            disabled={save.isPending}
            onClick={() => setConfirmSign(true)}
          >
            Sign and lock
          </Button>
        </div>
      </form>

      <ConfirmDialog
        open={confirmSign}
        title="Sign the operation note?"
        confirmLabel="Sign and lock"
        busy={save.isPending}
        onConfirm={() => void submit(true)}
        onCancel={() => setConfirmSign(false)}
      >
        The note is saved with your name and the time and can no longer be changed. Check the procedure, specimens and
        post-operative orders first.
      </ConfirmDialog>
    </Section>
  );
}

function NoteView({ booking, note }: { booking: SurgeryDetail; note: OperationNoteDto }) {
  const styles = useStyles();
  const notify = useNotify();
  const facilityName = useSession((s) => s.user?.facilityName) ?? 'Nana Hospitals';
  return (
    <Section
      title="Operation note"
      actions={
        <>
          {note.signedAt && <WhoWhen prefix="Signed by" by={note.signedByName} at={note.signedAt} locked />}
          <Button
            icon={<PrintRegular />}
            onClick={() =>
              void printOperationNote(facilityName, booking, note).catch((error: unknown) =>
                notify.error(error, 'Printing failed'),
              )
            }
          >
            Print
          </Button>
        </>
      }
    >
      <div className={styles.stack}>
        {note.signedAt ? (
          <MessageBar intent="success" icon={<LockClosedRegular />}>
            <MessageBarBody>
              <MessageBarTitle>Signed and locked</MessageBarTitle>
              This note can no longer be changed.
            </MessageBarBody>
          </MessageBar>
        ) : (
          <MessageBar intent="warning">
            <MessageBarBody>Draft — not signed yet.</MessageBarBody>
          </MessageBar>
        )}
        <DetailGrid minColumnWidth={260}>
          <Detail label="Pre-operative diagnosis">{note.preOperativeDiagnosis}</Detail>
          <Detail label="Post-operative diagnosis">{note.postOperativeDiagnosis}</Detail>
          <Detail label="Procedure performed">{note.procedurePerformed}</Detail>
          <Detail label="Drains">{note.drains}</Detail>
          <Detail label="Estimated blood loss">
            {note.estimatedBloodLossMl !== null ? `${note.estimatedBloodLossMl} ml` : null}
          </Detail>
          <Detail label="Complications">{note.complications}</Detail>
        </DetailGrid>
        <Detail label="Findings">
          <span className={styles.pre}>{note.findings}</span>
        </Detail>
        <Detail label="Procedure details">
          <span className={styles.pre}>{note.procedureDetails}</span>
        </Detail>
        <Detail label="Specimens">
          {note.specimens
            .map((s) => `${s.description} → ${specimenLabel[s.sentTo]}${s.label ? ` (${s.label})` : ''}`)
            .join('; ') || 'None'}
        </Detail>
        <Section title="Post-operative orders">
          <Text className={styles.pre}>{note.postOpOrders ?? 'None written.'}</Text>
        </Section>
      </div>
    </Section>
  );
}
