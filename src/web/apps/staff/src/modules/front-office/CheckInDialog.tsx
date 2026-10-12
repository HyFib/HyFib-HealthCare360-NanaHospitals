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
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkCircleRegular, PrintRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { useSession, type PayerType } from '@hc360/core';
import {
  applyServerErrors,
  ChoiceField,
  FormGrid,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { enumOptions, toOptions, useDepartments, usePractitioners } from '@/lib/lookups';
import { printTokenSlip } from '@/lib/print';
import { patientApi, patientKeys, type VisitDetail } from '@/modules/patients/api';

const schema = z
  .object({
    type: z.enum(['OutPatient', 'Emergency', 'DayCare', 'HealthCheckup']),
    departmentId: z.string().nullable(),
    practitionerId: z.string().nullable(),
    payerType: z.enum(['Self', 'Insurance', 'Corporate', 'GovernmentScheme', 'Staff', 'Charity']),
    priority: z.enum(['Normal', 'Priority', 'Emergency']),
    referralSource: z.enum([
      'Self',
      'Doctor',
      'Hospital',
      'HealthCamp',
      'Online',
      'Corporate',
      'Advertisement',
      'Other',
    ]),
    referredBy: z.string().trim().max(200),
    chiefComplaint: z.string().trim().max(500),
    isMlc: z.boolean(),
    mlcNumber: z.string().trim().max(64),
    notifyPatient: z.boolean(),
  })
  .superRefine((v, ctx) => {
    if (!v.departmentId) {
      ctx.addIssue({ code: 'custom', path: ['departmentId'], message: 'Choose a department.' });
    }
    if (v.isMlc && !v.mlcNumber) {
      ctx.addIssue({ code: 'custom', path: ['mlcNumber'], message: 'Enter the MLC number for a medico-legal case.' });
    }
  });

type CheckInValues = z.infer<typeof schema>;

const useStyles = makeStyles({
  done: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '8px',
    padding: '12px 0',
    textAlign: 'center',
  },
  token: {
    fontSize: '56px',
    lineHeight: '64px',
    fontWeight: tokens.fontWeightBold,
    color: tokens.colorBrandForeground1,
    letterSpacing: '2px',
  },
  muted: { color: tokens.colorNeutralForeground3 },
});

export interface CheckInPatient {
  id: string;
  uhid: string;
  fullName: string;
}

/**
 * Check-in / new OP visit with a queue token (SRS FO-03/04). Defaults suit a walk-in OP patient, so the common
 * case is: choose department → Enter.
 */
export function CheckInDialog({
  patient,
  open,
  onClose,
}: {
  patient: CheckInPatient;
  open: boolean;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const facilityName = useSession((s) => s.user?.facilityName) ?? 'Nana Hospitals';
  const [visit, setVisit] = useState<VisitDetail | null>(null);

  const form = useForm<CheckInValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      type: 'OutPatient',
      departmentId: null,
      practitionerId: null,
      payerType: 'Self',
      priority: 'Normal',
      referralSource: 'Self',
      referredBy: '',
      chiefComplaint: '',
      isMlc: false,
      mlcNumber: '',
      notifyPatient: true,
    },
  });
  const [departmentId, isMlc, referralSource] = useWatch({
    control: form.control,
    name: ['departmentId', 'isMlc', 'referralSource'],
  });
  const departments = useDepartments('Clinical');
  const practitioners = usePractitioners(departmentId);

  const create = useMutation({
    mutationFn: (values: CheckInValues) =>
      patientApi.createVisit({
        patientId: patient.id,
        type: values.type,
        departmentId: values.departmentId,
        practitionerId: values.practitionerId,
        appointmentId: null,
        payerType: values.payerType as PayerType,
        payerId: null,
        referralSource: values.referralSource,
        referredBy: values.referredBy || null,
        chiefComplaint: values.chiefComplaint || null,
        isMlc: values.isMlc,
        mlcNumber: values.isMlc ? values.mlcNumber : null,
        notes: null,
        priority: values.priority,
        issueToken: true,
        notifyPatient: values.notifyPatient,
      }),
    onSuccess: (result) => {
      setVisit(result);
      void queryClient.invalidateQueries({ queryKey: patientKeys.all });
      void queryClient.invalidateQueries({ queryKey: ['visits'] });
      void queryClient.invalidateQueries({ queryKey: ['front-office'] });
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not check the patient in');
      }
    },
  });

  const close = () => {
    setVisit(null);
    form.reset();
    onClose();
  };

  const departmentName = departments.data?.find((d) => d.id === visit?.departmentId)?.name ?? '';
  const doctorName = practitioners.data?.find((p) => p.id === visit?.practitionerId)?.name ?? null;

  const print = () => {
    if (!visit?.token) {
      return;
    }
    void printTokenSlip({
      facilityName,
      tokenLabel: visit.token.label,
      department: departmentName,
      doctor: doctorName,
      patientName: patient.fullName,
      uhid: patient.uhid,
      visitNumber: visit.visitNumber,
      issuedAt: visit.token.issuedAt,
    }).catch((error: unknown) => notify.error(error, 'Printing failed'));
  };

  const guard = useUnsavedChanges({
    dirty: !visit && form.formState.isDirty,
    busy: create.isPending,
    open: open,
    onClose: close,
  });

  return (
    <Dialog open={open} onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 720 }}>
        {visit ? (
          <DialogBody>
            <DialogTitle>Checked in</DialogTitle>
            <DialogContent>
              <div className={styles.done} role="status">
                <CheckmarkCircleRegular fontSize={40} color={tokens.colorPaletteGreenForeground1} />
                <Text>
                  {patient.fullName} · {patient.uhid}
                </Text>
                {visit.token ? (
                  <>
                    <Caption1 className={styles.muted}>Token number</Caption1>
                    <span className={styles.token}>{visit.token.label}</span>
                    <Text>
                      {departmentName}
                      {doctorName ? ` · ${doctorName}` : ''}
                    </Text>
                  </>
                ) : (
                  <Text>Visit {visit.visitNumber} created.</Text>
                )}
                <Caption1 className={styles.muted}>Visit number {visit.visitNumber}</Caption1>
              </div>
            </DialogContent>
            <DialogActions>
              {visit.token && (
                <Button icon={<PrintRegular />} onClick={print}>
                  Print token slip
                </Button>
              )}
              <Button appearance="primary" onClick={close} autoFocus>
                Done
              </Button>
            </DialogActions>
          </DialogBody>
        ) : (
          <form onSubmit={form.handleSubmit((values) => create.mutate(values))} noValidate>
            <DialogBody>
              <DialogTitle>
                Check in {patient.fullName}
                <Caption1 block className={styles.muted}>
                  UHID {patient.uhid}
                </Caption1>
              </DialogTitle>
              <DialogContent>
                <FormGrid columns={2}>
                  <SelectField
                    control={form.control}
                    name="departmentId"
                    label="Department"
                    required
                    placeholder={departments.isLoading ? 'Loading…' : 'Choose department'}
                    options={toOptions(departments.data)}
                  />
                  <SelectField
                    control={form.control}
                    name="practitionerId"
                    label="Doctor"
                    placeholder={departmentId ? 'Any available doctor' : 'Choose a department first'}
                    options={toOptions(practitioners.data)}
                    disabled={!departmentId}
                  />
                  <SelectField
                    control={form.control}
                    name="type"
                    label="Visit type"
                    options={enumOptions(['OutPatient', 'Emergency', 'DayCare', 'HealthCheckup'] as const, {
                      OutPatient: 'Out-patient (OP)',
                      DayCare: 'Day care',
                      HealthCheckup: 'Health check-up',
                    })}
                  />
                  <SelectField
                    control={form.control}
                    name="payerType"
                    label="Payment by"
                    options={enumOptions(
                      ['Self', 'Insurance', 'Corporate', 'GovernmentScheme', 'Staff', 'Charity'] as const,
                      {
                        Self: 'Self (cash / card / UPI)',
                        GovernmentScheme: 'Government scheme',
                      },
                    )}
                  />
                </FormGrid>
                <div style={{ height: 12 }} />
                <ChoiceField
                  control={form.control}
                  name="priority"
                  label="Queue priority"
                  hint="Priority: senior citizens, pregnant women, differently-abled"
                  options={[
                    { value: 'Normal', label: 'Normal' },
                    { value: 'Priority', label: 'Priority' },
                    { value: 'Emergency', label: 'Emergency' },
                  ]}
                />
                <TextAreaField
                  control={form.control}
                  name="chiefComplaint"
                  label="Reason for visit"
                  rows={2}
                  maxLength={500}
                />
                <FormGrid columns={2}>
                  <SelectField
                    control={form.control}
                    name="referralSource"
                    label="Referred by"
                    options={enumOptions(
                      [
                        'Self',
                        'Doctor',
                        'Hospital',
                        'HealthCamp',
                        'Online',
                        'Corporate',
                        'Advertisement',
                        'Other',
                      ] as const,
                      {
                        Self: 'Self / walk-in',
                        HealthCamp: 'Health camp',
                      },
                    )}
                  />
                  {referralSource !== 'Self' && (
                    <TextField control={form.control} name="referredBy" label="Referrer name" />
                  )}
                </FormGrid>
                <div style={{ height: 8 }} />
                <SwitchField control={form.control} name="isMlc" label="Medico-legal case (MLC)" />
                {isMlc && <TextField control={form.control} name="mlcNumber" label="MLC number" required />}
                <SwitchField
                  control={form.control}
                  name="notifyPatient"
                  label="Send the token number to the patient by SMS/WhatsApp"
                />
              </DialogContent>
              <DialogActions>
                <Button onClick={guard.requestClose} disabled={create.isPending}>
                  Cancel
                </Button>
                <Button
                  type="submit"
                  appearance="primary"
                  disabled={create.isPending}
                  icon={create.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
                >
                  Check in and issue token
                </Button>
              </DialogActions>
            </DialogBody>
          </form>
        )}
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
