import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Spinner,
  Switch,
  Text,
  tokens,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular, DocumentTextRegular, MoreHorizontalRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDate, formatDateOnly, formatDateTime, humanize, todayIso } from '@hc360/core';
import {
  applyServerErrors,
  DataTable,
  DateField,
  EmptyState,
  FormGrid,
  NumberField,
  StatusBadge,
  SwitchField,
  TextAreaField,
  TextField,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { api } from '@/lib/api';
import { usePermission } from '@/lib/permissions';
import { patientQueries } from '@/modules/patients/api';
import { emrApi, emrKeys, emrQueries, encounterTypeLabel, type ImmunizationDto } from './api';
import { ReasonDialog } from './ui';

const useStyles = makeStyles({
  toolbar: { display: 'flex', gap: '12px', alignItems: 'center', flexWrap: 'wrap', marginBottom: '12px' },
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
  viewer: { width: '100%', height: '70vh', border: 'none', backgroundColor: tokens.colorNeutralBackground3 },
  image: { maxWidth: '100%', maxHeight: '70vh', display: 'block', margin: '0 auto' },
});

// ------------------------------------------------------------------------------------------------ encounters

export function EncountersTab({ patientId }: { patientId: string }) {
  const navigate = useNavigate();
  const [page, setPage] = useState(1);
  const encounters = useQuery(emrQueries.encounters(patientId, page));
  return (
    <DataTable
      ariaLabel="Encounters"
      rows={encounters.data?.items}
      loading={encounters.isLoading}
      error={encounters.error}
      onRetry={() => void encounters.refetch()}
      getRowId={(e) => e.id}
      onRowClick={(e) => void navigate({ to: '/emr/encounters/$encounterId', params: { encounterId: e.id } })}
      size="small"
      columns={[
        { key: 'date', header: 'Date', width: 170, render: (e) => formatDateTime(e.startedAt) },
        { key: 'type', header: 'Type', render: (e) => encounterTypeLabel[e.type] },
        { key: 'dept', header: 'Department', priority: 'low', render: (e) => e.departmentName ?? '—' },
        { key: 'doctor', header: 'Doctor', render: (e) => e.practitionerName ?? '—' },
        {
          key: 'complaint',
          header: 'Complaint / diagnosis',
          render: (e) => (e.diagnoses.length > 0 ? e.diagnoses.join('; ') : (e.chiefComplaint ?? '—')),
        },
        {
          key: 'status',
          header: 'Status',
          width: 120,
          render: (e) => (
            <StatusBadge
              status={e.status === 'InProgress' ? 'Draft' : e.status}
              label={e.status === 'InProgress' ? 'Unsigned' : undefined}
            />
          ),
        },
      ]}
      emptyTitle="No encounters yet"
      emptyDescription="Start an encounter from the doctor's worklist or with Start encounter above."
      page={page}
      pageSize={15}
      totalCount={encounters.data?.totalCount}
      onPageChange={setPage}
    />
  );
}

// ------------------------------------------------------------------------------------------------ immunisations

const immunizationSchema = z
  .object({
    vaccineName: z.string().trim().min(1, 'Enter the vaccine.').max(200),
    vaccineCode: z.string().max(32),
    doseNumber: z.number().int().min(1).max(10).nullable(),
    administeredOn: z.string().min(1, 'Enter the date given.'),
    isExternal: z.boolean(),
    route: z.string().max(32),
    site: z.string().max(64),
    batchNumber: z.string().max(64),
    manufacturer: z.string().max(128),
    expiryDate: z.string().nullable(),
    administeredBy: z.string().max(200),
    nextDueOn: z.string().nullable(),
    notes: z.string().max(500),
  })
  .superRefine((v, ctx) => {
    if (!v.isExternal && !v.batchNumber.trim()) {
      ctx.addIssue({ code: 'custom', path: ['batchNumber'], message: 'Record the batch number of the vial used.' });
    }
    if (!v.isExternal && !v.expiryDate) {
      ctx.addIssue({ code: 'custom', path: ['expiryDate'], message: 'Record the expiry date of the batch.' });
    }
    if (!v.isExternal && v.expiryDate && v.administeredOn && v.expiryDate < v.administeredOn) {
      ctx.addIssue({
        code: 'custom',
        path: ['expiryDate'],
        message: 'This batch had expired on the day it was given. Report it as an incident instead.',
      });
    }
    if (v.nextDueOn && v.administeredOn && v.nextDueOn <= v.administeredOn) {
      ctx.addIssue({ code: 'custom', path: ['nextDueOn'], message: 'The next dose must be due after this dose.' });
    }
  });

type ImmunizationValues = z.infer<typeof immunizationSchema>;

function RecordImmunizationDialog({
  patientId,
  open,
  onClose,
}: {
  patientId: string;
  open: boolean;
  onClose: () => void;
}) {
  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 720 }}>
        {open && <RecordImmunizationForm patientId={patientId} onClose={onClose} />}
      </DialogSurface>
    </Dialog>
  );
}

function RecordImmunizationForm({ patientId, onClose }: { patientId: string; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const today = todayIso();
  const form = useForm<ImmunizationValues>({
    resolver: zodResolver(immunizationSchema),
    defaultValues: {
      vaccineName: '',
      vaccineCode: '',
      doseNumber: null,
      administeredOn: today,
      isExternal: false,
      route: 'IM',
      site: '',
      batchNumber: '',
      manufacturer: '',
      expiryDate: null,
      administeredBy: '',
      nextDueOn: null,
      notes: '',
    },
  });
  const isExternal = useWatch({ control: form.control, name: 'isExternal' });
  const save = useMutation({
    mutationFn: (v: ImmunizationValues) =>
      emrApi.recordImmunization(patientId, {
        encounterId: null,
        vaccineName: v.vaccineName.trim(),
        vaccineCode: v.vaccineCode.trim() || null,
        doseNumber: v.doseNumber,
        administeredOn: v.administeredOn,
        route: v.route.trim() || null,
        site: v.site.trim() || null,
        batchNumber: v.batchNumber.trim() || null,
        manufacturer: v.manufacturer.trim() || null,
        expiryDate: v.expiryDate,
        administeredBy: v.administeredBy.trim() || null,
        isExternal: v.isExternal,
        nextDueOn: v.nextDueOn,
        notes: v.notes.trim() || null,
      }),
    onSuccess: () => {
      notify.success('Immunisation recorded');
      void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });
      onClose();
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not record the immunisation');
      }
    },
  });

  return (
    <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate>
      <DialogBody>
        <DialogTitle>Record immunisation</DialogTitle>
        <DialogContent className={styles.form}>
          <SwitchField
            control={form.control}
            name="isExternal"
            label="Given elsewhere (copied from a vaccination card)"
            hint="Doses given here need the batch and expiry for adverse-event tracing."
          />
          <FormGrid columns={3}>
            <TextField
              control={form.control}
              name="vaccineName"
              label="Vaccine"
              required
              autoFocus
              maxLength={200}
              placeholder="e.g. Hepatitis B, Tdap"
            />
            <NumberField control={form.control} name="doseNumber" label="Dose number" min={1} max={10} />
            <DateField control={form.control} name="administeredOn" label="Given on" required max={today} />
            <TextField control={form.control} name="vaccineCode" label="Code" maxLength={32} />
            <TextField control={form.control} name="route" label="Route" maxLength={32} />
            <TextField control={form.control} name="site" label="Site" maxLength={64} placeholder="e.g. left deltoid" />
            {!isExternal && (
              <TextField control={form.control} name="batchNumber" label="Batch number" required maxLength={64} />
            )}
            {!isExternal && <DateField control={form.control} name="expiryDate" label="Batch expiry" required />}
            <TextField control={form.control} name="manufacturer" label="Manufacturer" maxLength={128} />
            <TextField control={form.control} name="administeredBy" label="Given by" maxLength={200} />
            <DateField control={form.control} name="nextDueOn" label="Next dose due" min={today} />
          </FormGrid>
          <TextAreaField control={form.control} name="notes" label="Notes" rows={2} maxLength={500} />
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
            Record
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}

export function ImmunizationsTab({ patientId }: { patientId: string }) {
  const styles = useStyles();
  // Closing a dialog opened from a row's menu returns focus to that row's menu button.
  const restoreFocusTarget = useRestoreFocusTarget();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canRecord = usePermission('clinical.immunizations.record');
  const [showErrors, setShowErrors] = useState(false);
  const [recording, setRecording] = useState(false);
  const [voiding, setVoiding] = useState<ImmunizationDto | null>(null);
  const immunizations = useQuery(emrQueries.immunizations(patientId, showErrors));
  const markError = useMutation({
    mutationFn: ({ id, reason }: { id: string; reason: string }) => emrApi.immunizationEnteredInError(id, reason),
    onSuccess: () => {
      setVoiding(null);
      notify.success('Marked as entered in error');
      void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });
    },
    onError: (error) => notify.error(error, 'Could not update the immunisation'),
  });

  return (
    <>
      <div className={styles.toolbar}>
        {canRecord && (
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setRecording(true)}>
            Record immunisation
          </Button>
        )}
        <Switch
          checked={showErrors}
          onChange={(_, d) => setShowErrors(d.checked)}
          label="Show entries marked in error"
        />
      </div>
      <DataTable
        ariaLabel="Immunisations"
        rows={immunizations.data}
        loading={immunizations.isLoading}
        error={immunizations.error}
        onRetry={() => void immunizations.refetch()}
        getRowId={(i) => i.id}
        size="small"
        columns={[
          { key: 'date', header: 'Given on', width: 120, render: (i) => formatDateOnly(i.administeredOn) },
          {
            key: 'vaccine',
            header: 'Vaccine',
            render: (i) => (
              <span>
                <Text weight="semibold" style={i.isEnteredInError ? { textDecoration: 'line-through' } : undefined}>
                  {i.vaccineName}
                </Text>
                {i.doseNumber && <Caption1> · dose {i.doseNumber}</Caption1>}
              </span>
            ),
          },
          {
            key: 'batch',
            header: 'Batch / expiry',
            priority: 'low',
            render: (i) =>
              i.batchNumber ? `${i.batchNumber}${i.expiryDate ? ` · exp ${formatDateOnly(i.expiryDate)}` : ''}` : '—',
          },
          { key: 'where', header: 'Where', render: (i) => (i.isExternal ? 'Elsewhere (card)' : 'This hospital') },
          { key: 'next', header: 'Next due', render: (i) => (i.nextDueOn ? formatDateOnly(i.nextDueOn) : '—') },
          {
            key: 'status',
            header: 'Status',
            priority: 'low',
            render: (i) =>
              i.isEnteredInError ? <StatusBadge status="Voided" tone="danger" label="Entered in error" /> : null,
          },
        ]}
        rowActions={
          canRecord
            ? (i) =>
                i.isEnteredInError ? null : (
                  <Menu>
                    <MenuTrigger disableButtonEnhancement>
                      <Button
                        size="small"
                        appearance="subtle"
                        icon={<MoreHorizontalRegular />}
                        aria-label={`Actions for ${i.vaccineName}`}
                        {...restoreFocusTarget}
                      />
                    </MenuTrigger>
                    <MenuPopover>
                      <MenuList>
                        <MenuItem icon={<DeleteRegular />} onClick={() => setVoiding(i)}>
                          Mark as entered in error
                        </MenuItem>
                      </MenuList>
                    </MenuPopover>
                  </Menu>
                )
            : undefined
        }
        emptyTitle="No immunisations recorded"
        emptyDescription="Record doses given here, or copy them from the patient's vaccination card."
      />
      <RecordImmunizationDialog patientId={patientId} open={recording} onClose={() => setRecording(false)} />
      <ReasonDialog
        open={voiding !== null}
        title={`Mark ${voiding?.vaccineName ?? 'this dose'} as entered in error?`}
        description="The entry stays in the audit trail but no longer counts as a dose."
        label="What was wrong"
        confirmLabel="Mark as entered in error"
        destructive
        busy={markError.isPending}
        onConfirm={(reason) => voiding && markError.mutate({ id: voiding.id, reason })}
        onCancel={() => setVoiding(null)}
      />
    </>
  );
}

// ------------------------------------------------------------------------------------------------ documents

export function DocumentsTab({ patientId }: { patientId: string }) {
  const styles = useStyles();
  const notify = useNotify();
  const canRead = usePermission(['patients.documents', 'patients.read']);
  const documents = useQuery({ ...patientQueries.documents(patientId), enabled: canRead });
  const [viewer, setViewer] = useState<{ url: string; contentType: string; title: string } | null>(null);

  // Release the decrypted document from memory when the viewer closes or shows another file.
  useEffect(
    () => () => {
      if (viewer) {
        URL.revokeObjectURL(viewer.url);
      }
    },
    [viewer],
  );

  const open = async (fileId: string, contentType: string | null, title: string) => {
    try {
      const blob = await api.blob(`/api/v1/files/${fileId}`);
      setViewer({ url: URL.createObjectURL(blob), contentType: contentType ?? blob.type, title });
    } catch (error) {
      notify.error(error, 'Could not open the document');
    }
  };

  if (!canRead) {
    return (
      <EmptyState
        title="Documents are not available to you"
        description="Ask the medical records department for access."
      />
    );
  }

  return (
    <>
      <DataTable
        ariaLabel="Documents"
        rows={documents.data}
        loading={documents.isLoading}
        error={documents.error}
        onRetry={() => void documents.refetch()}
        getRowId={(d) => d.id}
        onRowClick={(d) => void open(d.fileId, d.contentType, d.title)}
        size="small"
        columns={[
          { key: 'title', header: 'Title', render: (d) => d.title },
          { key: 'type', header: 'Type', render: (d) => humanize(d.type) },
          {
            key: 'date',
            header: 'Document date',
            priority: 'low',
            render: (d) => (d.documentDate ? formatDateOnly(d.documentDate) : '—'),
          },
          { key: 'added', header: 'Added', render: (d) => formatDate(d.createdAt) },
          { key: 'source', header: 'Source', priority: 'low', render: (d) => d.source ?? '—' },
        ]}
        emptyTitle="No documents"
        emptyDescription="Scanned reports, referral letters and outside records are attached from the patient's profile."
      />
      <Dialog open={viewer !== null} onOpenChange={(_, d) => !d.open && setViewer(null)}>
        <DialogSurface style={{ maxWidth: '90vw', width: 1000 }}>
          <DialogBody>
            <DialogTitle>{viewer?.title}</DialogTitle>
            <DialogContent>
              {viewer?.contentType.startsWith('image/') ? (
                <img src={viewer.url} alt={viewer.title} className={styles.image} />
              ) : (
                viewer && <iframe src={viewer.url} title={viewer.title} className={styles.viewer} />
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={() => setViewer(null)}>Close</Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </>
  );
}

// ------------------------------------------------------------------------------------------------ admissions and discharge summaries

export function AdmissionsTab({ patientId }: { patientId: string }) {
  const navigate = useNavigate();
  const canRead = usePermission('inpatient.admissions.read');
  const admissions = useQuery({ ...emrQueries.admissions(patientId), enabled: canRead });
  if (!canRead) {
    return (
      <EmptyState
        title="Admissions are not available to you"
        description="Your role cannot view in-patient admissions."
      />
    );
  }
  return (
    <DataTable
      ariaLabel="Admissions"
      rows={admissions.data}
      loading={admissions.isLoading}
      error={admissions.error}
      onRetry={() => void admissions.refetch()}
      getRowId={(a) => a.id}
      onRowClick={(a) =>
        void navigate({
          to: '/emr/patients/$patientId/discharge/$admissionId',
          params: { patientId, admissionId: a.id },
        })
      }
      size="small"
      columns={[
        {
          key: 'number',
          header: 'Admission',
          width: 150,
          render: (a) => <Text font="monospace">{a.admissionNumber}</Text>,
        },
        { key: 'admitted', header: 'Admitted', render: (a) => formatDateTime(a.admittedAt) },
        {
          key: 'discharged',
          header: 'Discharged',
          render: (a) => (a.dischargedAt ? formatDateTime(a.dischargedAt) : '—'),
        },
        { key: 'ward', header: 'Ward', priority: 'low', render: (a) => a.wardName ?? '—' },
        { key: 'consultant', header: 'Consultant', priority: 'low', render: (a) => a.consultantName ?? '—' },
        { key: 'los', header: 'Stay', width: 80, priority: 'low', render: (a) => `${a.lengthOfStayDays} d` },
        { key: 'status', header: 'Status', render: (a) => <StatusBadge status={a.status} /> },
      ]}
      rowActions={(a) => (
        <Button
          size="small"
          icon={<DocumentTextRegular />}
          onClick={() =>
            void navigate({
              to: '/emr/patients/$patientId/discharge/$admissionId',
              params: { patientId, admissionId: a.id },
            })
          }
        >
          Discharge summary
        </Button>
      )}
      emptyTitle="No admissions"
      emptyDescription="In-patient stays and their discharge summaries appear here."
    />
  );
}
