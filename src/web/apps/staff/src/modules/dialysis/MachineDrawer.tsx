import {
  Button,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  OverlayDrawer,
  Spinner,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { ChoiceField, SelectField, SwitchField, TextField, useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { applyFormErrors } from '@/modules/ot/shared/errors';
import { ActionError } from '@/modules/ot/shared/feedback';
import { dialysisApi, dialysisKeys, MACHINE_STATUSES, SERO_STATUSES, type DialysisMachineDto } from './api';
import { machineStatusLabel, seroLabel } from './labels';

const useStyles = makeStyles({ form: { display: 'flex', flexDirection: 'column', gap: '12px' } });

const machineSchema = z.object({
  code: z
    .string()
    .trim()
    .min(1, 'Enter the machine code.')
    .max(16)
    .regex(/^[A-Za-z0-9-]+$/, 'Use letters, digits and hyphens (for example HD-01).'),
  model: z.string().trim().min(1, 'Enter the model.').max(100),
  manufacturer: z.string().trim().max(100),
  serialNumber: z.string().trim().max(64),
  station: z.string().trim().max(64),
  seroDedication: z.enum(SERO_STATUSES),
  status: z.enum(MACHINE_STATUSES),
  isActive: z.boolean(),
});

type MachineValues = z.infer<typeof machineSchema>;

/** Adds or edits a dialysis machine. Its sero dedication decides which patients it may treat. */
export function MachineDrawer({ machine, onClose }: { machine: DialysisMachineDto | null; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<MachineValues>({
    resolver: zodResolver(machineSchema),
    defaultValues: {
      code: machine?.code ?? '',
      model: machine?.model ?? '',
      manufacturer: machine?.manufacturer ?? '',
      serialNumber: machine?.serialNumber ?? '',
      station: machine?.station ?? '',
      seroDedication: machine?.seroDedication ?? 'Negative',
      status: machine?.status ?? 'Available',
      isActive: machine?.isActive ?? true,
    },
  });
  const [sero] = useWatch({ control: form.control, name: ['seroDedication'] });
  const rededicating = machine !== null && machine.seroDedication !== sero;
  const blockedRededication = rededicating && (machine.status === 'InUse' || machine.needsDisinfection);
  const statuses = MACHINE_STATUSES.filter((s) => s !== 'InUse' || machine?.status === 'InUse');

  const save = useMutation({
    mutationFn: (v: MachineValues) =>
      dialysisApi.saveMachine(machine?.id ?? null, {
        code: v.code.toUpperCase(),
        model: v.model,
        manufacturer: v.manufacturer || null,
        serialNumber: v.serialNumber || null,
        station: v.station || null,
        seroDedication: v.seroDedication,
        status: v.status,
        isActive: v.isActive,
        version: machine?.version ?? null,
      }),
    onSuccess: () => {
      notify.success(machine ? 'Machine saved' : 'Machine added');
      void queryClient.invalidateQueries({ queryKey: dialysisKeys.all });
      onClose();
    },
    onError: (error) => {
      applyFormErrors(error, form.setError);
    },
  });

  return (
    <OverlayDrawer open position="end" size="medium" onOpenChange={(_, d) => !d.open && onClose()}>
      <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate style={{ display: 'contents' }}>
        <DrawerHeader>
          <DrawerHeaderTitle
            action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}
          >
            {machine ? `Machine ${machine.code}` : 'Add dialysis machine'}
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          <div className={styles.form}>
            <TextField
              control={form.control}
              name="code"
              label="Code"
              required
              autoFocus
              maxLength={16}
              hint="Shown on the board, for example HD-01."
            />
            <TextField
              control={form.control}
              name="station"
              label="Station / chair"
              maxLength={64}
              placeholder="e.g. Station 3, Isolation room B"
            />
            <ChoiceField
              control={form.control}
              name="seroDedication"
              label="Dedicated to"
              required
              options={SERO_STATUSES.map((s) => ({ value: s, label: `${seroLabel[s]} patients` }))}
              hint="A machine treats patients of one sero category only."
            />
            {rededicating && (
              <MessageBar intent={blockedRededication ? 'error' : 'warning'} layout="multiline">
                <MessageBarBody>
                  <MessageBarTitle>
                    {blockedRededication ? 'Disinfect the machine first' : 'Changing the dedication'}
                  </MessageBarTitle>
                  {blockedRededication
                    ? 'The dedication can only change when the machine is idle and disinfected after its last session.'
                    : `From now on only ${seroLabel[sero].toLowerCase()} patients can be dialysed on ${machine.code}. Label the machine accordingly.`}
                </MessageBarBody>
              </MessageBar>
            )}
            <TextField control={form.control} name="model" label="Model" required maxLength={100} />
            <TextField control={form.control} name="manufacturer" label="Manufacturer" maxLength={100} />
            <TextField control={form.control} name="serialNumber" label="Serial number" maxLength={64} />
            <SelectField
              control={form.control}
              name="status"
              label="Status"
              options={statuses.map((s) => ({ value: s, label: machineStatusLabel[s] }))}
              hint="'In use' is set by starting a session."
              disabled={machine?.status === 'InUse'}
            />
            <SwitchField
              control={form.control}
              name="isActive"
              label="Active"
              hint="Inactive machines are hidden from the board and cannot be booked."
            />
            <ActionError error={save.error} />
          </div>
        </DrawerBody>
        <DrawerFooter>
          <Button
            type="submit"
            appearance="primary"
            disabled={save.isPending || blockedRededication}
            icon={save.isPending ? <Spinner size="tiny" /> : undefined}
          >
            {machine ? 'Save' : 'Add machine'}
          </Button>
          <Button onClick={onClose}>Cancel</Button>
        </DrawerFooter>
      </form>
    </OverlayDrawer>
  );
}
