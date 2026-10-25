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
  Switch,
  Tab,
  TabList,
  Text,
  Textarea,
} from '@fluentui/react-components';
import { AddRegular, ArrowExitRegular, PrintRegular } from '@fluentui/react-icons';
import { formatDateTime, todayIso } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { useServerDocuments } from '@/lib/documents';
import { usePermission } from '@/lib/permissions';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { fromIstInput } from '@/modules/inpatient/time';
import { errorMessage } from '@/modules/lab/common/errors';
import {
  MORTUARY_PERMISSIONS,
  MORTUARY_READERS,
  mortuaryApi,
  mortuaryKeys,
  mortuaryQueries,
  parseSlotCodes,
  RELEASE_LABELS,
  releaseProblems,
  SOURCE_LABELS,
  type Body,
  type BodySource,
  type BodyStatus,
  type MortuarySlot,
  type ReleaseTo,
} from '@/modules/mortuary/api';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';

type MortuaryTab = 'slots' | 'register' | 'setup';
const TABS: MortuaryTab[] = ['slots', 'register', 'setup'];

export const Route = createFileRoute('/_app/mortuary/')({
  validateSearch: (s: Record<string, unknown>): { tab?: MortuaryTab } => ({
    tab: TABS.includes(s.tab as MortuaryTab) ? (s.tab as MortuaryTab) : undefined,
  }),
  component: () => (
    <RequirePermission permission={MORTUARY_READERS} what="the mortuary">
      <MortuaryPage />
    </RequirePermission>
  ),
});

const daysAgo = (days: number) => {
  const d = new Date(`${todayIso()}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() - days);
  return d.toISOString().slice(0, 10);
};

const blankToNull = (s: string) => (s.trim() ? s.trim() : null);

/**
 * The mortuary: who is in which cold-storage slot, bodies received and released with the authority each needs, and the
 * register. A body kept beyond 72 hours unreleased is unclaimed: the police are to be told.
 */
function MortuaryPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canManage = usePermission(MORTUARY_PERMISSIONS.manage);
  const canConfigure = usePermission(MORTUARY_PERMISSIONS.configure);
  const tabs = TABS.filter((t) => (t === 'setup' ? canConfigure : t === 'register' ? canManage : true));
  const tab = search.tab && tabs.includes(search.tab) ? search.tab : 'slots';

  return (
    <Page>
      <PageHeader
        title="Mortuary"
        subtitle="Receive a body into a cold-storage slot, and release it to the relatives, the police, a medical college or the municipality on the authority its case needs."
      />
      <TabList selectedValue={tab} onTabSelect={(_, d) => void navigate({ search: { tab: d.value as MortuaryTab } })}>
        <Tab value="slots">Cold storage</Tab>
        {canManage && <Tab value="register">Register</Tab>}
        {canConfigure && <Tab value="setup">Set up slots</Tab>}
      </TabList>
      {tab === 'slots' && <SlotsTab />}
      {tab === 'register' && <RegisterTab />}
      {tab === 'setup' && <SetupTab />}
    </Page>
  );
}

function SlotsTab() {
  const canManage = usePermission(MORTUARY_PERMISSIONS.manage);
  const slots = useQuery(mortuaryQueries.slots());
  const [receiving, setReceiving] = useState(false);
  const [releasing, setReleasing] = useState<string | null>(null);
  const active = (slots.data ?? []).filter((s) => s.isActive || s.bodyId);
  const free = active.filter((s) => s.isActive && !s.bodyId);
  const unclaimed = active.filter((s) => s.unclaimed).length;

  return (
    <Section
      flush
      actions={
        canManage && (
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setReceiving(true)} disabled={!free.length}>
            Receive a body
          </Button>
        )
      }
    >
      <div style={{ padding: '8px 16px 12px', display: 'flex', flexDirection: 'column', gap: 8 }}>
        {slots.data && (
          <Caption1>
            {active.length - free.length} of {active.length} slots occupied
          </Caption1>
        )}
        {unclaimed > 0 && (
          <MessageBar intent="warning">
            <MessageBarBody>
              {unclaimed === 1 ? 'One body has' : `${unclaimed} bodies have`} been kept beyond 72 hours unclaimed. Tell
              the police station so they can trace the family or clear disposal.
            </MessageBarBody>
          </MessageBar>
        )}
      </div>
      <DataTable<MortuarySlot>
        ariaLabel="Cold-storage slots"
        rows={slots.data ? active : undefined}
        loading={slots.isLoading}
        error={slots.error}
        onRetry={() => void slots.refetch()}
        getRowId={(s) => s.id}
        size="small"
        columns={[
          { key: 'code', header: 'Slot', width: 100, render: (s) => <Text weight="semibold">{s.code}</Text> },
          { key: 'chamber', header: 'Chamber', width: 140, render: (s) => s.chamber ?? '—' },
          {
            key: 'body',
            header: 'Body',
            render: (s) =>
              s.bodyId ? (
                <span>
                  {s.name}
                  <Caption1 block>{s.bodyNumber}</Caption1>
                </span>
              ) : (
                <Badge appearance="tint" color="success">
                  Free
                </Badge>
              ),
          },
          {
            key: 'received',
            header: 'Received',
            width: 200,
            render: (s) =>
              s.receivedAt && (
                <span>
                  {formatDateTime(s.receivedAt)}
                  {s.unclaimed && (
                    <Badge size="small" appearance="filled" color="danger" style={{ marginLeft: 6 }}>
                      Unclaimed
                    </Badge>
                  )}
                </span>
              ),
          },
        ]}
        rowActions={(s) =>
          canManage &&
          s.bodyId && (
            <Button size="small" icon={<ArrowExitRegular />} onClick={() => setReleasing(s.bodyId)}>
              Release
            </Button>
          )
        }
        emptyTitle="No slots set up"
        emptyDescription="The administrator sets up the cold-storage slots under Set up slots."
      />
      {receiving && <ReceiveDialog slots={free} onClose={() => setReceiving(false)} />}
      {releasing && <ReleaseDialog bodyId={releasing} onClose={() => setReleasing(null)} />}
    </Section>
  );
}

function ReceiveDialog({ slots, onClose }: { slots: MortuarySlot[]; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [source, setSource] = useState<BodySource>('InHospitalDeath');
  const [patient, setPatient] = useState<PickedPatient | null>(null);
  const [name, setName] = useState('');
  const [identified, setIdentified] = useState(true);
  const [sex, setSex] = useState('');
  const [age, setAge] = useState('');
  const [diedAt, setDiedAt] = useState('');
  const [broughtBy, setBroughtBy] = useState('');
  const [isMlc, setIsMlc] = useState(false);
  const [mlcNumber, setMlcNumber] = useState('');
  const [policeStation, setPoliceStation] = useState('');
  const [marks, setMarks] = useState('');
  const [belongings, setBelongings] = useState('');
  const [slotId, setSlotId] = useState(slots[0]?.id ?? '');
  const [tried, setTried] = useState(false);
  const linked = source === 'InHospitalDeath';
  const needsPolice = isMlc || source === 'Police';
  const problems = {
    patient: linked && !patient ? 'Choose the patient who died in the hospital.' : null,
    name: !linked && !name.trim() ? 'Give the name, or describe an unidentified body.' : null,
    police: needsPolice && !policeStation.trim() ? 'Name the police station.' : null,
    slot: slotId ? null : 'Choose a free slot.',
  };
  const valid = Object.values(problems).every((p) => p === null);
  const show = (p: string | null) => (tried ? (p ?? undefined) : undefined);
  const save = useMutation({
    mutationFn: () =>
      mortuaryApi.receive({
        source,
        patientId: linked ? (patient?.id ?? null) : null,
        name: linked ? null : name.trim(),
        identified: linked || identified,
        sex: linked ? null : blankToNull(sex),
        ageYears: linked || !age.trim() ? null : Number(age),
        diedAt: linked ? null : fromIstInput(diedAt),
        broughtBy: blankToNull(broughtBy),
        isMlc: isMlc || source === 'Police',
        mlcNumber: blankToNull(mlcNumber),
        policeStation: blankToNull(policeStation),
        identificationMarks: blankToNull(marks),
        belongings: blankToNull(belongings),
        slotId,
      }),
    onSuccess: (body) => {
      void queryClient.invalidateQueries({ queryKey: mortuaryKeys.all });
      notify.success('Body received', `${body.number} in slot ${body.slotCode}.`);
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !save.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 620 }}>
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
            <DialogTitle>Receive a body</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {save.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(save.error, 'The body could not be received.')}</MessageBarBody>
                </MessageBar>
              )}
              <Field label="Received from" required>
                <Select value={source} onChange={(_, d) => setSource(d.value as BodySource)}>
                  {(Object.keys(SOURCE_LABELS) as BodySource[]).map((s) => (
                    <option key={s} value={s}>
                      {SOURCE_LABELS[s]}
                    </option>
                  ))}
                </Select>
              </Field>
              {linked ? (
                <PatientPicker
                  value={patient}
                  onChange={setPatient}
                  required
                  validationMessage={show(problems.patient)}
                  hint="The death must be certified, or the stay discharged as expired, first. A medico-legal visit makes the body medico-legal."
                />
              ) : (
                <>
                  <Field
                    label="Name"
                    required
                    validationMessage={show(problems.name)}
                    hint='For an unidentified body, a description such as "Unknown male, about 40".'
                  >
                    <Input value={name} maxLength={200} onChange={(_, d) => setName(d.value)} />
                  </Field>
                  <Checkbox checked={identified} onChange={(_, d) => setIdentified(!!d.checked)} label="Identified" />
                  <div style={{ display: 'flex', gap: 12 }}>
                    <Field label="Sex" style={{ flex: 1 }}>
                      <Select value={sex} onChange={(_, d) => setSex(d.value)}>
                        <option value="">Not known</option>
                        <option value="Male">Male</option>
                        <option value="Female">Female</option>
                        <option value="Other">Other</option>
                      </Select>
                    </Field>
                    <Field label="Age (years)" style={{ flex: 1 }}>
                      <Input type="number" min={0} max={130} value={age} onChange={(_, d) => setAge(d.value)} />
                    </Field>
                    <Field label="Died (if known)" style={{ flex: 2 }}>
                      <Input type="datetime-local" value={diedAt} onChange={(_, d) => setDiedAt(d.value)} />
                    </Field>
                  </div>
                </>
              )}
              <Field label="Brought by">
                <Input value={broughtBy} maxLength={200} onChange={(_, d) => setBroughtBy(d.value)} />
              </Field>
              {source !== 'Police' && (
                <Switch checked={isMlc} onChange={(_, d) => setIsMlc(d.checked)} label="Medico-legal case" />
              )}
              {needsPolice && (
                <div style={{ display: 'flex', gap: 12 }}>
                  <Field label="MLC / FIR number" style={{ flex: 1 }}>
                    <Input value={mlcNumber} maxLength={64} onChange={(_, d) => setMlcNumber(d.value)} />
                  </Field>
                  <Field label="Police station" required validationMessage={show(problems.police)} style={{ flex: 2 }}>
                    <Input value={policeStation} maxLength={200} onChange={(_, d) => setPoliceStation(d.value)} />
                  </Field>
                </div>
              )}
              <Field label="Identification marks">
                <Textarea value={marks} maxLength={1000} onChange={(_, d) => setMarks(d.value)} />
              </Field>
              <Field label="Belongings received with the body" hint="Jewellery, money, clothes, phone…">
                <Textarea value={belongings} maxLength={2000} onChange={(_, d) => setBelongings(d.value)} />
              </Field>
              <Field label="Slot" required validationMessage={show(problems.slot)}>
                <Select value={slotId} onChange={(_, d) => setSlotId(d.value)}>
                  {slots.map((s) => (
                    <option key={s.id} value={s.id}>
                      {s.code}
                      {s.chamber ? ` · ${s.chamber}` : ''}
                    </option>
                  ))}
                </Select>
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
                Receive
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

function ReleaseDialog({ bodyId, onClose }: { bodyId: string; onClose: () => void }) {
  const body = useQuery({ queryKey: ['mortuary', 'body', bodyId], queryFn: () => mortuaryApi.get(bodyId) });
  return body.data ? (
    <ReleaseForm body={body.data} onClose={onClose} />
  ) : (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 400 }}>
        <DialogBody>
          <DialogTitle>Release</DialogTitle>
          <DialogContent>
            {body.error ? (
              <MessageBar intent="error">
                <MessageBarBody>{errorMessage(body.error, 'The body could not be opened.')}</MessageBarBody>
              </MessageBar>
            ) : (
              <Spinner label="Opening…" />
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

function ReleaseForm({ body, onClose }: { body: Body; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const { print } = useServerDocuments();
  const [to, setTo] = useState<ReleaseTo | ''>('');
  const [receivedBy, setReceivedBy] = useState('');
  const [relationship, setRelationship] = useState('');
  const [idProof, setIdProof] = useState('');
  const [policeClearance, setPoliceClearance] = useState('');
  const [handedOver, setHandedOver] = useState(true);
  const [notes, setNotes] = useState('');
  const [tried, setTried] = useState(false);
  const problems = releaseProblems(body, { to, receivedBy, relationship, idProof, policeClearance });
  const valid = Object.values(problems).every((p) => p === null);
  const show = (p: string | null) => (tried ? (p ?? undefined) : undefined);
  const save = useMutation({
    mutationFn: () =>
      mortuaryApi.release(body.id, {
        to: to as ReleaseTo,
        receivedBy: receivedBy.trim(),
        relationship: blankToNull(relationship),
        idProof: blankToNull(idProof),
        policeClearance: blankToNull(policeClearance),
        belongingsHandedOver: handedOver,
        notes: blankToNull(notes),
        version: body.version,
      }),
    onSuccess: (released) => {
      void queryClient.invalidateQueries({ queryKey: mortuaryKeys.all });
      notify.success('Body released', released.number);
      print.mutate(mortuaryApi.slip(released.id));
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !save.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
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
              Release {body.name} ({body.number})
            </DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {save.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(save.error, 'The body could not be released.')}</MessageBarBody>
                </MessageBar>
              )}
              {body.isMlc && (
                <MessageBar intent="warning">
                  <MessageBarBody>
                    Medico-legal ({body.mlcNumber ?? 'no number'}, {body.policeStation}): to the family only on the
                    police&apos;s clearance; never for donation.
                  </MessageBarBody>
                </MessageBar>
              )}
              <Field label="Released to" required validationMessage={show(problems.to)}>
                <Select value={to} onChange={(_, d) => setTo(d.value as ReleaseTo | '')}>
                  <option value="">Choose…</option>
                  {(Object.keys(RELEASE_LABELS) as ReleaseTo[]).map((r) => (
                    <option key={r} value={r}>
                      {RELEASE_LABELS[r]}
                    </option>
                  ))}
                </Select>
              </Field>
              <Field label="Received by" required validationMessage={show(problems.receivedBy)}>
                <Input value={receivedBy} maxLength={200} onChange={(_, d) => setReceivedBy(d.value)} />
              </Field>
              {to === 'Relatives' && (
                <Field label="Relationship" required validationMessage={show(problems.relationship)}>
                  <Input value={relationship} maxLength={64} onChange={(_, d) => setRelationship(d.value)} />
                </Field>
              )}
              {(to === 'Relatives' || to === 'PoliceForPostMortem') && (
                <Field
                  label={to === 'Relatives' ? 'Identity proof' : "Officer's name, number and station"}
                  required
                  validationMessage={show(problems.idProof)}
                >
                  <Input value={idProof} maxLength={200} onChange={(_, d) => setIdProof(d.value)} />
                </Field>
              )}
              {((to === 'Relatives' && body.isMlc) || to === 'Municipality') && (
                <Field
                  label="Police clearance"
                  required
                  validationMessage={show(problems.policeClearance)}
                  hint="The inquest or post-mortem reference, or the letter clearing disposal."
                >
                  <Input value={policeClearance} maxLength={200} onChange={(_, d) => setPoliceClearance(d.value)} />
                </Field>
              )}
              {body.belongings && (
                <Checkbox
                  checked={handedOver}
                  onChange={(_, d) => setHandedOver(!!d.checked)}
                  label={`Belongings handed over: ${body.belongings}`}
                />
              )}
              <Field label="Notes">
                <Textarea value={notes} maxLength={1000} onChange={(_, d) => setNotes(d.value)} />
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
                Release and print the slip
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

function RegisterTab() {
  const [status, setStatus] = useState<BodyStatus | ''>('');
  const [from, setFrom] = useState(daysAgo(30));
  const [to, setTo] = useState(todayIso());
  const register = useQuery(mortuaryQueries.register(status || null, from, to));
  const { print } = useServerDocuments();

  return (
    <Section flush>
      <FilterBar>
        <Field label="Status">
          <Select value={status} onChange={(_, d) => setStatus(d.value as BodyStatus | '')}>
            <option value="">All received</option>
            <option value="InStorage">In storage</option>
            <option value="Released">Released</option>
          </Select>
        </Field>
        <Field label="From">
          <Input type="date" value={from} onChange={(_, d) => setFrom(d.value)} disabled={status === 'InStorage'} />
        </Field>
        <Field label="To">
          <Input type="date" value={to} onChange={(_, d) => setTo(d.value)} disabled={status === 'InStorage'} />
        </Field>
      </FilterBar>
      <DataTable<Body>
        ariaLabel="Mortuary register"
        rows={register.data}
        loading={register.isLoading}
        error={register.error}
        onRetry={() => void register.refetch()}
        getRowId={(b) => b.id}
        size="small"
        columns={[
          {
            key: 'number',
            header: 'Register no.',
            width: 140,
            render: (b) => <Text font="monospace">{b.number}</Text>,
          },
          {
            key: 'name',
            header: 'Name',
            render: (b) => (
              <span>
                {b.name}
                {!b.identified && (
                  <Badge size="small" appearance="tint" color="warning" style={{ marginLeft: 6 }}>
                    Unidentified
                  </Badge>
                )}
                {b.isMlc && (
                  <Badge size="small" appearance="tint" color="danger" style={{ marginLeft: 6 }}>
                    MLC
                  </Badge>
                )}
                <Caption1 block>{[b.uhid, SOURCE_LABELS[b.source]].filter(Boolean).join(' · ')}</Caption1>
              </span>
            ),
          },
          {
            key: 'received',
            header: 'Received',
            width: 190,
            render: (b) => (
              <span>
                {formatDateTime(b.receivedAt)}
                <Caption1 block>Slot {b.slotCode}</Caption1>
              </span>
            ),
          },
          {
            key: 'released',
            header: 'Released',
            width: 220,
            render: (b) =>
              b.releasedAt ? (
                <span>
                  {formatDateTime(b.releasedAt)}
                  <Caption1 block>
                    {b.releasedTo && RELEASE_LABELS[b.releasedTo]}: {b.receivedBy}
                  </Caption1>
                </span>
              ) : (
                <Badge appearance="tint" color={b.unclaimed ? 'danger' : 'informative'}>
                  {b.unclaimed ? 'Unclaimed' : 'In storage'}
                </Badge>
              ),
          },
        ]}
        rowActions={(b) =>
          b.status === 'Released' && (
            <Button size="small" icon={<PrintRegular />} onClick={() => print.mutate(mortuaryApi.slip(b.id))}>
              Slip
            </Button>
          )
        }
        emptyTitle="No bodies in this period"
      />
    </Section>
  );
}

function SetupTab() {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const slots = useQuery(mortuaryQueries.slots());
  const [codes, setCodes] = useState('');
  const [chamber, setChamber] = useState('');
  const save = useMutation({
    mutationFn: (input: Parameters<typeof mortuaryApi.saveSlots>[0]) => mortuaryApi.saveSlots(input),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: mortuaryKeys.all });
      notify.success('Slots saved');
      setCodes('');
    },
  });
  const newCodes = parseSlotCodes(codes);

  return (
    <Section flush>
      <div style={{ padding: '8px 16px 12px', display: 'flex', gap: 12, alignItems: 'end', flexWrap: 'wrap' }}>
        <Field label="New slots" hint="Labels on the chamber doors, one per line or separated by commas.">
          <Textarea value={codes} onChange={(_, d) => setCodes(d.value)} placeholder={'A-1, A-2, A-3'} />
        </Field>
        <Field label="Chamber">
          <Input value={chamber} maxLength={100} onChange={(_, d) => setChamber(d.value)} />
        </Field>
        <Button
          appearance="primary"
          icon={<AddRegular />}
          disabled={!newCodes.length || save.isPending}
          onClick={() =>
            save.mutate(
              newCodes.map((code) => ({
                id: null,
                code,
                chamber: blankToNull(chamber),
                isActive: true,
                version: null,
              })),
            )
          }
        >
          Add slots
        </Button>
      </div>
      {save.error && (
        <MessageBar intent="error">
          <MessageBarBody>{errorMessage(save.error, 'The slots could not be saved.')}</MessageBarBody>
        </MessageBar>
      )}
      <DataTable<MortuarySlot>
        ariaLabel="Slots"
        rows={slots.data}
        loading={slots.isLoading}
        error={slots.error}
        onRetry={() => void slots.refetch()}
        getRowId={(s) => s.id}
        size="small"
        columns={[
          { key: 'code', header: 'Slot', width: 120, render: (s) => s.code },
          { key: 'chamber', header: 'Chamber', render: (s) => s.chamber ?? '—' },
          { key: 'body', header: 'Holds', width: 160, render: (s) => s.bodyNumber ?? '—' },
          {
            key: 'active',
            header: 'In use',
            width: 120,
            render: (s) => (
              <Switch
                aria-label={`Slot ${s.code} in use`}
                checked={s.isActive}
                disabled={save.isPending || (!!s.bodyId && s.isActive)}
                onChange={(_, d) =>
                  save.mutate([{ id: s.id, code: s.code, chamber: s.chamber, isActive: d.checked, version: s.version }])
                }
              />
            ),
          },
        ]}
        emptyTitle="No slots yet"
        emptyDescription="Add the slots of each cold chamber above."
      />
    </Section>
  );
}
