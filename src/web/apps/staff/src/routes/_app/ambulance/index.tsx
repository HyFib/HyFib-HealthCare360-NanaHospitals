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
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
  Switch,
  Tab,
  TabList,
  Text,
} from '@fluentui/react-components';
import { AddRegular, EditRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, todayIso } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState, type ReactNode } from 'react';
import { usePermission } from '@/lib/permissions';
import {
  AMBULANCE_PERMISSIONS,
  AMBULANCE_READERS,
  ambulanceApi,
  ambulanceKeys,
  ambulanceQueries,
  availableFor,
  chargedKm,
  INCLUDED_KM,
  KIND_LABELS,
  PURPOSE_LABELS,
  STATUS_LABELS,
  type AmbulanceKind,
  type AmbulanceTrip,
  type AmbulanceVehicle,
  type TripPurpose,
} from '@/modules/ambulance/api';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { errorMessage } from '@/modules/lab/common/errors';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';

type AmbulanceTab = 'dispatch' | 'log' | 'fleet';
const TABS: AmbulanceTab[] = ['dispatch', 'log', 'fleet'];

export const Route = createFileRoute('/_app/ambulance/')({
  validateSearch: (s: Record<string, unknown>): { tab?: AmbulanceTab } => ({
    tab: TABS.includes(s.tab as AmbulanceTab) ? (s.tab as AmbulanceTab) : undefined,
  }),
  component: () => (
    <RequirePermission permission={AMBULANCE_READERS} what="the ambulance desk">
      <AmbulancePage />
    </RequirePermission>
  ),
});

const blankToNull = (s: string) => (s.trim() ? s.trim() : null);

const statusColor: Record<AmbulanceTrip['status'], 'warning' | 'brand' | 'informative' | 'success' | 'subtle'> = {
  Requested: 'warning',
  Dispatched: 'brand',
  AtScene: 'brand',
  Transporting: 'informative',
  Completed: 'success',
  Cancelled: 'subtle',
};

/** The ambulance desk: calls waiting and vehicles out, each step as the crew report it, the fleet, and the trip log. */
function AmbulancePage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canDispatch = usePermission(AMBULANCE_PERMISSIONS.dispatch);
  const canFleet = usePermission(AMBULANCE_PERMISSIONS.fleet);
  const tabs = TABS.filter((t) => (t === 'fleet' ? canFleet || canDispatch : canDispatch));
  const tab = search.tab && tabs.includes(search.tab) ? search.tab : (tabs[0] ?? 'fleet');

  return (
    <Page>
      <PageHeader
        title="Ambulance"
        subtitle={`Take the call, send a vehicle suited to it with its papers in force, and record each step. A registered patient's trip goes on their bill: the base fare covers the first ${INCLUDED_KM} km.`}
      />
      <TabList selectedValue={tab} onTabSelect={(_, d) => void navigate({ search: { tab: d.value as AmbulanceTab } })}>
        {canDispatch && <Tab value="dispatch">Dispatch</Tab>}
        {canDispatch && <Tab value="log">Trip log</Tab>}
        <Tab value="fleet">Fleet</Tab>
      </TabList>
      {tab === 'dispatch' && <DispatchTab />}
      {tab === 'log' && <LogTab />}
      {tab === 'fleet' && <FleetTab />}
    </Page>
  );
}

function DispatchTab() {
  const trips = useQuery(ambulanceQueries.active());
  const [calling, setCalling] = useState(false);
  const [acting, setActing] = useState<{ trip: AmbulanceTrip; action: 'dispatch' | 'complete' | 'cancel' } | null>(
    null,
  );
  const step = useStep();

  return (
    <Section
      flush
      actions={
        <Button appearance="primary" icon={<AddRegular />} onClick={() => setCalling(true)}>
          Take a call
        </Button>
      }
    >
      <DataTable<AmbulanceTrip>
        ariaLabel="Open trips"
        rows={trips.data}
        loading={trips.isLoading}
        error={trips.error}
        onRetry={() => void trips.refetch()}
        getRowId={(t) => t.id}
        size="small"
        columns={[
          {
            key: 'trip',
            header: 'Trip',
            width: 200,
            render: (t) => (
              <span>
                <Text font="monospace">{t.number}</Text>
                <Caption1 block>
                  {PURPOSE_LABELS[t.purpose]} · {formatDateTime(t.requestedAt)}
                </Caption1>
              </span>
            ),
          },
          {
            key: 'patient',
            header: 'Patient',
            render: (t) => (
              <span>
                {t.patientName}
                {t.uhid && <Caption1 block>{t.uhid}</Caption1>}
                {t.complaint && <Caption1 block>{t.complaint}</Caption1>}
              </span>
            ),
          },
          {
            key: 'where',
            header: 'Pickup',
            render: (t) => (
              <span>
                {t.pickupAddress}
                <Caption1 block>
                  {t.callerName}, {t.callerPhone}
                </Caption1>
              </span>
            ),
          },
          {
            key: 'status',
            header: 'Status',
            width: 200,
            render: (t) => (
              <span>
                <Badge appearance="tint" color={statusColor[t.status]}>
                  {STATUS_LABELS[t.status]}
                </Badge>
                {t.vehicleNumber && (
                  <Caption1 block>
                    {t.vehicleNumber} · {t.driverName}
                    {t.crewName ? `, ${t.crewName}` : ''}
                  </Caption1>
                )}
              </span>
            ),
          },
        ]}
        rowActions={(t) => (
          <span style={{ display: 'flex', gap: 4 }}>
            {t.status === 'Requested' && (
              <Button size="small" appearance="primary" onClick={() => setActing({ trip: t, action: 'dispatch' })}>
                Send
              </Button>
            )}
            {t.status === 'Dispatched' && (
              <Button size="small" disabled={step.isPending} onClick={() => step.mutate({ trip: t, to: 'at-scene' })}>
                Reached
              </Button>
            )}
            {t.status === 'AtScene' && (
              <Button size="small" disabled={step.isPending} onClick={() => step.mutate({ trip: t, to: 'left-scene' })}>
                On board
              </Button>
            )}
            {t.status === 'Transporting' && (
              <Button size="small" appearance="primary" onClick={() => setActing({ trip: t, action: 'complete' })}>
                Back
              </Button>
            )}
            {t.status !== 'Transporting' && (
              <Button size="small" onClick={() => setActing({ trip: t, action: 'cancel' })}>
                Cancel
              </Button>
            )}
          </span>
        )}
        emptyTitle="No open trips"
        emptyDescription="Calls appear here until the vehicle is back."
      />
      {step.error && (
        <MessageBar intent="error">
          <MessageBarBody>{errorMessage(step.error, 'The step could not be recorded.')}</MessageBarBody>
        </MessageBar>
      )}
      {calling && <CallDialog onClose={() => setCalling(false)} />}
      {acting?.action === 'dispatch' && <SendDialog trip={acting.trip} onClose={() => setActing(null)} />}
      {acting?.action === 'complete' && <CompleteDialog trip={acting.trip} onClose={() => setActing(null)} />}
      {acting?.action === 'cancel' && <CancelDialog trip={acting.trip} onClose={() => setActing(null)} />}
    </Section>
  );
}

function useStep() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ trip, to }: { trip: AmbulanceTrip; to: 'at-scene' | 'left-scene' }) =>
      to === 'at-scene' ? ambulanceApi.atScene(trip.id, trip.version) : ambulanceApi.leftScene(trip.id, trip.version),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ambulanceKeys.all }),
  });
}

/** A dialog with a form, its error and its two buttons, as every dialog on this page has. */
function FormDialog({
  title,
  submit,
  pending,
  error,
  fallback,
  onSubmit,
  onClose,
  children,
}: {
  title: string;
  submit: string;
  pending: boolean;
  error: unknown;
  fallback: string;
  onSubmit: () => void;
  onClose: () => void;
  children: ReactNode;
}) {
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !pending && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (!pending) {
              onSubmit();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>{title}</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {error ? (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(error, fallback)}</MessageBarBody>
                </MessageBar>
              ) : null}
              {children}
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={pending}>
                Close
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={pending}
                icon={pending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                {submit}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

function CallDialog({ onClose }: { onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [purpose, setPurpose] = useState<TripPurpose>('EmergencyPickup');
  const [patient, setPatient] = useState<PickedPatient | null>(null);
  const [patientName, setPatientName] = useState('');
  const [callerName, setCallerName] = useState('');
  const [callerPhone, setCallerPhone] = useState('');
  const [pickup, setPickup] = useState('');
  const [drop, setDrop] = useState('');
  const [complaint, setComplaint] = useState('');
  const [tried, setTried] = useState(false);
  const problems = {
    patient: !patient && !patientName.trim() ? 'Find the patient, or give the name the caller gave.' : null,
    callerName: callerName.trim() ? null : 'Name the caller.',
    callerPhone: /^\+?[0-9 -]{6,16}$/.test(callerPhone.trim()) ? null : "Enter the caller's phone number.",
    pickup: pickup.trim() ? null : 'Where is the patient?',
    drop: purpose !== 'EmergencyPickup' && !drop.trim() ? 'Where does the trip go?' : null,
  };
  const show = (p: string | null) => (tried ? (p ?? undefined) : undefined);
  const save = useMutation({
    mutationFn: () =>
      ambulanceApi.request({
        purpose,
        patientId: patient?.id ?? null,
        patientName: patient ? null : patientName.trim(),
        callerName: callerName.trim(),
        callerPhone: callerPhone.trim(),
        pickupAddress: pickup.trim(),
        dropAddress: blankToNull(drop),
        complaint: blankToNull(complaint),
      }),
    onSuccess: (trip) => {
      void queryClient.invalidateQueries({ queryKey: ambulanceKeys.all });
      notify.success('Call recorded', `${trip.number}: send a vehicle.`);
      onClose();
    },
  });

  return (
    <FormDialog
      title="Take a call"
      submit="Record the call"
      pending={save.isPending}
      error={save.error}
      fallback="The call could not be recorded."
      onSubmit={() => {
        setTried(true);
        if (Object.values(problems).every((p) => p === null)) {
          save.mutate();
        }
      }}
      onClose={onClose}
    >
      <Field label="Trip" required>
        <Select value={purpose} onChange={(_, d) => setPurpose(d.value as TripPurpose)}>
          {(Object.keys(PURPOSE_LABELS) as TripPurpose[]).map((p) => (
            <option key={p} value={p}>
              {PURPOSE_LABELS[p]}
            </option>
          ))}
        </Select>
      </Field>
      <PatientPicker
        value={patient}
        onChange={setPatient}
        hint="If the patient is registered. Their trip then goes on their bill."
        validationMessage={patient ? undefined : show(problems.patient)}
      />
      {!patient && (
        <Field label="Patient's name" required validationMessage={show(problems.patient)}>
          <Input value={patientName} maxLength={200} onChange={(_, d) => setPatientName(d.value)} />
        </Field>
      )}
      <div style={{ display: 'flex', gap: 12 }}>
        <Field label="Caller" required validationMessage={show(problems.callerName)} style={{ flex: 1 }}>
          <Input value={callerName} maxLength={200} onChange={(_, d) => setCallerName(d.value)} />
        </Field>
        <Field label="Caller's phone" required validationMessage={show(problems.callerPhone)} style={{ flex: 1 }}>
          <Input type="tel" value={callerPhone} maxLength={16} onChange={(_, d) => setCallerPhone(d.value)} />
        </Field>
      </div>
      <Field label="Pickup address" required validationMessage={show(problems.pickup)}>
        <Input value={pickup} maxLength={500} onChange={(_, d) => setPickup(d.value)} />
      </Field>
      <Field label="Drop address" required={purpose !== 'EmergencyPickup'} validationMessage={show(problems.drop)}>
        <Input value={drop} maxLength={500} onChange={(_, d) => setDrop(d.value)} />
      </Field>
      <Field label="Complaint">
        <Input value={complaint} maxLength={500} onChange={(_, d) => setComplaint(d.value)} />
      </Field>
    </FormDialog>
  );
}

function SendDialog({ trip, onClose }: { trip: AmbulanceTrip; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const vehicles = useQuery(ambulanceQueries.vehicles());
  const available = availableFor(vehicles.data ?? [], trip.purpose);
  const [vehicleId, setVehicleId] = useState('');
  const [driver, setDriver] = useState('');
  const [crew, setCrew] = useState('');
  const chosen = vehicleId || available[0]?.id || '';
  const save = useMutation({
    mutationFn: () => ambulanceApi.dispatch(trip.id, chosen, driver.trim(), blankToNull(crew), trip.version),
    onSuccess: (sent) => {
      void queryClient.invalidateQueries({ queryKey: ambulanceKeys.all });
      notify.success('Vehicle sent', `${sent.vehicleNumber} for ${sent.number}.`);
      onClose();
    },
  });

  return (
    <FormDialog
      title={`Send a vehicle: ${trip.number}`}
      submit="Send"
      pending={save.isPending}
      error={save.error}
      fallback="The vehicle could not be sent."
      onSubmit={() => chosen && driver.trim() && save.mutate()}
      onClose={onClose}
    >
      {vehicles.data && !available.length && (
        <MessageBar intent="warning">
          <MessageBarBody>
            No vehicle suited to a {PURPOSE_LABELS[trip.purpose].toLowerCase()} is free with its papers in force.
          </MessageBarBody>
        </MessageBar>
      )}
      <Field label="Vehicle" required>
        <Select value={chosen} onChange={(_, d) => setVehicleId(d.value)}>
          {available.map((v) => (
            <option key={v.id} value={v.id}>
              {v.registrationNumber} · {KIND_LABELS[v.kind]}
            </option>
          ))}
        </Select>
      </Field>
      <Field label="Driver" required>
        <Input value={driver} maxLength={200} onChange={(_, d) => setDriver(d.value)} />
      </Field>
      <Field
        label="Technician or paramedic"
        required={trip.purpose === 'EmergencyPickup' || trip.purpose === 'InterFacilityTransfer'}
        hint="Goes with the patient on an emergency or a transfer in a life-support ambulance."
      >
        <Input value={crew} maxLength={200} onChange={(_, d) => setCrew(d.value)} />
      </Field>
    </FormDialog>
  );
}

function CompleteDialog({ trip, onClose }: { trip: AmbulanceTrip; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [odometer, setOdometer] = useState('');
  const [waive, setWaive] = useState(false);
  const [reason, setReason] = useState('');
  const charge = chargedKm(trip.startOdometerKm, Number(odometer));
  const save = useMutation({
    mutationFn: () => ambulanceApi.complete(trip.id, Number(odometer), waive ? reason.trim() : null, trip.version),
    onSuccess: (done) => {
      void queryClient.invalidateQueries({ queryKey: ambulanceKeys.all });
      notify.success('Trip completed', `${done.number}: ${done.kilometres} km.`);
      onClose();
    },
  });

  return (
    <FormDialog
      title={`Vehicle back: ${trip.number}`}
      submit="Complete the trip"
      pending={save.isPending}
      error={save.error}
      fallback="The trip could not be completed."
      onSubmit={() => charge && (!waive || reason.trim()) && save.mutate()}
      onClose={onClose}
    >
      <Field
        label="Odometer now (km)"
        required
        hint={`It read ${trip.startOdometerKm?.toLocaleString('en-IN')} km when the vehicle left.`}
        validationMessage={
          odometer && !charge ? 'The odometer cannot read less than when the vehicle left.' : undefined
        }
      >
        <Input
          type="number"
          min={trip.startOdometerKm ?? 0}
          value={odometer}
          onChange={(_, d) => setOdometer(d.value)}
        />
      </Field>
      {trip.patientId && (
        <>
          <Switch checked={waive} onChange={(_, d) => setWaive(d.checked)} label="Do not charge this trip" />
          {waive ? (
            <Field label="Why" required hint="For example a free scheme transfer.">
              <Input value={reason} maxLength={300} onChange={(_, d) => setReason(d.value)} />
            </Field>
          ) : (
            charge && (
              <Caption1>
                {charge.km} km: the base fare
                {charge.beyond > 0 ? `, and ${charge.beyond} km beyond the first ${INCLUDED_KM}` : ''} on the
                patient&apos;s bill.
              </Caption1>
            )
          )}
        </>
      )}
    </FormDialog>
  );
}

function CancelDialog({ trip, onClose }: { trip: AmbulanceTrip; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [reason, setReason] = useState('');
  const save = useMutation({
    mutationFn: () => ambulanceApi.cancel(trip.id, reason.trim(), trip.version),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ambulanceKeys.all });
      onClose();
    },
  });

  return (
    <FormDialog
      title={`Cancel ${trip.number}`}
      submit="Cancel the trip"
      pending={save.isPending}
      error={save.error}
      fallback="The trip could not be cancelled."
      onSubmit={() => reason.trim() && save.mutate()}
      onClose={onClose}
    >
      <Field label="Reason" required>
        <Input value={reason} maxLength={300} onChange={(_, d) => setReason(d.value)} autoFocus />
      </Field>
    </FormDialog>
  );
}

function LogTab() {
  const [from, setFrom] = useState(todayIso());
  const [to, setTo] = useState(todayIso());
  const log = useQuery(ambulanceQueries.log(from, to));
  const data = log.data;

  return (
    <Section flush>
      <FilterBar>
        <Field label="From">
          <Input type="date" value={from} onChange={(_, d) => setFrom(d.value)} />
        </Field>
        <Field label="To">
          <Input type="date" value={to} onChange={(_, d) => setTo(d.value)} />
        </Field>
      </FilterBar>
      {data && (
        <div style={{ padding: '0 16px 12px', display: 'flex', gap: 8, flexWrap: 'wrap' }}>
          <Badge appearance="tint">{data.completed} completed</Badge>
          <Badge appearance="tint">{data.cancelled} cancelled</Badge>
          <Badge appearance="tint">{data.kilometres.toLocaleString('en-IN')} km</Badge>
          <Badge appearance="tint">Emergency call to dispatch: {data.medianDispatchMinutes ?? '—'} min (median)</Badge>
          <Badge appearance="tint">Emergency call to patient: {data.medianResponseMinutes ?? '—'} min (median)</Badge>
        </div>
      )}
      <DataTable<AmbulanceTrip>
        ariaLabel="Trip log"
        rows={data?.trips}
        loading={log.isLoading}
        error={log.error}
        onRetry={() => void log.refetch()}
        getRowId={(t) => t.id}
        size="small"
        columns={[
          { key: 'number', header: 'Trip', width: 150, render: (t) => <Text font="monospace">{t.number}</Text> },
          {
            key: 'what',
            header: 'Patient',
            render: (t) => (
              <span>
                {t.patientName}
                <Caption1 block>{PURPOSE_LABELS[t.purpose]}</Caption1>
              </span>
            ),
          },
          { key: 'called', header: 'Called', width: 170, render: (t) => formatDateTime(t.requestedAt) },
          { key: 'vehicle', header: 'Vehicle', width: 150, render: (t) => t.vehicleNumber ?? '—' },
          {
            key: 'response',
            header: 'Reached in',
            width: 110,
            render: (t) => (t.responseMinutes === null ? '—' : `${t.responseMinutes} min`),
          },
          { key: 'km', header: 'Km', width: 80, render: (t) => t.kilometres ?? '—' },
          {
            key: 'status',
            header: 'Outcome',
            width: 200,
            render: (t) => (
              <span>
                <Badge appearance="tint" color={statusColor[t.status]}>
                  {STATUS_LABELS[t.status]}
                </Badge>
                {(t.cancelReason ?? t.chargeWaivedReason) && (
                  <Caption1 block>{t.cancelReason ?? `Not charged: ${t.chargeWaivedReason}`}</Caption1>
                )}
              </span>
            ),
          },
        ]}
        emptyTitle="No trips in this period"
      />
    </Section>
  );
}

function FleetTab() {
  const canFleet = usePermission(AMBULANCE_PERMISSIONS.fleet);
  const vehicles = useQuery(ambulanceQueries.vehicles());
  const [editing, setEditing] = useState<AmbulanceVehicle | 'new' | null>(null);

  return (
    <Section
      flush
      actions={
        canFleet && (
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
            Add a vehicle
          </Button>
        )
      }
    >
      <DataTable<AmbulanceVehicle>
        ariaLabel="Fleet"
        rows={vehicles.data}
        loading={vehicles.isLoading}
        error={vehicles.error}
        onRetry={() => void vehicles.refetch()}
        getRowId={(v) => v.id}
        size="small"
        columns={[
          {
            key: 'number',
            header: 'Vehicle',
            width: 150,
            render: (v) => <Text weight="semibold">{v.registrationNumber}</Text>,
          },
          { key: 'kind', header: 'Kind', width: 170, render: (v) => KIND_LABELS[v.kind] },
          {
            key: 'papers',
            header: 'Fitness · insurance · pollution',
            render: (v) => (
              <span>
                {[v.fitnessValidUntil, v.insuranceValidUntil, v.pollutionValidUntil]
                  .map((d) => (d ? formatDateOnly(d) : '—'))
                  .join(' · ')}
                {v.lapsed.length > 0 && (
                  <Badge size="small" appearance="filled" color="danger" style={{ marginLeft: 6 }}>
                    {v.lapsed.join(', ')} lapsed
                  </Badge>
                )}
              </span>
            ),
          },
          {
            key: 'odometer',
            header: 'Odometer',
            width: 120,
            render: (v) => `${v.odometerKm.toLocaleString('en-IN')} km`,
          },
          {
            key: 'state',
            header: 'Now',
            width: 130,
            render: (v) =>
              !v.isActive ? (
                <Badge appearance="tint" color="subtle">
                  Out of service
                </Badge>
              ) : v.onTripId ? (
                <Badge appearance="tint" color="brand">
                  On a trip
                </Badge>
              ) : (
                <Badge appearance="tint" color="success">
                  Free
                </Badge>
              ),
          },
        ]}
        rowActions={(v) =>
          canFleet && (
            <Button size="small" icon={<EditRegular />} onClick={() => setEditing(v)}>
              Edit
            </Button>
          )
        }
        emptyTitle="No vehicles yet"
        emptyDescription="The administrator adds the hospital's ambulances and hearse."
      />
      {editing && <VehicleDialog vehicle={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </Section>
  );
}

function VehicleDialog({ vehicle, onClose }: { vehicle: AmbulanceVehicle | null; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [number, setNumber] = useState(vehicle?.registrationNumber ?? '');
  const [kind, setKind] = useState<AmbulanceKind>(vehicle?.kind ?? 'Bls');
  const [fitness, setFitness] = useState(vehicle?.fitnessValidUntil ?? '');
  const [insurance, setInsurance] = useState(vehicle?.insuranceValidUntil ?? '');
  const [pollution, setPollution] = useState(vehicle?.pollutionValidUntil ?? '');
  const [odometer, setOdometer] = useState(String(vehicle?.odometerKm ?? 0));
  const [active, setActive] = useState(vehicle?.isActive ?? true);
  const save = useMutation({
    mutationFn: () =>
      ambulanceApi.saveVehicle({
        id: vehicle?.id ?? null,
        registrationNumber: number.trim(),
        kind,
        fitnessValidUntil: fitness || null,
        insuranceValidUntil: insurance || null,
        pollutionValidUntil: pollution || null,
        odometerKm: Number(odometer),
        isActive: active,
        version: vehicle?.version ?? null,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ambulanceKeys.all });
      notify.success('Vehicle saved', number.trim().toUpperCase());
      onClose();
    },
  });

  return (
    <FormDialog
      title={vehicle ? `Edit ${vehicle.registrationNumber}` : 'Add a vehicle'}
      submit="Save"
      pending={save.isPending}
      error={save.error}
      fallback="The vehicle could not be saved."
      onSubmit={() => number.trim() && save.mutate()}
      onClose={onClose}
    >
      <Field label="Registration number" required>
        <Input value={number} maxLength={20} onChange={(_, d) => setNumber(d.value)} placeholder="TN 49 AX 2071" />
      </Field>
      <Field label="Kind" required>
        <Select value={kind} onChange={(_, d) => setKind(d.value as AmbulanceKind)}>
          {(Object.keys(KIND_LABELS) as AmbulanceKind[]).map((k) => (
            <option key={k} value={k}>
              {KIND_LABELS[k]}
            </option>
          ))}
        </Select>
      </Field>
      <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap' }}>
        <Field label="Fitness valid until" style={{ flex: 1 }}>
          <Input type="date" value={fitness} onChange={(_, d) => setFitness(d.value)} />
        </Field>
        <Field label="Insurance valid until" style={{ flex: 1 }}>
          <Input type="date" value={insurance} onChange={(_, d) => setInsurance(d.value)} />
        </Field>
        <Field label="Pollution certificate until" style={{ flex: 1 }}>
          <Input type="date" value={pollution} onChange={(_, d) => setPollution(d.value)} />
        </Field>
      </div>
      <Field label="Odometer (km)" hint="It only goes forward.">
        <Input
          type="number"
          min={vehicle?.odometerKm ?? 0}
          value={odometer}
          onChange={(_, d) => setOdometer(d.value)}
        />
      </Field>
      <Switch checked={active} onChange={(_, d) => setActive(d.checked)} label="In service" />
    </FormDialog>
  );
}
