import {
  Button,
  Caption1,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  makeStyles,
  OverlayDrawer,
  Spinner,
  Tag,
  TagGroup,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, DismissRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import {
  DataTable,
  NumberField,
  Page,
  PageHeader,
  Section,
  SelectField,
  StatusBadge,
  SwitchField,
  TextField,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { z } from 'zod';
import { usePermission } from '@/lib/permissions';
import { otApi, otKeys, otQueries, THEATRE_TYPES, type TheatreDto } from '@/modules/ot/api';
import { theatreTypeLabel } from '@/modules/ot/labels';
import { applyFormErrors } from '@/modules/ot/shared/errors';
import { ActionError, NoAccess } from '@/modules/ot/shared/feedback';

export const Route = createFileRoute('/_app/ot/theatres')({
  component: TheatresPage,
});

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
  tags: { flexWrap: 'wrap', gap: '4px' },
});

function TheatresPage() {
  const styles = useStyles();
  const canRead = usePermission('ot.read');
  const canManage = usePermission('ot.theatres.manage');
  const theatres = useQuery({ ...otQueries.theatres(true), enabled: canRead });
  const [editing, setEditing] = useState<TheatreDto | 'new' | null>(null);

  if (!canRead) {
    return (
      <Page>
        <NoAccess what="the theatre list" />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader
        title="Operation theatres"
        subtitle="Rooms that take bookings, with their type and major equipment (used when choosing a theatre)."
        actions={
          canManage && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
              Add theatre
            </Button>
          )
        }
      />
      <Section flush>
        <DataTable
          ariaLabel="Operation theatres"
          rows={theatres.data}
          loading={theatres.isLoading}
          error={theatres.error}
          onRetry={() => void theatres.refetch()}
          getRowId={(t) => t.id}
          onRowClick={canManage ? (t) => setEditing(t) : undefined}
          columns={[
            { key: 'code', header: 'Code', width: 100, render: (t) => <Text weight="semibold">{t.code}</Text> },
            { key: 'name', header: 'Name', render: (t) => t.name },
            { key: 'type', header: 'Type', width: 130, render: (t) => theatreTypeLabel[t.type] },
            { key: 'location', header: 'Location', priority: 'low', render: (t) => t.location ?? '—' },
            {
              key: 'equipment',
              header: 'Equipment',
              priority: 'low',
              render: (t) =>
                t.equipment.length ? (
                  <TagGroup className={styles.tags} aria-label={`Equipment in ${t.code}`}>
                    {t.equipment.slice(0, 4).map((item) => (
                      <Tag key={item} size="small" appearance="outline">
                        {item}
                      </Tag>
                    ))}
                    {t.equipment.length > 4 && (
                      <Caption1 className={styles.muted}>+{t.equipment.length - 4} more</Caption1>
                    )}
                  </TagGroup>
                ) : (
                  '—'
                ),
            },
            { key: 'order', header: 'Order', width: 80, align: 'end', priority: 'low', render: (t) => t.displayOrder },
            {
              key: 'status',
              header: 'Status',
              width: 110,
              render: (t) => (
                <StatusBadge
                  status={t.isActive ? 'Active' : 'Inactive'}
                  tone={t.isActive ? 'success' : 'informative'}
                />
              ),
            },
          ]}
          emptyTitle="No theatres yet"
          emptyDescription={
            canManage
              ? 'Add each operating room so surgeries can be booked into it.'
              : 'Ask the OT coordinator to add the theatres.'
          }
        />
      </Section>
      {editing && <TheatreDrawer theatre={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </Page>
  );
}

const theatreSchema = z.object({
  code: z
    .string()
    .trim()
    .min(1, 'Enter a short code.')
    .max(16)
    .regex(/^[A-Za-z0-9-]+$/, 'Use letters, digits and hyphens (for example OT-1).'),
  name: z.string().trim().min(1, 'Enter the name.').max(100),
  type: z.enum(THEATRE_TYPES),
  location: z.string().trim().max(200),
  displayOrder: z.number().nullable(),
  equipment: z.string().max(6000),
  isActive: z.boolean(),
});

type TheatreValues = z.infer<typeof theatreSchema>;

function TheatreDrawer({ theatre, onClose }: { theatre: TheatreDto | null; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<TheatreValues>({
    resolver: zodResolver(theatreSchema),
    defaultValues: {
      code: theatre?.code ?? '',
      name: theatre?.name ?? '',
      type: theatre?.type ?? 'Major',
      location: theatre?.location ?? '',
      displayOrder: theatre?.displayOrder ?? 0,
      equipment: (theatre?.equipment ?? []).join('\n'),
      isActive: theatre?.isActive ?? true,
    },
  });

  const save = useMutation({
    mutationFn: (values: TheatreValues) =>
      otApi.saveTheatre(theatre?.id ?? null, {
        code: values.code.toUpperCase(),
        name: values.name,
        type: values.type,
        location: values.location || null,
        displayOrder: values.displayOrder ?? 0,
        equipment: values.equipment
          .split(/\r?\n|,/)
          .map((item) => item.trim())
          .filter(Boolean),
        isActive: values.isActive,
        version: theatre?.version ?? null,
      }),
    onSuccess: () => {
      notify.success(theatre ? 'Theatre saved' : 'Theatre added');
      void queryClient.invalidateQueries({ queryKey: ['ot', 'theatres'] });
      void queryClient.invalidateQueries({ queryKey: otKeys.calendars });
      onClose();
    },
    onError: (error) => {
      applyFormErrors(error, form.setError);
    },
  });

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <OverlayDrawer open position="end" size="medium" onOpenChange={guard.onOpenChange}>
      <form onSubmit={form.handleSubmit((values) => save.mutate(values))} noValidate style={{ display: 'contents' }}>
        <DrawerHeader>
          <DrawerHeaderTitle
            action={
              <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={guard.requestClose} />
            }
          >
            {theatre ? `${theatre.code} · ${theatre.name}` : 'Add theatre'}
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
              hint="Shown on the OT board, for example OT-1."
            />
            <TextField
              control={form.control}
              name="name"
              label="Name"
              required
              maxLength={100}
              hint="For example Main theatre 1 (laminar flow)."
            />
            <SelectField
              control={form.control}
              name="type"
              label="Type"
              options={THEATRE_TYPES.map((t) => ({ value: t, label: theatreTypeLabel[t] }))}
            />
            <TextField
              control={form.control}
              name="location"
              label="Location"
              maxLength={200}
              placeholder="Block, floor"
            />
            <Controller
              control={form.control}
              name="equipment"
              render={({ field, fieldState }) => (
                <Field
                  label="Major equipment"
                  hint="One item per line, for example C-arm, laparoscopy tower, laminar flow."
                  validationMessage={fieldState.error?.message}
                  validationState={fieldState.error ? 'error' : 'none'}
                >
                  <Textarea {...field} rows={5} resize="vertical" />
                </Field>
              )}
            />
            <NumberField control={form.control} name="displayOrder" label="Order on the board" min={0} max={10000} />
            <SwitchField
              control={form.control}
              name="isActive"
              label="Takes new bookings"
              hint="Inactive theatres keep their history but cannot be booked."
            />
            <ActionError error={save.error} />
          </div>
        </DrawerBody>
        <DrawerFooter>
          <Button
            type="submit"
            appearance="primary"
            disabled={save.isPending}
            icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            {theatre ? 'Save' : 'Add theatre'}
          </Button>
          <Button onClick={guard.requestClose}>Cancel</Button>
        </DrawerFooter>
      </form>
      {guard.dialogs}
    </OverlayDrawer>
  );
}
