import {
  Badge,
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  Select,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, CheckmarkRegular, TaskListLtrRegular } from '@fluentui/react-icons';
import { formatDateTime } from '@hc360/core';
import { EmptyState, ErrorState, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { SHIFT_LABEL } from '../time';
import {
  CARE_PLAN_PROGRESS_LABEL,
  wardCareApi,
  wardCareKeys,
  wardCareQueries,
  type CarePlanProblemDto,
  type CarePlanProgress,
} from '../wardCareApi';

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: '10px' },
  problem: {
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
  },
  row: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap', justifyContent: 'space-between' },
  add: {
    display: 'flex',
    gap: '8px',
    alignItems: 'flex-end',
    flexWrap: 'wrap',
    padding: '12px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  muted: { color: tokens.colorNeutralForeground3 },
  interventions: { margin: 0, paddingInlineStart: '20px' },
});

const PROGRESS: CarePlanProgress[] = ['Improving', 'Unchanged', 'Worsening', 'GoalMet'];

/**
 * The stay's basic nursing care plan: problems from the ward's short list (or "Other"), each with its goal and
 * interventions, evaluated once a shift and resolved when done. Shown on the handover sheet and the downtime print.
 */
export function CarePlan({ admissionId, canWrite }: { admissionId: string; canWrite: boolean }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const plan = useQuery(wardCareQueries.carePlan(admissionId));
  const templates = useQuery({ ...wardCareQueries.carePlanProblems(), enabled: canWrite });
  const [code, setCode] = useState('');
  const [title, setTitle] = useState('');
  const [goal, setGoal] = useState('');
  const [intervention, setIntervention] = useState('');
  const [frequency, setFrequency] = useState('');

  const refresh = () => void queryClient.invalidateQueries({ queryKey: wardCareKeys.carePlan(admissionId) });
  const add = useMutation({
    mutationFn: () =>
      wardCareApi.addProblem(admissionId, {
        problemCode: code,
        title: code === 'OTHER' ? title : null,
        goal: code === 'OTHER' ? goal : null,
        interventions: code === 'OTHER' ? [{ text: intervention, frequency }] : null,
      }),
    onSuccess: (p) => {
      notify.success(`"${p.title}" added to the care plan`);
      setCode('');
      setTitle('');
      setGoal('');
      setIntervention('');
      setFrequency('');
      refresh();
    },
    onError: (error) => notify.error(error, 'Could not add the problem'),
  });

  const other = code === 'OTHER';
  const canAdd = code !== '' && (!other || (title.trim() && goal.trim() && intervention.trim() && frequency.trim()));
  const active = (plan.data ?? []).filter((p) => p.status === 'Active');
  const resolved = (plan.data ?? []).filter((p) => p.status === 'Resolved');

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      {canWrite && (
        <div className={styles.add} role="group" aria-label="Add a problem to the care plan">
          <Field label="Problem" style={{ minWidth: 240 }}>
            <Select value={code} onChange={(_, d) => setCode(d.value)}>
              <option value="">Choose a problem</option>
              {(templates.data ?? []).map((t) => (
                <option key={t.code} value={t.code}>
                  {t.title}
                </option>
              ))}
              <option value="OTHER">Other…</option>
            </Select>
          </Field>
          {other && (
            <>
              <Field label="Problem name" required>
                <Input value={title} onChange={(_, d) => setTitle(d.value)} maxLength={200} />
              </Field>
              <Field label="Goal" required>
                <Input value={goal} onChange={(_, d) => setGoal(d.value)} maxLength={500} />
              </Field>
              <Field label="Intervention" required>
                <Input value={intervention} onChange={(_, d) => setIntervention(d.value)} maxLength={300} />
              </Field>
              <Field label="How often" required>
                <Input value={frequency} onChange={(_, d) => setFrequency(d.value)} maxLength={100} />
              </Field>
            </>
          )}
          <Button
            appearance="primary"
            icon={add.isPending ? <Spinner size="tiny" aria-hidden /> : <AddRegular />}
            disabled={!canAdd || add.isPending}
            onClick={() => add.mutate()}
          >
            Add to plan
          </Button>
        </div>
      )}
      <Section title="Care plan" description="Evaluate each problem once a shift.">
        {plan.error ? (
          <ErrorState error={plan.error} onRetry={() => void plan.refetch()} />
        ) : plan.isLoading ? (
          <Spinner size="small" label="Loading the care plan" />
        ) : active.length === 0 ? (
          <EmptyState
            icon={<TaskListLtrRegular />}
            title="No active problems"
            description="Add the patient's nursing problems above."
          />
        ) : (
          <div className={styles.list}>
            {active.map((p) => (
              <ProblemCard key={p.id} problem={p} canWrite={canWrite} onChanged={refresh} />
            ))}
          </div>
        )}
      </Section>
      {resolved.length > 0 && (
        <Section title="Resolved">
          <div className={styles.list}>
            {resolved.map((p) => (
              <Caption1 key={p.id}>
                {p.title}: resolved {p.resolvedAt ? formatDateTime(p.resolvedAt) : ''} by {p.resolvedByName ?? '—'}.{' '}
                {p.resolutionNote}
              </Caption1>
            ))}
          </div>
        </Section>
      )}
    </div>
  );
}

function ProblemCard({
  problem: p,
  canWrite,
  onChanged,
}: {
  problem: CarePlanProblemDto;
  canWrite: boolean;
  onChanged: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const [progress, setProgress] = useState<CarePlanProgress>('Improving');
  const [note, setNote] = useState('');
  const evaluate = useMutation({
    mutationFn: () => wardCareApi.evaluate(p.id, { progress, note: note.trim() || null }),
    onSuccess: () => {
      notify.success('Evaluation saved');
      setNote('');
      onChanged();
    },
    onError: (error) => notify.error(error, 'Could not save the evaluation'),
  });
  const resolve = useMutation({
    mutationFn: () => wardCareApi.resolve(p.id, { note: note.trim() || 'Goal met', version: p.version }),
    onSuccess: () => {
      notify.success(`"${p.title}" resolved`);
      onChanged();
    },
    onError: (error) => notify.error(error, 'Could not resolve the problem'),
  });
  return (
    <div className={styles.problem}>
      <div className={styles.row}>
        <Text weight="semibold">{p.title}</Text>
        {p.evaluatedThisShift ? (
          <Badge appearance="tint" color="success" icon={<CheckmarkRegular />}>
            Evaluated this shift
          </Badge>
        ) : (
          <Badge appearance="tint" color="warning">
            Due this shift
          </Badge>
        )}
      </div>
      <Caption1>Goal: {p.goal}</Caption1>
      <ul className={styles.interventions}>
        {p.interventions.map((i) => (
          <li key={`${i.text}-${i.frequency}`}>
            <Caption1>
              {i.text} — {i.frequency}
            </Caption1>
          </li>
        ))}
      </ul>
      {p.lastEvaluation && (
        <Caption1 className={styles.muted}>
          Last evaluated {SHIFT_LABEL[p.lastEvaluation.shift].toLowerCase()}{' '}
          {formatDateTime(p.lastEvaluation.evaluatedAt)} by {p.lastEvaluation.evaluatedByName ?? '—'}:{' '}
          {CARE_PLAN_PROGRESS_LABEL[p.lastEvaluation.progress]}
          {p.lastEvaluation.note ? `. ${p.lastEvaluation.note}` : ''}
        </Caption1>
      )}
      {canWrite && (
        <div className={styles.row} style={{ justifyContent: 'flex-start' }}>
          <Field label="Progress">
            <Select value={progress} onChange={(_, d) => setProgress(d.value as CarePlanProgress)}>
              {PROGRESS.map((g) => (
                <option key={g} value={g}>
                  {CARE_PLAN_PROGRESS_LABEL[g]}
                </option>
              ))}
            </Select>
          </Field>
          <Field label="Note" style={{ flex: 1, minWidth: 200 }}>
            <Input value={note} onChange={(_, d) => setNote(d.value)} maxLength={500} />
          </Field>
          <Button disabled={p.evaluatedThisShift || evaluate.isPending} onClick={() => evaluate.mutate()}>
            Evaluate
          </Button>
          <Button appearance="subtle" disabled={resolve.isPending} onClick={() => resolve.mutate()}>
            Resolve
          </Button>
        </div>
      )}
    </div>
  );
}

/** The active problems as one line each, for the handover sheet. */
export function CarePlanSummary({ problems }: { problems: CarePlanProblemDto[] }) {
  if (problems.length === 0) {
    return null;
  }
  return (
    <Caption1 block>
      Care plan:{' '}
      {problems
        .map(
          (p) =>
            `${p.title} (${p.lastEvaluation ? CARE_PLAN_PROGRESS_LABEL[p.lastEvaluation.progress].toLowerCase() : 'not evaluated'}${
              p.evaluatedThisShift ? '' : ', due this shift'
            })`,
        )
        .join('; ')}
    </Caption1>
  );
}
