import {
  Button,
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
  TextAreaField,
  TextField,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { usePermission } from '@/lib/permissions';
import { inventoryApi, inventoryKeys, inventoryQueries, type ItemCategoryDto } from '@/modules/inventory/api';
import { ITEM_TYPES, itemTypeLabel } from '@/modules/inventory/labels';
import { SUPPLY_PERMISSIONS as S } from '@/modules/inventory/permissions';

export const Route = createFileRoute('/_app/inventory/categories')({
  component: CategoriesPage,
});

/** Item categories (tablets, injections, sutures, linen…) used to group and filter the item master. */
function CategoriesPage() {
  const canManage = usePermission(S.itemsManage);
  const [showInactive, setShowInactive] = useState(false);
  const [editing, setEditing] = useState<ItemCategoryDto | 'new' | null>(null);
  const categories = useQuery(inventoryQueries.categories(false));
  const rows = (categories.data ?? []).filter((c) => showInactive || c.isActive);

  return (
    <Page maxWidth={1100}>
      <PageHeader
        title="Item categories"
        subtitle="Groups for the item master. A category's type is the default for new items in it."
        actions={
          canManage && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
              Add category
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
        <DataTable<ItemCategoryDto>
          ariaLabel="Item categories"
          rows={categories.data ? rows : undefined}
          loading={categories.isLoading}
          error={categories.error}
          onRetry={() => void categories.refetch()}
          getRowId={(c) => c.id}
          onRowClick={canManage ? (c) => setEditing(c) : undefined}
          size="small"
          columns={[
            { key: 'code', header: 'Code', width: 130, render: (c) => <Text font="monospace">{c.code}</Text> },
            { key: 'name', header: 'Name', render: (c) => <Text weight="semibold">{c.name}</Text> },
            { key: 'type', header: 'Default type', render: (c) => itemTypeLabel[c.defaultType] },
            { key: 'desc', header: 'Description', priority: 'low', render: (c) => c.description ?? '—' },
            {
              key: 'status',
              header: 'Status',
              width: 110,
              render: (c) => (
                <StatusBadge
                  status={c.isActive ? 'Active' : 'Inactive'}
                  tone={c.isActive ? 'success' : 'informative'}
                />
              ),
            },
          ]}
          emptyTitle="No categories yet"
          emptyDescription="Add categories such as Tablets, Injections, Sutures and Linen to group items."
          emptyAction={
            canManage ? (
              <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
                Add category
              </Button>
            ) : undefined
          }
        />
      </Section>
      {editing && <CategoryDrawer category={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </Page>
  );
}

const schema = z.object({
  code: z
    .string()
    .trim()
    .min(1, 'Enter a short code.')
    .max(24)
    .regex(/^[A-Za-z0-9\-_/]+$/, "Use letters, digits, '-', '_' or '/'."),
  name: z.string().trim().min(1, 'Enter the name.').max(100),
  defaultType: z.enum(ITEM_TYPES),
  description: z.string().trim().max(300),
  isActive: z.boolean(),
});
type CategoryValues = z.infer<typeof schema>;

function CategoryDrawer({ category, onClose }: { category: ItemCategoryDto | null; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<CategoryValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      code: category?.code ?? '',
      name: category?.name ?? '',
      defaultType: category?.defaultType ?? 'Drug',
      description: category?.description ?? '',
      isActive: category?.isActive ?? true,
    },
  });
  const save = useMutation({
    mutationFn: (v: CategoryValues) =>
      inventoryApi.saveCategory(category?.id ?? null, {
        code: v.code,
        name: v.name,
        defaultType: v.defaultType,
        description: v.description || null,
        isActive: v.isActive,
        version: category?.version ?? null,
      }),
    onSuccess: () => {
      notify.success(category ? 'Category saved' : 'Category added');
      void queryClient.invalidateQueries({ queryKey: inventoryKeys.items });
      onClose();
    },
    onError: (error) => {
      if (isApiError(error) && error.code === 'category.duplicate_code') {
        form.setError('code', { type: 'server', message: error.userMessage });
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the category');
      }
    },
  });
  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <OverlayDrawer open position="end" size="small" onOpenChange={guard.onOpenChange}>
      <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate style={{ display: 'contents' }}>
        <DrawerHeader>
          <DrawerHeaderTitle
            action={
              <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={guard.requestClose} />
            }
          >
            {category ? category.name : 'Add category'}
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <TextField control={form.control} name="code" label="Code" required autoFocus maxLength={24} />
            <TextField control={form.control} name="name" label="Name" required maxLength={100} />
            <SelectField
              control={form.control}
              name="defaultType"
              label="Default item type"
              options={ITEM_TYPES.map((t) => ({ value: t, label: itemTypeLabel[t] }))}
            />
            <TextAreaField control={form.control} name="description" label="Description" rows={2} maxLength={300} />
            <SwitchField control={form.control} name="isActive" label="Active" />
          </div>
        </DrawerBody>
        <DrawerFooter>
          <Button
            type="submit"
            appearance="primary"
            disabled={save.isPending}
            icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            Save
          </Button>
          <Button onClick={guard.requestClose}>Cancel</Button>
        </DrawerFooter>
      </form>
      {guard.dialogs}
    </OverlayDrawer>
  );
}
