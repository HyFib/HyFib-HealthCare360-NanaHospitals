import { Button, Caption1, Field, Input, makeStyles, Text, tokens } from '@fluentui/react-components';
import { FoodRegular, PrintRegular } from '@fluentui/react-icons';
import { formatDateOnly, todayIso, useSession } from '@hc360/core';
import { DataTable, EmptyState, ErrorState, FilterBar, KpiCard, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { dietaryQueries, type ProductionLine } from '@/modules/dietary/api';
import { printProductionSheet } from '@/modules/dietary/print';
import { MEAL_SLOT_LABEL, MEAL_SLOTS } from '@/modules/dietary/rules';
import { formatTimeOfDay } from '@/modules/inpatient/time';

const DATE = /^\d{4}-\d{2}-\d{2}$/;

export const Route = createFileRoute('/_app/dietary/kitchen')({
  validateSearch: (s: Record<string, unknown>): { date?: string } => ({ date: typeof s.date === 'string' && DATE.test(s.date) ? s.date : undefined }),
  component: KitchenPage,
});

const useStyles = makeStyles({
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(150px, 1fr))' },
  rounds: { display: 'flex', flexDirection: 'column', gap: '16px' },
  notes: { color: tokens.colorPaletteRedForeground1 },
});

/** Kitchen production list (DIET-03): for each round and diet, how many veg, non-veg, egg and Jain meals to cook. */
function KitchenPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const facilityName = useSession((s) => s.user?.facilityName) ?? 'Nana Hospitals';
  const today = todayIso();
  const date = search.date ?? today;
  const production = useQuery(dietaryQueries.production(date));
  const p = production.data;
  const lines = p?.lines ?? [];
  const sum = (pick: (l: ProductionLine) => number) => lines.reduce((total, l) => total + pick(l), 0);

  return (
    <Page>
      <PageHeader
        title="Kitchen production"
        icon={<FoodRegular />}
        subtitle={`${formatDateOnly(date)} · counts include every planned meal not cancelled`}
        actions={
          <Button icon={<PrintRegular />} disabled={!p} onClick={() => p && void printProductionSheet(p, facilityName).catch((error: unknown) => notify.error(error, 'Printing failed'))}>
            Print cooking list
          </Button>
        }
      />
      <Section>
        <FilterBar>
          <Field label="Date">
            <Input type="date" value={date} onChange={(_, d) => d.value && void navigate({ search: { date: d.value === today ? undefined : d.value }, replace: true })} />
          </Field>
        </FilterBar>
      </Section>
      {production.error && !p ? (
        <ErrorState error={production.error} onRetry={() => void production.refetch()} />
      ) : (
        <>
          <div className={styles.kpis}>
            <KpiCard label="Meals" value={p ? p.totalMeals : '…'} />
            <KpiCard label="Vegetarian" value={p ? sum((l) => l.vegetarian) : '…'} />
            <KpiCard label="Non-vegetarian" value={p ? sum((l) => l.nonVegetarian) : '…'} />
            <KpiCard label="Egg" value={p ? sum((l) => l.eggetarian) : '…'} />
            <KpiCard label="Jain" value={p ? sum((l) => l.jain) : '…'} />
          </div>
          {p && lines.length === 0 ? (
            <EmptyState icon={<FoodRegular />} title="Nothing to cook" description="No meals are planned for this day yet." />
          ) : (
            <div className={styles.rounds}>
              {MEAL_SLOTS.map((slot) => {
                const rows = lines.filter((l) => l.slot === slot);
                if (p && rows.length === 0) {
                  return null;
                }
                return (
                  <Section
                    key={slot}
                    title={`${MEAL_SLOT_LABEL[slot]}${rows[0] ? ` · ${formatTimeOfDay(rows[0].serviceTime)}` : ''}`}
                    description={p ? `${rows.reduce((t, l) => t + l.total, 0)} meals` : undefined}
                    flush
                  >
                    <DataTable
                      ariaLabel={`${MEAL_SLOT_LABEL[slot]} production`}
                      rows={p ? rows : undefined}
                      loading={production.isLoading}
                      getRowId={(l) => `${l.slot}-${l.dietCode}`}
                      size="small"
                      columns={[
                        { key: 'diet', header: 'Diet', render: (l) => <Text weight="semibold">{l.dietName ?? l.dietCode}</Text> },
                        { key: 'veg', header: 'Veg', width: 80, align: 'end', render: (l) => l.vegetarian || '—' },
                        { key: 'nonveg', header: 'Non-veg', width: 90, align: 'end', render: (l) => l.nonVegetarian || '—' },
                        { key: 'egg', header: 'Egg', width: 70, align: 'end', render: (l) => l.eggetarian || '—' },
                        { key: 'jain', header: 'Jain', width: 70, align: 'end', render: (l) => l.jain || '—' },
                        { key: 'total', header: 'Total', width: 80, align: 'end', render: (l) => <Text weight="bold">{l.total}</Text> },
                        {
                          key: 'notes',
                          header: 'Special notes',
                          priority: 'low',
                          render: (l) =>
                            l.specialNotes.length ? (
                              <span>
                                {l.specialNotes.map((n) => (
                                  <Caption1 key={n} block className={/allerg/i.test(n) ? styles.notes : undefined}>
                                    {n}
                                  </Caption1>
                                ))}
                              </span>
                            ) : (
                              '—'
                            ),
                        },
                      ]}
                      emptyTitle="No meals in this round"
                    />
                  </Section>
                );
              })}
            </div>
          )}
        </>
      )}
    </Page>
  );
}
