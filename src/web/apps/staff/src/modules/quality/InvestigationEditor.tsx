import {
  Badge,
  Button,
  Caption1,
  Checkbox,
  Field,
  Input,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Select,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, CheckmarkCircleRegular, DeleteRegular, LockOpenRegular, SaveRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateTime, isApiError } from '@hc360/core';
import {
  applyServerErrors,
  ChoiceField,
  ConfirmDialog,
  EmptyState,
  ErrorState,
  LoadingState,
  Section,
  TextAreaField,
  useNotify,
} from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useFieldArray, useForm, useWatch, type Control } from 'react-hook-form';
import { z } from 'zod';
import { Detail, DetailGrid } from '@/modules/hr/shared/DetailGrid';
import { UserPicker } from '@/modules/hr/shared/UserPicker';
import type { PickedUser } from '@/modules/hr/shared/users';
import { qualityApi, qualityKeys, qualityQueries, type InvestigationDto, type PersonRef } from './api';
import { FACTOR_CATEGORIES, rcaMethodLabel, type ContributingFactorCategory } from './labels';
import { investigationGaps } from './rules';

const schema = z.object({
  method: z.enum(['FiveWhys', 'Fishbone']),
  problemStatement: z.string().trim().min(1, 'State the problem in one or two sentences.').max(2000),
  whys: z.array(z.object({ value: z.string().max(500) })).max(10),
  factors: z
    .array(
      z.object({ category: z.enum(FACTOR_CATEGORIES), description: z.string().max(500), isRootCause: z.boolean() }),
    )
    .max(50),
  findings: z.string().trim().max(4000),
  recommendations: z.string().trim().max(4000),
  investigators: z.array(z.object({ id: z.string(), name: z.string() })).max(10),
});

type Values = z.infer<typeof schema>;

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: '8px' },
  row: { display: 'flex', gap: '8px', alignItems: 'flex-end', flexWrap: 'wrap' },
  grow: { flex: '1 1 280px' },
  bones: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))', gap: '10px' },
  bone: {
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  head: {
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorBrandBackground2,
    borderLeft: `4px solid ${tokens.colorBrandStroke1}`,
  },
  cause: { display: 'block', marginTop: '4px' },
  root: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
  muted: { color: tokens.colorNeutralForeground3 },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap', justifyContent: 'flex-end' },
});

function toValues(inv: InvestigationDto | null): Values {
  return {
    method: inv?.method ?? 'FiveWhys',
    problemStatement: inv?.problemStatement ?? '',
    whys: (inv?.whys.length ? inv.whys : ['', '', '']).map((value) => ({ value })),
    factors:
      inv?.factors.map((f) => ({ category: f.category, description: f.description, isRootCause: f.isRootCause })) ?? [],
    findings: inv?.findings ?? '',
    recommendations: inv?.recommendations ?? '',
    investigators: inv?.investigators ?? [],
  };
}

/**
 * Root-cause analysis of an incident: 5 whys or a fishbone of contributing factors, findings and recommendations. The
 * first save moves the incident to "Investigation"; completing needs the NABH minimum (problem, findings, a root
 * cause and 3 whys or 2 factors).
 */
export function InvestigationEditor({ incidentId, canEdit }: { incidentId: string; canEdit: boolean }) {
  const investigation = useQuery(qualityQueries.investigation(incidentId));
  const [starting, setStarting] = useState(false);
  const notStarted = isApiError(investigation.error) && investigation.error.status === 404;

  if (investigation.isLoading) {
    return <LoadingState />;
  }
  if (investigation.error && !notStarted) {
    return <ErrorState error={investigation.error} onRetry={() => void investigation.refetch()} />;
  }
  if (notStarted && !starting) {
    return (
      <EmptyState
        title="No root-cause analysis yet"
        description={
          canEdit
            ? 'Start the analysis to find out why it happened. Saving it moves the incident to "Investigation".'
            : 'The quality team has not started the analysis.'
        }
        action={
          canEdit ? (
            <Button appearance="primary" onClick={() => setStarting(true)}>
              Start analysis
            </Button>
          ) : undefined
        }
      />
    );
  }

  const data = investigation.data ?? null;
  if (data?.status === 'Completed') {
    return <CompletedView investigation={data} incidentId={incidentId} canEdit={canEdit} />;
  }
  return (
    <EditorForm
      key={data ? `${data.id}:${data.version}` : 'new'}
      incidentId={incidentId}
      investigation={data}
      canEdit={canEdit}
    />
  );
}

function EditorForm({
  incidentId,
  investigation,
  canEdit,
}: {
  incidentId: string;
  investigation: InvestigationDto | null;
  canEdit: boolean;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [busy, setBusy] = useState<'save' | 'complete' | null>(null);
  const form = useForm<Values>({ resolver: zodResolver(schema), defaultValues: toValues(investigation) });
  const whys = useFieldArray({ control: form.control, name: 'whys' });
  const factors = useFieldArray({ control: form.control, name: 'factors' });
  const [method, problemStatement, findings, whyValues, factorValues] = useWatch({
    control: form.control,
    name: ['method', 'problemStatement', 'findings', 'whys', 'factors'],
  });
  const gaps = investigationGaps({
    method,
    problemStatement,
    findings,
    whys: whyValues.map((w) => w.value),
    factors: factorValues,
  });
  const dirty = form.formState.isDirty;

  const save = async (values: Values): Promise<boolean> => {
    setBusy('save');
    try {
      await qualityApi.saveInvestigation(incidentId, {
        method: values.method,
        problemStatement: values.problemStatement.trim(),
        whys: values.whys.map((w) => w.value.trim()).filter(Boolean),
        factors: values.factors
          .filter((f) => f.description.trim())
          .map((f) => ({ ...f, description: f.description.trim() })),
        findings: values.findings.trim() || null,
        recommendations: values.recommendations.trim() || null,
        investigatorUserIds: values.investigators.map((i) => i.id),
      });
      notify.success('Analysis saved');
      void queryClient.invalidateQueries({ queryKey: qualityKeys.incidents });
      return true;
    } catch (error) {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the analysis');
      }
      return false;
    } finally {
      setBusy(null);
    }
  };

  const complete = async () => {
    const valid = await form.trigger();
    if (!valid) {
      return;
    }
    if (dirty || !investigation) {
      const saved = await save(form.getValues());
      if (!saved) {
        return;
      }
    }
    setBusy('complete');
    try {
      await qualityApi.completeInvestigation(incidentId);
      notify.success('Analysis complete', 'The incident can now move to actions or be closed.');
      void queryClient.invalidateQueries({ queryKey: qualityKeys.incidents });
    } catch (error) {
      notify.error(error, 'Could not complete the analysis');
    } finally {
      setBusy(null);
    }
  };

  return (
    <form
      onSubmit={form.handleSubmit((values) => void save(values))}
      noValidate
      style={{ display: 'flex', flexDirection: 'column', gap: 16 }}
    >
      <ChoiceField
        control={form.control}
        name="method"
        label="Method"
        disabled={!canEdit}
        options={[
          { value: 'FiveWhys', label: rcaMethodLabel.FiveWhys },
          { value: 'Fishbone', label: rcaMethodLabel.Fishbone },
        ]}
      />
      <TextAreaField
        control={form.control}
        name="problemStatement"
        label="Problem statement"
        required
        rows={2}
        maxLength={2000}
        disabled={!canEdit}
        placeholder="What went wrong, for whom, where and when"
      />

      {method === 'FiveWhys' && (
        <Section
          title="5 whys"
          description="Ask 'why?' until you reach a cause the hospital can fix (at least 3 times)."
        >
          <div className={styles.list}>
            {whys.fields.map((field, index) => (
              <div key={field.id} className={styles.row}>
                <Controller
                  control={form.control}
                  name={`whys.${index}.value`}
                  render={({ field: input }) => (
                    <Field label={`Why ${index + 1}?`} className={styles.grow}>
                      <Input
                        value={input.value}
                        onChange={(_, d) => input.onChange(d.value)}
                        onBlur={input.onBlur}
                        disabled={!canEdit}
                        maxLength={500}
                      />
                    </Field>
                  )}
                />
                {canEdit && whys.fields.length > 1 && (
                  <Button
                    appearance="subtle"
                    icon={<DeleteRegular />}
                    aria-label={`Remove why ${index + 1}`}
                    onClick={() => whys.remove(index)}
                  />
                )}
              </div>
            ))}
          </div>
          {canEdit && whys.fields.length < 10 && (
            <Button icon={<AddRegular />} style={{ marginTop: 8 }} onClick={() => whys.append({ value: '' })}>
              Ask why again
            </Button>
          )}
        </Section>
      )}

      <Section
        title={method === 'Fishbone' ? 'Fishbone: contributing factors' : 'Contributing factors'}
        description="Group what contributed by category and mark the root cause(s) the actions must fix."
      >
        <div className={styles.list}>
          {factors.fields.map((field, index) => (
            <FactorRow
              key={field.id}
              control={form.control}
              index={index}
              canEdit={canEdit}
              onRemove={() => factors.remove(index)}
            />
          ))}
          {factors.fields.length === 0 && (
            <Caption1 className={styles.muted}>No contributing factors recorded yet.</Caption1>
          )}
        </div>
        {canEdit && factors.fields.length < 50 && (
          <Button
            icon={<AddRegular />}
            style={{ marginTop: 8 }}
            onClick={() => factors.append({ category: 'Process', description: '', isRootCause: false })}
          >
            Add factor
          </Button>
        )}
        {method === 'Fishbone' && factorValues.some((f) => f.description.trim()) && (
          <FishboneView problem={problemStatement} factors={factorValues} />
        )}
      </Section>

      <TextAreaField
        control={form.control}
        name="findings"
        label="Findings"
        rows={3}
        maxLength={4000}
        disabled={!canEdit}
        placeholder="What the analysis showed"
      />
      <TextAreaField
        control={form.control}
        name="recommendations"
        label="Recommendations"
        rows={3}
        maxLength={4000}
        disabled={!canEdit}
        placeholder="What should change; plan them as actions (CAPA)"
      />
      <InvestigatorsField control={form.control} canEdit={canEdit} />

      {canEdit && gaps.length > 0 && (
        <MessageBar intent="info" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Before the analysis can be completed</MessageBarTitle>
            <ul style={{ margin: '4px 0 0', paddingLeft: 18 }}>
              {gaps.map((gap) => (
                <li key={gap}>{gap}</li>
              ))}
            </ul>
          </MessageBarBody>
        </MessageBar>
      )}

      {canEdit && (
        <div className={styles.actions}>
          <Button
            type="submit"
            icon={busy === 'save' ? <Spinner size="tiny" /> : <SaveRegular />}
            disabled={busy !== null || (!dirty && Boolean(investigation))}
          >
            Save analysis
          </Button>
          <Button
            appearance="primary"
            icon={busy === 'complete' ? <Spinner size="tiny" /> : <CheckmarkCircleRegular />}
            disabled={busy !== null || gaps.length > 0}
            onClick={() => void complete()}
          >
            Complete analysis
          </Button>
        </div>
      )}
    </form>
  );
}

function FactorRow({
  control,
  index,
  canEdit,
  onRemove,
}: {
  control: Control<Values>;
  index: number;
  canEdit: boolean;
  onRemove: () => void;
}) {
  const styles = useStyles();
  return (
    <div className={styles.row}>
      <Controller
        control={control}
        name={`factors.${index}.category`}
        render={({ field }) => (
          <Field label="Category">
            <Select
              value={field.value}
              onChange={(_, d) => field.onChange(d.value as ContributingFactorCategory)}
              disabled={!canEdit}
            >
              {FACTOR_CATEGORIES.map((c) => (
                <option key={c} value={c}>
                  {c}
                </option>
              ))}
            </Select>
          </Field>
        )}
      />
      <Controller
        control={control}
        name={`factors.${index}.description`}
        render={({ field }) => (
          <Field label="What contributed" className={styles.grow}>
            <Input
              value={field.value}
              onChange={(_, d) => field.onChange(d.value)}
              onBlur={field.onBlur}
              disabled={!canEdit}
              maxLength={500}
            />
          </Field>
        )}
      />
      <Controller
        control={control}
        name={`factors.${index}.isRootCause`}
        render={({ field }) => (
          <Checkbox
            checked={field.value}
            onChange={(_, d) => field.onChange(Boolean(d.checked))}
            disabled={!canEdit}
            label="Root cause"
          />
        )}
      />
      {canEdit && <Button appearance="subtle" icon={<DeleteRegular />} aria-label="Remove factor" onClick={onRemove} />}
    </div>
  );
}

/** Cause-and-effect view: the problem as the head, one bone per category, root causes in red. */
function FishboneView({
  problem,
  factors,
}: {
  problem: string;
  factors: { category: ContributingFactorCategory; description: string; isRootCause: boolean }[];
}) {
  const styles = useStyles();
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10, marginTop: 16 }} aria-label="Fishbone diagram">
      <div className={styles.head}>
        <Caption1 className={styles.muted}>Problem</Caption1>
        <Text block weight="semibold">
          {problem.trim() || 'State the problem above'}
        </Text>
      </div>
      <div className={styles.bones}>
        {FACTOR_CATEGORIES.map((category) => {
          const items = factors.filter((f) => f.category === category && f.description.trim());
          return (
            <div key={category} className={styles.bone}>
              <Text weight="semibold">{category}</Text>
              {items.length === 0 ? (
                <Caption1 className={mergeClasses(styles.cause, styles.muted)}>Nothing recorded</Caption1>
              ) : (
                items.map((item, index) => (
                  <Caption1 key={index} className={mergeClasses(styles.cause, item.isRootCause && styles.root)}>
                    {item.isRootCause ? 'Root cause: ' : '• '}
                    {item.description}
                  </Caption1>
                ))
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}

function InvestigatorsField({ control, canEdit }: { control: Control<Values>; canEdit: boolean }) {
  const [picking, setPicking] = useState<PickedUser | null>(null);
  return (
    <Controller
      control={control}
      name="investigators"
      render={({ field }) => (
        <Field label="Investigation team">
          <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', marginBottom: 6 }}>
            {field.value.length === 0 && <Caption1>Nobody named yet.</Caption1>}
            {field.value.map((person: PersonRef) => (
              <Badge key={person.id} appearance="tint" size="large">
                {person.name}
                {canEdit && (
                  <Button
                    size="small"
                    appearance="transparent"
                    icon={<DeleteRegular />}
                    aria-label={`Remove ${person.name}`}
                    onClick={() => field.onChange(field.value.filter((p: PersonRef) => p.id !== person.id))}
                  />
                )}
              </Badge>
            ))}
          </div>
          {canEdit && field.value.length < 10 && (
            <UserPicker
              label="Add a team member"
              value={picking}
              onChange={(user) => {
                if (user && !field.value.some((p: PersonRef) => p.id === user.id)) {
                  field.onChange([...field.value, user]);
                }
                setPicking(null);
              }}
            />
          )}
        </Field>
      )}
    />
  );
}

function CompletedView({
  investigation: inv,
  incidentId,
  canEdit,
}: {
  investigation: InvestigationDto;
  incidentId: string;
  canEdit: boolean;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [reopening, setReopening] = useState(false);
  const [busy, setBusy] = useState(false);

  const reopen = async () => {
    setBusy(true);
    try {
      await qualityApi.reopenInvestigation(incidentId);
      notify.success('Analysis reopened');
      setReopening(false);
      void queryClient.invalidateQueries({ queryKey: qualityKeys.incidents });
    } catch (error) {
      notify.error(error, 'Could not reopen the analysis');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      <MessageBar intent="success">
        <MessageBarBody>
          <MessageBarTitle>Analysis complete</MessageBarTitle>
          {rcaMethodLabel[inv.method]} · completed {formatDateTime(inv.completedAt)}
        </MessageBarBody>
      </MessageBar>
      <DetailGrid>
        <Detail label="Problem statement" wide>
          {inv.problemStatement}
        </Detail>
        {inv.method === 'FiveWhys' && (
          <Detail label="5 whys" wide>
            <ol style={{ margin: 0, paddingLeft: 18 }}>
              {inv.whys.map((why, index) => (
                <li key={index}>{why}</li>
              ))}
            </ol>
          </Detail>
        )}
        <Detail label="Findings" wide>
          {inv.findings}
        </Detail>
        <Detail label="Recommendations" wide>
          {inv.recommendations}
        </Detail>
        <Detail label="Investigation team">{inv.investigators.map((p) => p.name).join(', ')}</Detail>
      </DetailGrid>
      <FishboneView problem={inv.problemStatement} factors={inv.factors} />
      {canEdit && (
        <div className={styles.actions}>
          <Button icon={<LockOpenRegular />} onClick={() => setReopening(true)}>
            Reopen analysis
          </Button>
        </div>
      )}
      <ConfirmDialog
        open={reopening}
        title="Reopen the analysis?"
        confirmLabel="Reopen"
        busy={busy}
        onConfirm={() => void reopen()}
        onCancel={() => setReopening(false)}
      >
        The analysis becomes editable again. The incident cannot leave "Investigation" until it is completed again.
      </ConfirmDialog>
    </div>
  );
}
