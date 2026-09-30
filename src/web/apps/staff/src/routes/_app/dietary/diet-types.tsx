import { Button, Caption1, Switch, Text } from '@fluentui/react-components';
import { AddRegular, EditRegular, FoodRegular } from '@fluentui/react-icons';
import { DataTable, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { DIET_CATEGORY_LABEL, dietaryQueries, type DietTypeDto } from '@/modules/dietary/api';
import { DietTypeDrawer } from '@/modules/dietary/DietTypeDrawer';
import { describeSlots } from '@/modules/dietary/rules';

export const Route = createFileRoute('/_app/dietary/diet-types')({
  component: DietTypesPage,
});

/** Diet master (DIET-01): therapeutic and consistency diets with their usual rounds and daily charge. */
function DietTypesPage() {
  const canManage = usePermission('dietary.masters.manage');
  const [showInactive, setShowInactive] = useState(false);
  const [editing, setEditing] = useState<DietTypeDto | 'new' | null>(null);
  const types = useQuery(dietaryQueries.dietTypes(showInactive));

  return (
    <Page>
      <PageHeader
        title="Diet types"
        icon={<FoodRegular />}
        subtitle="Diets doctors and dietitians can order"
        actions={
          canManage && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
              Add diet
            </Button>
          )
        }
      />
      <Section
        flush
        actions={
          <Switch
            checked={showInactive}
            onChange={(_, d) => setShowInactive(d.checked)}
            label="Show diets not in use"
          />
        }
      >
        <DataTable
          ariaLabel="Diet types"
          rows={types.data}
          loading={types.isLoading}
          error={types.error}
          onRetry={() => void types.refetch()}
          getRowId={(t) => t.id}
          onRowClick={canManage ? (t) => setEditing(t) : undefined}
          columns={[
            {
              key: 'name',
              header: 'Diet',
              render: (t) => (
                <span>
                  <Text weight="semibold">{t.name}</Text> <Caption1>{t.code}</Caption1>
                  {t.description && <Caption1 block>{t.description}</Caption1>}
                </span>
              ),
            },
            { key: 'category', header: 'Category', render: (t) => DIET_CATEGORY_LABEL[t.category] },
            { key: 'rounds', header: 'Usual rounds', priority: 'low', render: (t) => describeSlots(t.defaultMeals) },
            {
              key: 'charge',
              header: 'Charge',
              width: 120,
              priority: 'low',
              render: (t) => t.chargeServiceCode ?? 'Not charged',
            },
            {
              key: 'status',
              header: 'Status',
              width: 110,
              render: (t) => (
                <StatusBadge
                  status={t.isActive ? 'Active' : 'Inactive'}
                  label={t.isActive ? 'In use' : 'Not in use'}
                  tone={t.isActive ? 'success' : 'informative'}
                />
              ),
            },
          ]}
          rowActions={
            canManage
              ? (t) => (
                  <Button
                    size="small"
                    appearance="subtle"
                    icon={<EditRegular />}
                    aria-label={`Edit ${t.name}`}
                    onClick={() => setEditing(t)}
                  />
                )
              : undefined
          }
          emptyTitle="No diets set up"
        />
      </Section>
      {editing && <DietTypeDrawer dietType={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </Page>
  );
}
