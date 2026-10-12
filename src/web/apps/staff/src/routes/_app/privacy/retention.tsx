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
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import {
  ArchiveRegular,
  BroomRegular,
  CalendarClockRegular,
  EditRegular,
  EyeRegular,
  LockClosedRegular,
} from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateTime } from '@hc360/core';
import { applyServerErrors, DataTable, ErrorState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { z } from 'zod';
import { usePermission } from '@/lib/permissions';
import { EditorDrawer } from '@/modules/hr/shared/EditorDrawer';
import { Columns, Stack } from '@/modules/hr/shared/layout';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { privacyApi, privacyKeys, privacyQueries, type RetentionRuleDto } from '@/modules/privacy/api';
import { RetentionActionBadge } from '@/modules/privacy/badges';
import { PRIVACY_PERMISSIONS as P, retentionKeyName } from '@/modules/privacy/labels';
import { PlainTable } from '@/modules/privacy/PlainTable';
import {
  isMedicalRecordsRule,
  keptUntilText,
  retentionFixedReason,
  retentionPeriodText,
  retentionRange,
  retentionRunSource,
  retentionValueError,
} from '@/modules/privacy/rules';

export const Route = createFileRoute('/_app/privacy/retention')({
  component: () => (
    <RequirePermission permission={P.retentionManage} what="the retention schedule">
      <RetentionPage />
    </RequirePermission>
  ),
});

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  fixed: { display: 'flex', alignItems: 'flex-start', gap: '4px', color: tokens.colorNeutralForeground3 },
  danger: { color: tokens.colorPaletteRedForeground1 },
  facts: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(200px, 1fr))', gap: '12px' },
  fact: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    padding: '12px 14px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  factValue: {
    fontSize: tokens.fontSizeHero700,
    lineHeight: tokens.lineHeightHero700,
    fontWeight: tokens.fontWeightSemibold,
  },
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  pad: { padding: '0 16px 12px' },
});

type CleanupMode = 'preview' | 'run';

/** The retention schedule (SRS PRV-02): how long each kind of data is kept, the clean-up, and what waits for review. */
function RetentionPage() {
  const styles = useStyles();
  const rules = useQuery(privacyQueries.retentionRules());
  const runs = useQuery(privacyQueries.retentionRuns());
  const [editing, setEditing] = useState<RetentionRuleDto | null>(null);
  const [cleanup, setCleanup] = useState<CleanupMode | null>(null);

  return (
    <Page>
      <PageHeader
        title="Retention"
        subtitle="How long each kind of data is kept, and what happens after. Medical records are never deleted automatically: the medical records team reviews them."
        icon={<CalendarClockRegular />}
        actions={
          <>
            <Button icon={<EyeRegular />} onClick={() => setCleanup('preview')}>
              Preview clean-up
            </Button>
            <Button appearance="primary" icon={<BroomRegular />} onClick={() => setCleanup('run')}>
              Run clean-up now
            </Button>
          </>
        }
      />

      <Section
        title="How long data is kept"
        description="The periods can be made longer, never shorter than the law or hospital policy allows. The clean-up runs every night after 2 am."
        flush
      >
        {rules.error ? (
          <div className={styles.pad}>
            <ErrorState error={rules.error} onRetry={() => void rules.refetch()} />
          </div>
        ) : !rules.data ? (
          <div className={styles.pad}>
            <Spinner size="small" label="Loading the schedule…" labelPosition="after" />
          </div>
        ) : (
          <PlainTable
            ariaLabel="Retention schedule"
            rows={rules.data}
            getRowId={(r) => r.key}
            columns={[
              {
                key: 'what',
                header: 'What',
                width: '30%',
                render: (r) => (
                  <span>
                    <Text weight="semibold">{r.title}</Text>
                    <Caption1 block className={styles.muted}>
                      {r.description}
                    </Caption1>
                  </span>
                ),
              },
              {
                key: 'period',
                header: 'Kept for',
                width: 200,
                render: (r) => {
                  const fixed = retentionFixedReason(r);
                  return (
                    <span>
                      <Text weight="semibold">{retentionPeriodText(r.value, r.unit)}</Text>
                      {fixed ? (
                        <Caption1 block className={styles.fixed}>
                          <LockClosedRegular aria-hidden /> {fixed}
                        </Caption1>
                      ) : (
                        r.defaultValue !== null &&
                        r.value !== r.defaultValue && (
                          <Caption1 block className={styles.muted}>
                            Default {retentionPeriodText(r.defaultValue, r.unit)}
                          </Caption1>
                        )
                      )}
                    </span>
                  );
                },
              },
              {
                key: 'action',
                header: 'After that',
                width: 190,
                render: (r) => <RetentionActionBadge action={r.action} />,
              },
              { key: 'basis', header: 'Why', priority: 'low', render: (r) => <Caption1>{r.basis}</Caption1> },
              {
                key: 'last',
                header: 'Last clean-up',
                width: 150,
                priority: 'low',
                render: (r) =>
                  r.lastRemoved !== null ? (
                    <span>
                      <Text>{r.lastRemoved.toLocaleString('en-IN')} removed</Text>
                      {r.lastRunAt && (
                        <Caption1 block className={styles.muted}>
                          {formatDateTime(r.lastRunAt)}
                        </Caption1>
                      )}
                    </span>
                  ) : (
                    <Caption1 className={styles.muted}>{r.action === 'Delete' ? 'Not run yet' : '—'}</Caption1>
                  ),
              },
              {
                key: 'change',
                header: <span className={styles.muted}>Change</span>,
                width: 110,
                render: (r) => {
                  const fixed = retentionFixedReason(r);
                  const button = (
                    <Button
                      size="small"
                      icon={<EditRegular />}
                      disabledFocusable={Boolean(fixed)}
                      onClick={() => !fixed && setEditing(r)}
                      aria-label={`Change the period for ${r.title.toLowerCase()}`}
                    >
                      Change
                    </Button>
                  );
                  return fixed ? (
                    <Tooltip content={fixed} relationship="description">
                      {button}
                    </Tooltip>
                  ) : (
                    button
                  );
                },
              },
            ]}
          />
        )}
      </Section>

      <Columns>
        <ReviewSection />
        <Section title="Recent clean-ups" description="Nightly runs, and those started from here." flush>
          <DataTable
            ariaLabel="Recent clean-ups"
            rows={runs.data}
            loading={runs.isLoading}
            error={runs.error}
            onRetry={() => void runs.refetch()}
            getRowId={(run) => run.id}
            size="small"
            columns={[
              {
                key: 'when',
                header: 'When',
                render: (run) => (
                  <span>
                    <Text>{formatDateTime(run.startedAt)}</Text>
                    <Caption1 block className={styles.muted}>
                      {retentionRunSource(run.trigger, run.startedByName)}
                    </Caption1>
                  </span>
                ),
              },
              {
                key: 'removed',
                header: 'Removed',
                render: (run) => {
                  const parts = run.results.filter((r) => r.removed > 0);
                  return (
                    <span>
                      <Text weight="semibold">{run.totalRemoved.toLocaleString('en-IN')}</Text>
                      <Caption1 block className={styles.muted}>
                        {parts.length === 0
                          ? 'Nothing was past its period'
                          : parts
                              .map((r) => `${retentionKeyName(r.key)} ${r.removed.toLocaleString('en-IN')}`)
                              .join(' · ')}
                      </Caption1>
                    </span>
                  );
                },
              },
              {
                key: 'result',
                header: 'Result',
                render: (run) =>
                  run.error ? (
                    <Tooltip content={run.error} relationship="description">
                      <Text className={styles.danger}>Stopped early</Text>
                    </Tooltip>
                  ) : run.finishedAt ? (
                    <Text>Finished</Text>
                  ) : (
                    <Spinner size="extra-tiny" label="Running" labelPosition="after" />
                  ),
              },
            ]}
            emptyTitle="No clean-up has run yet"
            emptyDescription="The first one runs tonight after 2 am, or now with “Run clean-up now”."
          />
        </Section>
      </Columns>

      {editing && <RetentionDrawer rule={editing} onClose={() => setEditing(null)} />}
      {cleanup && <CleanupDialog mode={cleanup} onClose={() => setCleanup(null)} />}
    </Page>
  );
}

function ReviewSection() {
  const styles = useStyles();
  const navigate = useNavigate();
  const canReviewCharts = usePermission('mrd.records');
  const canOpenRequests = usePermission(P.requestsRead);
  const review = useQuery(privacyQueries.retentionReview());
  const r = review.data;

  return (
    <Section title="Waiting for review" description="What is past its period but is never deleted automatically.">
      {review.error ? (
        <ErrorState error={review.error} onRetry={() => void review.refetch()} />
      ) : !r ? (
        <Spinner size="small" label="Loading…" labelPosition="after" />
      ) : (
        <Stack>
          <div className={styles.facts}>
            <div className={styles.fact}>
              <span className={styles.factValue}>{r.outpatientChartsDue.toLocaleString('en-IN')}</span>
              <Text>Out-patient charts past their retention</Text>
            </div>
            <div className={styles.fact}>
              <span className={styles.factValue}>{r.inpatientChartsDue.toLocaleString('en-IN')}</span>
              <Text>In-patient charts past their retention</Text>
            </div>
          </div>
          {canReviewCharts ? (
            <div>
              <Button
                icon={<ArchiveRegular />}
                onClick={() => void navigate({ to: '/mrd/records', search: { retentionDue: true } })}
              >
                Review the charts in medical records
              </Button>
            </div>
          ) : (
            <Caption1 className={styles.muted}>
              The medical records team reviews these under Medical records, Patient charts. Medico-legal charts are
              never listed.
            </Caption1>
          )}
          <Text weight="semibold">Erasure requests with records kept by law</Text>
          <DataTable
            ariaLabel="Erasure requests with records kept by law"
            rows={r.erasureHolds}
            getRowId={(h) => h.requestId}
            size="small"
            onRowClick={
              canOpenRequests
                ? (h) => void navigate({ to: '/privacy/requests/$requestId', params: { requestId: h.requestId } })
                : undefined
            }
            columns={[
              {
                key: 'request',
                header: 'Request',
                width: 150,
                render: (h) => <Text font="monospace">{h.requestNumber}</Text>,
              },
              {
                key: 'patient',
                header: 'Patient',
                render: (h) => (
                  <span>
                    <Text>{h.principalName}</Text>
                    <Caption1 block className={styles.muted}>
                      {h.uhid ?? 'Not a registered patient'}
                    </Caption1>
                  </span>
                ),
              },
              { key: 'kept', header: 'Kept', render: (h) => keptUntilText(h.retainedUntil, h.retainedPermanently) },
              {
                key: 'status',
                header: 'Review',
                width: 140,
                render: (h) =>
                  h.isDue ? (
                    <Badge appearance="tint" color="warning">
                      Due for review
                    </Badge>
                  ) : (
                    <Caption1 className={styles.muted}>Not yet</Caption1>
                  ),
              },
            ]}
            emptyTitle="No erasure requests are waiting"
            emptyDescription="When the law requires records to be kept after an erasure request, the request is listed here until they can go."
          />
        </Stack>
      )}
    </Section>
  );
}

function RetentionDrawer({ rule, onClose }: { rule: RetentionRuleDto; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const range = retentionRange(rule);
  const unit = rule.unit === 'Years' ? 'years' : 'days';
  const [schema] = useState(() =>
    z.object({ value: z.number({ error: 'Enter a whole number.' }).nullable() }).superRefine((v, ctx) => {
      const error = retentionValueError(rule, v.value);
      if (error) {
        ctx.addIssue({ code: 'custom', path: ['value'], message: error });
      }
    }),
  );
  const form = useForm<{ value: number | null }>({
    resolver: zodResolver(schema),
    defaultValues: { value: rule.value ?? rule.defaultValue },
  });
  const save = useMutation({
    mutationFn: (value: number) => privacyApi.updateRetention(rule.key, value),
    onSuccess: () => {
      notify.success(
        'Retention period saved',
        isMedicalRecordsRule(rule.key) ? 'The retention dates of completed charts have moved to match.' : undefined,
      );
      void queryClient.invalidateQueries({ queryKey: privacyKeys.retention });
      onClose();
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the period');
      }
    },
  });

  return (
    <EditorDrawer
      open
      title={`How long to keep: ${rule.title.toLowerCase()}`}
      dirty={form.formState.isDirty}
      onClose={onClose}
      onSubmit={form.handleSubmit((v) => v.value !== null && save.mutate(v.value))}
      busy={save.isPending}
    >
      <Text>{rule.description}</Text>
      <Controller
        control={form.control}
        name="value"
        render={({ field, fieldState }) => (
          <Field
            label={`Kept for (${unit})`}
            required
            hint={`${range.text}.${rule.defaultValue !== null ? ` The default is ${retentionPeriodText(rule.defaultValue, rule.unit)}.` : ''}`}
            validationMessage={fieldState.error?.message}
            validationState={fieldState.error ? 'error' : 'none'}
          >
            <Input
              type="number"
              name={field.name}
              ref={field.ref}
              onBlur={field.onBlur}
              value={field.value === null || Number.isNaN(field.value) ? '' : String(field.value)}
              onChange={(_, d) => field.onChange(d.value === '' ? null : Number(d.value))}
              min={range.min}
              max={range.max}
              autoFocus
            />
          </Field>
        )}
      />
      <Caption1>{rule.basis}</Caption1>
      {isMedicalRecordsRule(rule.key) ? (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Completed charts move with this</MessageBarTitle>
            Saving moves the retention date of every completed{' '}
            {rule.key === 'records.inpatient' ? 'in-patient' : 'out-patient'} chart to match, straight away. Check the
            state’s rules and NABH with the medical records committee first.
          </MessageBarBody>
        </MessageBar>
      ) : (
        rule.action === 'Delete' && (
          <MessageBar intent="info" layout="multiline">
            <MessageBarBody>
              A shorter period means the next clean-up deletes more. Deleted records cannot be brought back.
            </MessageBarBody>
          </MessageBar>
        )
      )}
    </EditorDrawer>
  );
}

function CleanupDialog({ mode, onClose }: { mode: CleanupMode; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const preview = useQuery(privacyQueries.retentionPreview());
  const run = useMutation({
    mutationFn: () => privacyApi.runRetention(),
    onSuccess: () => {
      notify.success(
        'The clean-up has started and runs in the background',
        'Recent clean-ups shows what it removed when it finishes, usually within minutes.',
      );
      void queryClient.invalidateQueries({ queryKey: privacyKeys.retention });
      onClose();
    },
    onError: (error) => notify.error(error, 'Could not start the clean-up'),
  });
  const total = (preview.data ?? []).reduce((sum, r) => sum + r.removed, 0);

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !run.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 620 }}>
        <DialogBody>
          <DialogTitle>{mode === 'run' ? 'Run the clean-up now?' : 'What a clean-up would remove now'}</DialogTitle>
          <DialogContent className={styles.stack}>
            <Text>
              The clean-up deletes the records below that are past their period. Pending messages are never touched, and
              medical records, bills and the audit trail are kept.
            </Text>
            {preview.error ? (
              <ErrorState error={preview.error} onRetry={() => void preview.refetch()} />
            ) : !preview.data ? (
              <Spinner size="small" label="Counting…" labelPosition="after" />
            ) : preview.data.length === 0 || total === 0 ? (
              <MessageBar intent="success">
                <MessageBarBody>Nothing is past its period now.</MessageBarBody>
              </MessageBar>
            ) : (
              <PlainTable
                ariaLabel="What the clean-up would remove"
                rows={preview.data}
                getRowId={(r) => r.key}
                columns={[
                  { key: 'kind', header: 'Kind', render: (r) => retentionKeyName(r.key) },
                  { key: 'before', header: 'Older than', render: (r) => formatDateTime(r.before) },
                  {
                    key: 'removed',
                    header: 'Would be removed',
                    align: 'end',
                    render: (r) => r.removed.toLocaleString('en-IN'),
                  },
                ]}
              />
            )}
            {total > 0 && (
              <Text weight="semibold">
                {total.toLocaleString('en-IN')} records in all. Deleted records cannot be brought back.
              </Text>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={run.isPending}>
              {mode === 'run' ? 'Cancel' : 'Close'}
            </Button>
            <Button
              appearance="primary"
              icon={run.isPending ? <Spinner size="tiny" /> : <BroomRegular />}
              disabled={run.isPending || !preview.data}
              onClick={() => run.mutate()}
              style={total > 0 && !run.isPending ? { backgroundColor: tokens.colorPaletteRedBackground3 } : undefined}
            >
              Run clean-up now
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
