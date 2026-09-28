import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatInr, isApiError, normalizePhone } from '@hc360/core';
import { applyServerErrors, FormGrid, NumberField, SelectField, SwitchField, TextAreaField, TextField, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { enumOptions, toOptions, useDepartments } from '@/lib/lookups';
import {
  bedApi,
  GENDER_POLICIES,
  GENDER_POLICY_LABEL,
  inpatientKeys,
  inpatientQueries,
  WARD_TYPE_LABEL,
  WARD_TYPES,
  wardApi,
  type BedDto,
  type RoomDto,
  type WardDto,
} from './api';
import { bulkBedLabels } from './bedState';

function ServerError({ error }: { error: unknown }) {
  if (!error || (isApiError(error) && error.isValidation)) {
    return null;
  }
  return (
    <MessageBar intent="error" layout="multiline">
      <MessageBarBody>{isApiError(error) ? error.userMessage : 'Could not save. Please try again.'}</MessageBarBody>
    </MessageBar>
  );
}

function useSaved(message: string, onClose: () => void) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  return () => {
    notify.success(message);
    void queryClient.invalidateQueries({ queryKey: inpatientKeys.all });
    onClose();
  };
}

// ------------------------------------------------------------------ ward

const wardSchema = z.object({
  code: z
    .string()
    .trim()
    .min(1, 'Enter a short code, e.g. GWM.')
    .max(16)
    .regex(/^[A-Za-z0-9-]+$/, 'Use letters, digits or dashes.'),
  name: z.string().trim().min(1, 'Enter the ward name.').max(128),
  type: z.enum(WARD_TYPES),
  floor: z.string().trim().max(32),
  block: z.string().trim().max(64),
  genderPolicy: z.enum(GENDER_POLICIES),
  nurseStation: z.string().trim().max(64),
  phone: z
    .string()
    .trim()
    .refine((v) => !v || normalizePhone(v) !== null, 'Enter a valid phone number or extension with the full number.'),
  departmentId: z.string().nullable(),
  bedServiceItemId: z.string().nullable(),
  displayOrder: z.number({ message: 'Enter a number.' }).int().min(0).max(10_000),
  isActive: z.boolean(),
  notes: z.string().trim().max(500),
});

type WardValues = z.infer<typeof wardSchema>;

/** Add or edit a ward. The bed tariff (a bed-charge service) is charged for every patient-day in the ward. */
export function WardDrawer({ ward, onClose }: { ward: WardDto | null; onClose: () => void }) {
  const notify = useNotify();
  const departments = useDepartments('Clinical');
  const services = useQuery(inpatientQueries.bedServices());
  const saved = useSaved(ward ? 'Ward saved' : 'Ward added', onClose);
  const form = useForm<WardValues>({
    resolver: zodResolver(wardSchema),
    defaultValues: {
      code: ward?.code ?? '',
      name: ward?.name ?? '',
      type: ward?.type ?? 'General',
      floor: ward?.floor ?? '',
      block: ward?.block ?? '',
      genderPolicy: ward?.genderPolicy ?? 'Any',
      nurseStation: ward?.nurseStation ?? '',
      phone: ward?.phone ?? '',
      departmentId: ward?.departmentId ?? null,
      bedServiceItemId: ward?.bedServiceItemId ?? null,
      displayOrder: ward?.displayOrder ?? 0,
      isActive: ward?.isActive ?? true,
      notes: ward?.notes ?? '',
    },
  });

  const save = useMutation({
    mutationFn: async (v: WardValues): Promise<void> => {
      const body = {
        code: v.code,
        name: v.name,
        type: v.type,
        floor: v.floor || null,
        block: v.block || null,
        genderPolicy: v.genderPolicy,
        nurseStation: v.nurseStation || null,
        phone: v.phone ? normalizePhone(v.phone) : null,
        departmentId: v.departmentId,
        bedServiceItemId: v.bedServiceItemId,
        displayOrder: v.displayOrder,
        isActive: v.isActive,
        notes: v.notes || null,
        version: ward?.version ?? null,
      };
      if (ward) {
        await wardApi.update(ward.id, body);
      } else {
        await wardApi.create(body);
      }
    },
    onSuccess: saved,
    onError: (error) => {
      if (isApiError(error) && error.isConflict && error.code === 'concurrency') {
        notify.error(error, 'Someone else changed this ward');
      }
      applyServerErrors(error, form.setError);
    },
  });

  const serviceOptions = (services.data ?? []).map((s) => ({ value: s.id, label: `${s.name} (${s.code}) · ${formatInr(s.basePrice)}/day` }));

  return (
    <OverlayDrawer open position="end" size="medium" onOpenChange={(_, d) => !d.open && onClose()}>
      <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate style={{ display: 'contents' }}>
        <DrawerHeader>
          <DrawerHeaderTitle action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}>
            {ward ? `Edit ${ward.name}` : 'Add ward'}
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 12, paddingBottom: 12 }}>
            <FormGrid columns={2} minColumnWidth={180}>
              <TextField control={form.control} name="code" label="Code" required maxLength={16} hint="Short and unique, e.g. GWM" autoFocus={!ward} />
              <TextField control={form.control} name="name" label="Name" required maxLength={128} placeholder="General Ward — Male" />
              <SelectField control={form.control} name="type" label="Ward class" options={enumOptions(WARD_TYPES, WARD_TYPE_LABEL)} />
              <SelectField control={form.control} name="genderPolicy" label="Admits" options={enumOptions(GENDER_POLICIES, GENDER_POLICY_LABEL)} />
              <TextField control={form.control} name="floor" label="Floor" maxLength={32} />
              <TextField control={form.control} name="block" label="Block / wing" maxLength={64} />
              <TextField control={form.control} name="nurseStation" label="Nurse station" maxLength={64} />
              <TextField control={form.control} name="phone" label="Ward phone" type="tel" maxLength={16} />
            </FormGrid>
            <SelectField
              control={form.control}
              name="bedServiceItemId"
              label="Bed tariff (charged per day)"
              placeholder={services.isLoading ? 'Loading…' : 'No bed charge'}
              options={serviceOptions}
              hint={services.error ? 'Could not load bed-charge services; ask the administrator for the masters permission.' : 'Changing it re-prices new stays only.'}
            />
            <SelectField control={form.control} name="departmentId" label="Department" placeholder="None" options={toOptions(departments.data)} />
            <NumberField control={form.control} name="displayOrder" label="Display order" min={0} max={10_000} hint="Lower numbers are shown first" />
            <TextAreaField control={form.control} name="notes" label="Notes" rows={2} maxLength={500} />
            <SwitchField control={form.control} name="isActive" label="Ward is open" hint="Close a ward only after moving its patients out." />
            <ServerError error={save.error} />
          </div>
        </DrawerBody>
        <DrawerFooter>
          <Button type="submit" appearance="primary" disabled={save.isPending} icon={save.isPending ? <Spinner size="tiny" /> : undefined}>
            {ward ? 'Save' : 'Add ward'}
          </Button>
          <Button onClick={onClose}>Cancel</Button>
        </DrawerFooter>
      </form>
    </OverlayDrawer>
  );
}

// ------------------------------------------------------------------ room

const roomSchema = z.object({
  number: z
    .string()
    .trim()
    .min(1, 'Enter the room number.')
    .max(16)
    .regex(/^[A-Za-z0-9-/ ]+$/, "Use letters, digits, spaces, '/' or '-'."),
  description: z.string().trim().max(200),
  isIsolation: z.boolean(),
  isActive: z.boolean(),
});

type RoomValues = z.infer<typeof roomSchema>;

export function RoomDialog({ wardId, room, onClose }: { wardId: string; room: RoomDto | null; onClose: () => void }) {
  const saved = useSaved(room ? 'Room saved' : 'Room added', onClose);
  const form = useForm<RoomValues>({
    resolver: zodResolver(roomSchema),
    defaultValues: { number: room?.number ?? '', description: room?.description ?? '', isIsolation: room?.isIsolation ?? false, isActive: room?.isActive ?? true },
  });
  const save = useMutation({
    mutationFn: async (v: RoomValues): Promise<void> => {
      const body = { wardId, number: v.number, description: v.description || null, isIsolation: v.isIsolation, isActive: v.isActive, version: room?.version ?? null };
      if (room) {
        await wardApi.updateRoom(room.id, body);
      } else {
        await wardApi.createRoom(body);
      }
    },
    onSuccess: saved,
    onError: (error) => applyServerErrors(error, form.setError),
  });
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !save.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 480 }}>
        <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate>
          <DialogBody>
            <DialogTitle>{room ? `Edit room ${room.number}` : 'Add room'}</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <TextField control={form.control} name="number" label="Room number" required maxLength={16} autoFocus />
              <TextField control={form.control} name="description" label="Description" maxLength={200} placeholder="e.g. Twin-sharing with attached bath" />
              <SwitchField control={form.control} name="isIsolation" label="Isolation room" />
              <SwitchField control={form.control} name="isActive" label="In use" />
              <ServerError error={save.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button type="submit" appearance="primary" disabled={save.isPending}>
                {room ? 'Save' : 'Add room'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

// ------------------------------------------------------------------ single bed

const bedSchema = z.object({
  label: z
    .string()
    .trim()
    .min(1, 'Enter the bed label.')
    .max(24)
    .regex(/^[A-Za-z0-9-/ ]+$/, "Use letters, digits, spaces, '/' or '-'."),
  roomId: z.string().nullable(),
  hasOxygen: z.boolean(),
  hasVentilator: z.boolean(),
  hasMonitor: z.boolean(),
  displayOrder: z.number({ message: 'Enter a number.' }).int().min(0).max(10_000),
  isActive: z.boolean(),
});

type BedValues = z.infer<typeof bedSchema>;

export function BedDialog({ wardId, bed, rooms, nextOrder, onClose }: { wardId: string; bed: BedDto | null; rooms: RoomDto[]; nextOrder: number; onClose: () => void }) {
  const saved = useSaved(bed ? 'Bed saved' : 'Bed added', onClose);
  const inUse = bed ? bed.status === 'Occupied' || bed.status === 'Reserved' : false;
  const form = useForm<BedValues>({
    resolver: zodResolver(bedSchema),
    defaultValues: {
      label: bed?.label ?? '',
      roomId: bed?.roomId ?? null,
      hasOxygen: bed?.hasOxygen ?? false,
      hasVentilator: bed?.hasVentilator ?? false,
      hasMonitor: bed?.hasMonitor ?? false,
      displayOrder: bed?.displayOrder ?? nextOrder,
      isActive: bed?.isActive ?? true,
    },
  });
  const save = useMutation({
    mutationFn: async (v: BedValues): Promise<void> => {
      const body = { wardId, ...v, version: bed?.version ?? null };
      if (bed) {
        await bedApi.update(bed.id, body);
      } else {
        await bedApi.create(body);
      }
    },
    onSuccess: saved,
    onError: (error) => applyServerErrors(error, form.setError),
  });
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !save.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate>
          <DialogBody>
            <DialogTitle>{bed ? `Edit bed ${bed.label}` : 'Add bed'}</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <FormGrid columns={2} minColumnWidth={180}>
                <TextField control={form.control} name="label" label="Bed label" required maxLength={24} hint="Unique in the hospital" autoFocus />
                <SelectField
                  control={form.control}
                  name="roomId"
                  label="Room"
                  placeholder="Open ward (no room)"
                  options={rooms.map((r) => ({ value: r.id, label: `Room ${r.number}${r.isIsolation ? ' (isolation)' : ''}` }))}
                />
              </FormGrid>
              <SwitchField control={form.control} name="hasOxygen" label="Oxygen point" />
              <SwitchField control={form.control} name="hasVentilator" label="Ventilator" />
              <SwitchField control={form.control} name="hasMonitor" label="Cardiac monitor" />
              <NumberField control={form.control} name="displayOrder" label="Display order" min={0} max={10_000} />
              <SwitchField
                control={form.control}
                name="isActive"
                label="In service"
                disabled={inUse}
                hint={inUse ? 'An occupied or reserved bed cannot be taken out of service.' : undefined}
              />
              <ServerError error={save.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button type="submit" appearance="primary" disabled={save.isPending}>
                {bed ? 'Save' : 'Add bed'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

// ------------------------------------------------------------------ bulk beds

const bulkSchema = z.object({
  labelPrefix: z
    .string()
    .trim()
    .min(1, 'Enter a prefix, e.g. the ward code.')
    .max(16)
    .regex(/^[A-Za-z0-9-]+$/, 'Use letters, digits or dashes.'),
  startNumber: z.number({ message: 'Enter a number.' }).int().min(1).max(999),
  count: z.number({ message: 'Enter a number.' }).int().min(1, 'Add at least one bed.').max(100, 'Add at most 100 beds at a time.'),
  roomId: z.string().nullable(),
  hasOxygen: z.boolean(),
  hasVentilator: z.boolean(),
  hasMonitor: z.boolean(),
});

type BulkValues = z.infer<typeof bulkSchema>;

/** Add many beds at once (ward set-up): labels follow "{prefix}-{nn}", continuing the ward's series. */
export function BulkBedsDialog({ ward, rooms, startNumber, onClose }: { ward: WardDto; rooms: RoomDto[]; startNumber: number; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<BulkValues>({
    resolver: zodResolver(bulkSchema),
    defaultValues: { labelPrefix: ward.code, startNumber, count: 10, roomId: null, hasOxygen: false, hasVentilator: false, hasMonitor: false },
  });
  const [prefix, start, count] = useWatch({ control: form.control, name: ['labelPrefix', 'startNumber', 'count'] });
  const labels = bulkBedLabels(prefix ?? '', Number(start), Math.min(Number(count) || 0, 100));
  const save = useMutation({
    mutationFn: (v: BulkValues) => bedApi.createMany({ wardId: ward.id, ...v }),
    onSuccess: (beds) => {
      notify.success(`${beds.length} beds added to ${ward.name}`);
      void queryClient.invalidateQueries({ queryKey: inpatientKeys.all });
      onClose();
    },
    onError: (error) => applyServerErrors(error, form.setError),
  });
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !save.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate>
          <DialogBody>
            <DialogTitle>Add beds to {ward.name}</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <FormGrid columns={3} minColumnWidth={140}>
                <TextField control={form.control} name="labelPrefix" label="Label prefix" required maxLength={16} />
                <NumberField control={form.control} name="startNumber" label="First number" min={1} max={999} />
                <NumberField control={form.control} name="count" label="How many" min={1} max={100} />
              </FormGrid>
              <SelectField
                control={form.control}
                name="roomId"
                label="Room"
                placeholder="Open ward (no room)"
                options={rooms.map((r) => ({ value: r.id, label: `Room ${r.number}` }))}
              />
              <SwitchField control={form.control} name="hasOxygen" label="Oxygen point at every bed" />
              <SwitchField control={form.control} name="hasVentilator" label="Ventilator at every bed" />
              <SwitchField control={form.control} name="hasMonitor" label="Cardiac monitor at every bed" />
              <div aria-live="polite">
                {labels.length > 0 ? (
                  <Text>
                    Creates <strong>{labels.length}</strong> beds:{' '}
                    <Text font="monospace">{labels.length <= 6 ? labels.join(', ') : `${labels.slice(0, 3).join(', ')} … ${labels.at(-1)}`}</Text>
                  </Text>
                ) : (
                  <Caption1 style={{ color: tokens.colorNeutralForeground3 }}>Enter a prefix and a count to preview the labels.</Caption1>
                )}
              </div>
              <ServerError error={save.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button type="submit" appearance="primary" disabled={save.isPending || labels.length === 0} icon={save.isPending ? <Spinner size="tiny" /> : undefined}>
                Add {labels.length || ''} beds
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
