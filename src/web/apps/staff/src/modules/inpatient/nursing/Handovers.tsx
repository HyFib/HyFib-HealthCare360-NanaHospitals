import {
  Badge,
  Button,
  Caption1,
  Field,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Select,
  Spinner,
  Subtitle2,
  Text,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkRegular, DocumentArrowLeftRegular, PeopleSwapRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateOnly, formatDateTime, formatRelative, isApiError, useHotkey } from '@hc360/core';
import {
  applyServerErrors,
  EmptyState,
  ErrorState,
  LEAVE_KEEPING_DRAFT,
  Section,
  TextAreaField,
  useLeaveGuard,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { useFormDraft } from '@/lib/useFormDraft';
import { nursingApi, nursingKeys, nursingQueries, type HandoverDto } from '../nursingApi';
import { addDays, dutyDate, NURSING_SHIFTS, SHIFT_LABEL, shiftAt, type NursingShift } from '../time';

const useStyles = makeStyles({
  form: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
    padding: '12px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  grid: {
    display: 'grid',
    gap: '10px',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    '@media (max-width: 760px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  list: { display: 'flex', flexDirection: 'column', gap: '10px', marginTop: '16px' },
  card: {
    padding: '12px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  pending: { borderLeft: `4px solid ${tokens.colorPaletteMarigoldBorderActive}` },
  head: {
    display: 'flex',
    gap: '8px',
    alignItems: 'center',
    flexWrap: 'wrap',
    justifyContent: 'space-between',
    marginBottom: '6px',
  },
  sbar: { display: 'grid', gridTemplateColumns: '28px minmax(0, 1fr)', gap: '4px 8px' },
  letter: { fontWeight: tokens.fontWeightBold, color: tokens.colorBrandForeground1 },
  srOnly: {
    position: 'absolute',
    width: '1px',
    height: '1px',
    overflow: 'hidden',
    clip: 'rect(0 0 0 0)',
    whiteSpace: 'nowrap',
  },
  text: { whiteSpace: 'pre-wrap', wordBreak: 'break-word' },
  muted: { color: tokens.colorNeutralForeground3 },
  filters: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'flex-end' },
});

const schema = z.object({
  situation: z.string().trim().min(3, 'Describe the current situation.').max(2000),
  background: z.string().trim().max(2000),
  assessment: z.string().trim().max(2000),
  recommendation: z.string().trim().max(2000),
  pendingTasks: z.string().trim().max(1000),
});

type HandoverValues = z.infer<typeof schema>;

const EMPTY: HandoverValues = { situation: '', background: '', assessment: '', recommendation: '', pendingTasks: '' };

function SbarBody({ h }: { h: HandoverDto }) {
  const styles = useStyles();
  const rows: [string, string, string | null][] = [
    ['S', 'Situation', h.situation],
    ['B', 'Background', h.background],
    ['A', 'Assessment', h.assessment],
    ['R', 'Recommendation', h.recommendation],
  ];
  return (
    <div className={styles.sbar}>
      {rows
        .filter(([, , value]) => value)
        .map(([letter, label, value]) => (
          <div key={letter} style={{ display: 'contents' }}>
            {/* A plain span cannot carry a name (aria-label), so the word itself is there for screen readers. */}
            <span className={styles.letter} title={label}>
              <span aria-hidden>{letter}</span>
              <span className={styles.srOnly}>{label}</span>
            </span>
            <Text className={styles.text}>{value}</Text>
          </div>
        ))}
      {h.pendingTasks && (
        <>
          <span className={styles.letter} aria-hidden>
            ☐
          </span>
          <Text className={styles.text}>
            <strong>Pending:</strong> {h.pendingTasks}
          </Text>
        </>
      )}
    </div>
  );
}

function useAcknowledge(onDone: () => void) {
  const notify = useNotify();
  return useMutation({
    mutationFn: (h: HandoverDto) => nursingApi.acknowledgeHandover(h.id),
    onSuccess: () => {
      notify.success('Handover acknowledged', 'You now have responsibility for this patient.');
      onDone();
    },
    onError: (error) => notify.error(error, 'Could not acknowledge the handover'),
  });
}

function HandoverCard({
  h,
  header,
  canAcknowledge,
  onAcknowledge,
  busy,
}: {
  h: HandoverDto;
  header?: string;
  canAcknowledge: boolean;
  onAcknowledge: () => void;
  busy: boolean;
}) {
  const styles = useStyles();
  return (
    <article className={h.acknowledgedAt ? styles.card : `${styles.card} ${styles.pending}`}>
      <div className={styles.head}>
        <span>
          {header && <Text weight="semibold">{header} · </Text>}
          <Text weight="semibold">{SHIFT_LABEL[h.fromShift]}</Text>{' '}
          <Caption1 className={styles.muted}>
            by {h.handedOverByName ?? 'Unknown'} · {formatDateTime(h.handedOverAt)}
          </Caption1>
        </span>
        {h.acknowledgedAt ? (
          <Badge appearance="tint" color="success" icon={<CheckmarkRegular />}>
            Received by {h.acknowledgedByName ?? 'nurse'} {formatRelative(h.acknowledgedAt)}
          </Badge>
        ) : canAcknowledge ? (
          <Button
            size="small"
            appearance="primary"
            icon={busy ? <Spinner size="tiny" /> : <CheckmarkRegular />}
            disabled={busy}
            onClick={onAcknowledge}
          >
            Acknowledge
          </Button>
        ) : (
          <Badge appearance="tint" color="warning">
            Waiting for the receiving nurse
          </Badge>
        )}
      </div>
      <SbarBody h={h} />
    </article>
  );
}

/** SBAR handover for one patient: hand over at the end of the shift; the receiving nurse acknowledges it. */
export function PatientHandovers({ admissionId, canWrite }: { admissionId: string; canWrite: boolean }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const handovers = useQuery(nursingQueries.handovers(admissionId));
  const form = useForm<HandoverValues>({ resolver: zodResolver(schema), defaultValues: EMPTY });
  const draft = useFormDraft(canWrite ? form : null, `nursing-handover:${admissionId}`);
  const refresh = () => void queryClient.invalidateQueries({ queryKey: nursingKeys.all });
  const acknowledge = useAcknowledge(refresh);

  const add = useMutation({
    mutationFn: (v: HandoverValues) =>
      nursingApi.addHandover(admissionId, {
        situation: v.situation,
        background: v.background || null,
        assessment: v.assessment || null,
        recommendation: v.recommendation || null,
        pendingTasks: v.pendingTasks || null,
      }),
    onSuccess: () => {
      draft.clear();
      form.reset(EMPTY);
      notify.success('Handover given', 'The receiving nurse will acknowledge it.');
      refresh();
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the handover');
      }
    },
  });
  const submit = form.handleSubmit((v) => add.mutate(v));
  useHotkey('mod+enter', () => void submit(), { allowInInputs: true, enabled: canWrite && !add.isPending });

  // Leaving the screen or closing the window with unsaved changes asks first (SRS UX-08).
  useLeaveGuard({ dirty: canWrite && form.formState.isDirty, busy: add.isPending, leaveQuestion: LEAVE_KEEPING_DRAFT });

  return (
    <div>
      {canWrite && (
        <form onSubmit={submit} noValidate className={styles.form}>
          <Subtitle2 as="h3">Hand over this patient (SBAR)</Subtitle2>
          {draft.saved && (
            <MessageBar intent="info" icon={<DocumentArrowLeftRegular />}>
              <MessageBarBody>
                An unsaved handover from {formatRelative(new Date(draft.saved.savedAt).toISOString())} was kept on this
                computer.
              </MessageBarBody>
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
          <div className={styles.grid}>
            <TextAreaField
              control={form.control}
              name="situation"
              label="S — Situation"
              required
              rows={3}
              maxLength={2000}
              placeholder="Current condition and concerns"
            />
            <TextAreaField
              control={form.control}
              name="background"
              label="B — Background"
              rows={3}
              maxLength={2000}
              placeholder="Diagnosis, surgery, relevant history"
            />
            <TextAreaField
              control={form.control}
              name="assessment"
              label="A — Assessment"
              rows={3}
              maxLength={2000}
              placeholder="Vitals trend, risks, lines and drains"
            />
            <TextAreaField
              control={form.control}
              name="recommendation"
              label="R — Recommendation"
              rows={3}
              maxLength={2000}
              placeholder="What the next shift should do or watch"
            />
          </div>
          <TextAreaField
            control={form.control}
            name="pendingTasks"
            label="Pending tasks"
            rows={2}
            maxLength={1000}
            placeholder="e.g. Repeat potassium at 6 am, dressing due"
          />
          <div>
            <Button
              type="submit"
              appearance="primary"
              icon={add.isPending ? <Spinner size="tiny" /> : <PeopleSwapRegular />}
              disabled={add.isPending}
            >
              Give handover (Ctrl+Enter)
            </Button>
          </div>
        </form>
      )}
      {handovers.error ? (
        <ErrorState error={handovers.error} onRetry={() => void handovers.refetch()} />
      ) : handovers.isLoading ? (
        <Spinner size="small" label="Loading handovers…" />
      ) : !handovers.data || handovers.data.length === 0 ? (
        <EmptyState
          icon={<PeopleSwapRegular />}
          title="No handovers yet"
          description="Handovers given at the end of each shift appear here."
        />
      ) : (
        <div className={styles.list}>
          {handovers.data.map((h) => (
            <HandoverCard
              key={h.id}
              h={h}
              canAcknowledge={canWrite}
              busy={acknowledge.isPending && acknowledge.variables?.id === h.id}
              onAcknowledge={() => acknowledge.mutate(h)}
            />
          ))}
        </div>
      )}
    </div>
  );
}

/** The ward's handover sheet for a duty date and shift: read at the start of the shift, acknowledge each patient. */
export function WardHandoverSheet({ wardId, canWrite }: { wardId: string; canWrite: boolean }) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const [now] = useState(() => new Date());
  const [date, setDate] = useState(() => dutyDate(now));
  // The shift being handed over is the one before the current shift.
  const [shift, setShift] = useState<NursingShift | ''>(() => {
    const current = shiftAt(now);
    return current === 'Morning' ? 'Night' : current === 'Evening' ? 'Morning' : 'Evening';
  });
  const sheet = useQuery(nursingQueries.wardHandovers(wardId, date, shift || null));
  const acknowledge = useAcknowledge(() => void queryClient.invalidateQueries({ queryKey: nursingKeys.all }));
  const rows = sheet.data ?? [];
  const waiting = rows.filter((r) => !r.handover.acknowledgedAt).length;
  const today = dutyDate(now);

  return (
    <Section
      title="Handover sheet"
      description={
        sheet.data
          ? `${rows.length} patients handed over${waiting ? `, ${waiting} not yet acknowledged` : ''}`
          : 'SBAR handovers of the ward'
      }
      actions={
        <div className={styles.filters}>
          <Field label="Duty date">
            <Select value={date} onChange={(_, d) => setDate(d.value)}>
              {[0, 1, 2].map((back) => {
                const value = addDays(today, -back);
                return (
                  <option key={value} value={value}>
                    {back === 0 ? 'Today' : back === 1 ? 'Yesterday' : formatDateOnly(value)}
                  </option>
                );
              })}
            </Select>
          </Field>
          <Field label="From shift">
            <Select value={shift} onChange={(_, d) => setShift(d.value as NursingShift | '')}>
              <option value="">All shifts</option>
              {NURSING_SHIFTS.map((s) => (
                <option key={s} value={s}>
                  {SHIFT_LABEL[s]}
                </option>
              ))}
            </Select>
          </Field>
        </div>
      }
    >
      {sheet.error ? (
        <ErrorState error={sheet.error} onRetry={() => void sheet.refetch()} />
      ) : sheet.isLoading ? (
        <Spinner size="small" label="Loading the handover sheet…" />
      ) : rows.length === 0 ? (
        <EmptyState
          icon={<PeopleSwapRegular />}
          title="No handovers for this shift"
          description="Nurses hand over each patient from the patient's Handover tab."
        />
      ) : (
        <div className={styles.list}>
          {rows.map((r) => (
            <HandoverCard
              key={r.handover.id}
              h={r.handover}
              header={`${r.bedLabel ?? '—'} · ${r.patientName}`}
              canAcknowledge={canWrite}
              busy={acknowledge.isPending && acknowledge.variables?.id === r.handover.id}
              onAcknowledge={() => acknowledge.mutate(r.handover)}
            />
          ))}
        </div>
      )}
      {isApiError(acknowledge.error) && <Caption1 className={styles.muted}>{acknowledge.error.userMessage}</Caption1>}
    </Section>
  );
}
