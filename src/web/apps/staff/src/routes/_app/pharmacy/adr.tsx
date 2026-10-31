import {
  Badge,
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
  Spinner,
  Text,
  Textarea,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular, PrintRegular, SendRegular } from '@fluentui/react-icons';
import { formatDateOnly, todayIso } from '@hc360/core';
import { CheckboxGroup, DataTable, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { useServerDocuments } from '@/lib/documents';
import { usePermission } from '@/lib/permissions';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { errorMessage } from '@/modules/lab/common/errors';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';
import {
  ADR_READERS,
  adrApi,
  adrDocument,
  adrKeys,
  adrProblems,
  adrQueries,
  blankDrug,
  CAUSALITIES,
  causalityLabel,
  OUTCOMES,
  outcomeLabel,
  SERIOUSNESS,
  seriousnessNames,
  seriousnessValue,
  type AdrDrug,
  type AdrOutcome,
  type AdrReport,
  type AdrReportListItem,
  type ChallengeResult,
  type WhoUmcCausality,
} from '@/modules/pharmacy/adr';
import { PHARMACY_PERMISSIONS } from '@/modules/pharmacy/permissions';

export const Route = createFileRoute('/_app/pharmacy/adr')({
  component: () => (
    <RequirePermission permission={ADR_READERS} what="adverse drug reaction reports">
      <AdrPage />
    </RequirePermission>
  ),
});

/**
 * Suspected adverse drug reactions (Pharmacovigilance Programme of India): any clinician reports what they saw;
 * pharmacovigilance assesses causality (WHO-UMC) and sends the report to the ADR monitoring centre. A serious reaction is
 * sent within 15 days of the hospital learning of it.
 */
function AdrPage() {
  const canReport = usePermission(PHARMACY_PERMISSIONS.adrReport);
  const canManage = usePermission(PHARMACY_PERMISSIONS.adrManage);
  const list = useQuery(adrQueries.list());
  const [reporting, setReporting] = useState(false);
  const [open, setOpen] = useState<string | null>(null);
  const overdue = list.data?.filter((r) => r.overdue).length ?? 0;

  return (
    <Page>
      <PageHeader
        title="Adverse drug reactions"
        subtitle={
          canManage
            ? 'Every suspected reaction reported in the hospital. Assess its causality and send it to the ADR monitoring centre; a serious one within 15 days.'
            : 'The suspected reactions you reported. Pharmacovigilance assesses them and sends them to the ADR monitoring centre.'
        }
        actions={
          canReport && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setReporting(true)}>
              Report a reaction
            </Button>
          )
        }
      />
      {overdue > 0 && (
        <MessageBar intent="error">
          <MessageBarBody>
            {overdue === 1 ? 'A serious reaction is' : `${overdue} serious reactions are`} past the 15 days and not yet
            sent.
          </MessageBarBody>
        </MessageBar>
      )}
      <Section flush>
        <DataTable<AdrReportListItem>
          ariaLabel="Adverse drug reaction reports"
          rows={list.data}
          loading={list.isLoading}
          error={list.error}
          onRetry={() => void list.refetch()}
          getRowId={(r) => r.id}
          onRowClick={(r) => setOpen(r.id)}
          size="small"
          columns={[
            {
              key: 'number',
              header: 'Report',
              width: 170,
              render: (r) => (
                <span>
                  {r.number}
                  <Caption1 block>{formatDateOnly(r.learnedOn)}</Caption1>
                </span>
              ),
            },
            {
              key: 'patient',
              header: 'Patient',
              width: 160,
              render: (r) => (
                <span>
                  {r.patientInitials}
                  <Caption1 block>{r.uhid}</Caption1>
                </span>
              ),
            },
            {
              key: 'reaction',
              header: 'Reaction',
              render: (r) => (
                <span>
                  {r.reaction}
                  <Caption1 block>{r.suspectedDrugs}</Caption1>
                </span>
              ),
            },
            {
              key: 'serious',
              header: 'Serious',
              width: 150,
              render: (r) =>
                r.serious ? (
                  <span>
                    <Badge appearance="filled" color={r.overdue ? 'danger' : 'warning'}>
                      Serious
                    </Badge>
                    {r.dueBy && r.status === 'Draft' && <Caption1 block>Send by {formatDateOnly(r.dueBy)}</Caption1>}
                  </span>
                ) : (
                  'No'
                ),
            },
            {
              key: 'status',
              header: 'Status',
              width: 170,
              render: (r) => (
                <StatusBadge
                  status={r.status}
                  tone={r.status === 'Submitted' ? 'success' : 'warning'}
                  label={
                    r.status === 'Submitted' ? `Sent · ${r.causality ? causalityLabel[r.causality] : ''}` : 'Draft'
                  }
                />
              ),
            },
          ]}
          emptyTitle="No reports"
          emptyDescription="Report a suspected reaction as soon as it is seen, even when unsure the drug caused it."
        />
      </Section>
      {reporting && <ReportDialog existing={null} onClose={() => setReporting(false)} />}
      {open && <ReportDrawer id={open} canManage={canManage} onClose={() => setOpen(null)} />}
    </Page>
  );
}

function ReportDrawer({ id, canManage, onClose }: { id: string; canManage: boolean; onClose: () => void }) {
  const report = useQuery(adrQueries.report(id));
  const { print } = useServerDocuments();
  const [editing, setEditing] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const r = report.data;

  if (editing && r) {
    return <ReportDialog existing={r} onClose={() => setEditing(false)} />;
  }
  if (submitting && r) {
    return <SubmitDialog report={r} onClose={() => setSubmitting(false)} />;
  }

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 720 }}>
        <DialogBody>
          <DialogTitle>
            {r ? `ADR report ${r.number}` : 'ADR report'}
            {r && (
              <Caption1 block>
                {r.patientInitials} · {r.uhid} · reported by {r.reporterName}
              </Caption1>
            )}
          </DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
            {report.error && (
              <MessageBar intent="error">
                <MessageBarBody>{errorMessage(report.error, 'The report could not be opened.')}</MessageBarBody>
              </MessageBar>
            )}
            {!r && !report.error && <Spinner label="Opening the report…" />}
            {r && (
              <>
                <Text>{r.reaction}</Text>
                <Caption1>
                  Started {formatDateOnly(r.reactionStartedOn)}
                  {r.reactionStoppedOn ? `, stopped ${formatDateOnly(r.reactionStoppedOn)}` : ''} ·{' '}
                  {outcomeLabel[r.outcome]} ·{' '}
                  {seriousnessNames(r.seriousness).length === 0
                    ? 'Not serious'
                    : seriousnessNames(r.seriousness)
                        .map((n) => SERIOUSNESS.find(([name]) => name === n)?.[2] ?? n)
                        .join(', ')}
                </Caption1>
                {r.drugs.map((d, i) => (
                  <Caption1 block key={i}>
                    {d.suspected ? 'Suspected' : 'Concomitant'}: {d.name}
                    {d.batchNumber ? ` · batch ${d.batchNumber}` : ''}
                    {d.dose ? ` · ${[d.dose, d.route, d.frequency].filter(Boolean).join(' ')}` : ''}
                  </Caption1>
                ))}
                {r.status === 'Submitted' && (
                  <Text>
                    Sent on {formatDateOnly(r.submittedOn!)} · {r.causality ? causalityLabel[r.causality] : ''} ·{' '}
                    {r.submissionReference ?? 'no reference'}
                  </Text>
                )}
              </>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>Close</Button>
            {r && (
              <Button
                icon={print.isPending ? <Spinner size="tiny" aria-hidden /> : <PrintRegular />}
                onClick={() => print.mutate(adrDocument(r.id))}
              >
                Print the PvPI form
              </Button>
            )}
            {r?.status === 'Draft' && <Button onClick={() => setEditing(true)}>Correct</Button>}
            {r?.status === 'Draft' && canManage && (
              <Button appearance="primary" icon={<SendRegular />} onClick={() => setSubmitting(true)}>
                Assess and send
              </Button>
            )}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

const CHALLENGES: ChallengeResult[] = ['NotApplicable', 'Yes', 'No', 'Unknown'];
const challengeLabel: Record<ChallengeResult, string> = {
  NotApplicable: 'Not applicable',
  Yes: 'Yes',
  No: 'No',
  Unknown: 'Unknown',
};

function ReportDialog({ existing, onClose }: { existing: AdrReport | null; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const today = todayIso();
  const [patient, setPatient] = useState<PickedPatient | null>(
    existing ? { id: existing.patientId, uhid: existing.uhid, fullName: existing.patientInitials } : null,
  );
  const [reaction, setReaction] = useState(existing?.reaction ?? '');
  const [startedOn, setStartedOn] = useState(existing?.reactionStartedOn ?? today);
  const [stoppedOn, setStoppedOn] = useState(existing?.reactionStoppedOn ?? '');
  const [learnedOn, setLearnedOn] = useState(existing?.learnedOn ?? today);
  const [serious, setSerious] = useState<string[]>(existing ? seriousnessNames(existing.seriousness) : []);
  const [outcome, setOutcome] = useState<AdrOutcome>(existing?.outcome ?? 'Recovering');
  const [weight, setWeight] = useState(existing?.weightKg ? String(existing.weightKg) : '');
  const [history, setHistory] = useState(existing?.medicalHistory ?? '');
  const [tests, setTests] = useState(existing?.relevantTests ?? '');
  const [designation, setDesignation] = useState(existing?.reporterDesignation ?? '');
  const [drugs, setDrugs] = useState<AdrDrug[]>(existing?.drugs ?? [blankDrug(true)]);
  const [tried, setTried] = useState(false);
  const medicines = useQuery({ ...adrQueries.medicines(patient?.id ?? ''), enabled: !!patient && !existing });
  const problems = adrProblems({
    patientId: patient?.id ?? null,
    reaction,
    startedOn,
    stoppedOn,
    learnedOn,
    outcome,
    seriousness: serious,
    drugs,
    today,
  });
  const valid = Object.values(problems).every((p) => p === null);
  const show = (p: string | null) => (tried ? (p ?? undefined) : undefined);
  const setDrug = (i: number, change: Partial<AdrDrug>) =>
    setDrugs((all) => all.map((d, j) => (j === i ? { ...d, ...change } : d)));

  const save = useMutation({
    mutationFn: () => {
      const request = {
        patientId: patient!.id,
        incidentNumber: existing?.incidentNumber ?? null,
        learnedOn,
        reaction: reaction.trim(),
        reactionStartedOn: startedOn,
        reactionStoppedOn: stoppedOn || null,
        seriousness: seriousnessValue(serious),
        outcome,
        weightKg: weight ? Number(weight) : null,
        relevantTests: tests.trim() || null,
        medicalHistory: history.trim() || null,
        drugs: drugs.filter((d) => d.name.trim()),
        reporterDesignation: designation.trim() || null,
      };
      return existing ? adrApi.correct(existing.id, { ...request, version: existing.version }) : adrApi.report(request);
    },
    onSuccess: (saved) => {
      void queryClient.invalidateQueries({ queryKey: adrKeys.all });
      notify.success(
        existing ? 'Report corrected' : 'Reaction reported',
        `${saved.number}. Pharmacovigilance will assess it.`,
      );
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !save.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 820 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            setTried(true);
            if (valid && !save.isPending) {
              save.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>
              {existing ? `Correct ${existing.number}` : 'Report a suspected adverse drug reaction'}
            </DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {save.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(save.error, 'The report could not be saved.')}</MessageBarBody>
                </MessageBar>
              )}
              {existing ? (
                <Text>
                  Patient: {existing.patientInitials} · {existing.uhid}
                </Text>
              ) : (
                <PatientPicker
                  value={patient}
                  onChange={setPatient}
                  label="Patient"
                  required
                  validationMessage={show(problems.patient)}
                  autoFocus
                />
              )}
              <Field label="The reaction" required validationMessage={show(problems.reaction)}>
                <Textarea value={reaction} maxLength={2000} onChange={(_, d) => setReaction(d.value)} />
              </Field>
              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))', gap: 12 }}>
                <Field label="Started" required validationMessage={show(problems.startedOn)}>
                  <Input type="date" value={startedOn} max={today} onChange={(_, d) => setStartedOn(d.value)} />
                </Field>
                <Field label="Stopped" validationMessage={show(problems.stoppedOn)}>
                  <Input type="date" value={stoppedOn} max={today} onChange={(_, d) => setStoppedOn(d.value)} />
                </Field>
                <Field label="Hospital learned of it" required validationMessage={show(problems.learnedOn)}>
                  <Input type="date" value={learnedOn} max={today} onChange={(_, d) => setLearnedOn(d.value)} />
                </Field>
                <Field label="Outcome" required validationMessage={show(problems.outcome)}>
                  <Select value={outcome} onChange={(_, d) => setOutcome(d.value as AdrOutcome)}>
                    {OUTCOMES.map((o) => (
                      <option key={o} value={o}>
                        {outcomeLabel[o]}
                      </option>
                    ))}
                  </Select>
                </Field>
                <Field label="Weight (kg)">
                  <Input type="number" inputMode="decimal" value={weight} onChange={(_, d) => setWeight(d.value)} />
                </Field>
              </div>
              <CheckboxGroup label="Serious because" hint="None ticked: not serious.">
                <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
                  {SERIOUSNESS.map(([name, , label]) => (
                    <Checkbox
                      key={name}
                      label={label}
                      checked={serious.includes(name)}
                      onChange={(_, d) => setSerious((s) => (d.checked ? [...s, name] : s.filter((x) => x !== name)))}
                    />
                  ))}
                </div>
              </CheckboxGroup>
              <Text weight="semibold">Drugs</Text>
              {problems.drugs && tried && (
                <MessageBar intent="warning">
                  <MessageBarBody>{problems.drugs}</MessageBarBody>
                </MessageBar>
              )}
              {medicines.data && medicines.data.length > 0 && (
                <Caption1>
                  Given lately:{' '}
                  {medicines.data.slice(0, 8).map((m) => (
                    <Button
                      key={`${m.name}-${m.batchNumber}`}
                      size="small"
                      appearance="subtle"
                      onClick={() =>
                        setDrugs((all) => [
                          ...all.filter((d) => d.name.trim()),
                          {
                            ...blankDrug(all.every((d) => !d.name.trim())),
                            name: m.name,
                            manufacturer: m.manufacturer,
                            batchNumber: m.batchNumber,
                            expiryDate: m.expiryDate,
                          },
                        ])
                      }
                    >
                      {m.name} ({m.batchNumber})
                    </Button>
                  ))}
                </Caption1>
              )}
              {drugs.map((d, i) => (
                <div
                  key={i}
                  style={{
                    display: 'grid',
                    gridTemplateColumns: 'repeat(auto-fit, minmax(150px, 1fr))',
                    gap: 8,
                    borderTop: '1px solid #ddd',
                    paddingTop: 8,
                  }}
                >
                  <Field label="Drug">
                    <Input value={d.name} maxLength={200} onChange={(_, v) => setDrug(i, { name: v.value })} />
                  </Field>
                  <Field label="Batch">
                    <Input
                      value={d.batchNumber ?? ''}
                      maxLength={40}
                      onChange={(_, v) => setDrug(i, { batchNumber: v.value || null })}
                    />
                  </Field>
                  <Field label="Dose, route, frequency">
                    <Input
                      value={d.dose ?? ''}
                      maxLength={60}
                      onChange={(_, v) => setDrug(i, { dose: v.value || null })}
                    />
                  </Field>
                  <Field label="Given from">
                    <Input
                      type="date"
                      value={d.startedOn ?? ''}
                      onChange={(_, v) => setDrug(i, { startedOn: v.value || null })}
                    />
                  </Field>
                  <Field label="Stopped on">
                    <Input
                      type="date"
                      value={d.stoppedOn ?? ''}
                      onChange={(_, v) => setDrug(i, { stoppedOn: v.value || null })}
                    />
                  </Field>
                  <Field label="Abated on stopping">
                    <Select
                      value={d.dechallenge}
                      onChange={(_, v) => setDrug(i, { dechallenge: v.value as ChallengeResult })}
                    >
                      {CHALLENGES.map((c) => (
                        <option key={c} value={c}>
                          {challengeLabel[c]}
                        </option>
                      ))}
                    </Select>
                  </Field>
                  <Field label="Came back on giving again">
                    <Select
                      value={d.rechallenge}
                      onChange={(_, v) => setDrug(i, { rechallenge: v.value as ChallengeResult })}
                    >
                      {CHALLENGES.map((c) => (
                        <option key={c} value={c}>
                          {challengeLabel[c]}
                        </option>
                      ))}
                    </Select>
                  </Field>
                  <span style={{ display: 'flex', alignItems: 'end', gap: 8 }}>
                    <Checkbox
                      label="Suspected"
                      checked={d.suspected}
                      onChange={(_, v) => setDrug(i, { suspected: !!v.checked })}
                    />
                    {drugs.length > 1 && (
                      <Button
                        aria-label={`Remove ${d.name || 'drug'}`}
                        icon={<DeleteRegular />}
                        appearance="subtle"
                        onClick={() => setDrugs((all) => all.filter((_, j) => j !== i))}
                      />
                    )}
                  </span>
                </div>
              ))}
              <div>
                <Button
                  size="small"
                  icon={<AddRegular />}
                  onClick={() => setDrugs((all) => [...all, blankDrug(false)])}
                >
                  Add a drug
                </Button>
              </div>
              <Field label="Relevant tests and laboratory data">
                <Textarea value={tests} maxLength={2000} onChange={(_, d) => setTests(d.value)} />
              </Field>
              <Field
                label="Relevant medical history"
                hint="Allergies, pregnancy, liver or kidney disease, smoking, alcohol."
              >
                <Textarea value={history} maxLength={2000} onChange={(_, d) => setHistory(d.value)} />
              </Field>
              <Field label="Your designation">
                <Input value={designation} maxLength={120} onChange={(_, d) => setDesignation(d.value)} />
              </Field>
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={save.isPending}
                icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                {existing ? 'Save' : 'Report'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

function SubmitDialog({ report, onClose }: { report: AdrReport; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [causality, setCausality] = useState<WhoUmcCausality>('Possible');
  const [submittedOn, setSubmittedOn] = useState(todayIso());
  const [reference, setReference] = useState('');
  const save = useMutation({
    mutationFn: () => adrApi.submit(report.id, causality, submittedOn, reference.trim() || null, report.version),
    onSuccess: (saved) => {
      void queryClient.invalidateQueries({ queryKey: adrKeys.all });
      notify.success('Report sent', `${saved.number} is recorded as sent to the ADR monitoring centre.`);
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !save.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (!save.isPending) {
              save.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Assess and send {report.number}</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {save.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(save.error, 'The report could not be sent.')}</MessageBarBody>
                </MessageBar>
              )}
              <Field label="Causality (WHO-UMC)" required>
                <Select value={causality} onChange={(_, d) => setCausality(d.value as WhoUmcCausality)}>
                  {CAUSALITIES.map((c) => (
                    <option key={c} value={c}>
                      {causalityLabel[c]}
                    </option>
                  ))}
                </Select>
              </Field>
              <Field label="Sent on" required>
                <Input
                  type="date"
                  value={submittedOn}
                  min={report.learnedOn}
                  max={todayIso()}
                  onChange={(_, d) => setSubmittedOn(d.value)}
                />
              </Field>
              <Field label="AMC or VigiFlow reference">
                <Input value={reference} maxLength={64} onChange={(_, d) => setReference(d.value)} />
              </Field>
              <Caption1>
                Once sent, the report cannot change here; a follow-up is a new report that names this one.
              </Caption1>
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={save.isPending}
                icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Record as sent
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
