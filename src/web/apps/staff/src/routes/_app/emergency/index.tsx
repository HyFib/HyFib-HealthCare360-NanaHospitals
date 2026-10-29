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
  Radio,
  RadioGroup,
  Select,
  Spinner,
  Tab,
  TabList,
  Text,
  Textarea,
} from '@fluentui/react-components';
import { PrintRegular } from '@fluentui/react-icons';
import { formatDateTime, useRealtimeEvent, useSession } from '@hc360/core';
import { DataTable, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { useServerDocuments } from '@/lib/documents';
import { usePermission } from '@/lib/permissions';
import {
  ARRIVAL_LABELS,
  BROUGHT_BY_LABELS,
  CAUSE_LABELS,
  DISPOSITION_LABELS,
  doctorWaitBreached,
  ER_PERMISSIONS,
  ER_READERS,
  erApi,
  erKeys,
  erQueries,
  ESI_LEVELS,
  formatMinutes,
  type ArrivalMode,
  type EmergencyCase,
  type EmergencyDisposition,
  type ErBoardRow,
  type MlcBroughtBy,
  type MlcCause,
  type MlcIntimationMode,
  type TriageInput,
} from '@/modules/emergency/api';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { errorMessage } from '@/modules/lab/common/errors';

export const Route = createFileRoute('/_app/emergency/')({
  component: () => (
    <RequirePermission permission={ER_READERS} what="the ER board">
      <ErBoardPage />
    </RequirePermission>
  ),
});

function LevelBadge({ level }: { level: number | null }) {
  if (level === null) {
    return <StatusBadge status="Untriaged" tone="subtle" label="Not triaged" />;
  }
  const esi = ESI_LEVELS[level]!;
  return <StatusBadge status={`ESI ${level}`} tone={esi.tone} label={`ESI ${level} · ${esi.name}`} />;
}

function ErBoardPage() {
  const facilityId = useSession((s) => s.user?.facilityId);
  const queryClient = useQueryClient();
  const board = useQuery({ ...erQueries.board(), refetchInterval: 60_000 });
  const [open, setOpen] = useState<string | null>(null);
  useRealtimeEvent(facilityId ? `facility:${facilityId}` : null, 'er.changed', () => {
    void queryClient.invalidateQueries({ queryKey: erKeys.all });
  });
  const data = board.data;

  return (
    <Page>
      <PageHeader
        title="ER board"
        subtitle={
          data
            ? `${data.rows.length} in the department · ${data.untriaged} waiting for triage · ` +
              data.byLevel.map((n, i) => `ESI ${i + 1}: ${n}`).join(' · ')
            : 'Patients in the emergency department, most urgent first'
        }
      />
      <Section flush>
        <DataTable<ErBoardRow>
          ariaLabel="Patients in the emergency department"
          rows={data?.rows}
          loading={board.isLoading}
          error={board.error}
          onRetry={() => void board.refetch()}
          getRowId={(r) => r.caseId}
          onRowClick={(r) => setOpen(r.caseId)}
          size="small"
          emptyTitle="Nobody in the emergency department"
          columns={[
            { key: 'level', header: 'Triage', width: 190, render: (r) => <LevelBadge level={r.level} /> },
            {
              key: 'patient',
              header: 'Patient',
              render: (r) => (
                <div>
                  <Text weight="semibold">{r.patientName}</Text>
                  <Caption1 style={{ display: 'block' }}>
                    {r.uhid}
                    {r.ageYears !== null ? ` · ${r.ageYears} y` : ''}
                    {r.gender ? ` · ${r.gender}` : ''} · {ARRIVAL_LABELS[r.arrivalMode]}
                  </Caption1>
                </div>
              ),
            },
            { key: 'complaint', header: 'Complaint', render: (r) => r.chiefComplaint ?? '—' },
            { key: 'in', header: 'In ER', width: 110, render: (r) => formatMinutes(r.minutesInDepartment) },
            {
              key: 'doctor',
              header: 'Doctor',
              width: 170,
              render: (r) =>
                r.doctorName ? (
                  `${r.doctorName} (${formatMinutes(r.doorToDoctorMinutes ?? 0)})`
                ) : doctorWaitBreached(r.level, r.minutesInDepartment, false) ? (
                  <StatusBadge status="Waiting" tone="danger" label="Needs a doctor now" />
                ) : (
                  'Waiting'
                ),
            },
            {
              key: 'mlc',
              header: 'MLC',
              width: 150,
              render: (r) =>
                r.isMlc ? (
                  <StatusBadge
                    status="MLC"
                    tone={r.mlcIntimationOverdue ? 'danger' : 'warning'}
                    label={r.mlcIntimationOverdue ? 'MLC: police not told' : `MLC ${r.mlcNumber ?? ''}`}
                  />
                ) : null,
            },
          ]}
        />
      </Section>
      {open && <CaseDialog caseId={open} onClose={() => setOpen(null)} />}
    </Page>
  );
}

type CaseTab = 'triage' | 'doctor' | 'mlc';

function CaseDialog({ caseId, onClose }: { caseId: string; onClose: () => void }) {
  const detail = useQuery(erQueries.case(caseId));
  const canTriage = usePermission(ER_PERMISSIONS.triage);
  const canDecide = usePermission(ER_PERMISSIONS.disposition);
  const canMlc = usePermission(ER_PERMISSIONS.mlc);
  const [tab, setTab] = useState<CaseTab>('triage');
  const c = detail.data;

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 820 }}>
        <DialogBody>
          <DialogTitle>
            {c ? `${c.patientName} · ${c.uhid}` : 'Emergency case'} {c && <LevelBadge level={c.level} />}
          </DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            {detail.isLoading && <Spinner size="small" label="Loading the case…" />}
            {detail.error && (
              <MessageBar intent="error">
                <MessageBarBody>{errorMessage(detail.error, 'The case could not be loaded.')}</MessageBarBody>
              </MessageBar>
            )}
            {c && (
              <>
                <Caption1>
                  Arrived {formatDateTime(c.arrivedAt)} · {ARRIVAL_LABELS[c.arrivalMode]}
                  {c.doctorName ? ` · seen by ${c.doctorName} at ${formatDateTime(c.doctorSeenAt!)}` : ''}
                  {c.disposition ? ` · ${DISPOSITION_LABELS[c.disposition]} ${formatDateTime(c.disposedAt!)}` : ''}
                </Caption1>
                <TabList selectedValue={tab} onTabSelect={(_, d) => setTab(d.value as CaseTab)}>
                  <Tab value="triage">Triage</Tab>
                  <Tab value="doctor">Doctor</Tab>
                  <Tab value="mlc">Medico-legal</Tab>
                </TabList>
                {tab === 'triage' && <TriageTab c={c} canTriage={canTriage && !c.disposition} />}
                {tab === 'doctor' && <DoctorTab c={c} canDecide={canDecide} />}
                {tab === 'mlc' && <MlcTab c={c} canMlc={canMlc} />}
              </>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>Close</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

const num = (value: string): number | null => {
  const n = Number(value);
  return value.trim() === '' || !Number.isFinite(n) ? null : n;
};

function TriageTab({ c, canTriage }: { c: EmergencyCase; canTriage: boolean }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const last = c.triages[0];
  const [complaint, setComplaint] = useState(last?.chiefComplaint ?? '');
  const [arrival, setArrival] = useState<ArrivalMode>(c.arrivalMode);
  const [lifeSaving, setLifeSaving] = useState(false);
  const [highRisk, setHighRisk] = useState(false);
  const [confused, setConfused] = useState(false);
  const [distress, setDistress] = useState(false);
  const [resources, setResources] = useState('2');
  const [vitals, setVitals] = useState({ pulse: '', rr: '', spo2: '', sbp: '', dbp: '', temp: '', gcs: '', pain: '' });
  const [level, setLevel] = useState('');
  const [reason, setReason] = useState('');

  const input: TriageInput = {
    chiefComplaint: complaint.trim(),
    arrivalMode: arrival,
    needsLifeSavingIntervention: lifeSaving,
    highRiskSituation: highRisk,
    confusedLethargicOrDisoriented: confused,
    severeDistress: distress,
    expectedResources: Number(resources),
    pulse: num(vitals.pulse),
    respiratoryRate: num(vitals.rr),
    spO2: num(vitals.spo2),
    systolicBp: num(vitals.sbp),
    diastolicBp: num(vitals.dbp),
    temperatureCelsius: num(vitals.temp),
    gcs: num(vitals.gcs),
    painScore: num(vitals.pain),
    level: level ? Number(level) : null,
    overrideReason: reason.trim() || null,
    version: c.version,
  };
  const preview = useQuery({
    queryKey: ['emergency', 'preview', c.id, { ...input, level: null, overrideReason: null, chiefComplaint: '' }],
    queryFn: () => erApi.preview(c.id, { ...input, level: null, overrideReason: null }),
    enabled: canTriage,
    placeholderData: keepPreviousData,
  });
  const computed = preview.data?.level;
  const overriding = level !== '' && computed !== undefined && Number(level) !== computed;
  const save = useMutation({
    mutationFn: () => erApi.triage(c.id, input),
    onSuccess: () => {
      notify.success('Triaged', 'The level is on the board; the vitals are on the chart.');
      void queryClient.invalidateQueries({ queryKey: erKeys.all });
    },
  });
  const field = (key: keyof typeof vitals, label: string) => (
    <Field label={label}>
      <Input inputMode="decimal" value={vitals[key]} onChange={(_, d) => setVitals({ ...vitals, [key]: d.value })} />
    </Field>
  );

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
      {c.triages.map((t) => (
        <Caption1 key={t.id}>
          {formatDateTime(t.assessedAt)} · {t.assessedByName}: ESI {t.level}
          {t.level !== t.computedLevel ? ` (algorithm ${t.computedLevel}: ${t.overrideReason})` : ''} ·{' '}
          {t.chiefComplaint}
        </Caption1>
      ))}
      {canTriage && (
        <>
          <div style={{ display: 'grid', gridTemplateColumns: '2fr 1fr', gap: 8 }}>
            <Field label="Chief complaint" required>
              <Input value={complaint} maxLength={300} onChange={(_, d) => setComplaint(d.value)} />
            </Field>
            <Field label="Arrival">
              <Select value={arrival} onChange={(_, d) => setArrival(d.value as ArrivalMode)}>
                {Object.entries(ARRIVAL_LABELS).map(([value, label]) => (
                  <option key={value} value={value}>
                    {label}
                  </option>
                ))}
              </Select>
            </Field>
          </div>
          <Checkbox
            checked={lifeSaving}
            onChange={(_, d) => setLifeSaving(Boolean(d.checked))}
            label="A · Needs a life-saving intervention now"
          />
          <Checkbox
            checked={highRisk}
            onChange={(_, d) => setHighRisk(Boolean(d.checked))}
            label="B · High-risk situation"
          />
          <Checkbox
            checked={confused}
            onChange={(_, d) => setConfused(Boolean(d.checked))}
            label="B · New confusion, lethargy or disorientation"
          />
          <Checkbox
            checked={distress}
            onChange={(_, d) => setDistress(Boolean(d.checked))}
            label="B · Severe pain or distress"
          />
          <Field label="C · Resources expected (labs, imaging, IV fluids or drugs, a specialist, a procedure)">
            <RadioGroup layout="horizontal" value={resources} onChange={(_, d) => setResources(d.value)}>
              <Radio value="0" label="None" />
              <Radio value="1" label="One" />
              <Radio value="2" label="Many" />
            </RadioGroup>
          </Field>
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: 8 }}>
            {field('pulse', 'Pulse /min')}
            {field('rr', 'Breathing /min')}
            {field('spo2', 'SpO2 %')}
            {field('temp', 'Temp °C')}
            {field('sbp', 'BP systolic')}
            {field('dbp', 'BP diastolic')}
            {field('gcs', 'GCS (3–15)')}
            {field('pain', 'Pain (0–10)')}
          </div>
          {preview.data && (
            <MessageBar intent={preview.data.level <= 2 ? 'warning' : 'info'}>
              <MessageBarBody>
                ESI {preview.data.level} · {ESI_LEVELS[preview.data.level]!.name}: {preview.data.reasons.join(' ')}
              </MessageBarBody>
            </MessageBar>
          )}
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 2fr', gap: 8 }}>
            <Field label="Level">
              <Select value={level} onChange={(_, d) => setLevel(d.value)}>
                <option value="">As the algorithm gives</option>
                {[1, 2, 3, 4, 5].map((l) => (
                  <option key={l} value={l}>
                    ESI {l} · {ESI_LEVELS[l]!.name}
                  </option>
                ))}
              </Select>
            </Field>
            {overriding && (
              <Field label="Why this level" required>
                <Input value={reason} maxLength={300} onChange={(_, d) => setReason(d.value)} />
              </Field>
            )}
          </div>
          {save.error && (
            <MessageBar intent="error">
              <MessageBarBody>{errorMessage(save.error, 'The triage could not be saved.')}</MessageBarBody>
            </MessageBar>
          )}
          <Button
            appearance="primary"
            style={{ alignSelf: 'flex-start' }}
            disabled={!complaint.trim() || (overriding && !reason.trim()) || save.isPending}
            onClick={() => save.mutate()}
          >
            Save triage
          </Button>
        </>
      )}
    </div>
  );
}

function DoctorTab({ c, canDecide }: { c: EmergencyCase; canDecide: boolean }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [disposition, setDisposition] = useState<EmergencyDisposition>('Discharged');
  const [notes, setNotes] = useState('');
  const [referredTo, setReferredTo] = useState('');
  const refresh = () => void queryClient.invalidateQueries({ queryKey: erKeys.all });
  const seen = useMutation({ mutationFn: () => erApi.seen(c.id, c.version), onSuccess: refresh });
  const dispose = useMutation({
    mutationFn: () => erApi.dispose(c.id, disposition, notes.trim() || null, referredTo.trim() || null, c.version),
    onSuccess: () => {
      notify.success('Disposition recorded', 'The patient is off the board and the emergency visit is closed.');
      refresh();
    },
  });
  if (c.disposition) {
    return (
      <Text>
        {DISPOSITION_LABELS[c.disposition]} by {c.disposedByName} at {formatDateTime(c.disposedAt!)}
        {c.referredTo ? ` to ${c.referredTo}` : ''}
        {c.dispositionNotes ? `: ${c.dispositionNotes}` : ''}
      </Text>
    );
  }
  if (!canDecide) {
    return <Caption1>{c.doctorName ? `Seen by ${c.doctorName}.` : 'Not yet seen by a doctor.'}</Caption1>;
  }
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
      {!c.doctorSeenAt && (
        <Button style={{ alignSelf: 'flex-start' }} disabled={seen.isPending} onClick={() => seen.mutate()}>
          I am seeing this patient
        </Button>
      )}
      <Field label="Disposition">
        <Select value={disposition} onChange={(_, d) => setDisposition(d.value as EmergencyDisposition)}>
          {Object.entries(DISPOSITION_LABELS).map(([value, label]) => (
            <option key={value} value={value}>
              {label}
            </option>
          ))}
        </Select>
      </Field>
      {disposition === 'ReferredOut' && (
        <Field label="Referred to" required>
          <Input value={referredTo} maxLength={200} onChange={(_, d) => setReferredTo(d.value)} />
        </Field>
      )}
      <Field
        label="Notes"
        required={disposition === 'Lama' || disposition === 'Absconded' || disposition === 'Died'}
        hint={
          disposition === 'Admitted'
            ? 'Then admit the patient from the bed board; the stay goes on as its own visit.'
            : undefined
        }
      >
        <Textarea value={notes} maxLength={1000} onChange={(_, d) => setNotes(d.value)} />
      </Field>
      {dispose.error && (
        <MessageBar intent="error">
          <MessageBarBody>{errorMessage(dispose.error, 'The disposition could not be recorded.')}</MessageBarBody>
        </MessageBar>
      )}
      <Button
        appearance="primary"
        style={{ alignSelf: 'flex-start' }}
        disabled={dispose.isPending}
        onClick={() => dispose.mutate()}
      >
        Record disposition
      </Button>
    </div>
  );
}

function MlcTab({ c, canMlc }: { c: EmergencyCase; canMlc: boolean }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const { print } = useServerDocuments();
  const m = c.mlc;
  const [broughtBy, setBroughtBy] = useState<MlcBroughtBy>(m?.broughtBy ?? 'Relative');
  const [broughtByName, setBroughtByName] = useState(m?.broughtByName ?? '');
  const [cause, setCause] = useState<MlcCause>(m?.cause ?? 'RoadTrafficAccident');
  const [incidentPlace, setIncidentPlace] = useState(m?.incidentPlace ?? '');
  const [injuries, setInjuries] = useState(m?.injuries ?? '');
  const [policeStation, setPoliceStation] = useState(m?.policeStation ?? '');
  const [registration, setRegistration] = useState(m?.doctorRegistrationNumber ?? '');
  const [mode, setMode] = useState<MlcIntimationMode>('Phone');
  const [receivedBy, setReceivedBy] = useState('');
  const [entry, setEntry] = useState('');
  const refresh = () => void queryClient.invalidateQueries({ queryKey: erKeys.all });
  const save = useMutation({
    mutationFn: () =>
      erApi.saveMlc(c.id, {
        broughtBy,
        broughtByName: broughtByName.trim() || null,
        cause,
        incidentAt: null,
        incidentPlace: incidentPlace.trim() || null,
        injuries: injuries.trim(),
        consciousOnArrival: null,
        smellOfAlcohol: null,
        policeStation: policeStation.trim(),
        doctorRegistrationNumber: registration.trim() || null,
        version: m?.version ?? null,
      }),
    onSuccess: () => {
      notify.success('MLC register updated', 'Inform the police station and record who received it.');
      refresh();
    },
  });
  const intimate = useMutation({
    mutationFn: () =>
      erApi.intimate(c.id, new Date().toISOString(), mode, receivedBy.trim(), entry.trim() || null, m!.version),
    onSuccess: refresh,
  });
  const editable = canMlc && !m?.intimatedAt;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
      {m && (
        <Text>
          MLC {m.number} · identified {formatDateTime(m.identifiedAt)} by {m.doctorName}
          {m.intimatedAt
            ? ` · police informed ${formatDateTime(m.intimatedAt)} (${m.intimationMode}), received by ${m.receivedBy}`
            : m.intimationOverdue
              ? ' · police not yet informed: overdue'
              : ' · police not yet informed'}
        </Text>
      )}
      {!m && !canMlc && <Caption1>Not a medico-legal case.</Caption1>}
      {editable && (
        <>
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 8 }}>
            <Field label="Brought by">
              <Select value={broughtBy} onChange={(_, d) => setBroughtBy(d.value as MlcBroughtBy)}>
                {Object.entries(BROUGHT_BY_LABELS).map(([value, label]) => (
                  <option key={value} value={value}>
                    {label}
                  </option>
                ))}
              </Select>
            </Field>
            <Field
              label="Name (police: name and number)"
              required={broughtBy !== 'Self' && broughtBy !== 'Public'}
              hint={
                broughtBy === 'Public'
                  ? 'A Good Samaritan need not give their name or any other details (Motor Vehicles Act, s.134A).'
                  : undefined
              }
            >
              <Input value={broughtByName} maxLength={200} onChange={(_, d) => setBroughtByName(d.value)} />
            </Field>
            <Field label="Alleged cause">
              <Select value={cause} onChange={(_, d) => setCause(d.value as MlcCause)}>
                {Object.entries(CAUSE_LABELS).map(([value, label]) => (
                  <option key={value} value={value}>
                    {label}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Where it happened">
              <Input value={incidentPlace} maxLength={300} onChange={(_, d) => setIncidentPlace(d.value)} />
            </Field>
            <Field label="Police station" required>
              <Input value={policeStation} maxLength={200} onChange={(_, d) => setPoliceStation(d.value)} />
            </Field>
            <Field label="Your council registration no.">
              <Input value={registration} maxLength={64} onChange={(_, d) => setRegistration(d.value)} />
            </Field>
          </div>
          <Field label="Injuries as examined" required hint="Kind, site, size, and whether simple or grievous.">
            <Textarea value={injuries} rows={3} maxLength={4000} onChange={(_, d) => setInjuries(d.value)} />
          </Field>
          {save.error && (
            <MessageBar intent="error">
              <MessageBarBody>{errorMessage(save.error, 'The MLC entry could not be saved.')}</MessageBarBody>
            </MessageBar>
          )}
          <Button
            appearance="primary"
            style={{ alignSelf: 'flex-start' }}
            disabled={!injuries.trim() || !policeStation.trim() || save.isPending}
            onClick={() => save.mutate()}
          >
            {m ? 'Correct the MLC entry' : 'Enter in the MLC register'}
          </Button>
        </>
      )}
      {m && canMlc && !m.intimatedAt && (
        <div style={{ display: 'grid', gridTemplateColumns: '1fr 2fr 1fr auto', gap: 8, alignItems: 'end' }}>
          <Field label="Informed by">
            <Select value={mode} onChange={(_, d) => setMode(d.value as MlcIntimationMode)}>
              <option value="Phone">Phone</option>
              <option value="Written">Written</option>
              <option value="InPerson">In person</option>
            </Select>
          </Field>
          <Field label="Received by (officer and number)" required>
            <Input value={receivedBy} maxLength={200} onChange={(_, d) => setReceivedBy(d.value)} />
          </Field>
          <Field label="GD/DD entry no.">
            <Input value={entry} maxLength={64} onChange={(_, d) => setEntry(d.value)} />
          </Field>
          <Button disabled={!receivedBy.trim() || intimate.isPending} onClick={() => intimate.mutate()}>
            Police informed now
          </Button>
        </div>
      )}
      {m && (
        <Button
          style={{ alignSelf: 'flex-start' }}
          icon={print.isPending ? <Spinner size="tiny" aria-hidden /> : <PrintRegular />}
          onClick={() => print.mutate(erApi.mlcDocument(c.id))}
        >
          Print the intimation
        </Button>
      )}
    </div>
  );
}
