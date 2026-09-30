import {
  Button,
  Caption1,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  OverlayDrawer,
  Spinner,
  Switch,
  Text,
} from '@fluentui/react-components';
import { AddRegular, DismissRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { isApiError } from '@hc360/core';
import {
  applyServerErrors,
  DataTable,
  Page,
  PageHeader,
  Section,
  SelectField,
  StatusBadge,
  SwitchField,
  TextField,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { toOptions, useDepartments } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { inventoryApi, inventoryKeys, inventoryQueries, type StoreDto } from '@/modules/inventory/api';
import { STORE_TYPES, storeTypeLabel } from '@/modules/inventory/labels';
import { SUPPLY_PERMISSIONS as S } from '@/modules/inventory/permissions';

export const Route = createFileRoute('/_app/inventory/stores')({
  component: StoresPage,
});

/** Physical stock locations of this hospital: central store, pharmacies, ward sub-stores, OT, lab, CSSD. */
function StoresPage() {
  const navigate = useNavigate();
  const canManage = usePermission(S.itemsManage);
  const canSeeStock = usePermission(S.stockRead);
  const [showInactive, setShowInactive] = useState(false);
  const [editing, setEditing] = useState<StoreDto | 'new' | null>(null);
  const stores = useQuery(inventoryQueries.stores(undefined, showInactive));

  return (
    <Page maxWidth={1200}>
      <PageHeader
        title="Stores"
        subtitle="Every batch, movement and stock document belongs to a store"
        actions={
          canManage && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
              Add store
            </Button>
          )
        }
      />
      <Section
        flush
        actions={
          <Switch checked={showInactive} onChange={(_, d) => setShowInactive(d.checked)} label="Show inactive" />
        }
      >
        <DataTable<StoreDto>
          ariaLabel="Stores"
          rows={stores.data}
          loading={stores.isLoading}
          error={stores.error}
          onRetry={() => void stores.refetch()}
          getRowId={(s) => s.id}
          onRowClick={canManage ? (s) => setEditing(s) : undefined}
          size="small"
          columns={[
            { key: 'code', header: 'Code', width: 100, render: (s) => <Text font="monospace">{s.code}</Text> },
            {
              key: 'name',
              header: 'Store',
              render: (s) => (
                <span>
                  <Text weight="semibold">{s.name}</Text>
                  {s.location && <Caption1 block>{s.location}</Caption1>}
                </span>
              ),
            },
            { key: 'type', header: 'Type', render: (s) => storeTypeLabel[s.type] },
            { key: 'dept', header: 'Department', priority: 'low', render: (s) => s.departmentName ?? '—' },
            { key: 'incharge', header: 'In charge', priority: 'low', render: (s) => s.inChargeName ?? '—' },
            {
              key: 'status',
              header: 'Status',
              width: 110,
              render: (s) => (
                <StatusBadge
                  status={s.isActive ? 'Active' : 'Inactive'}
                  tone={s.isActive ? 'success' : 'informative'}
                />
              ),
            },
          ]}
          rowActions={
            canSeeStock
              ? (s) => (
                  <Button
                    size="small"
                    appearance="subtle"
                    onClick={() => void navigate({ to: '/inventory/stock', search: { storeId: s.id } })}
                  >
                    Stock
                  </Button>
                )
              : undefined
          }
          emptyTitle="No stores yet"
          emptyDescription="Add the central store and the pharmacies first; ward sub-stores can follow."
        />
      </Section>
      {editing && <StoreDrawer store={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </Page>
  );
}

const schema = z.object({
  code: z
    .string()
    .trim()
    .min(1, 'Enter a short code, e.g. OPPH.')
    .max(16)
    .regex(/^[A-Za-z0-9\-_/]+$/, "Use letters, digits, '-', '_' or '/'."),
  name: z.string().trim().min(1, 'Enter the name.').max(100),
  type: z.enum(STORE_TYPES),
  departmentId: z.string().nullable(),
  location: z.string().trim().max(200),
  isActive: z.boolean(),
});
type StoreValues = z.infer<typeof schema>;

function StoreDrawer({ store, onClose }: { store: StoreDto | null; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const departments = useDepartments();
  const form = useForm<StoreValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      code: store?.code ?? '',
      name: store?.name ?? '',
      type: store?.type ?? 'Ward',
      departmentId: store?.departmentId ?? null,
      location: store?.location ?? '',
      isActive: store?.isActive ?? true,
    },
  });
  const save = useMutation({
    mutationFn: (v: StoreValues) =>
      inventoryApi.saveStore(store?.id ?? null, {
        code: v.code,
        name: v.name,
        type: v.type,
        departmentId: v.departmentId,
        inChargeUserId: store?.inChargeUserId ?? null,
        location: v.location || null,
        isActive: v.isActive,
        version: store?.version ?? null,
      }),
    onSuccess: () => {
      notify.success(store ? 'Store saved' : 'Store added');
      void queryClient.invalidateQueries({ queryKey: inventoryKeys.stores });
      onClose();
    },
    onError: (error) => {
      if (isApiError(error) && error.code === 'store.duplicate_code') {
        form.setError('code', { type: 'server', message: error.userMessage });
      } else if (isApiError(error) && error.code === 'store.has_stock') {
        form.setError('isActive', { type: 'server', message: error.userMessage });
        notify.error(error, 'The store still holds stock');
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the store');
      }
    },
  });
  return (
    <OverlayDrawer open position="end" size="small" onOpenChange={(_, d) => !d.open && onClose()}>
      <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate style={{ display: 'contents' }}>
        <DrawerHeader>
          <DrawerHeaderTitle
            action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}
          >
            {store ? store.name : 'Add store'}
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <TextField control={form.control} name="code" label="Code" required autoFocus maxLength={16} />
            <TextField
              control={form.control}
              name="name"
              label="Name"
              required
              maxLength={100}
              hint="e.g. ICU sub-store"
            />
            <SelectField
              control={form.control}
              name="type"
              label="Type"
              options={STORE_TYPES.map((t) => ({ value: t, label: storeTypeLabel[t] }))}
              hint="Pharmacy stores can sell and dispense."
            />
            <SelectField
              control={form.control}
              name="departmentId"
              label="Department"
              placeholder="—"
              options={toOptions(departments.data)}
            />
            <TextField control={form.control} name="location" label="Location" hint="Building, floor or room" />
            <SwitchField
              control={form.control}
              name="isActive"
              label="Active"
              hint="A store that still holds stock cannot be deactivated."
            />
            {store?.inChargeName && <Caption1>In charge: {store.inChargeName}</Caption1>}
          </div>
        </DrawerBody>
        <DrawerFooter>
          <Button
            type="submit"
            appearance="primary"
            disabled={save.isPending}
            icon={save.isPending ? <Spinner size="tiny" /> : undefined}
          >
            Save
          </Button>
          <Button onClick={onClose}>Cancel</Button>
        </DrawerFooter>
      </form>
    </OverlayDrawer>
  );
}
