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
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Radio,
  RadioGroup,
  Select,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { formatDate, isApiError, todayIso } from '@hc360/core';
import { DataTable, LoadingState, StatusBadge } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { addDays } from '@/modules/hr/shared/dates';
import { UserPicker } from '@/modules/hr/shared/UserPicker';
import {
  AUDIT_STATUS_LABELS,
  auditApi,
  auditKeys,
  auditQueries,
  CHAPTER_LABELS,
  RECURRENCE_LABELS,
  RECURRENCES,
  RESULT_LABELS,
  type AuditDetail,
  type AuditFinding,
  type AuditListItem,
  type AuditRecurrence,
  type AuditStatus,
  type FindingResult,
} from './auditApi';
import { capaStatusLabel } from './labels';

type PickedUser = NonNullable<Parameters<typeof UserPicker>[0]['value']>;

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  row: { display: 'flex', gap: '12px', flexWrap: 'wrap' },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', paddingBlock: '6px', whiteSpace: 'normal' },
  muted: { color: tokens.colorNeutralForeground3 },
  finding: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    paddingBlock: '10px',
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
});

const problemOf = (error: unknown, fallback: string) => (isApiError(error) ? error.message : fallback);

const statusTone = (status: AuditStatus, overdue: boolean) =>
  status === 'Closed'
    ? 'success'
    : status === 'Cancelled'
      ? 'informative'
      : overdue
        ? 'danger'
        : status === 'Completed'
          ? 'warning'
          : 'informative';

/** The audits on the schedule (or the signed-in auditor's own); a row opens it. */
export function AuditsTable({
  rows,
  loading,
  error,
  onRetry,
  onOpen,
}: {
  rows: AuditListItem[] | undefined;
  loading: boolean;
  error: unknown;
  onRetry: () => void;
  onOpen: (id: string) => void;
}) {
  const styles = useStyles();
  return (
    <DataTable
      ariaLabel="Internal audits"
      rows={rows}
      loading={loading}
      error={error}
      onRetry={onRetry}
      getRowId={(a) => a.id}
      onRowClick={(a) => onOpen(a.id)}
      columns={[
        {
          key: 'audit',
          header: 'Audit',
          render: (a) => (
            <div className={styles.cell}>
              <Text weight="semibold">
                {a.auditNumber} · {a.title}
              </Text>
              <Caption1 className={styles.muted}>
                {CHAPTER_LABELS[a.chapter].split(' · ')[0]}
                {a.area ? ` · ${a.area}` : ''} · {RECURRENCE_LABELS[a.recurrence]}
              </Caption1>
            </div>
          ),
        },
        { key: 'on', header: 'Scheduled', width: 130, render: (a) => formatDate(a.scheduledOn) },
        { key: 'lead', header: 'Lead auditor', priority: 'low', render: (a) => a.leadAuditorName ?? '—' },
        {
          key: 'score',
          header: 'Compliance',
          width: 150,
          render: (a) =>
            a.compliancePercent === null
              ? '—'
              : `${a.compliancePercent}%${a.nonCompliances > 0 ? ` · ${a.nonCompliances} gap${a.nonCompliances === 1 ? '' : 's'}` : ''}`,
        },
        {
          key: 'status',
          header: 'Status',
          width: 170,
          render: (a) => (
            <div className={styles.cell}>
              <StatusBadge
                status={a.status}
                tone={statusTone(a.status, a.isOverdue)}
                label={a.isOverdue ? 'Overdue' : AUDIT_STATUS_LABELS[a.status]}
              />
              {a.openCapas > 0 && <Caption1 className={styles.muted}>{a.openCapas} CAPA open</Caption1>}
            </div>
          ),
        },
      ]}
      emptyTitle="No audits"
      emptyDescription="Schedule internal audits against the NABH chapter checklists."
      size="small"
    />
  );
}

/** Scheduling an audit: the checklist, the day, the lead auditor, the area and how often it repeats. */
export function ScheduleAuditDialog({
  onClose,
  onScheduled,
}: {
  onClose: () => void;
  onScheduled: (id: string) => void;
}) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const templates = useQuery(auditQueries.templates());
  const [templateId, setTemplateId] = useState('');
  const [on, setOn] = useState(todayIso());
  const [lead, setLead] = useState<PickedUser | null>(null);
  const [area, setArea] = useState('');
  const [recurrence, setRecurrence] = useState<AuditRecurrence>('Monthly');
  const schedule = useMutation({
    mutationFn: () =>
      auditApi.schedule({
        templateId,
        scheduledOn: on,
        leadAuditorUserId: lead!.id,
        departmentId: null,
        area: area.trim() || null,
        recurrence,
      }),
    onSuccess: (audit) => {
      void queryClient.invalidateQueries({ queryKey: auditKeys.all });
      onScheduled(audit.id);
    },
  });
  const valid = templateId !== '' && lead !== null && on !== '';
  return (
    <Dialog open onOpenChange={(_, data) => !data.open && !schedule.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 600 }}>
        <DialogBody>
          <DialogTitle>Schedule an audit</DialogTitle>
          <DialogContent className={styles.body}>
            <Field label="Checklist" required>
              <Select value={templateId} onChange={(_, data) => setTemplateId(data.value)}>
                <option value="">Choose a checklist</option>
                {(templates.data ?? []).map((t) => (
                  <option key={t.id} value={t.id}>
                    {CHAPTER_LABELS[t.chapter].split(' · ')[0]} · {t.title} ({t.items.length} items)
                  </option>
                ))}
              </Select>
            </Field>
            <div className={styles.row}>
              <Field label="On" required>
                <Input type="date" value={on} onChange={(_, data) => setOn(data.value)} />
              </Field>
              <Field label="Repeats" required>
                <Select value={recurrence} onChange={(_, data) => setRecurrence(data.value as AuditRecurrence)}>
                  {RECURRENCES.map((r) => (
                    <option key={r} value={r}>
                      {RECURRENCE_LABELS[r]}
                    </option>
                  ))}
                </Select>
              </Field>
            </div>
            <UserPicker label="Lead auditor" required value={lead} onChange={setLead} />
            <Field label="Ward, unit or area">
              <Input value={area} maxLength={150} onChange={(_, data) => setArea(data.value)} />
            </Field>
            {schedule.error && (
              <MessageBar intent={'error'}>
                <MessageBarBody>
                  <MessageBarTitle>Not saved</MessageBarTitle>
                  {problemOf(schedule.error, 'The audit could not be scheduled.')}
                </MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={schedule.isPending}>
              Cancel
            </Button>
            <Button appearance="primary" disabled={!valid || schedule.isPending} onClick={() => schedule.mutate()}>
              Schedule
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

/**
 * An audit: its findings item by item (the lead auditor or the quality team), a CAPA for each non-compliance (the quality
 * team), completion, and closure once every CAPA is verified.
 */
export function AuditDialog({
  auditId,
  canPlanCapa,
  onClose,
}: {
  auditId: string;
  canPlanCapa: boolean;
  onClose: () => void;
}) {
  const audit = useQuery(auditQueries.audit(auditId));
  return (
    <Dialog open onOpenChange={(_, data) => !data.open && onClose()}>
      <DialogSurface style={{ maxWidth: 820 }}>
        {audit.data ? (
          <AuditBody audit={audit.data} canPlanCapa={canPlanCapa} onClose={onClose} />
        ) : (
          <DialogBody>
            <DialogTitle>Audit</DialogTitle>
            <DialogContent>
              {audit.error ? (
                <MessageBar intent={'error'}>
                  <MessageBarBody>
                    <MessageBarTitle>Not saved</MessageBarTitle>
                    {problemOf(audit.error, 'The audit could not be opened.')}
                  </MessageBarBody>
                </MessageBar>
              ) : (
                <LoadingState />
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose}>Close</Button>
            </DialogActions>
          </DialogBody>
        )}
      </DialogSurface>
    </Dialog>
  );
}

function AuditBody({
  audit: a,
  canPlanCapa,
  onClose,
}: {
  audit: AuditDetail;
  canPlanCapa: boolean;
  onClose: () => void;
}) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const [summary, setSummary] = useState('');
  const [planning, setPlanning] = useState<AuditFinding | null>(null);
  const refresh = (next: AuditDetail) => {
    queryClient.setQueryData(auditQueries.audit(a.id).queryKey, next);
    void queryClient.invalidateQueries({ queryKey: auditKeys.all });
  };
  const complete = useMutation({
    mutationFn: () => auditApi.complete(a.id, summary.trim() || null, a.version),
    onSuccess: refresh,
  });
  const close = useMutation({ mutationFn: () => auditApi.close(a.id, a.version), onSuccess: refresh });
  const error = complete.error ?? close.error;
  const open = a.status === 'Scheduled' || a.status === 'InProgress';

  return (
    <DialogBody>
      <DialogTitle>
        {a.auditNumber} · {a.title}
      </DialogTitle>
      <DialogContent className={styles.body}>
        <Text>
          {CHAPTER_LABELS[a.chapter]} · {formatDate(a.scheduledOn)}
          {a.area ? ` · ${a.area}` : ''} · lead auditor {a.leadAuditorName ?? '—'} · {AUDIT_STATUS_LABELS[a.status]}
          {a.compliancePercent !== null ? ` · ${a.compliancePercent}% compliant` : ''}
        </Text>
        <div>
          {a.findings.map((f) => (
            <FindingRow
              key={f.id}
              audit={a}
              finding={f}
              editable={open && a.canRecord}
              canPlanCapa={canPlanCapa && open}
              onSaved={refresh}
              onPlan={() => setPlanning(f)}
            />
          ))}
        </div>
        {open && a.completionGaps.length > 0 && (
          <MessageBar intent={'info'}>
            <MessageBarBody>
              <MessageBarTitle>Still to do</MessageBarTitle>Before completing: {a.completionGaps.join('; ')}.
            </MessageBarBody>
          </MessageBar>
        )}
        {open && a.canRecord && (
          <Field label="Summary">
            <Textarea value={summary} maxLength={2000} onChange={(_, data) => setSummary(data.value)} />
          </Field>
        )}
        {a.status === 'Completed' && (
          <MessageBar intent={'warning'}>
            <MessageBarBody>
              <MessageBarTitle>Please note</MessageBarTitle>
              The audit closes when every CAPA it raised is verified effective by someone other than its owner (on the
              corrective actions page).
            </MessageBarBody>
          </MessageBar>
        )}
        {a.summary && <Caption1 className={styles.muted}>Summary: {a.summary}</Caption1>}
        {error && (
          <MessageBar intent={'error'}>
            <MessageBarBody>
              <MessageBarTitle>Not saved</MessageBarTitle>
              {problemOf(error, 'The audit could not be updated.')}
            </MessageBarBody>
          </MessageBar>
        )}
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Close</Button>
        {open && a.canRecord && (
          <Button
            appearance="primary"
            disabled={complete.isPending || a.completionGaps.length > 0}
            onClick={() => complete.mutate()}
          >
            Complete the audit
          </Button>
        )}
        {a.status === 'Completed' && canPlanCapa && (
          <Button appearance="primary" disabled={close.isPending} onClick={() => close.mutate()}>
            Close the audit
          </Button>
        )}
      </DialogActions>
      {planning && (
        <RaiseCapaDialog audit={a} finding={planning} onClose={() => setPlanning(null)} onRaised={refresh} />
      )}
    </DialogBody>
  );
}

function FindingRow({
  audit,
  finding: f,
  editable,
  canPlanCapa,
  onSaved,
  onPlan,
}: {
  audit: AuditDetail;
  finding: AuditFinding;
  editable: boolean;
  canPlanCapa: boolean;
  onSaved: (next: AuditDetail) => void;
  onPlan: () => void;
}) {
  const styles = useStyles();
  const [result, setResult] = useState<FindingResult | ''>(f.result ?? '');
  const [observation, setObservation] = useState(f.observation ?? '');
  const save = useMutation({
    mutationFn: () =>
      auditApi.record(audit.id, f.itemKey, result as FindingResult, observation.trim() || null, audit.version),
    onSuccess: onSaved,
  });
  const changed = result !== (f.result ?? '') || observation !== (f.observation ?? '');
  const valid = result !== '' && (result !== 'NonCompliant' || observation.trim().length > 0);
  return (
    <div className={styles.finding}>
      <Text weight="semibold">
        {f.order}. {f.itemText}
      </Text>
      {editable ? (
        <>
          <RadioGroup
            layout="horizontal"
            aria-label={`Finding for item ${f.order}`}
            value={result}
            onChange={(_, data) => setResult(data.value as FindingResult)}
          >
            {(['Compliant', 'NonCompliant', 'NotApplicable'] as const).map((r) => (
              <Radio key={r} value={r} label={RESULT_LABELS[r]} disabled={f.capaId !== null && r !== 'NonCompliant'} />
            ))}
          </RadioGroup>
          {result === 'NonCompliant' && (
            <Field label={`What was found (item ${f.order})`} required>
              <Textarea value={observation} maxLength={2000} onChange={(_, data) => setObservation(data.value)} />
            </Field>
          )}
          {changed && (
            <Button
              size="small"
              style={{ alignSelf: 'flex-start' }}
              disabled={!valid || save.isPending}
              onClick={() => save.mutate()}
            >
              Save item {f.order}
            </Button>
          )}
          {save.error && (
            <MessageBar intent={'error'}>
              <MessageBarBody>
                <MessageBarTitle>Not saved</MessageBarTitle>
                {problemOf(save.error, 'The finding could not be saved.')}
              </MessageBarBody>
            </MessageBar>
          )}
        </>
      ) : (
        <Caption1>
          {f.result ? RESULT_LABELS[f.result] : 'Not answered'}
          {f.observation ? `: ${f.observation}` : ''}
        </Caption1>
      )}
      {f.capaId ? (
        <Caption1 className={styles.muted}>
          CAPA {f.capaNumber} · {f.capaOwnerName ?? 'owner'} · due {f.capaDueDate ? formatDate(f.capaDueDate) : '—'} ·{' '}
          {f.capaStatus ? capaStatusLabel[f.capaStatus] : ''}
        </Caption1>
      ) : (
        f.result === 'NonCompliant' &&
        canPlanCapa && (
          <Button size="small" style={{ alignSelf: 'flex-start' }} onClick={onPlan}>
            Raise a CAPA for item {f.order}
          </Button>
        )
      )}
    </div>
  );
}

function RaiseCapaDialog({
  audit,
  finding,
  onClose,
  onRaised,
}: {
  audit: AuditDetail;
  finding: AuditFinding;
  onClose: () => void;
  onRaised: (next: AuditDetail) => void;
}) {
  const styles = useStyles();
  const [action, setAction] = useState('');
  const [owner, setOwner] = useState<PickedUser | null>(null);
  const [due, setDue] = useState(addDays(todayIso(), 14));
  const raise = useMutation({
    mutationFn: () =>
      auditApi.raiseCapa(audit.id, finding.id, 'Corrective', action.trim(), owner!.id, due, audit.version),
    onSuccess: (next) => {
      onRaised(next);
      onClose();
    },
  });
  return (
    <Dialog open onOpenChange={(_, data) => !data.open && !raise.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <DialogBody>
          <DialogTitle>Corrective action for item {finding.order}</DialogTitle>
          <DialogContent className={styles.body}>
            <Text>
              {finding.itemText}: {finding.observation}
            </Text>
            <Field label="What will be done" required>
              <Textarea value={action} maxLength={1800} onChange={(_, data) => setAction(data.value)} />
            </Field>
            <UserPicker
              label="Owner"
              required
              value={owner}
              onChange={setOwner}
              hint="Someone else verifies it once it is done"
            />
            <Field label="Due by" required>
              <Input type="date" value={due} min={todayIso()} onChange={(_, data) => setDue(data.value)} />
            </Field>
            {raise.error && (
              <MessageBar intent={'error'}>
                <MessageBarBody>
                  <MessageBarTitle>Not saved</MessageBarTitle>
                  {problemOf(raise.error, 'The CAPA could not be raised.')}
                </MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={raise.isPending}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={action.trim().length < 10 || owner === null || !due || raise.isPending}
              onClick={() => raise.mutate()}
            >
              Raise the CAPA
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
