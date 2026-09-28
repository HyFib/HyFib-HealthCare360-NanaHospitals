import {
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
  Option,
  Radio,
  RadioGroup,
  SearchBox,
  Select,
  Spinner,
  Switch,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, BuildingRegular, DeleteRegular } from '@fluentui/react-icons';
import { formatPhone, useDebouncedValue } from '@hc360/core';
import { ConfirmDialog, DataTable, FilterBar, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { searchServices } from '@/modules/billing/api';
import { SERVICE_CATEGORIES, SERVICE_CATEGORY_LABEL, type ServiceCategory } from '@/modules/billing/labels';
import { ActionError, NoAccess } from '@/modules/billing/ui';
import { enumParam, flagParam } from '@/modules/billing/searchParams';
import {
  insuranceApi,
  insuranceKeys,
  insuranceQueries,
  type NonPayableItemDto,
  type PayerDetail,
} from '@/modules/insurance/api';
import { PAYER_CATEGORIES, PAYER_CATEGORY_LABEL, type PayerCategory } from '@/modules/insurance/labels';
import { PayerDrawer } from '@/modules/insurance/PayerDrawer';

interface PayersSearch {
  category?: PayerCategory;
  inactive?: true;
}

export const Route = createFileRoute('/_app/insurance/payers')({
  validateSearch: (s: Record<string, unknown>): PayersSearch => ({
    category: enumParam(s.category, PAYER_CATEGORIES),
    inactive: flagParam(s.inactive),
  }),
  component: PayersPage,
});

const PAYER_READERS = [
  'insurance.payers.manage',
  'insurance.policies.manage',
  'insurance.preauth.manage',
  'insurance.claims.manage',
  'billing.op.create',
  'billing.ip.manage',
  'billing.charges.read',
] as const;

/** Insurers, TPAs, corporates and government schemes (INS-01/02), and what each does not pay for (IRDAI non-payables). */
function PayersPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canRead = usePermission(PAYER_READERS);
  const canManage = usePermission('insurance.payers.manage');
  const [term, setTerm] = useState('');
  const debounced = useDebouncedValue(term.trim(), 300);
  const [editing, setEditing] = useState<PayerDetail | 'new' | null>(null);
  const payers = useQuery({
    ...insuranceQueries.payers({
      category: search.category,
      includeInactive: Boolean(search.inactive),
      search: debounced || undefined,
    }),
    enabled: canRead,
  });

  if (!canRead) {
    return (
      <Page>
        <PageHeader title="Payers" />
        <NoAccess what="payers" />
      </Page>
    );
  }

  const open = async (id: string) => {
    try {
      setEditing(await queryClient.fetchQuery(insuranceQueries.payer(id)));
    } catch (error) {
      notify.error(error, 'Could not open the payer');
    }
  };

  return (
    <Page>
      <PageHeader
        title="Payers"
        subtitle="Insurance companies, TPAs, corporates and government schemes"
        icon={<BuildingRegular />}
        actions={
          canManage && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
              Add payer
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 240px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Name or code" />
            </Field>
            <Field label="Kind">
              <Select
                value={search.category ?? ''}
                onChange={(_, d) =>
                  void navigate({
                    search: (prev) => ({ ...prev, category: (d.value || undefined) as PayerCategory | undefined }),
                  })
                }
              >
                <option value="">All</option>
                {PAYER_CATEGORIES.map((c) => (
                  <option key={c} value={c}>
                    {PAYER_CATEGORY_LABEL[c]}
                  </option>
                ))}
              </Select>
            </Field>
            <Switch
              checked={Boolean(search.inactive)}
              onChange={(_, d) =>
                void navigate({ search: (prev) => ({ ...prev, inactive: d.checked ? true : undefined }) })
              }
              label="Show inactive"
            />
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Payers"
          rows={payers.data}
          loading={payers.isLoading}
          error={payers.error}
          onRetry={() => void payers.refetch()}
          getRowId={(p) => p.id}
          onRowClick={canManage ? (p) => void open(p.id) : undefined}
          columns={[
            {
              key: 'name',
              header: 'Payer',
              render: (p) => (
                <span>
                  <Text weight="semibold">{p.name}</Text>
                  <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                    {p.code}
                  </Caption1>
                </span>
              ),
            },
            { key: 'kind', header: 'Kind', render: (p) => PAYER_CATEGORY_LABEL[p.category] },
            {
              key: 'contact',
              header: 'Contact',
              priority: 'low',
              render: (p) =>
                [p.contactPerson, p.phone ? formatPhone(p.phone) : null].filter(Boolean).join(' · ') || '—',
            },
            { key: 'tariff', header: 'Tariff', priority: 'low', render: (p) => p.tariffPlanName ?? 'Default' },
            { key: 'credit', header: 'Credit', align: 'end', priority: 'low', render: (p) => `${p.creditDays} days` },
            {
              key: 'status',
              header: 'Status',
              render: (p) => (
                <StatusBadge
                  status={p.isActive ? 'Active' : 'Inactive'}
                  tone={p.isActive ? 'success' : 'informative'}
                />
              ),
            },
          ]}
          emptyTitle="No payers match"
          emptyDescription="Add the insurers, TPAs and corporates the hospital has agreements with."
          size="small"
        />
      </Section>
      <NonPayablesSection canManage={canManage} />
      {editing && <PayerDrawer payer={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </Page>
  );
}

function NonPayablesSection({ canManage }: { canManage: boolean }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canRead = usePermission(['insurance.payers.manage', 'insurance.claims.manage', 'billing.ip.manage']);
  const [payerId, setPayerId] = useState('');
  const [adding, setAdding] = useState(false);
  const [deleting, setDeleting] = useState<NonPayableItemDto | null>(null);
  const payers = useQuery(insuranceQueries.payers());
  const items = useQuery({ ...insuranceQueries.nonPayables(payerId || undefined), enabled: canRead });
  const remove = useMutation({
    mutationFn: (id: string) => insuranceApi.deleteNonPayable(id),
    onSuccess: () => {
      notify.success('Removed from the non-payable list');
      setDeleting(null);
      void queryClient.invalidateQueries({ queryKey: insuranceKeys.nonPayables });
    },
  });

  if (!canRead) {
    return null;
  }

  return (
    <Section
      title="Items insurers do not pay for"
      description="Consumables, admission kits and the like (IRDAI non-payables). The patient pays for these on insured bills."
      actions={
        canManage && (
          <Button icon={<AddRegular />} onClick={() => setAdding(true)}>
            Add item
          </Button>
        )
      }
      flush
    >
      <div style={{ padding: '0 16px 12px' }}>
        <Field label="For payer" style={{ maxWidth: 360 }}>
          <Select value={payerId} onChange={(_, d) => setPayerId(d.value)}>
            <option value="">All payers</option>
            {(payers.data ?? []).map((p) => (
              <option key={p.id} value={p.id}>
                {p.name}
              </option>
            ))}
          </Select>
        </Field>
      </div>
      <DataTable
        ariaLabel="Non-payable items"
        rows={items.data}
        loading={items.isLoading}
        error={items.error}
        onRetry={() => void items.refetch()}
        getRowId={(i) => i.id}
        columns={[
          {
            key: 'item',
            header: 'Item',
            render: (i) =>
              i.serviceItemId
                ? `${i.serviceName ?? ''} (${i.serviceCode ?? ''})`
                : `All of ${i.category ? SERVICE_CATEGORY_LABEL[i.category] : '—'}`,
          },
          { key: 'payer', header: 'Payer', render: (i) => i.payerName ?? 'Every payer' },
          { key: 'notes', header: 'Notes', priority: 'low', render: (i) => i.notes ?? '—' },
        ]}
        rowActions={
          canManage
            ? (i) => (
                <Button
                  size="small"
                  appearance="subtle"
                  icon={<DeleteRegular />}
                  aria-label="Remove item"
                  onClick={() => setDeleting(i)}
                />
              )
            : undefined
        }
        emptyTitle="No non-payable items"
        size="small"
      />
      {adding && <AddNonPayableDialog onClose={() => setAdding(false)} />}
      <ConfirmDialog
        open={deleting !== null}
        title="Remove this item from the non-payable list?"
        confirmLabel="Remove"
        destructive
        busy={remove.isPending}
        onConfirm={() => deleting && remove.mutate(deleting.id)}
        onCancel={() => setDeleting(null)}
      >
        Insurers will be asked to pay for it on new bills.
        <ActionError error={remove.error} />
      </ConfirmDialog>
    </Section>
  );
}

function AddNonPayableDialog({ onClose }: { onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const payers = useQuery(insuranceQueries.payers());
  const [payerId, setPayerId] = useState('');
  const [kind, setKind] = useState<'service' | 'category'>('service');
  const [service, setService] = useState<{ id: string; label: string } | null>(null);
  const [category, setCategory] = useState<ServiceCategory | ''>('');
  const [term, setTerm] = useState('');
  const [notes, setNotes] = useState('');
  const debounced = useDebouncedValue(term.trim(), 250);
  const results = useQuery({
    queryKey: ['billing', 'service-search', debounced],
    queryFn: () => searchServices(debounced),
    enabled: kind === 'service' && debounced.length >= 2,
  });
  const valid = kind === 'service' ? service !== null : category !== '';

  const add = useMutation({
    mutationFn: () =>
      insuranceApi.addNonPayable({
        payerId: payerId || null,
        serviceItemId: kind === 'service' ? (service?.id ?? null) : null,
        category: kind === 'category' && category ? category : null,
        notes: notes.trim() || null,
      }),
    onSuccess: () => {
      notify.success('Added to the non-payable list');
      void queryClient.invalidateQueries({ queryKey: insuranceKeys.nonPayables });
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !add.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !add.isPending) {
              add.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Add a non-payable item</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <Field label="Payer">
                <Select value={payerId} onChange={(_, d) => setPayerId(d.value)}>
                  <option value="">Every payer</option>
                  {(payers.data ?? []).map((p) => (
                    <option key={p.id} value={p.id}>
                      {p.name}
                    </option>
                  ))}
                </Select>
              </Field>
              <RadioGroup
                layout="horizontal"
                value={kind}
                onChange={(_, d) => setKind(d.value as 'service' | 'category')}
              >
                <Radio value="service" label="One service" />
                <Radio value="category" label="A whole category" />
              </RadioGroup>
              {kind === 'service' ? (
                <Field label="Service" required>
                  <Combobox
                    placeholder="Type a service name or code"
                    value={term || service?.label || ''}
                    selectedOptions={service ? [service.id] : []}
                    onChange={(event) => setTerm(event.target.value)}
                    onOptionSelect={(_, data) => {
                      const item = results.data?.find((s) => s.id === data.optionValue);
                      setService(item ? { id: item.id, label: `${item.name} (${item.code})` } : null);
                      setTerm('');
                    }}
                    autoFocus
                  >
                    {(results.data ?? []).map((s) => (
                      <Option key={s.id} value={s.id} text={`${s.name} (${s.code})`}>
                        {s.name} ({s.code})
                      </Option>
                    ))}
                  </Combobox>
                </Field>
              ) : (
                <Field label="Category" required>
                  <Select value={category} onChange={(_, d) => setCategory(d.value as ServiceCategory | '')}>
                    <option value="">Choose…</option>
                    {SERVICE_CATEGORIES.map((c) => (
                      <option key={c} value={c}>
                        {SERVICE_CATEGORY_LABEL[c]}
                      </option>
                    ))}
                  </Select>
                </Field>
              )}
              <Field label="Notes">
                <Input value={notes} onChange={(_, d) => setNotes(d.value)} maxLength={300} />
              </Field>
              <ActionError error={add.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={add.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || add.isPending}
                icon={add.isPending ? <Spinner size="tiny" /> : undefined}
              >
                Add
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
