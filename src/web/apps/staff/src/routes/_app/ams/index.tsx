import {
  Button,
  Caption1,
  Checkbox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Select,
  Switch,
  Tab,
  TabList,
  Text,
  Textarea,
} from '@fluentui/react-components';
import { formatDateTime } from '@hc360/core';
import { DataTable, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import {
  AMS_PERMISSIONS,
  AMS_READERS,
  amsApi,
  amsKeys,
  amsQueries,
  approvalState,
  DECISION_LABELS,
  SITE_LABELS,
  type AntimicrobialApproval,
  type ApprovalQueue,
  type InfectionSite,
  type StewardshipDecision,
} from '@/modules/ams/api';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { errorMessage } from '@/modules/lab/common/errors';

const QUEUES: ApprovalQueue[] = ['Pending', 'ReviewDue', 'All'];

export const Route = createFileRoute('/_app/ams/')({
  validateSearch: (s: Record<string, unknown>): { queue?: ApprovalQueue; mine?: boolean } => ({
    queue: QUEUES.includes(s.queue as ApprovalQueue) ? (s.queue as ApprovalQueue) : undefined,
    mine: s.mine === true || s.mine === 'true' ? true : undefined,
  }),
  component: () => (
    <RequirePermission permission={AMS_READERS} what="antimicrobial stewardship">
      <StewardshipPage />
    </RequirePermission>
  ),
});

function StewardshipPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const queue = search.queue ?? 'Pending';
  const mine = search.mine === true;
  const approvals = useQuery(amsQueries.queue(queue, mine));
  const report = useQuery(amsQueries.report());
  const [open, setOpen] = useState<AntimicrobialApproval | null>(null);
  const r = report.data;

  return (
    <Page>
      <PageHeader
        title="Antibiotic approvals"
        subtitle={
          r
            ? `Last 30 days: ${r.byGroup.map((g) => `${g.group} ${g.prescribed}`).join(' · ')} · median ${
                r.medianHoursToDecision ?? '—'
              } h to decision · ${r.decidedLate} late · culture sent ${r.cultureSentPercent ?? '—'}% · ${r.reviewDecisions.DeEscalate} narrowed`
            : 'Restricted antibiotics waiting for approval or review'
        }
      />
      <Section flush>
        <div style={{ padding: '8px 16px', display: 'flex', gap: 16, alignItems: 'center', flexWrap: 'wrap' }}>
          <TabList
            selectedValue={queue}
            onTabSelect={(_, d) => void navigate({ search: { ...search, queue: d.value as ApprovalQueue } })}
          >
            <Tab value="Pending">Waiting for approval</Tab>
            <Tab value="ReviewDue">Review due</Tab>
            <Tab value="All">Last 30 days</Tab>
          </TabList>
          <Switch
            checked={mine}
            onChange={(_, d) => void navigate({ search: { ...search, mine: d.checked ? true : undefined } })}
            label="My prescriptions only"
          />
        </div>
        <DataTable<AntimicrobialApproval>
          ariaLabel="Restricted antibiotics"
          rows={approvals.data}
          loading={approvals.isLoading}
          error={approvals.error}
          onRetry={() => void approvals.refetch()}
          getRowId={(a) => a.id}
          onRowClick={setOpen}
          size="small"
          emptyTitle="Nothing here"
          columns={[
            {
              key: 'state',
              header: 'Status',
              width: 200,
              render: (a) => {
                const state = approvalState(a);
                return <StatusBadge status={a.status} tone={state.tone} label={state.label} />;
              },
            },
            { key: 'drug', header: 'Antibiotic', render: (a) => `${a.drugName} · ${a.group}` },
            {
              key: 'patient',
              header: 'Patient',
              render: (a) => `${a.patientName} · ${a.uhid}${a.inpatient ? ' · IP' : ''}`,
            },
            {
              key: 'by',
              header: 'Prescribed',
              width: 220,
              render: (a) => `${a.prescribedByName}, ${formatDateTime(a.prescribedAt)}`,
            },
            {
              key: 'culture',
              header: 'Culture',
              render: (a) => (a.organism ? `${a.organism}: ${a.susceptibility ?? 'not tested'}` : '—'),
            },
          ]}
        />
      </Section>
      {open && <ApprovalDialog approval={open} onClose={() => setOpen(null)} />}
    </Page>
  );
}

function ApprovalDialog({ approval: a, onClose }: { approval: AntimicrobialApproval; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const steward = usePermission(AMS_PERMISSIONS.approve);
  const mine = a.isMine;
  const [site, setSite] = useState<InfectionSite>(a.site ?? 'Respiratory');
  const [indication, setIndication] = useState(a.indication ?? '');
  const [empirical, setEmpirical] = useState(a.empirical ?? true);
  const [cultureSent, setCultureSent] = useState(a.cultureSent ?? false);
  const [days, setDays] = useState(String(a.durationDays || 5));
  const [notes, setNotes] = useState('');
  const [decision, setDecision] = useState<StewardshipDecision>('Continue');
  const done = (title: string) => {
    notify.success(title);
    void queryClient.invalidateQueries({ queryKey: amsKeys.all });
    onClose();
  };
  const justify = useMutation({
    mutationFn: () => amsApi.justify(a.id, site, indication.trim(), empirical, cultureSent, a.version),
    onSuccess: () => done('Justification sent to the stewardship team'),
  });
  const decide = useMutation({
    mutationFn: (approve: boolean) =>
      amsApi.decide(a.id, approve, approve ? Number(days) : null, notes.trim() || null, a.version),
    onSuccess: (_, approve) => done(approve ? 'Approved' : 'Not approved: the pharmacy will not issue it'),
  });
  const review = useMutation({
    mutationFn: () => amsApi.review(a.id, decision, notes.trim() || null, a.version),
    onSuccess: () => done('Review recorded'),
  });
  const error = justify.error ?? decide.error ?? review.error;

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 680 }}>
        <DialogBody>
          <DialogTitle>
            {a.drugName} · {a.patientName}
          </DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            <Text>
              {a.antimicrobial} ({a.group}) · {a.dose ?? ''} {a.route ?? ''} for {a.durationDays} days · prescription{' '}
              {a.prescriptionNumber} by {a.prescribedByName}, {formatDateTime(a.prescribedAt)}
            </Text>
            <Caption1>
              {a.indication
                ? `${a.site ? SITE_LABELS[a.site] : ''}: ${a.indication} · ${a.empirical ? 'empirical' : 'targeted'} · culture ${a.cultureSent ? 'sent' : 'not sent'}`
                : 'No justification from the prescriber yet.'}
            </Caption1>
            {a.status !== 'Pending' && (
              <Caption1>
                {a.status === 'Approved' ? `Approved for ${a.approvedDays} days` : 'Not approved'} by {a.decidedByName},{' '}
                {formatDateTime(a.decidedAt!)}
                {a.decisionNotes ? `: ${a.decisionNotes}` : ''}
              </Caption1>
            )}
            {a.organism && (
              <MessageBar intent={a.susceptibility === 'Resistant' ? 'error' : 'info'}>
                <MessageBarBody>
                  Culture {formatDateTime(a.cultureReleasedAt!)}: {a.organism}, {a.antimicrobial}{' '}
                  {a.susceptibility ?? 'not tested'}.
                </MessageBarBody>
              </MessageBar>
            )}
            {a.reviewDecision && (
              <Caption1>
                Reviewed by {a.reviewedByName}: {DECISION_LABELS[a.reviewDecision]}
                {a.reviewNotes ? `, ${a.reviewNotes}` : ''}
              </Caption1>
            )}

            {a.status === 'Pending' && mine && (
              <>
                <Field label="Infection site">
                  <Select value={site} onChange={(_, d) => setSite(d.value as InfectionSite)}>
                    {Object.entries(SITE_LABELS).map(([value, label]) => (
                      <option key={value} value={value}>
                        {label}
                      </option>
                    ))}
                  </Select>
                </Field>
                <Field label="Indication" required>
                  <Input value={indication} maxLength={500} onChange={(_, d) => setIndication(d.value)} />
                </Field>
                <Checkbox
                  checked={empirical}
                  onChange={(_, d) => setEmpirical(Boolean(d.checked))}
                  label="Empirical (no culture result yet)"
                />
                <Checkbox
                  checked={cultureSent}
                  onChange={(_, d) => setCultureSent(Boolean(d.checked))}
                  label="Culture sent before the first dose"
                />
              </>
            )}
            {a.status === 'Pending' && steward && !mine && (
              <Field label="Approve for (days)">
                <Input type="number" min={1} max={42} value={days} onChange={(_, d) => setDays(d.value)} />
              </Field>
            )}
            {a.status !== 'Rejected' && a.reviewDue && !a.reviewDecision && (steward || mine) && (
              <Field label="Review decision">
                <Select value={decision} onChange={(_, d) => setDecision(d.value as StewardshipDecision)}>
                  {Object.entries(DECISION_LABELS).map(([value, label]) => (
                    <option key={value} value={value}>
                      {label}
                    </option>
                  ))}
                </Select>
              </Field>
            )}
            {((a.status === 'Pending' && steward && !mine) ||
              (a.reviewDue && !a.reviewDecision && (steward || mine))) && (
              <Field
                label="Notes"
                hint="Required to reject, and for any change of antibiotic: name what the patient goes on to."
              >
                <Textarea value={notes} maxLength={500} onChange={(_, d) => setNotes(d.value)} />
              </Field>
            )}
            {error && (
              <MessageBar intent="error">
                <MessageBarBody>{errorMessage(error, 'That could not be saved.')}</MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>Close</Button>
            {a.status === 'Pending' && mine && (
              <Button
                appearance="primary"
                disabled={!indication.trim() || justify.isPending}
                onClick={() => justify.mutate()}
              >
                Send the justification
              </Button>
            )}
            {a.status === 'Pending' && steward && !mine && (
              <>
                <Button disabled={!notes.trim() || decide.isPending} onClick={() => decide.mutate(false)}>
                  Do not approve
                </Button>
                <Button appearance="primary" disabled={decide.isPending} onClick={() => decide.mutate(true)}>
                  Approve
                </Button>
              </>
            )}
            {a.status !== 'Rejected' && a.reviewDue && !a.reviewDecision && (steward || mine) && (
              <Button appearance="primary" disabled={review.isPending} onClick={() => review.mutate()}>
                Record the review
              </Button>
            )}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
