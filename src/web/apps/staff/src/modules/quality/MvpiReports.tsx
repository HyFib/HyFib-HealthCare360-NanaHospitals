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
  MessageBarTitle,
  Select,
  Spinner,
  Text,
  Textarea,
  Tooltip,
} from '@fluentui/react-components';
import {
  AddRegular,
  ArrowDownloadRegular,
  ArrowForwardRegular,
  EditRegular,
  PrintRegular,
  SendRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, todayIso } from '@hc360/core';
import { CheckboxGroup, DataTable, FilterBar, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type ReactNode } from 'react';
import { useServerDocuments } from '@/lib/documents';
import { usePermission } from '@/lib/permissions';
import { FilterPad } from '@/modules/hr/shared/layout';
import { errorMessage } from '@/modules/lab/common/errors';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';
import type { IncidentDetail } from './api';
import { qualityKeys } from './api';
import {
  DEVICE_AVAILABILITIES,
  DEVICE_OPERATORS,
  DEVICE_USES,
  deviceAvailabilityLabel,
  deviceFromCharge,
  deviceOperatorLabel,
  deviceUseLabel,
  MVPI_OUTCOMES,
  MVPI_SERIOUSNESS,
  mvpiApi,
  mvpiDocument,
  mvpiDraftProblems,
  mvpiKeys,
  mvpiMissing,
  mvpiOutcomeLabel,
  mvpiQueries,
  mvpiSeriousnessNames,
  mvpiSeriousnessValue,
  RISK_CLASSES,
  riskClassLabel,
  type DeviceAvailability,
  type DeviceOperator,
  type DeviceRiskClass,
  type DeviceUse,
  type MvpiDevice,
  type MvpiFilters,
  type MvpiPatientOutcome,
  type MvpiPrefill,
  type MvpiReport,
  type MvpiReportListItem,
  type MvpiReportStatus,
} from './mvpi';

// The device adverse event register (MvPI) and its dialogs: raising a report (from an incident or for a patient), the
// MDAE form as a draft, recording it as sent, correcting MvPI's reference and adding a follow-up.

const grid = { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(170px, 1fr))', gap: 12 } as const;

const statusLabel = (status: MvpiReportStatus) => (status === 'Submitted' ? 'Sent to MvPI' : 'Draft');

export function MvpiStatusBadge({ status }: { status: MvpiReportStatus }) {
  return (
    <StatusBadge status={status} tone={status === 'Submitted' ? 'success' : 'warning'} label={statusLabel(status)} />
  );
}

/** The register: every report of the facility, by state and by the day of the event, newest first. */
export function MvpiRegister({
  filters,
  onFiltersChange,
  onOpen,
}: {
  filters: MvpiFilters;
  onFiltersChange: (filters: MvpiFilters) => void;
  onOpen: (id: string) => void;
}) {
  const list = useQuery(mvpiQueries.list(filters));
  return (
    <>
      <FilterPad>
        <FilterBar>
          <Field label="State">
            <Select
              value={filters.status ?? ''}
              onChange={(_, d) =>
                onFiltersChange({ ...filters, status: (d.value || undefined) as MvpiReportStatus | undefined })
              }
            >
              <option value="">All</option>
              <option value="Draft">Draft</option>
              <option value="Submitted">Sent to MvPI</option>
            </Select>
          </Field>
          <Field label="Event from">
            <Input
              type="date"
              value={filters.from ?? ''}
              onChange={(_, d) => onFiltersChange({ ...filters, from: d.value || undefined })}
            />
          </Field>
          <Field label="Event to">
            <Input
              type="date"
              value={filters.to ?? ''}
              onChange={(_, d) => onFiltersChange({ ...filters, to: d.value || undefined })}
            />
          </Field>
        </FilterBar>
      </FilterPad>
      <DataTable<MvpiReportListItem>
        ariaLabel="Device adverse event reports"
        rows={list.data}
        loading={list.isLoading}
        error={list.error}
        onRetry={() => void list.refetch()}
        getRowId={(r) => r.id}
        onRowClick={(r) => onOpen(r.id)}
        size="small"
        columns={[
          {
            key: 'number',
            header: 'Report',
            width: 170,
            render: (r) => (
              <span>
                {r.number}
                <Caption1 block>
                  {r.followUpOfNumber ? `Follow-up to ${r.followUpOfNumber}` : `Event ${formatDateOnly(r.eventOn)}`}
                </Caption1>
              </span>
            ),
          },
          {
            key: 'device',
            header: 'Device',
            render: (r) => (
              <span>
                {r.deviceName ?? 'Device not named yet'}
                {r.manufacturer && <Caption1 block>{r.manufacturer}</Caption1>}
              </span>
            ),
          },
          {
            key: 'patient',
            header: 'Patient',
            width: 150,
            render: (r) =>
              r.noPatient ? (
                'No patient'
              ) : (
                <span>
                  {r.patientInitials ?? '—'}
                  {r.uhid && <Caption1 block>{r.uhid}</Caption1>}
                </span>
              ),
          },
          {
            key: 'incident',
            header: 'Incident',
            width: 140,
            render: (r) => r.incidentNumber ?? '—',
          },
          {
            key: 'serious',
            header: 'Serious',
            width: 100,
            render: (r) =>
              r.serious ? (
                <Badge appearance="filled" color="warning">
                  Serious
                </Badge>
              ) : (
                'No'
              ),
          },
          {
            key: 'status',
            header: 'State',
            width: 190,
            render: (r) => (
              <span>
                <MvpiStatusBadge status={r.status} />
                {r.status === 'Submitted' && (
                  <Caption1 block>
                    {formatDateOnly(r.submittedOn!)} · {r.reference ?? 'no reference yet'}
                  </Caption1>
                )}
              </span>
            ),
          },
        ]}
        emptyTitle="No device adverse event reports"
        emptyDescription="Report an equipment failure, or an incident that involved a device, to MvPI from the incident."
      />
    </>
  );
}

/**
 * On an incident: offers to report it to MvPI when it is an equipment failure or involved a device, and lists the
 * reports raised from it.
 */
export function IncidentMvpiPanel({
  incident,
  onReport,
  onOpen,
}: {
  incident: Pick<IncidentDetail, 'isDeviceRelated' | 'mvpiReports'>;
  onReport: () => void;
  onOpen: (id: string) => void;
}) {
  if (!incident.isDeviceRelated && incident.mvpiReports.length === 0) {
    return null;
  }
  return (
    <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
      {incident.isDeviceRelated && (
        <Button icon={<SendRegular />} onClick={onReport}>
          Report to MvPI
        </Button>
      )}
      {incident.mvpiReports.map((r) => (
        <Button key={r.id} appearance="subtle" size="small" onClick={() => onOpen(r.id)}>
          {r.number} · {r.isFollowUp ? 'follow-up · ' : ''}
          {r.status === 'Submitted' ? `sent${r.reference ? ` (${r.reference})` : ''}` : 'draft'}
        </Button>
      ))}
    </div>
  );
}

/** A new report: from the incident (its event, patient and the implant on the patient's bill), or from scratch. */
export function MvpiRaiseDialog({
  incidentId,
  onClose,
  onSaved,
}: {
  incidentId: string | null;
  onClose: () => void;
  onSaved: (report: MvpiReport) => void;
}) {
  const prefill = useQuery(mvpiQueries.prefill(incidentId, null));
  if (!prefill.data) {
    return (
      <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
        <DialogSurface style={{ maxWidth: 520 }}>
          <DialogBody>
            <DialogTitle>Report a device adverse event</DialogTitle>
            <DialogContent>
              {prefill.error ? (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(prefill.error, 'The incident could not be opened.')}</MessageBarBody>
                </MessageBar>
              ) : (
                <Spinner label="Filling in from the incident…" />
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
  return <MvpiFormDialog start={prefill.data} existing={null} onClose={onClose} onSaved={onSaved} />;
}

/** The device's fields that are typed as text or picked as a date. */
type TextKey = {
  [K in keyof MvpiDevice]: MvpiDevice[K] extends string | null ? (string extends MvpiDevice[K] ? K : never) : never;
}[keyof MvpiDevice];

const text = (value: string | null | undefined) => value ?? '';
const orNull = (value: string) => (value.trim() ? value.trim() : null);

/**
 * The MDAE form as a draft. A draft may be saved half written; what MvPI needs before it can be sent is marked * and
 * listed until it is filled in.
 */
export function MvpiFormDialog({
  start,
  existing,
  onClose,
  onSaved,
}: {
  start: MvpiPrefill | null;
  existing: MvpiReport | null;
  onClose: () => void;
  onSaved: (report: MvpiReport) => void;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canLookUpPatients = usePermission('patients.read');
  const today = todayIso();
  const from = existing ?? start!;
  const incidentId = existing?.incidentId ?? start?.incidentId ?? null;
  const incidentNumber = existing?.incidentNumber ?? start?.incidentNumber ?? null;
  const [patient, setPatient] = useState<PickedPatient | null>(
    from.patientId ? { id: from.patientId, uhid: from.uhid ?? '', fullName: from.patientInitials ?? '' } : null,
  );
  const [noPatient, setNoPatient] = useState(existing?.noPatient ?? false);
  const [weight, setWeight] = useState(existing?.weightKg ? String(existing.weightKg) : '');
  const [eventOn, setEventOn] = useState(from.eventOn);
  const [awareOn, setAwareOn] = useState(from.awareOn);
  const [location, setLocation] = useState(text(from.location));
  const [description, setDescription] = useState(text(from.description));
  const [serious, setSerious] = useState<string[]>(mvpiSeriousnessNames(from.seriousness));
  const [deathOn, setDeathOn] = useState(text(existing?.deathOn));
  const [outcome, setOutcome] = useState<MvpiPatientOutcome>(from.outcome);
  const [device, setDevice] = useState<MvpiDevice>(from.device);
  const [designation, setDesignation] = useState(text(existing?.reporterDesignation));
  const [department, setDepartment] = useState(text(existing?.reporterDepartment));
  const [tried, setTried] = useState(false);

  // A patient chosen here (not the incident's): the devices billed to them, to fill in the device.
  const chosen = patient && patient.id !== start?.patientId && !existing ? patient.id : null;
  const forPatient = useQuery({ ...mvpiQueries.prefill(null, chosen), enabled: chosen !== null });
  const charged = chosen ? (forPatient.data?.devices ?? []) : (start?.devices ?? []);

  const problems = mvpiDraftProblems({
    patientId: patient?.id ?? null,
    noPatient,
    eventOn,
    awareOn,
    outcome,
    seriousness: serious,
    deathOn,
    device,
    today,
  });
  const valid = Object.values(problems).every((p) => p === null);
  const missing = mvpiMissing({
    patientId: patient?.id ?? null,
    noPatient,
    location,
    description,
    seriousness: serious,
    deathOn,
    device,
    reporterDesignation: designation,
  });
  const show = (p: string | null) => (tried ? (p ?? undefined) : undefined);
  const set = (change: Partial<MvpiDevice>) => setDevice((d) => ({ ...d, ...change }));
  const setText = (key: TextKey) => (_: unknown, d: { value: string }) =>
    set({ [key]: d.value || null } as Partial<MvpiDevice>);

  const save = useMutation({
    mutationFn: () => {
      const request = {
        incidentId,
        patientId: noPatient ? null : (patient?.id ?? null),
        noPatient,
        weightKg: weight && !noPatient ? Number(weight) : null,
        eventOn,
        awareOn,
        location: orNull(location),
        description: orNull(description),
        seriousness: mvpiSeriousnessValue(serious),
        deathOn: serious.includes('Death') && deathOn ? deathOn : null,
        outcome,
        device,
        reporterDesignation: orNull(designation),
        reporterDepartment: orNull(department),
      };
      return existing
        ? mvpiApi.correct(existing.id, { ...request, version: existing.version })
        : mvpiApi.raise(request);
    },
    onSuccess: (saved) => {
      void queryClient.invalidateQueries({ queryKey: mvpiKeys.all });
      void queryClient.invalidateQueries({ queryKey: qualityKeys.incidents });
      notify.success(
        existing ? 'Draft saved' : 'Report raised',
        Object.keys(saved.missing).length === 0
          ? `${saved.number} is ready to send to MvPI.`
          : `${saved.number} is a draft: some of what MvPI asks for is still to fill in.`,
      );
      onSaved(saved);
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !save.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 900 }}>
        <form
          noValidate
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
              {existing ? `Correct ${existing.number}` : 'Report a device adverse event to MvPI'}
              <Caption1 block>
                Medical device adverse event reporting form
                {incidentNumber ? ` · from incident ${incidentNumber}` : ''}
                {existing?.followUpOfNumber ? ` · follow-up to ${existing.followUpOfNumber}` : ''}
              </Caption1>
            </DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {save.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(save.error, 'The report could not be saved.')}</MessageBarBody>
                </MessageBar>
              )}
              <Caption1>
                * MvPI needs these before the report can be recorded as sent; a draft may be saved without.
              </Caption1>

              <Heading>A. Patient</Heading>
              {from.patientId && (existing || start?.incidentId) ? (
                <Text>
                  {from.patientInitials} · {from.uhid}
                  {from.ageYears !== null ? ` · ${from.ageYears} years` : ''}
                  {from.sex ? ` · ${from.sex}` : ''}
                </Text>
              ) : (
                !noPatient &&
                canLookUpPatients && (
                  <PatientPicker
                    value={patient}
                    onChange={setPatient}
                    label="Patient"
                    required
                    validationMessage={show(problems.patient)}
                  />
                )
              )}
              <div style={grid}>
                <Checkbox
                  label="No patient involved"
                  checked={noPatient}
                  disabled={Boolean(from.patientId && (existing || start?.incidentId))}
                  onChange={(_, d) => {
                    setNoPatient(!!d.checked);
                    if (d.checked) {
                      setPatient(null);
                    }
                  }}
                />
                {!noPatient && (
                  <Field label="Weight (kg)">
                    <Input type="number" inputMode="decimal" value={weight} onChange={(_, d) => setWeight(d.value)} />
                  </Field>
                )}
              </div>

              <Heading>B. Adverse event</Heading>
              <div style={grid}>
                <Field label="Date of the event" required validationMessage={show(problems.eventOn)}>
                  <Input type="date" value={eventOn} max={today} onChange={(_, d) => setEventOn(d.value)} />
                </Field>
                <Field label="Hospital became aware" required validationMessage={show(problems.awareOn)}>
                  <Input type="date" value={awareOn} max={today} onChange={(_, d) => setAwareOn(d.value)} />
                </Field>
                <Field label="Where it happened" required>
                  <Input value={location} maxLength={150} onChange={(_, d) => setLocation(d.value)} />
                </Field>
                <Field label="Patient outcome" validationMessage={show(problems.outcome)}>
                  <Select value={outcome} onChange={(_, d) => setOutcome(d.value as MvpiPatientOutcome)}>
                    {MVPI_OUTCOMES.map((o) => (
                      <option key={o} value={o}>
                        {mvpiOutcomeLabel[o]}
                      </option>
                    ))}
                  </Select>
                </Field>
              </div>
              <Field label="What happened" required hint="The event, what the device did, and what was done.">
                <Textarea value={description} maxLength={4000} rows={4} onChange={(_, d) => setDescription(d.value)} />
              </Field>
              <CheckboxGroup label="Serious because" hint="None ticked: not serious.">
                <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
                  {MVPI_SERIOUSNESS.map(([name, label]) => (
                    <Checkbox
                      key={name}
                      label={label}
                      checked={serious.includes(name)}
                      onChange={(_, d) => setSerious((s) => (d.checked ? [...s, name] : s.filter((x) => x !== name)))}
                    />
                  ))}
                </div>
              </CheckboxGroup>
              {serious.includes('Death') && (
                <Field label="Date of death" required validationMessage={show(problems.deathOn)}>
                  <Input type="date" value={deathOn} max={today} onChange={(_, d) => setDeathOn(d.value)} />
                </Field>
              )}

              <Heading>C. Medical device</Heading>
              {charged.length > 0 && (
                <Caption1>
                  Billed to the patient:{' '}
                  {charged.slice(0, 6).map((c) => (
                    <Button
                      key={`${c.description}-${c.batch ?? ''}-${c.serial ?? ''}`}
                      size="small"
                      appearance="subtle"
                      onClick={() => setDevice((d) => deviceFromCharge(c, d))}
                    >
                      {c.brand ?? c.description}
                      {c.batch ? ` (${c.batch})` : ''}
                      {c.isImplant ? ' · implant' : ''}
                    </Button>
                  ))}
                </Caption1>
              )}
              <div style={grid}>
                <Field label="Brand or trade name" required>
                  <Input value={text(device.name)} maxLength={200} onChange={setText('name')} />
                </Field>
                <Field label="Common name or category">
                  <Input value={text(device.genericName)} maxLength={200} onChange={setText('genericName')} />
                </Field>
                <Field label="Risk class (MDR 2017)">
                  <Select
                    value={device.riskClass ?? ''}
                    onChange={(_, d) => set({ riskClass: (d.value || null) as DeviceRiskClass | null })}
                  >
                    <option value="">Not known</option>
                    {RISK_CLASSES.map((c) => (
                      <option key={c} value={c}>
                        {riskClassLabel[c]}
                      </option>
                    ))}
                  </Select>
                </Field>
                <Field label="Manufacturer" required>
                  <Input value={text(device.manufacturer)} maxLength={200} onChange={setText('manufacturer')} />
                </Field>
                <Field label="Manufacturer's address">
                  <Input
                    value={text(device.manufacturerAddress)}
                    maxLength={500}
                    onChange={setText('manufacturerAddress')}
                  />
                </Field>
                <Field label="Importer, if imported">
                  <Input value={text(device.importer)} maxLength={500} onChange={setText('importer')} />
                </Field>
                <Field label="Model no.">
                  <Input value={text(device.modelNumber)} maxLength={80} onChange={setText('modelNumber')} />
                </Field>
                <Field label="Catalogue no.">
                  <Input value={text(device.catalogueNumber)} maxLength={80} onChange={setText('catalogueNumber')} />
                </Field>
                <Field label="Serial no." required hint="Or the model, catalogue or lot no., or the UDI.">
                  <Input value={text(device.serialNumber)} maxLength={80} onChange={setText('serialNumber')} />
                </Field>
                <Field label="Lot or batch no.">
                  <Input value={text(device.lotNumber)} maxLength={80} onChange={setText('lotNumber')} />
                </Field>
                <Field label="UDI">
                  <Input value={text(device.udi)} maxLength={120} onChange={setText('udi')} />
                </Field>
                <Field label="Software version">
                  <Input value={text(device.softwareVersion)} maxLength={60} onChange={setText('softwareVersion')} />
                </Field>
                <Field label="Made on" validationMessage={show(problems.manufacturedOn)}>
                  <Input type="date" value={text(device.manufacturedOn)} onChange={setText('manufacturedOn')} />
                </Field>
                <Field label="Expires on">
                  <Input type="date" value={text(device.expiresOn)} onChange={setText('expiresOn')} />
                </Field>
                <Field label="Use">
                  <Select value={device.use} onChange={(_, d) => set({ use: d.value as DeviceUse })}>
                    {DEVICE_USES.map((u) => (
                      <option key={u} value={u}>
                        {deviceUseLabel[u]}
                      </option>
                    ))}
                  </Select>
                </Field>
                <Field label="Operated by">
                  <Select value={device.operatedBy} onChange={(_, d) => set({ operatedBy: d.value as DeviceOperator })}>
                    {DEVICE_OPERATORS.map((o) => (
                      <option key={o} value={o}>
                        {deviceOperatorLabel[o]}
                      </option>
                    ))}
                  </Select>
                </Field>
              </div>
              <div style={grid}>
                <Checkbox
                  label="Reprocessed before this use"
                  checked={device.reprocessed}
                  onChange={(_, d) => set({ reprocessed: !!d.checked })}
                />
                <Checkbox
                  label="Implantable"
                  checked={device.implantable}
                  onChange={(_, d) => set({ implantable: !!d.checked })}
                />
                {device.implantable && (
                  <Field label="Implanted on" required validationMessage={show(problems.implantedOn)}>
                    <Input type="date" value={text(device.implantedOn)} max={today} onChange={setText('implantedOn')} />
                  </Field>
                )}
                {device.implantable && (
                  <Field label="Explanted on" validationMessage={show(problems.explantedOn)}>
                    <Input type="date" value={text(device.explantedOn)} max={today} onChange={setText('explantedOn')} />
                  </Field>
                )}
              </div>
              <div style={grid}>
                <Field label="Available for evaluation">
                  <Select
                    value={device.availability}
                    onChange={(_, d) => set({ availability: d.value as DeviceAvailability })}
                  >
                    {DEVICE_AVAILABILITIES.map((a) => (
                      <option key={a} value={a}>
                        {deviceAvailabilityLabel[a]}
                      </option>
                    ))}
                  </Select>
                </Field>
                {device.availability === 'Available' && (
                  <Field
                    label="Kept at"
                    required
                    hint="Quarantine it: the manufacturer or MvPI may ask to evaluate it."
                  >
                    <Input value={text(device.keptAt)} maxLength={200} onChange={setText('keptAt')} />
                  </Field>
                )}
                {device.availability === 'ReturnedToManufacturer' && (
                  <Field label="Returned on" required validationMessage={show(problems.returnedOn)}>
                    <Input type="date" value={text(device.returnedOn)} max={today} onChange={setText('returnedOn')} />
                  </Field>
                )}
              </div>

              <Heading>D. Reporter</Heading>
              <div style={grid}>
                <Field label="Reporter">
                  <Input value={existing?.reporterName ?? 'You'} readOnly />
                </Field>
                <Field label="Designation" required>
                  <Input value={designation} maxLength={120} onChange={(_, d) => setDesignation(d.value)} />
                </Field>
                <Field label="Department">
                  <Input value={department} maxLength={120} onChange={(_, d) => setDepartment(d.value)} />
                </Field>
              </div>
              {missing.length > 0 && (
                <MessageBar intent="info" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>Still needed before it can be sent</MessageBarTitle>
                    {missing.join('; ')}.
                  </MessageBarBody>
                </MessageBar>
              )}
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
                Save draft
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

function Heading({ children }: { children: ReactNode }) {
  return <Text weight="semibold">{children}</Text>;
}

/** A report: what it says, its MDAE form to print, and what may be done with it in its state. */
export function MvpiReportDialog({
  id,
  onClose,
  onOpen,
}: {
  id: string;
  onClose: () => void;
  onOpen: (id: string) => void;
}) {
  const report = useQuery(mvpiQueries.report(id));
  const { print, downloadPdf } = useServerDocuments();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [step, setStep] = useState<'view' | 'edit' | 'submit' | 'reference'>('view');
  const followUp = useMutation({
    mutationFn: () => mvpiApi.followUp(id),
    onSuccess: (saved) => {
      void queryClient.invalidateQueries({ queryKey: mvpiKeys.all });
      notify.success('Follow-up started', `${saved.number} is a draft that names ${saved.followUpOfNumber}.`);
      onOpen(saved.id);
    },
    onError: (error) => notify.error(error, 'Could not start a follow-up'),
  });
  const r = report.data;

  if (r && step === 'edit') {
    return <MvpiFormDialog start={null} existing={r} onClose={() => setStep('view')} onSaved={() => setStep('view')} />;
  }
  if (r && step === 'submit') {
    return <SubmitDialog report={r} onClose={() => setStep('view')} />;
  }
  if (r && step === 'reference') {
    return <ReferenceDialog report={r} onClose={() => setStep('view')} />;
  }

  const missing = r ? Object.values(r.missing) : [];
  const d = r?.device;
  return (
    <Dialog open onOpenChange={(_, data) => !data.open && onClose()}>
      <DialogSurface style={{ maxWidth: 760 }}>
        <DialogBody>
          <DialogTitle>
            {r ? `MvPI report ${r.number}` : 'MvPI report'}
            {r && (
              <Caption1 block>
                {r.noPatient ? 'No patient involved' : `${r.patientInitials ?? '—'} · ${r.uhid ?? ''}`} · raised by{' '}
                {r.reporterName} on {formatDateOnly(r.reportedOn)}
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
            {r && d && (
              <>
                <div>
                  <MvpiStatusBadge status={r.status} />
                </div>
                {r.followUpOfNumber && r.followUpOfId && (
                  <Caption1>
                    Follow-up to{' '}
                    <Button size="small" appearance="transparent" onClick={() => onOpen(r.followUpOfId!)}>
                      {r.followUpOfNumber}
                    </Button>
                    {r.followUpOfReference ? ` (MvPI reference ${r.followUpOfReference})` : ''}
                  </Caption1>
                )}
                <Text weight="semibold">
                  {d.name ?? 'Device not named yet'}
                  {d.manufacturer ? ` · ${d.manufacturer}` : ''}
                </Text>
                <Caption1>
                  {[
                    d.modelNumber && `model ${d.modelNumber}`,
                    d.serialNumber && `serial ${d.serialNumber}`,
                    d.lotNumber && `lot ${d.lotNumber}`,
                    d.implantable && d.implantedOn && `implanted ${formatDateOnly(d.implantedOn)}`,
                    d.availability === 'Available' && d.keptAt && `kept at ${d.keptAt}`,
                  ]
                    .filter(Boolean)
                    .join(' · ')}
                </Caption1>
                <Text>{r.description}</Text>
                <Caption1>
                  Event {formatDateOnly(r.eventOn)}
                  {r.location ? ` at ${r.location}` : ''} · {mvpiOutcomeLabel[r.outcome]} ·{' '}
                  {mvpiSeriousnessNames(r.seriousness).length === 0
                    ? 'Not serious'
                    : mvpiSeriousnessNames(r.seriousness)
                        .map((n) => MVPI_SERIOUSNESS.find(([name]) => name === n)?.[1] ?? n)
                        .join(', ')}
                </Caption1>
                {r.incidentNumber && <Caption1>Raised from incident {r.incidentNumber}</Caption1>}
                {r.status === 'Draft' && missing.length > 0 && (
                  <MessageBar intent="info" layout="multiline">
                    <MessageBarBody>
                      <MessageBarTitle>Still needed before it can be sent</MessageBarTitle>
                      {missing.join(' ')}
                    </MessageBarBody>
                  </MessageBar>
                )}
                {r.status === 'Submitted' && (
                  <Text>
                    Sent to MvPI on {formatDateOnly(r.submittedOn!)} by {r.submittedByName} ·{' '}
                    {r.reference ?? 'no reference yet'}
                  </Text>
                )}
                {r.referenceCorrectionReason && (
                  <Caption1>
                    Reference corrected {r.referenceCorrectedAt ? formatDateTime(r.referenceCorrectedAt) : ''} by{' '}
                    {r.referenceCorrectedByName}: {r.referenceCorrectionReason}
                  </Caption1>
                )}
                {r.followUps.length > 0 && (
                  <Caption1>
                    Follow-ups:{' '}
                    {r.followUps.map((f) => (
                      <Button key={f.id} size="small" appearance="transparent" onClick={() => onOpen(f.id)}>
                        {f.number} ({statusLabel(f.status).toLowerCase()})
                      </Button>
                    ))}
                  </Caption1>
                )}
              </>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>Close</Button>
            {r && (
              <Button
                icon={print.isPending ? <Spinner size="tiny" aria-hidden /> : <PrintRegular />}
                onClick={() => print.mutate(mvpiDocument(r.id))}
              >
                Print the MDAE form
              </Button>
            )}
            {r && (
              <Button
                icon={downloadPdf.isPending ? <Spinner size="tiny" aria-hidden /> : <ArrowDownloadRegular />}
                onClick={() => downloadPdf.mutate(mvpiDocument(r.id))}
              >
                PDF
              </Button>
            )}
            {r?.status === 'Draft' && (
              <Button icon={<EditRegular />} onClick={() => setStep('edit')}>
                Correct
              </Button>
            )}
            {r?.status === 'Draft' &&
              (missing.length > 0 ? (
                <Tooltip content="Fill in what MvPI still needs first." relationship="description">
                  <Button appearance="primary" icon={<SendRegular />} disabledFocusable>
                    Record as sent
                  </Button>
                </Tooltip>
              ) : (
                <Button appearance="primary" icon={<SendRegular />} onClick={() => setStep('submit')}>
                  Record as sent
                </Button>
              ))}
            {r?.status === 'Submitted' && (
              <Button icon={<EditRegular />} onClick={() => setStep('reference')}>
                Correct the reference
              </Button>
            )}
            {r?.status === 'Submitted' && (
              <Button
                appearance="primary"
                icon={followUp.isPending ? <Spinner size="tiny" aria-hidden /> : <AddRegular />}
                disabled={followUp.isPending}
                onClick={() => followUp.mutate()}
              >
                Add a follow-up
              </Button>
            )}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

function SubmitDialog({ report, onClose }: { report: MvpiReport; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [submittedOn, setSubmittedOn] = useState(todayIso());
  const [by, setBy] = useState('');
  const [reference, setReference] = useState('');
  const save = useMutation({
    mutationFn: () => mvpiApi.submit(report.id, submittedOn, orNull(by), orNull(reference), report.version),
    onSuccess: (saved) => {
      void queryClient.invalidateQueries({ queryKey: mvpiKeys.all });
      void queryClient.invalidateQueries({ queryKey: qualityKeys.incidents });
      notify.success('Recorded as sent', `${saved.number} is recorded as sent to MvPI.`);
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
            <DialogTitle>Record {report.number} as sent to MvPI</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {save.error && (
                <MessageBar intent="error">
                  <MessageBarBody>
                    {errorMessage(save.error, 'The report could not be recorded as sent.')}
                  </MessageBarBody>
                </MessageBar>
              )}
              <Field label="Sent on" required>
                <Input
                  type="date"
                  value={submittedOn}
                  min={report.awareOn}
                  max={todayIso()}
                  onChange={(_, d) => setSubmittedOn(d.value)}
                />
              </Field>
              <Field label="Sent by" hint="Leave empty if you sent it.">
                <Input value={by} maxLength={200} onChange={(_, d) => setBy(d.value)} />
              </Field>
              <Field label="MvPI or monitoring centre reference" hint="It can be added later if MvPI gives it later.">
                <Input value={reference} maxLength={64} onChange={(_, d) => setReference(d.value)} />
              </Field>
              <Caption1>
                Once sent, the report cannot change here; what is learned later goes on a follow-up report.
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

function ReferenceDialog({ report, onClose }: { report: MvpiReport; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [reference, setReference] = useState(report.reference ?? '');
  const [reason, setReason] = useState('');
  const [tried, setTried] = useState(false);
  const problems = {
    reference: reference.trim()
      ? reference.trim() === report.reference
        ? 'That is the reference already recorded.'
        : null
      : "Give MvPI's reference.",
    reason: reason.trim() ? null : 'Say why the reference is changed.',
  };
  const save = useMutation({
    mutationFn: () => mvpiApi.correctReference(report.id, reference.trim(), reason.trim(), report.version),
    onSuccess: (saved) => {
      void queryClient.invalidateQueries({ queryKey: mvpiKeys.all });
      notify.success('Reference corrected', `${saved.number}: ${saved.reference}`);
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !save.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <form
          noValidate
          onSubmit={(event) => {
            event.preventDefault();
            setTried(true);
            if (!problems.reference && !problems.reason && !save.isPending) {
              save.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Correct the reference of {report.number}</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {save.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(save.error, 'The reference could not be corrected.')}</MessageBarBody>
                </MessageBar>
              )}
              <Field
                label="MvPI or monitoring centre reference"
                required
                validationMessage={tried ? (problems.reference ?? undefined) : undefined}
              >
                <Input value={reference} maxLength={64} onChange={(_, d) => setReference(d.value)} />
              </Field>
              <Field label="Why" required validationMessage={tried ? (problems.reason ?? undefined) : undefined}>
                <Textarea value={reason} maxLength={500} onChange={(_, d) => setReason(d.value)} />
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
                icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : <ArrowForwardRegular />}
              >
                Correct
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
