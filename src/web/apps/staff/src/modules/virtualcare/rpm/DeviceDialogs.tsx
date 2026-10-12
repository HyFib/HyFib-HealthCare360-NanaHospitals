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
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkRegular, CopyRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import {
  applyServerErrors,
  CloseRequestScope,
  FormGrid,
  SelectField,
  TextField,
  useCloseRequests,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { z } from 'zod';
import { DEVICE_TYPES, rpmApi, rpmKeys, type DeviceKeyIssued, type RpmDeviceDto } from './api';
import { EnrollmentPicker, type PickedEnrollment } from './EnrollmentPicker';
import { deviceMeasures, deviceTypeLabel, readingTypeInfo } from './labels';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  secret: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase300,
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
    wordBreak: 'break-all',
    userSelect: 'all',
  },
  row: { display: 'flex', gap: '8px', alignItems: 'flex-start' },
  grow: { flex: 1, minWidth: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
});

// ------------------------------------------------------------------ register

const registerSchema = z.object({
  serialNumber: z
    .string()
    .trim()
    .min(1, 'Enter the serial number printed on the device.')
    .max(64, 'Use at most 64 characters.')
    .regex(/^[A-Za-z0-9._:/-]+$/, 'Use letters, digits and . _ : / - only.'),
  type: z.enum(DEVICE_TYPES),
  manufacturer: z.string().trim().max(100),
  model: z.string().trim().max(100),
  enrollment: z.custom<PickedEnrollment | null>(),
});

type RegisterValues = z.infer<typeof registerSchema>;

/** Registers a monitoring device (or a patient's phone gateway) and hands its API key to the next dialog. */
export function RegisterDeviceDialog({
  open,
  enrollment,
  onClose,
  onRegistered,
}: {
  open: boolean;
  enrollment?: PickedEnrollment | null;
  onClose: () => void;
  onRegistered: (key: DeviceKeyIssued) => void;
}) {
  const closing = useCloseRequests({ onClose });
  return (
    <Dialog open={open} onOpenChange={closing.onOpenChange}>
      <DialogSurface style={{ maxWidth: 600 }}>
        <CloseRequestScope value={closing}>
          {open && <RegisterContent enrollment={enrollment ?? null} onClose={onClose} onRegistered={onRegistered} />}
        </CloseRequestScope>
      </DialogSurface>
    </Dialog>
  );
}

function RegisterContent({
  enrollment,
  onClose,
  onRegistered,
}: {
  enrollment: PickedEnrollment | null;
  onClose: () => void;
  onRegistered: (key: DeviceKeyIssued) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<RegisterValues>({
    resolver: zodResolver(registerSchema),
    defaultValues: { serialNumber: '', type: 'BpMonitor', manufacturer: '', model: '', enrollment },
  });

  const register = useMutation({
    mutationFn: (v: RegisterValues) =>
      rpmApi.registerDevice({
        serialNumber: v.serialNumber,
        type: v.type,
        manufacturer: v.manufacturer || null,
        model: v.model || null,
        enrollmentId: v.enrollment?.id ?? null,
      }),
    onSuccess: (key) => {
      void queryClient.invalidateQueries({ queryKey: rpmKeys.all });
      onRegistered(key);
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not register the device');
      }
    },
  });

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: register.isPending, onClose });

  return (
    <form onSubmit={form.handleSubmit((v) => register.mutate(v))} noValidate>
      <DialogBody>
        <DialogTitle>Register device</DialogTitle>
        <DialogContent className={styles.form}>
          <FormGrid columns={2}>
            <TextField
              control={form.control}
              name="serialNumber"
              label="Serial number"
              required
              autoFocus
              maxLength={64}
            />
            <SelectField
              control={form.control}
              name="type"
              label="Type"
              required
              options={DEVICE_TYPES.map((t) => ({ value: t, label: deviceTypeLabel[t] }))}
            />
            <TextField control={form.control} name="manufacturer" label="Make (optional)" maxLength={100} />
            <TextField control={form.control} name="model" label="Model (optional)" maxLength={100} />
          </FormGrid>
          <Controller
            control={form.control}
            name="enrollment"
            render={({ field }) => (
              <EnrollmentPicker
                value={field.value ?? null}
                onChange={field.onChange}
                label="Give to patient (optional)"
                hint="You can also assign it later."
              />
            )}
          />
          <Caption1 className={styles.muted}>
            A key for the device is shown next, once. Have the device or its setup app ready.
          </Caption1>
        </DialogContent>
        <DialogActions>
          <Button onClick={guard.requestClose} disabled={register.isPending}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={register.isPending}
            icon={register.isPending ? <Spinner size="tiny" /> : undefined}
          >
            Register and show key
          </Button>
        </DialogActions>
      </DialogBody>
      {guard.dialogs}
    </form>
  );
}

// ------------------------------------------------------------------ show the key once

/**
 * Shows a device API key exactly once (RPM-02): the server keeps only its hash. The dialog cannot be dismissed by
 * accident; the user confirms the key is stored before closing, or rotates the key later if it is lost.
 */
export function DeviceKeyDialog({ issued, onClose }: { issued: DeviceKeyIssued | null; onClose: () => void }) {
  return (
    <Dialog open={issued !== null} modalType="alert">
      <DialogSurface style={{ maxWidth: 620 }}>
        {issued && <DeviceKeyContent key={issued.deviceId} issued={issued} onClose={onClose} />}
      </DialogSurface>
    </Dialog>
  );
}

function DeviceKeyContent({ issued, onClose }: { issued: DeviceKeyIssued; onClose: () => void }) {
  const styles = useStyles();
  const [copied, setCopied] = useState<'key' | 'url' | null>(null);
  const [stored, setStored] = useState(false);
  const ingestUrl = new URL(issued.ingestPath, window.location.origin).toString();

  const copy = (what: 'key' | 'url', text: string) => {
    void navigator.clipboard?.writeText(text).then(
      () => setCopied(what),
      () => setCopied(null),
    );
  };

  return (
    <DialogBody>
      <DialogTitle>Device key for {issued.serialNumber}</DialogTitle>
      <DialogContent className={styles.form}>
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Shown only this once</MessageBarTitle>
            Enter it in the device or its phone gateway now. HealthCare360 cannot show it again; if it is lost, rotate
            the key (the old one stops working).
          </MessageBarBody>
        </MessageBar>
        <Field label="Device key">
          <div className={styles.row}>
            <div className={mergeClasses(styles.secret, styles.grow)}>{issued.apiKey}</div>
            <Button
              icon={copied === 'key' ? <CheckmarkRegular /> : <CopyRegular />}
              onClick={() => copy('key', issued.apiKey)}
            >
              {copied === 'key' ? 'Copied' : 'Copy'}
            </Button>
          </div>
        </Field>
        <Field label="Readings are sent to">
          <div className={styles.row}>
            <div className={mergeClasses(styles.secret, styles.grow)}>POST {ingestUrl}</div>
            <Button
              icon={copied === 'url' ? <CheckmarkRegular /> : <CopyRegular />}
              onClick={() => copy('url', ingestUrl)}
            >
              {copied === 'url' ? 'Copied' : 'Copy'}
            </Button>
          </div>
        </Field>
        <Text>
          With the header <code>{issued.headerName}</code> set to the key. Readings are accepted as JSON or FHIR
          Observations.
        </Text>
        <Checkbox
          checked={stored}
          onChange={(_, d) => setStored(Boolean(d.checked))}
          label="I have entered or safely stored this key"
        />
      </DialogContent>
      <DialogActions>
        <Button appearance="primary" disabled={!stored} onClick={onClose}>
          Done
        </Button>
      </DialogActions>
    </DialogBody>
  );
}

// ------------------------------------------------------------------ assign

/** Gives a device to a monitored patient, moves it to another one, or takes it back. */
export function AssignDeviceDialog({ device, onClose }: { device: RpmDeviceDto | null; onClose: () => void }) {
  return (
    <Dialog open={device !== null} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        {device && <AssignContent key={device.id} device={device} onClose={onClose} />}
      </DialogSurface>
    </Dialog>
  );
}

function AssignContent({ device, onClose }: { device: RpmDeviceDto; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [enrollment, setEnrollment] = useState<PickedEnrollment | null>(null);
  const assign = useMutation({
    mutationFn: (enrollmentId: string | null) => rpmApi.assignDevice(device.id, enrollmentId),
    onSuccess: (_, enrollmentId) => {
      notify.success(enrollmentId ? 'Device assigned' : 'Device taken back');
      void queryClient.invalidateQueries({ queryKey: rpmKeys.all });
      onClose();
    },
    onError: (error) => notify.error(error, 'Could not change the assignment'),
  });

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        if (enrollment) {
          assign.mutate(enrollment.id);
        }
      }}
    >
      <DialogBody>
        <DialogTitle>
          {device.enrollmentId ? 'Move or take back device' : 'Give device to a patient'}
          <Caption1 block className={styles.muted}>
            {deviceTypeLabel[device.type]} · {device.serialNumber} · measures{' '}
            {deviceMeasures[device.type].map((t) => readingTypeInfo[t].label.toLowerCase()).join(', ')}
          </Caption1>
        </DialogTitle>
        <DialogContent className={styles.form}>
          <EnrollmentPicker
            value={enrollment}
            onChange={setEnrollment}
            label={device.enrollmentId ? 'Move to patient' : 'Patient'}
            required
          />
        </DialogContent>
        <DialogActions>
          {device.enrollmentId && (
            <Button disabled={assign.isPending} onClick={() => assign.mutate(null)}>
              Take back from patient
            </Button>
          )}
          <Button onClick={onClose} disabled={assign.isPending}>
            Cancel
          </Button>
          <Button type="submit" appearance="primary" disabled={!enrollment || assign.isPending}>
            Assign
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
