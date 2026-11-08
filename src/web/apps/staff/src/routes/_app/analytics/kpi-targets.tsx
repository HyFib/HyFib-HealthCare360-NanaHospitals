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
  Select,
  Text,
  tokens,
} from '@fluentui/react-components';
import { DeleteRegular, EditRegular, TargetArrowRegular } from '@fluentui/react-icons';
import { formatDateTime, isApiError } from '@hc360/core';
import { DataTable, ErrorState, LoadingState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import {
  analyticsApi,
  analyticsKeys,
  analyticsQueries,
  type KpiStatus,
  type KpiTargetItem,
} from '@/modules/analytics/api';
import { formatKpi } from '@/modules/analytics/format';
import { ANALYTICS_PERMISSIONS } from '@/modules/analytics/labels';
import {
  parseTargetForm,
  targetBasis,
  targetFormDefaults,
  targetUnitHint,
  type TargetForm,
  type TargetFormErrors,
} from '@/modules/analytics/targets';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';

export const Route = createFileRoute('/_app/analytics/kpi-targets')({
  component: () => (
    <RequirePermission permission={ANALYTICS_PERMISSIONS.kpiTargetsManage} what="the KPI targets">
      <KpiTargetsPage />
    </RequirePermission>
  ),
});

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground2 },
  actions: { display: 'flex', gap: '4px' },
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  row: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(140px, 1fr))', gap: '12px' },
});

const STATUS: Record<KpiStatus, { label: string; color: 'success' | 'warning' | 'danger' }> = {
  Green: { label: 'On target', color: 'success' },
  Amber: { label: 'Needs attention', color: 'warning' },
  Red: { label: 'Target missed', color: 'danger' },
};

/**
 * KPI targets of the signed-in facility (BI-01): each executive KPI's target and amber and red thresholds, and the role
 * told when this month's figure gets worse. Set by administrators; every change is in the audit trail.
 */
function KpiTargetsPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const targets = useQuery(analyticsQueries.targets());
  const [editing, setEditing] = useState<KpiTargetItem | null>(null);

  const remove = useMutation({
    mutationFn: (item: KpiTargetItem) => analyticsApi.deleteTarget(item.key, item.target?.version ?? 0),
    onSuccess: (_, item) => {
      void queryClient.invalidateQueries({ queryKey: analyticsKeys.targets });
      void queryClient.invalidateQueries({ queryKey: analyticsKeys.kpis });
      notify.success(`Target for ${item.label} removed`);
    },
    onError: (error) => notify.error(error, 'Could not remove the target'),
  });

  const crumbs = [{ label: 'Analytics', onClick: () => void navigate({ to: '/analytics' }) }, { label: 'KPI targets' }];

  if (targets.isLoading) {
    return <LoadingState label="Loading the KPI targets…" />;
  }
  if (targets.error || !targets.data) {
    return (
      <Page maxWidth={900}>
        <PageHeader title="KPI targets" icon={<TargetArrowRegular />} breadcrumbs={crumbs} />
        <ErrorState error={targets.error} onRetry={() => void targets.refetch()} title="Could not load the KPI targets" />
      </Page>
    );
  }

  const data = targets.data;
  const set = data.kpis.filter((k) => k.target).length;

  return (
    <Page>
      <PageHeader
        title="KPI targets"
        icon={<TargetArrowRegular />}
        subtitle={`${data.facilityName}: ${set} of ${data.kpis.length} KPIs have a target. The dashboard shows each against its target, and the owner role is told when this month's figure turns amber or red.`}
        breadcrumbs={crumbs}
      />
      <Section title="Targets of this facility">
        <DataTable<KpiTargetItem>
          ariaLabel="KPI targets"
          rows={data.kpis}
          getRowId={(k) => k.key}
          columns={[
            {
              key: 'kpi',
              header: 'KPI',
              width: 240,
              render: (k) => (
                <>
                  <Text block weight="semibold">
                    {k.label}
                  </Text>
                  <Caption1 className={styles.muted}>{targetBasis(k)}</Caption1>
                </>
              ),
            },
            {
              key: 'target',
              header: 'Target',
              render: (k) => (k.target ? formatKpi(k.target.target, k.unit) : <Caption1 className={styles.muted}>Not set</Caption1>),
            },
            {
              key: 'amber',
              header: 'Amber at',
              render: (k) => (k.target ? formatKpi(k.target.amberThreshold, k.unit) : '—'),
            },
            {
              key: 'red',
              header: 'Red at',
              render: (k) => (k.target ? formatKpi(k.target.redThreshold, k.unit) : '—'),
            },
            { key: 'owner', header: 'Told', render: (k) => k.target?.ownerRole ?? '—' },
            {
              key: 'check',
              header: 'This month so far',
              render: (k) =>
                k.target?.lastStatus ? (
                  <>
                    <Badge appearance="tint" color={STATUS[k.target.lastStatus].color}>
                      {STATUS[k.target.lastStatus].label}
                    </Badge>
                    <Caption1 block className={styles.muted}>
                      {formatKpi(k.target.lastValue, k.unit)}
                      {k.target.lastCheckedAt ? ` · checked ${formatDateTime(k.target.lastCheckedAt)}` : ''}
                    </Caption1>
                  </>
                ) : (
                  <Caption1 className={styles.muted}>{k.target ? 'Not checked yet' : '—'}</Caption1>
                ),
            },
            {
              key: 'actions',
              header: 'Actions',
              render: (k) => (
                <span className={styles.actions}>
                  <Button
                    size="small"
                    icon={<EditRegular />}
                    onClick={() => setEditing(k)}
                    aria-label={k.target ? `Change the target for ${k.label}` : `Set a target for ${k.label}`}
                  >
                    {k.target ? 'Change' : 'Set'}
                  </Button>
                  {k.target && (
                    <Button
                      size="small"
                      appearance="subtle"
                      icon={<DeleteRegular />}
                      disabled={remove.isPending}
                      onClick={() => remove.mutate(k)}
                      aria-label={`Remove the target for ${k.label}`}
                    />
                  )}
                </span>
              ),
            },
          ]}
        />
      </Section>
      {editing && <TargetDialog item={editing} roles={data.roles} onClose={() => setEditing(null)} />}
    </Page>
  );
}

function TargetDialog({ item, roles, onClose }: { item: KpiTargetItem; roles: string[]; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [form, setForm] = useState<TargetForm>(() => targetFormDefaults(item, roles));
  const [errors, setErrors] = useState<TargetFormErrors>({});

  const save = useMutation({
    mutationFn: (request: Parameters<typeof analyticsApi.saveTarget>[1]) => analyticsApi.saveTarget(item.key, request),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: analyticsKeys.targets });
      void queryClient.invalidateQueries({ queryKey: analyticsKeys.kpis });
      notify.success(`Target for ${item.label} saved`);
      onClose();
    },
    onError: (error) => {
      if (isApiError(error) && error.isValidation) {
        const next: TargetFormErrors = {};
        for (const [key, messages] of Object.entries(error.fieldErrors)) {
          const field = key.charAt(0).toLowerCase() + key.slice(1);
          if (field === 'target' || field === 'amberThreshold' || field === 'redThreshold' || field === 'ownerRole') {
            next[field] = messages[0];
          }
        }
        if (Object.keys(next).length > 0) {
          setErrors(next);
          return;
        }
      }
      notify.error(error, 'Could not save the target');
    },
  });

  const submit = () => {
    const parsed = parseTargetForm(form, item);
    if (parsed.errors) {
      setErrors(parsed.errors);
      return;
    }
    setErrors({});
    save.mutate(parsed.request);
  };

  const hint = targetUnitHint(item.unit, item.monthly);
  const change = (key: keyof TargetForm) => (value: string) => setForm((current) => ({ ...current, [key]: value }));

  return (
    <Dialog open modalType="modal" onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <form
          onSubmit={(e) => {
            e.preventDefault();
            submit();
          }}
        >
          <DialogBody>
            <DialogTitle>{`Target for ${item.label}`}</DialogTitle>
            <DialogContent className={styles.form}>
              <Caption1 className={styles.muted}>{targetBasis(item)}</Caption1>
              <div className={styles.row}>
                <Field label="Target" hint={hint} required validationMessage={errors.target}>
                  <Input inputMode="decimal" value={form.target} onChange={(_, d) => change('target')(d.value)} />
                </Field>
                <Field label="Amber at" hint={item.higherIsBetter ? 'Or below' : 'Or above'} required validationMessage={errors.amberThreshold}>
                  <Input inputMode="decimal" value={form.amberThreshold} onChange={(_, d) => change('amberThreshold')(d.value)} />
                </Field>
                <Field label="Red at" hint={item.higherIsBetter ? 'Or below' : 'Or above'} required validationMessage={errors.redThreshold}>
                  <Input inputMode="decimal" value={form.redThreshold} onChange={(_, d) => change('redThreshold')(d.value)} />
                </Field>
              </div>
              <Field
                label="Tell this role when it turns amber or red"
                required
                validationMessage={errors.ownerRole}
              >
                <Select value={form.ownerRole} onChange={(_, d) => change('ownerRole')(d.value)}>
                  <option value="">Choose a role</option>
                  {roles.map((role) => (
                    <option key={role} value={role}>
                      {role}
                    </option>
                  ))}
                </Select>
              </Field>
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={onClose}>
                Cancel
              </Button>
              <Button appearance="primary" type="submit" disabled={save.isPending}>
                Save target
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
