import { Badge, Button, Caption1, Field, SearchBox, Select, Switch, Text, tokens } from '@fluentui/react-components';
import { AddRegular, BoxRegular, EditRegular, FoodRegular } from '@fluentui/react-icons';
import { formatInr, isApiError } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { canteenApi, canteenKeys, canteenQueries, MENU_CATEGORIES, menuItemBody, type MenuCategory, type MenuItemDto } from '@/modules/canteen/api';
import { MenuItemDrawer, StockDialog } from '@/modules/canteen/MenuForms';

export const Route = createFileRoute('/_app/canteen/menu')({
  component: CanteenMenuPage,
});

/** Menu, prices, GST and stock (CAN-01). Availability can be switched per item as the day goes on. */
function CanteenMenuPage() {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManage = usePermission('canteen.menu.manage');
  const menu = useQuery(canteenQueries.menu(false));
  const [category, setCategory] = useState<MenuCategory | ''>('');
  const [term, setTerm] = useState('');
  const [editing, setEditing] = useState<MenuItemDto | 'new' | null>(null);
  const [stock, setStock] = useState<MenuItemDto | null>(null);

  const toggle = useMutation({
    mutationFn: (item: MenuItemDto) => canteenApi.updateMenuItem(item.id, menuItemBody(item, { isAvailable: !item.isAvailable })),
    onSuccess: (_, item) => {
      notify.success(item.isAvailable ? `${item.name} taken off the menu` : `${item.name} is on the menu`);
      void queryClient.invalidateQueries({ queryKey: canteenKeys.all });
    },
    onError: (error) => {
      notify.error(error, 'Could not change availability');
      if (isApiError(error) && error.isConflict) {
        void queryClient.invalidateQueries({ queryKey: canteenKeys.all });
      }
    },
  });

  const needle = term.trim().toLowerCase();
  const rows = (menu.data ?? []).filter((m) => (!category || m.category === category) && (!needle || m.name.toLowerCase().includes(needle) || m.code.toLowerCase().includes(needle)));

  return (
    <Page>
      <PageHeader
        title="Canteen menu"
        icon={<FoodRegular />}
        subtitle="Prices, GST and availability. Packaged items can track stock."
        actions={
          canManage && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
              Add item
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Find" style={{ flex: '1 1 220px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Name or code" />
            </Field>
            <Field label="Category">
              <Select value={category} onChange={(_, d) => setCategory(d.value as MenuCategory | '')}>
                <option value="">All</option>
                {MENU_CATEGORIES.map((c) => (
                  <option key={c} value={c}>
                    {c}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Menu items"
          rows={menu.data ? rows : undefined}
          loading={menu.isLoading}
          error={menu.error}
          onRetry={() => void menu.refetch()}
          getRowId={(m) => m.id}
          onRowClick={canManage ? (m) => setEditing(m) : undefined}
          size="small"
          columns={[
            {
              key: 'name',
              header: 'Item',
              render: (m) => (
                <span>
                  <Text weight="semibold">{m.name}</Text> <Caption1>{m.code}</Caption1>
                  <Badge size="small" appearance="outline" color={m.isVeg ? 'success' : 'danger'} style={{ marginLeft: 6 }}>
                    {m.isVeg ? 'Veg' : 'Non-veg'}
                  </Badge>
                </span>
              ),
            },
            { key: 'category', header: 'Category', width: 120, render: (m) => m.category },
            { key: 'price', header: 'Price', width: 110, align: 'end', render: (m) => <Text weight="semibold">{formatInr(m.price)}</Text> },
            { key: 'gst', header: 'GST', width: 120, priority: 'low', render: (m) => `${m.gstRate}% ${m.priceIncludesGst ? 'incl.' : 'extra'}` },
            {
              key: 'stock',
              header: 'Stock',
              width: 100,
              align: 'end',
              render: (m) =>
                m.trackStock ? (
                  <Text style={m.stockOnHand <= 5 ? { color: tokens.colorPaletteRedForeground1, fontWeight: 600 } : undefined}>{m.stockOnHand}</Text>
                ) : (
                  <Caption1>Not tracked</Caption1>
                ),
            },
            {
              key: 'available',
              header: 'On menu',
              width: 110,
              render: (m) =>
                canManage ? (
                  <span onClick={(event) => event.stopPropagation()} onKeyDown={(event) => event.stopPropagation()} role="presentation">
                    <Switch
                      checked={m.isAvailable}
                      disabled={toggle.isPending}
                      onChange={() => toggle.mutate(m)}
                      aria-label={`${m.name} on today's menu`}
                      label={m.isAvailable ? 'Yes' : 'No'}
                    />
                  </span>
                ) : m.isAvailable ? (
                  'Yes'
                ) : (
                  'No'
                ),
            },
          ]}
          rowActions={
            canManage
              ? (m) => (
                  <span style={{ display: 'inline-flex', gap: 4 }}>
                    {m.trackStock && (
                      <Button size="small" icon={<BoxRegular />} onClick={() => setStock(m)}>
                        Stock
                      </Button>
                    )}
                    <Button size="small" appearance="subtle" icon={<EditRegular />} aria-label={`Edit ${m.name}`} onClick={() => setEditing(m)} />
                  </span>
                )
              : undefined
          }
          emptyTitle={menu.data?.length ? 'No item matches' : 'The menu is empty'}
          emptyDescription={canManage && !menu.data?.length ? 'Add the canteen items with their prices.' : undefined}
        />
      </Section>
      {editing && <MenuItemDrawer item={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
      {stock && <StockDialog item={stock} onClose={() => setStock(null)} />}
    </Page>
  );
}
