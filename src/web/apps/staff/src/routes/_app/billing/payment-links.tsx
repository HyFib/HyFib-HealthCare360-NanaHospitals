import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Radio,
  RadioGroup,
  SearchBox,
  Select,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowClockwiseRegular,
  BedRegular,
  CopyRegular,
  LinkRegular,
  MoreHorizontalRegular,
  ReceiptRegular,
} from '@fluentui/react-icons';
import { formatDateTime, formatInr, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { billingApi, billingQueries, type GatewayOrderDto } from '@/modules/billing/api';
import { useBillingLive, useRefreshBilling } from '@/modules/billing/hooks';
import { ipQueries } from '@/modules/billing/ipApi';
import { GATEWAY_ORDER_STATUS_LABEL, GATEWAY_ORDER_STATUSES, type GatewayOrderStatus } from '@/modules/billing/labels';
import { PaymentLinkDialog, type LinkTarget } from '@/modules/billing/PaymentLinkDialog';
import { enumParam, pageParam } from '@/modules/billing/searchParams';
import { Amount, LinkStatusBadge, NoAccess } from '@/modules/billing/ui';

interface LinksSearch {
  status?: GatewayOrderStatus;
  page?: number;
}

export const Route = createFileRoute('/_app/billing/payment-links')({
  validateSearch: (s: Record<string, unknown>): LinksSearch => ({
    status: enumParam(s.status, GATEWAY_ORDER_STATUSES),
    page: pageParam(s.page),
  }),
  component: PaymentLinksPage,
});

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** Payment links (INT-PG): links sent by SMS/WhatsApp for bills and IP advances, their status, and a refresh from the gateway. */
function PaymentLinksPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const refresh = useRefreshBilling();
  const canRead = usePermission(['billing.payments.collect', 'billing.charges.read']);
  const canCreate = usePermission(['billing.payments.collect', 'billing.deposits']);
  const [choosing, setChoosing] = useState(false);
  const [target, setTarget] = useState<LinkTarget | null>(null);
  useBillingLive();
  const links = useQuery({
    ...billingQueries.paymentLinks({ status: search.status, page: search.page ?? 1 }),
    enabled: canRead,
  });

  const check = useMutation({
    mutationFn: (id: string) => billingApi.refreshPaymentLink(id),
    onSuccess: (order) => {
      notify.info(`Link ${GATEWAY_ORDER_STATUS_LABEL[order.status].toLowerCase()}`, formatInr(order.amount));
      void refresh();
    },
    onError: (error) => notify.error(error, 'Could not check the link'),
  });

  if (!canRead) {
    return (
      <Page>
        <PageHeader title="Payment links" />
        <NoAccess what="payment links" />
      </Page>
    );
  }

  const copy = (order: GatewayOrderDto) => {
    if (order.linkUrl) {
      void navigator.clipboard?.writeText(order.linkUrl).then(() => notify.info('Link copied'));
    }
  };

  return (
    <Page>
      <PageHeader
        title="Payment links"
        subtitle="Online payment requests sent to patients; receipts are recorded automatically when paid"
        icon={<LinkRegular />}
        actions={
          canCreate && (
            <Button appearance="primary" icon={<LinkRegular />} onClick={() => setChoosing(true)}>
              New payment link
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Status">
              <Select
                value={search.status ?? ''}
                onChange={(_, d) =>
                  void navigate({ search: { status: (d.value || undefined) as GatewayOrderStatus | undefined } })
                }
              >
                <option value="">All</option>
                {GATEWAY_ORDER_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {GATEWAY_ORDER_STATUS_LABEL[s]}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Payment links"
          rows={links.data?.items}
          loading={links.isLoading}
          error={links.error}
          onRetry={() => void links.refetch()}
          getRowId={(o) => o.id}
          columns={[
            { key: 'created', header: 'Created', width: 170, render: (o) => formatDateTime(o.createdAt) },
            {
              key: 'for',
              header: 'For',
              render: (o) => (o.billId ? 'Bill balance' : o.ipAccountId ? 'IP advance' : '—'),
            },
            {
              key: 'channel',
              header: 'Started from',
              priority: 'low',
              render: (o) =>
                o.channel === 'Portal' ? 'Patient portal' : o.kind === 'PaymentLink' ? 'Link from staff' : 'Checkout',
            },
            { key: 'amount', header: 'Amount', align: 'end', render: (o) => <Amount value={o.amount} strong /> },
            {
              key: 'paid',
              header: 'Paid',
              priority: 'low',
              render: (o) => (o.paidAt ? formatDateTime(o.paidAt) : '—'),
            },
            { key: 'status', header: 'Status', width: 170, render: (o) => <LinkStatusBadge status={o.status} /> },
          ]}
          rowActions={(o) => (
            <Menu>
              <MenuTrigger disableButtonEnhancement>
                <Button size="small" appearance="subtle" icon={<MoreHorizontalRegular />} aria-label="Link actions" />
              </MenuTrigger>
              <MenuPopover>
                <MenuList>
                  {o.status === 'Created' && canCreate && (
                    <MenuItem icon={<ArrowClockwiseRegular />} onClick={() => check.mutate(o.id)}>
                      Check payment now
                    </MenuItem>
                  )}
                  {o.linkUrl && (
                    <MenuItem icon={<CopyRegular />} onClick={() => copy(o)}>
                      Copy link
                    </MenuItem>
                  )}
                  {o.billId && (
                    <MenuItem
                      icon={<ReceiptRegular />}
                      onClick={() => void navigate({ to: '/billing/bills/$billId', params: { billId: o.billId! } })}
                    >
                      Open the bill
                    </MenuItem>
                  )}
                  {o.ipAccountId && (
                    <MenuItem
                      icon={<BedRegular />}
                      onClick={() =>
                        void navigate({ to: '/billing/ip/$accountId', params: { accountId: o.ipAccountId! } })
                      }
                    >
                      Open the IP account
                    </MenuItem>
                  )}
                </MenuList>
              </MenuPopover>
            </Menu>
          )}
          emptyTitle="No payment links"
          emptyDescription="Send a link from a bill with a balance, or ask for an advance from an IP account."
          page={search.page ?? 1}
          pageSize={25}
          totalCount={links.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>
      {choosing && (
        <ChooseTargetDialog
          onClose={() => setChoosing(false)}
          onChosen={(chosen) => {
            setChoosing(false);
            setTarget(chosen);
          }}
        />
      )}
      {target && <PaymentLinkDialog open target={target} onClose={() => setTarget(null)} />}
    </Page>
  );
}

/** What the link is for: the balance of a bill, or an advance for a current admission. */
function ChooseTargetDialog({ onClose, onChosen }: { onClose: () => void; onChosen: (target: LinkTarget) => void }) {
  const styles = useStyles();
  const [kind, setKind] = useState<'bill' | 'ip'>('bill');
  const [term, setTerm] = useState('');
  const debounced = useDebouncedValue(term.trim(), 300);
  const bills = useQuery({
    ...billingQueries.bills({ withDueOnly: true, search: debounced || undefined, pageSize: 8 }),
    enabled: kind === 'bill',
  });
  const accounts = useQuery({
    ...ipQueries.accounts({ search: debounced || undefined, status: 'Open' }),
    enabled: kind === 'ip',
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 600 }}>
        <DialogBody>
          <DialogTitle>New payment link</DialogTitle>
          <DialogContent className={styles.body}>
            <RadioGroup layout="horizontal" value={kind} onChange={(_, d) => setKind(d.value as 'bill' | 'ip')}>
              <Radio value="bill" label="Balance of a bill" />
              <Radio value="ip" label="Advance for an admission" />
            </RadioGroup>
            <SearchBox
              value={term}
              onChange={(_, d) => setTerm(d.value)}
              placeholder={kind === 'bill' ? 'Bill number, UHID or name' : 'Admission number, UHID or name'}
              autoFocus
              aria-label="Search"
            />
            {kind === 'bill' ? (
              <DataTable
                ariaLabel="Bills with a balance"
                rows={bills.data?.items}
                loading={bills.isLoading}
                error={bills.error}
                getRowId={(b) => b.id}
                onRowClick={(b) => onChosen({ kind: 'bill', billId: b.id, due: b.dueAmount })}
                size="small"
                columns={[
                  {
                    key: 'bill',
                    header: 'Bill',
                    render: (b) => (
                      <span>
                        <Text weight="semibold">{b.patientName ?? '—'}</Text>
                        <Caption1 block className={styles.muted}>
                          {b.billNumber} · {b.uhid}
                        </Caption1>
                      </span>
                    ),
                  },
                  { key: 'due', header: 'Balance', align: 'end', render: (b) => <Amount value={b.dueAmount} strong /> },
                ]}
                emptyTitle="No bill with a balance matches"
              />
            ) : (
              <DataTable
                ariaLabel="Current admissions"
                rows={accounts.data?.items}
                loading={accounts.isLoading}
                error={accounts.error}
                getRowId={(a) => a.id}
                onRowClick={(a) => onChosen({ kind: 'ip', ipAccountId: a.id })}
                size="small"
                columns={[
                  {
                    key: 'patient',
                    header: 'Patient',
                    render: (a) => (
                      <span>
                        <Text weight="semibold">{a.patientName ?? '—'}</Text>
                        <Caption1 block className={styles.muted}>
                          {a.admissionNumber} · {a.uhid}
                        </Caption1>
                      </span>
                    ),
                  },
                  {
                    key: 'deposit',
                    header: 'Advance held',
                    align: 'end',
                    render: (a) => <Amount value={a.depositBalance} />,
                  },
                ]}
                emptyTitle="No current admission matches"
              />
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>Cancel</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
