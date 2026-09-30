import {
  Badge,
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  Select,
  Spinner,
  Text,
  ToggleButton,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowSyncRegular,
  BowlChopsticksRegular,
  CheckmarkRegular,
  DismissRegular,
  PrintRegular,
  WarningRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, formatTime, isApiError, todayIso, useSession } from '@hc360/core';
import {
  ConfirmDialog,
  DataTable,
  EmptyState,
  ErrorState,
  FilterBar,
  Page,
  PageHeader,
  Section,
  StatusBadge,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import {
  DELIVERY_LABEL,
  dietaryApi,
  dietaryKeys,
  dietaryQueries,
  type PlannedMealDto,
  type WardMealPlan,
} from '@/modules/dietary/api';
import { printWardMealList } from '@/modules/dietary/print';
import { isMealSlot, MEAL_SLOT_LABEL, MEAL_SLOTS, PREFERENCE_SHORT, type MealSlot } from '@/modules/dietary/rules';
import { inpatientQueries } from '@/modules/inpatient/api';
import { ReasonDialog } from '@/modules/inpatient/ReasonDialog';
import { addDays } from '@/modules/inpatient/time';

const DATE = /^\d{4}-\d{2}-\d{2}$/;

interface MealPlanSearch {
  date?: string;
  wardId?: string;
  slot?: MealSlot;
}

export const Route = createFileRoute('/_app/dietary/meal-plans')({
  validateSearch: (s: Record<string, unknown>): MealPlanSearch => ({
    date: typeof s.date === 'string' && DATE.test(s.date) ? s.date : undefined,
    wardId: typeof s.wardId === 'string' && s.wardId ? s.wardId : undefined,
    slot: isMealSlot(s.slot) ? s.slot : undefined,
  }),
  component: MealPlansPage,
});

const useStyles = makeStyles({
  slots: { display: 'flex', gap: '6px', flexWrap: 'wrap' },
  wards: { display: 'flex', flexDirection: 'column', gap: '16px' },
  allergy: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
  actions: { display: 'inline-flex', gap: '6px' },
  big: { minHeight: '36px' },
});

/** Ward-wise meal plans (DIET-02/05): who gets what at each round, and delivery marking on the ward. */
function MealPlansPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const facilityName = useSession((s) => s.user?.facilityName) ?? 'Nana Hospitals';
  const canManage = usePermission('dietary.meals.manage');
  const canReadWards = usePermission('inpatient.admissions.read');
  const today = todayIso();
  const date = search.date ?? today;
  const wards = useQuery({ ...inpatientQueries.wards(), enabled: canReadWards });
  const plans = useQuery(dietaryQueries.mealPlans(date, search.wardId ?? null, search.slot ?? null));
  const [notTaken, setNotTaken] = useState<PlannedMealDto | null>(null);
  const [bulk, setBulk] = useState<WardMealPlan | null>(null);
  const set = (patch: Partial<MealPlanSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch }), replace: true });
  const refresh = () => void queryClient.invalidateQueries({ queryKey: dietaryKeys.all });
  const canGenerate = date >= addDays(today, -1) && date <= addDays(today, 2);

  const generate = useMutation({
    mutationFn: () => dietaryApi.generatePlan(date),
    onSuccess: (r) => {
      notify.success(
        `Meal plan for ${formatDateOnly(r.date)} is up to date`,
        `${r.mealsPlanned} meals planned (${r.mealsAdded} added, ${r.mealsCancelled} cancelled).`,
      );
      refresh();
    },
    onError: (error) => notify.error(error, 'Could not generate the meal plan'),
  });
  const deliver = useMutation({
    mutationFn: ({
      meal,
      status,
      remarks,
    }: {
      meal: PlannedMealDto;
      status: 'Delivered' | 'NotTaken' | 'Returned';
      remarks: string | null;
    }) => dietaryApi.recordDelivery(meal.id, { status, remarks, version: meal.version }),
    onSuccess: (meal) => {
      notify.success(`${meal.patientName ?? 'Meal'}: ${DELIVERY_LABEL[meal.status].toLowerCase()}`);
      setNotTaken(null);
      refresh();
    },
    onError: (error) => {
      if (isApiError(error) && error.isConflict) {
        refresh();
      }
      if (!notTaken) {
        notify.error(error, 'Could not record the delivery');
      }
    },
  });
  const deliverRound = useMutation({
    mutationFn: (plan: WardMealPlan) =>
      dietaryApi.deliverRound({ date, slot: search.slot!, wardId: plan.wardId!, status: 'Delivered' }),
    onSuccess: (result, plan) => {
      notify.success(`${result.updated} meals marked delivered`, plan.wardName ?? undefined);
      setBulk(null);
      refresh();
    },
  });

  const data = plans.data ?? [];

  return (
    <Page>
      <PageHeader
        title="Meal plans"
        icon={<BowlChopsticksRegular />}
        subtitle={`${formatDateOnly(date)}${search.slot ? ` · ${MEAL_SLOT_LABEL[search.slot]}` : ' · all rounds'}`}
        actions={
          canManage && (
            <Button
              appearance="primary"
              icon={generate.isPending ? <Spinner size="tiny" /> : <ArrowSyncRegular />}
              disabled={!canGenerate || generate.isPending}
              title={
                canGenerate
                  ? 'Adds meals for active diet orders and cancels meals no longer needed'
                  : 'Plans can be generated from yesterday up to two days ahead'
              }
              onClick={() => generate.mutate()}
            >
              Update plan
            </Button>
          )
        }
      />
      <Section>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          <FilterBar>
            <Field label="Date">
              <Input
                type="date"
                value={date}
                onChange={(_, d) => d.value && set({ date: d.value === today ? undefined : d.value })}
              />
            </Field>
            {canReadWards && (
              <Field label="Ward">
                <Select value={search.wardId ?? ''} onChange={(_, d) => set({ wardId: d.value || undefined })}>
                  <option value="">All wards</option>
                  {wards.data?.map((w) => (
                    <option key={w.id} value={w.id}>
                      {w.name}
                    </option>
                  ))}
                </Select>
              </Field>
            )}
          </FilterBar>
          <div className={styles.slots} role="toolbar" aria-label="Meal round">
            <ToggleButton shape="circular" checked={!search.slot} onClick={() => set({ slot: undefined })}>
              All rounds
            </ToggleButton>
            {MEAL_SLOTS.map((slot) => (
              <ToggleButton
                key={slot}
                shape="circular"
                checked={search.slot === slot}
                onClick={() => set({ slot: search.slot === slot ? undefined : slot })}
              >
                {MEAL_SLOT_LABEL[slot]}
              </ToggleButton>
            ))}
          </div>
        </div>
      </Section>

      {plans.error && !plans.data ? (
        <ErrorState error={plans.error} onRetry={() => void plans.refetch()} />
      ) : plans.isLoading ? (
        <Section>
          <Spinner label="Loading meal plans…" />
        </Section>
      ) : data.length === 0 ? (
        <EmptyState
          icon={<BowlChopsticksRegular />}
          title="No meals planned"
          description={
            canManage && canGenerate
              ? 'Use Update plan to plan meals from the active diet orders. Plans also refresh by themselves every hour.'
              : 'Meals appear here once diet orders are written.'
          }
        />
      ) : (
        <div className={styles.wards}>
          {data.map((plan) => {
            const pending = plan.items.filter((m) => m.status === 'Planned').length;
            return (
              <Section
                key={plan.wardId ?? 'none'}
                title={plan.wardName ?? 'No ward'}
                description={`${plan.meals} meals · ${pending} to deliver`}
                actions={
                  <>
                    <Button
                      size="small"
                      icon={<PrintRegular />}
                      onClick={() =>
                        void printWardMealList(plan, date, facilityName).catch((error: unknown) =>
                          notify.error(error, 'Printing failed'),
                        )
                      }
                    >
                      Print list
                    </Button>
                    {canManage && search.slot && plan.wardId && pending > 0 && (
                      <Button
                        size="small"
                        appearance="primary"
                        icon={<CheckmarkRegular />}
                        onClick={() => setBulk(plan)}
                      >
                        Mark all {pending} delivered
                      </Button>
                    )}
                  </>
                }
                flush
              >
                <DataTable
                  ariaLabel={`Meals for ${plan.wardName ?? 'ward'}`}
                  rows={plan.items}
                  getRowId={(m) => m.id}
                  size="small"
                  columns={[
                    ...(search.slot
                      ? []
                      : [
                          {
                            key: 'slot',
                            header: 'Round',
                            width: 140,
                            render: (m: PlannedMealDto) => MEAL_SLOT_LABEL[m.slot],
                          },
                        ]),
                    {
                      key: 'bed',
                      header: 'Bed',
                      width: 80,
                      render: (m) => <Text weight="semibold">{m.bedLabel ?? '—'}</Text>,
                    },
                    { key: 'patient', header: 'Patient', render: (m) => m.patientName ?? '—' },
                    {
                      key: 'diet',
                      header: 'Diet',
                      render: (m) => (
                        <span>
                          {m.dietName ?? m.dietCode}{' '}
                          <Badge size="small" appearance="outline">
                            {PREFERENCE_SHORT[m.preference]}
                          </Badge>
                          {(m.restrictions.length > 0 || m.instructions) && (
                            <Caption1 block>
                              {[m.restrictions.join(', '), m.instructions].filter(Boolean).join(' · ')}
                            </Caption1>
                          )}
                          {m.allergyNotes && (
                            <Caption1 block className={styles.allergy}>
                              <WarningRegular aria-hidden /> Allergy: {m.allergyNotes}
                            </Caption1>
                          )}
                        </span>
                      ),
                    },
                    {
                      key: 'status',
                      header: 'Status',
                      width: 150,
                      render: (m) => (
                        <span>
                          <StatusBadge
                            status={m.status}
                            label={DELIVERY_LABEL[m.status]}
                            tone={
                              m.status === 'Delivered'
                                ? 'success'
                                : m.status === 'Planned'
                                  ? 'warning'
                                  : m.status === 'Cancelled'
                                    ? 'informative'
                                    : 'danger'
                            }
                          />
                          {m.deliveredAt && <Caption1 block>{formatTime(m.deliveredAt)}</Caption1>}
                          {m.remarks && <Caption1 block>{m.remarks}</Caption1>}
                        </span>
                      ),
                    },
                  ]}
                  rowActions={
                    canManage
                      ? (m) =>
                          m.status === 'Planned' ? (
                            <span className={styles.actions}>
                              <Button
                                size="small"
                                appearance="primary"
                                className={styles.big}
                                icon={<CheckmarkRegular />}
                                disabled={deliver.isPending}
                                onClick={() => deliver.mutate({ meal: m, status: 'Delivered', remarks: null })}
                              >
                                Delivered
                              </Button>
                              <Button
                                size="small"
                                className={styles.big}
                                icon={<DismissRegular />}
                                onClick={() => setNotTaken(m)}
                              >
                                Not taken
                              </Button>
                            </span>
                          ) : m.status === 'Delivered' ? (
                            <Button
                              size="small"
                              onClick={() => deliver.mutate({ meal: m, status: 'Returned', remarks: null })}
                            >
                              Returned
                            </Button>
                          ) : null
                      : undefined
                  }
                  emptyTitle="No meals"
                />
              </Section>
            );
          })}
        </div>
      )}

      <ReasonDialog
        open={notTaken !== null}
        title={notTaken ? `${notTaken.patientName ?? 'Patient'} did not take the meal` : 'Meal not taken'}
        description="The reason helps the dietitian adjust the diet."
        placeholder="e.g. Nil by mouth for procedure, patient away for scan, vomiting"
        confirmLabel="Record not taken"
        busy={deliver.isPending}
        error={deliver.error}
        onConfirm={(reason) => notTaken && deliver.mutate({ meal: notTaken, status: 'NotTaken', remarks: reason })}
        onCancel={() => {
          setNotTaken(null);
          deliver.reset();
        }}
      />
      <ConfirmDialog
        open={bulk !== null}
        title={
          bulk
            ? `Mark the ${search.slot ? MEAL_SLOT_LABEL[search.slot].toLowerCase() : ''} round delivered on ${bulk.wardName ?? 'the ward'}?`
            : ''
        }
        confirmLabel="Mark delivered"
        busy={deliverRound.isPending}
        onConfirm={() => bulk && deliverRound.mutate(bulk)}
        onCancel={() => setBulk(null)}
      >
        Every meal of this round still waiting on the ward is recorded as delivered now. Record meals that were not
        taken one by one first.
      </ConfirmDialog>
    </Page>
  );
}
