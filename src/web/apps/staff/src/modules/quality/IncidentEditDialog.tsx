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
  MessageBar,
  MessageBarBody,
  Spinner,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { isApiError } from '@hc360/core';
import { applyServerErrors, FormGrid, SelectField, SwitchField, TextField, useNotify } from '@hc360/ui';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { toOptions, useDepartments } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { UserPicker } from '@/modules/hr/shared/UserPicker';
import type { PickedUser } from '@/modules/hr/shared/users';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';
import { qualityApi, qualityKeys, type IncidentDetail } from './api';
import {
  INCIDENT_SEVERITIES,
  INCIDENT_TYPES,
  incidentTypeLabel,
  SENTINEL_KINDS,
  sentinelKindLabel,
  severityLabel,
} from './labels';
import { isSentinel } from './rules';

const schema = z.object({
  type: z.enum(INCIDENT_TYPES),
  severity: z.enum(INCIDENT_SEVERITIES),
  sentinelKind: z.enum(SENTINEL_KINDS).nullable(),
  flagAsSentinel: z.boolean(),
  location: z.string().trim().min(1, 'Enter where it happened.').max(150),
  departmentId: z.string().nullable(),
  keepPatient: z.boolean(),
  patient: z.object({ id: z.string(), uhid: z.string(), fullName: z.string() }).nullable(),
  staff: z.array(z.object({ id: z.string(), name: z.string() })).max(20),
});

type Values = z.infer<typeof schema>;

/** Re-grading after review: kind, harm grade, sentinel classification, place, patient and staff involved. */
export function IncidentEditDialog({
  open,
  onClose,
  incident,
}: {
  open: boolean;
  onClose: () => void;
  incident: IncidentDetail;
}) {
  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 760 }}>{open && <Body onClose={onClose} incident={incident} />}</DialogSurface>
    </Dialog>
  );
}

function Body({ onClose, incident }: { onClose: () => void; incident: IncidentDetail }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const departments = useDepartments();
  const canLookUpPatients = usePermission('patients.read');
  const [busy, setBusy] = useState(false);
  const [adding, setAdding] = useState<PickedUser | null>(null);
  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: {
      type: incident.type,
      severity: incident.severity,
      sentinelKind: incident.sentinelKind,
      flagAsSentinel:
        incident.isSentinel &&
        !incident.sentinelKind &&
        incident.severity !== 'Severe' &&
        incident.severity !== 'Death',
      location: incident.location,
      departmentId: incident.departmentId,
      keepPatient: Boolean(incident.patientId),
      patient: null,
      staff: incident.involvedStaff,
    },
  });
  const [severity, sentinelKind, flag, keepPatient] = useWatch({
    control: form.control,
    name: ['severity', 'sentinelKind', 'flagAsSentinel', 'keepPatient'],
  });
  const becomesSentinel = !incident.isSentinel && isSentinel(severity, sentinelKind, flag);

  const submit = async (v: Values) => {
    setBusy(true);
    try {
      await qualityApi.updateIncident(incident.id, {
        type: v.type,
        severity: v.severity,
        sentinelKind: v.sentinelKind,
        flagAsSentinel: v.flagAsSentinel,
        location: v.location.trim(),
        departmentId: v.departmentId,
        patientId: v.patient?.id ?? (v.keepPatient ? incident.patientId : null),
        involvedStaffUserIds: v.staff.map((s) => s.id),
        version: incident.version,
      });
      notify.success(
        'Incident updated',
        becomesSentinel ? 'It is now a sentinel event: the quality team has been alerted.' : undefined,
      );
      void queryClient.invalidateQueries({ queryKey: qualityKeys.all });
      onClose();
    } catch (error) {
      if (isApiError(error) && error.code === 'concurrency') {
        notify.error(error, 'Someone else changed this incident');
        void queryClient.invalidateQueries({ queryKey: qualityKeys.incidents });
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not update the incident');
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={form.handleSubmit(submit)} noValidate>
      <DialogBody>
        <DialogTitle>
          Review and re-grade
          <Caption1 block>{incident.incidentNumber}</Caption1>
        </DialogTitle>
        <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          <FormGrid columns={2} minColumnWidth={220}>
            <SelectField
              control={form.control}
              name="type"
              label="Kind of incident"
              required
              options={INCIDENT_TYPES.map((t) => ({ value: t, label: incidentTypeLabel[t] }))}
            />
            <SelectField
              control={form.control}
              name="severity"
              label="Harm grade"
              required
              options={INCIDENT_SEVERITIES.map((s) => ({ value: s, label: severityLabel[s] }))}
            />
            <SelectField
              control={form.control}
              name="sentinelKind"
              label="Never event"
              placeholder="None"
              options={SENTINEL_KINDS.map((k) => ({ value: k, label: sentinelKindLabel[k] }))}
            />
            <SwitchField control={form.control} name="flagAsSentinel" label="Treat as sentinel event" />
            <TextField control={form.control} name="location" label="Where" required maxLength={150} />
            <SelectField
              control={form.control}
              name="departmentId"
              label="Department"
              placeholder="None"
              options={toOptions(departments.data)}
            />
          </FormGrid>
          {becomesSentinel && (
            <MessageBar intent="warning">
              <MessageBarBody>
                Saving makes this a sentinel event: the quality team is alerted and a completed root-cause analysis is
                required before closing.
              </MessageBarBody>
            </MessageBar>
          )}
          {incident.patientId && (
            <SwitchField control={form.control} name="keepPatient" label="Keep the linked patient" />
          )}
          {canLookUpPatients && (!incident.patientId || !keepPatient) && (
            <Controller
              control={form.control}
              name="patient"
              render={({ field }) => (
                <PatientPicker
                  label={incident.patientId ? 'Link a different patient' : 'Patient involved'}
                  value={field.value as PickedPatient | null}
                  onChange={field.onChange}
                />
              )}
            />
          )}
          <Controller
            control={form.control}
            name="staff"
            render={({ field }) => (
              <Field label="Staff involved" hint="For learning and support, not blame. Up to 20.">
                <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', marginBottom: 6 }}>
                  {field.value.length === 0 && <Caption1>Nobody named.</Caption1>}
                  {field.value.map((person) => (
                    <Badge key={person.id} appearance="tint" size="large">
                      {person.name}
                      <Button
                        size="small"
                        appearance="transparent"
                        icon={<DismissRegular />}
                        aria-label={`Remove ${person.name}`}
                        onClick={() => field.onChange(field.value.filter((p) => p.id !== person.id))}
                      />
                    </Badge>
                  ))}
                </div>
                {field.value.length < 20 && (
                  <UserPicker
                    label="Add a staff member"
                    value={adding}
                    onChange={(user) => {
                      if (user && !field.value.some((p) => p.id === user.id)) {
                        field.onChange([...field.value, user]);
                      }
                      setAdding(null);
                    }}
                  />
                )}
              </Field>
            )}
          />
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={busy}>
            Cancel
          </Button>
          <Button type="submit" appearance="primary" disabled={busy} icon={busy ? <Spinner size="tiny" /> : undefined}>
            Save
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
