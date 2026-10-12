import {
  Badge,
  Button,
  Caption1,
  Combobox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  makeStyles,
  mergeClasses,
  Option,
  SearchBox,
  Spinner,
  Switch,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, ArrowSyncRegular, DeleteRegular, EditRegular, TableRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatInr, todayIso, useDebouncedValue } from '@hc360/core';
import {
  ConfirmDialog,
  DataTable,
  EmptyState,
  ErrorState,
  FilterBar,
  LoadingState,
  Page,
  PageHeader,
  Section,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { searchServices, type ServiceItemDto } from '@/modules/billing/api';
import { SERVICE_CATEGORY_LABEL, TARIFF_TYPE_LABEL } from '@/modules/billing/labels';
import {
  mastersApi,
  mastersKeys,
  mastersQueries,
  type TariffPlanDetail,
  type TariffPriceDto,
} from '@/modules/billing/mastersApi';
import { amountInputText, formatPercent, parseRupees } from '@/modules/billing/money';
import { dateParam, flagParam, idParam, pageParam, textParam } from '@/modules/billing/searchParams';
import { TariffBulkDialog } from '@/modules/billing/TariffBulkDialog';
import { TariffPlanDrawer } from '@/modules/billing/TariffPlanDrawer';
import { ActionError, Amount, AmountField, NoAccess } from '@/modules/billing/ui';

interface TariffsSearch {
  plan?: string;
  q?: string;
  onDate?: string;
  inactive?: true;
  page?: number;
}

export const Route = createFileRoute('/_app/billing/tariffs')({
  validateSearch: (s: Record<string, unknown>): TariffsSearch => ({
    plan: idParam(s.plan),
    q: textParam(s.q),
    onDate: dateParam(s.onDate),
    inactive: flagParam(s.inactive),
    page: pageParam(s.page),
  }),
  component: TariffsPage,
});

const useStyles = makeStyles({
  layout: {
    display: 'grid',
    gridTemplateColumns: 'minmax(220px, 300px) minmax(0, 1fr)',
    gap: '16px',
    alignItems: 'start',
    '@media (max-width: 1000px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  plans: { display: 'flex', flexDirection: 'column', gap: '4px' },
  plan: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    padding: '8px 10px',
    borderRadius: tokens.borderRadiusMedium,
    border: 'none',
    background: 'transparent',
    textAlign: 'start',
    cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  selected: {
    backgroundColor: tokens.colorBrandBackground2,
    ':hover': { backgroundColor: tokens.colorBrandBackground2Hover },
  },
  muted: { color: tokens.colorNeutralForeground3 },
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  right: { display: 'flex', flexDirection: 'column', gap: '16px', minWidth: 0 },
});

/** Tariff plans and their prices (MB-02): who pays what, from when; single edits or bulk revisions with history kept. */
function TariffsPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canRead = usePermission([
    'billing.tariffs.manage',
    'billing.charges.read',
    'billing.op.create',
    'billing.ip.manage',
  ]);
  const canManage = usePermission('billing.tariffs.manage');
  const [editingPlan, setEditingPlan] = useState<TariffPlanDetail | 'new' | null>(null);
  const [bulk, setBulk] = useState(false);
  const plans = useQuery({ ...mastersQueries.tariffPlans(Boolean(search.inactive)), enabled: canRead });
  const selectedId = search.plan ?? plans.data?.[0]?.id;
  const plan = useQuery({ ...mastersQueries.tariffPlan(selectedId ?? ''), enabled: Boolean(selectedId) });

  if (!canRead) {
    return (
      <Page>
        <PageHeader title="Tariffs" />
        <NoAccess what="tariffs" />
      </Page>
    );
  }

  const selectedListItem = plans.data?.find((p) => p.id === selectedId);

  return (
    <Page>
      <PageHeader
        title="Tariffs"
        subtitle="Price lists for walk-in, corporate, insurance, scheme and staff patients"
        icon={<TableRegular />}
        actions={
          canManage && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditingPlan('new')}>
              New tariff plan
            </Button>
          )
        }
      />
      <div className={styles.layout}>
        <Section
          title="Plans"
          actions={
            <Switch
              checked={Boolean(search.inactive)}
              onChange={(_, d) =>
                void navigate({ search: (prev) => ({ ...prev, inactive: d.checked ? true : undefined }) })
              }
              label="Inactive too"
            />
          }
        >
          {plans.isLoading ? (
            <LoadingState />
          ) : plans.error ? (
            <ErrorState error={plans.error} onRetry={() => void plans.refetch()} />
          ) : (
            <nav className={styles.plans} aria-label="Tariff plans">
              {(plans.data ?? []).map((p) => (
                <button
                  key={p.id}
                  type="button"
                  className={mergeClasses(styles.plan, p.id === selectedId && styles.selected)}
                  aria-current={p.id === selectedId ? 'true' : undefined}
                  onClick={() =>
                    void navigate({ search: (prev) => ({ ...prev, plan: p.id, page: undefined, q: undefined }) })
                  }
                >
                  <Text weight="semibold">
                    {p.name}{' '}
                    {p.isDefault && (
                      <Badge size="small" appearance="tint" color="brand">
                        Default
                      </Badge>
                    )}{' '}
                    {!p.isActive && (
                      <Badge size="small" appearance="tint">
                        Inactive
                      </Badge>
                    )}
                  </Text>
                  <Caption1 className={styles.muted}>
                    {p.code} · {TARIFF_TYPE_LABEL[p.type]}
                    {p.payerName ? ` · ${p.payerName}` : ''} · {p.priceCount} prices
                  </Caption1>
                </button>
              ))}
            </nav>
          )}
        </Section>

        <div className={styles.right}>
          {!selectedId ? (
            <EmptyState
              title="No tariff plan yet"
              description="Create the general plan first; services without a plan price use their standard price."
            />
          ) : plan.isLoading ? (
            <LoadingState />
          ) : plan.error || !plan.data ? (
            <ErrorState error={plan.error} onRetry={() => void plan.refetch()} />
          ) : (
            <>
              <Section
                title={plan.data.name}
                description={`${TARIFF_TYPE_LABEL[plan.data.type]}${plan.data.defaultDiscountPercent > 0 ? ` · default concession ${formatPercent(plan.data.defaultDiscountPercent)}` : ''}`}
                actions={
                  canManage && (
                    <>
                      <Button icon={<EditRegular />} onClick={() => setEditingPlan(plan.data)}>
                        Edit plan
                      </Button>
                      {selectedListItem && (
                        <Button icon={<ArrowSyncRegular />} onClick={() => setBulk(true)}>
                          Bulk price update
                        </Button>
                      )}
                    </>
                  )
                }
              >
                {plan.data.description && <Text block>{plan.data.description}</Text>}
                {plan.data.bedMultipliers.length > 0 && (
                  <Caption1 className={styles.muted}>
                    Room-class multipliers:{' '}
                    {plan.data.bedMultipliers
                      .map((m) => `${m.bedServiceName ?? m.bedServiceCode} × ${m.multiplier}`)
                      .join(' · ')}
                  </Caption1>
                )}
              </Section>
              <PricesSection planId={plan.data.id} canManage={canManage} search={search} />
            </>
          )}
        </div>
      </div>
      {editingPlan && (
        <TariffPlanDrawer
          plan={editingPlan === 'new' ? null : editingPlan}
          onClose={() => setEditingPlan(null)}
          onSaved={(saved) => {
            setEditingPlan(null);
            void navigate({ search: (prev) => ({ ...prev, plan: saved.id }) });
          }}
        />
      )}
      {bulk && selectedListItem && <TariffBulkDialog plan={selectedListItem} onClose={() => setBulk(false)} />}
    </Page>
  );
}

function PricesSection({ planId, canManage, search }: { planId: string; canManage: boolean; search: TariffsSearch }) {
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const [editing, setEditing] = useState<TariffPriceDto | 'new' | null>(null);
  const [deleting, setDeleting] = useState<TariffPriceDto | null>(null);
  const prices = useQuery(
    mastersQueries.tariffPrices(planId, {
      search: debounced || undefined,
      onDate: search.onDate,
      page: search.page ?? 1,
    }),
  );

  const remove = useMutation({
    mutationFn: (price: TariffPriceDto) => mastersApi.deletePrice(planId, price.id),
    onSuccess: () => {
      notify.success('Price removed');
      setDeleting(null);
      void queryClient.invalidateQueries({ queryKey: mastersKeys.tariffs });
    },
  });

  return (
    <Section
      title="Prices"
      description="A service without a price here is charged at its standard (base) price"
      actions={
        canManage && (
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
            Add price
          </Button>
        )
      }
      flush
    >
      <div style={{ padding: '0 16px 12px' }}>
        <FilterBar>
          <Field label="Service" style={{ flex: '1 1 240px' }}>
            <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Name or code" />
          </Field>
          <Field label="Prices in force on" hint="Empty shows every revision">
            <Input
              type="date"
              value={search.onDate ?? ''}
              onChange={(_, d) =>
                void navigate({ search: (prev) => ({ ...prev, onDate: d.value || undefined, page: undefined }) })
              }
            />
          </Field>
        </FilterBar>
      </div>
      <DataTable
        ariaLabel="Tariff prices"
        rows={prices.data?.items}
        loading={prices.isLoading}
        error={prices.error}
        onRetry={() => void prices.refetch()}
        getRowId={(p) => p.id}
        onRowClick={canManage ? (p) => setEditing(p) : undefined}
        columns={[
          {
            key: 'service',
            header: 'Service',
            render: (p) => (
              <span>
                <Text weight="semibold">{p.serviceName}</Text>
                <Caption1 block>
                  {p.serviceCode} · {SERVICE_CATEGORY_LABEL[p.category]}
                </Caption1>
              </span>
            ),
          },
          {
            key: 'base',
            header: 'Standard',
            align: 'end',
            priority: 'low',
            render: (p) => <Amount value={p.basePrice} tone="muted" />,
          },
          { key: 'price', header: 'Plan price', align: 'end', render: (p) => <Amount value={p.price} strong /> },
          { key: 'from', header: 'From', render: (p) => formatDateOnly(p.effectiveFrom) },
          { key: 'to', header: 'Until', render: (p) => (p.effectiveTo ? formatDateOnly(p.effectiveTo) : 'Open') },
        ]}
        rowActions={
          canManage
            ? (p) => (
                <Button
                  size="small"
                  appearance="subtle"
                  icon={<DeleteRegular />}
                  aria-label={`Remove the price of ${p.serviceName}`}
                  onClick={() => setDeleting(p)}
                />
              )
            : undefined
        }
        emptyTitle={debounced ? 'No price matches' : 'No plan prices yet'}
        emptyDescription="Add prices one by one, or use Bulk price update to paste a list."
        page={search.page ?? 1}
        pageSize={25}
        totalCount={prices.data?.totalCount}
        onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
        size="small"
      />
      {editing && (
        <PriceDialog planId={planId} price={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />
      )}
      <ConfirmDialog
        open={deleting !== null}
        title={`Remove the price of ${deleting?.serviceName ?? 'this service'}?`}
        confirmLabel="Remove price"
        destructive
        busy={remove.isPending}
        onConfirm={() => deleting && remove.mutate(deleting)}
        onCancel={() => setDeleting(null)}
      >
        {deleting
          ? `${formatInr(deleting.price)} from ${formatDateOnly(deleting.effectiveFrom)}. Services already charged keep their price.`
          : null}
        <ActionError error={remove.error} />
      </ConfirmDialog>
    </Section>
  );
}

function PriceDialog({
  planId,
  price,
  onClose,
}: {
  planId: string;
  price: TariffPriceDto | null;
  onClose: () => void;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [service, setService] = useState<Pick<ServiceItemDto, 'id' | 'name' | 'code' | 'basePrice'> | null>(
    price
      ? { id: price.serviceItemId, name: price.serviceName, code: price.serviceCode, basePrice: price.basePrice }
      : null,
  );
  const [term, setTerm] = useState('');
  const debounced = useDebouncedValue(term.trim(), 250);
  const [amount, setAmount] = useState(price ? amountInputText(price.price) : '');
  const [from, setFrom] = useState(price?.effectiveFrom ?? todayIso());
  const [to, setTo] = useState(price?.effectiveTo ?? '');
  const results = useQuery({
    queryKey: ['billing', 'service-search', debounced],
    queryFn: () => searchServices(debounced),
    enabled: !price && debounced.length >= 2,
  });
  const parsed = parseRupees(amount, { allowZero: true });
  const datesOk = !to || to >= from;
  const valid = service !== null && parsed.ok && Boolean(from) && datesOk;

  const save = useMutation({
    mutationFn: () =>
      mastersApi.savePrice(planId, {
        serviceItemId: service!.id,
        price: parsed.ok ? parsed.rupees : 0,
        effectiveFrom: from,
        effectiveTo: to || null,
      }),
    onSuccess: (result) => {
      notify.success(result.created ? 'Price added' : 'Price updated', service?.name);
      void queryClient.invalidateQueries({ queryKey: mastersKeys.tariffs });
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !save.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !save.isPending) {
              save.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>{price ? `Price of ${price.serviceName}` : 'Add a price'}</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {!price && (
                <Field label="Service" required>
                  <Combobox
                    placeholder="Type a service name or code"
                    value={term || (service ? `${service.name} (${service.code})` : '')}
                    selectedOptions={service ? [service.id] : []}
                    onChange={(event) => setTerm(event.target.value)}
                    onOptionSelect={(_, data) => {
                      setService(results.data?.find((s) => s.id === data.optionValue) ?? null);
                      setTerm('');
                    }}
                    autoFocus
                  >
                    {(results.data ?? []).map((s) => (
                      <Option key={s.id} value={s.id} text={`${s.name} (${s.code})`}>
                        {s.name} ({s.code}) · standard {formatInr(s.basePrice)}
                      </Option>
                    ))}
                  </Combobox>
                </Field>
              )}
              <AmountField
                label="Plan price"
                required
                value={amount}
                onChange={setAmount}
                error={amount && !parsed.ok ? parsed.error : null}
                hint={service ? `Standard price ${formatInr(service.basePrice)}` : undefined}
                autoFocus={Boolean(price)}
              />
              <Field
                label="From"
                required
                hint={
                  price ? 'Changing the start date adds a new revision' : 'The previous open price ends the day before'
                }
              >
                <Input type="date" value={from} onChange={(_, d) => setFrom(d.value)} />
              </Field>
              <Field
                label="Until"
                hint="Optional"
                validationMessage={datesOk ? undefined : 'The end date must be on or after the start date.'}
                validationState={datesOk ? 'none' : 'error'}
              >
                <Input type="date" value={to} min={from} onChange={(_, d) => setTo(d.value)} />
              </Field>
              <ActionError error={save.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || save.isPending}
                icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Save price
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
