import {
  Button,
  Caption1,
  Field,
  makeStyles,
  Menu,
  MenuDivider,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Select,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowClockwiseRegular,
  CheckmarkCircleRegular,
  MoreHorizontalRegular,
  ReceiptAddRegular,
  ReceiptRegular,
} from '@fluentui/react-icons';
import { formatDateTime } from '@hc360/core';
import { DataTable, FilterBar, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import {
  DISCREPANCY_KIND_HELP,
  DISCREPANCY_KIND_LABEL,
  DISCREPANCY_KINDS,
  gatewayApi,
  gatewayKeys,
  gatewayQueries,
  headlineAmount,
  resolvedBy,
  retryLabel,
  type DiscrepancyKind,
  type DiscrepancyStatus,
  type ExceptionFilters,
  type GatewayDiscrepancyDto,
} from './gatewayApi';
import { useRefreshBilling } from './hooks';
import { Amount, ReasonDialog } from './ui';

const useStyles = makeStyles({
  summary: { display: 'flex', flexDirection: 'column', gap: '2px', maxWidth: '560px' },
  muted: { color: tokens.colorNeutralForeground3 },
  amounts: { display: 'flex', flexDirection: 'column', alignItems: 'flex-end', gap: '2px' },
});

/** Worst first: money not credited is red, the rest amber or blue. */
const KIND_TONE: Record<DiscrepancyKind, 'danger' | 'warning' | 'informative'> = {
  UnrecordedPayment: 'danger',
  UnsettledReceipt: 'warning',
  AmountMismatch: 'warning',
  RefundMismatch: 'informative',
};

/** The documents an exception is about, by number (never a patient's name). */
function documents(d: GatewayDiscrepancyDto): string {
  const parts = [
    d.billNumber && `Bill ${d.billNumber}`,
    d.receiptNumber && `Receipt ${d.receiptNumber}`,
    d.refundNumber && `Refund ${d.refundNumber}`,
    d.appointmentNumber && `Appointment ${d.appointmentNumber}`,
  ].filter(Boolean);
  return parts.length > 0 ? parts.join(' · ') : 'Not linked to anything in HC360';
}

/**
 * The exceptions of the gateway reconciliation, worst first, and what can be done about each: record the receipt for
 * money the gateway took, try again, or mark it resolved with a note (audited).
 */
export function GatewayExceptions({
  filters,
  onFilters,
}: {
  filters: ExceptionFilters;
  onFilters: (next: ExceptionFilters) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const refreshBilling = useRefreshBilling();
  const [resolving, setResolving] = useState<GatewayDiscrepancyDto | null>(null);
  const status: DiscrepancyStatus = filters.status ?? 'Open';
  const list = useQuery(gatewayQueries.exceptions({ ...filters, status }));

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: gatewayKeys.all });
    void refreshBilling();
  };

  const record = useMutation({
    mutationFn: (d: GatewayDiscrepancyDto) => gatewayApi.recordReceipt(d.id),
    onSuccess: (result) => {
      notify.success('Receipt recorded', result.message);
      refresh();
    },
    onError: (error) => notify.error(error, 'Could not record the receipt'),
  });
  const retry = useMutation({
    mutationFn: (d: GatewayDiscrepancyDto) => gatewayApi.retry(d.id),
    onSuccess: (result) => {
      if (result.discrepancy.status === 'Resolved') {
        notify.success('Sorted', result.message);
      } else {
        notify.info('Checked again', result.message);
      }
      refresh();
    },
    onError: (error) => notify.error(error, 'Could not try again'),
  });
  const resolve = useMutation({
    mutationFn: ({ d, note }: { d: GatewayDiscrepancyDto; note: string }) => gatewayApi.resolve(d.id, note, d.version),
    onSuccess: () => {
      notify.success('Exception resolved', 'Your note is kept with it.');
      setResolving(null);
      refresh();
    },
  });
  const busy = record.isPending || retry.isPending;

  return (
    <>
      <div style={{ padding: '8px 16px 12px' }}>
        <FilterBar>
          <Field label="Show">
            <Select
              value={status}
              onChange={(_, data) =>
                onFilters({ ...filters, status: data.value as DiscrepancyStatus, page: undefined })
              }
            >
              <option value="Open">To look at</option>
              <option value="Resolved">Resolved</option>
            </Select>
          </Field>
          <Field label="Kind">
            <Select
              value={filters.kind ?? ''}
              onChange={(_, data) =>
                onFilters({
                  ...filters,
                  kind: (data.value || undefined) as DiscrepancyKind | undefined,
                  page: undefined,
                })
              }
            >
              <option value="">All kinds</option>
              {DISCREPANCY_KINDS.map((kind) => (
                <option key={kind} value={kind}>
                  {DISCREPANCY_KIND_LABEL[kind]}
                </option>
              ))}
            </Select>
          </Field>
        </FilterBar>
        {filters.kind && (
          <Caption1 block className={styles.muted}>
            {DISCREPANCY_KIND_HELP[filters.kind]}
          </Caption1>
        )}
      </div>
      <DataTable
        ariaLabel={status === 'Open' ? 'Exceptions to look at' : 'Resolved exceptions'}
        rows={list.data?.items}
        loading={list.isLoading}
        error={list.error}
        onRetry={() => void list.refetch()}
        getRowId={(d) => d.id}
        columns={[
          {
            key: 'kind',
            header: 'Kind',
            width: 150,
            render: (d) => (
              <StatusBadge status={d.kind} tone={KIND_TONE[d.kind]} label={DISCREPANCY_KIND_LABEL[d.kind]} />
            ),
          },
          {
            key: 'what',
            header: 'What happened',
            render: (d) => (
              <span className={styles.summary}>
                <Text>{d.summary}</Text>
                <Caption1 className={styles.muted}>{documents(d)}</Caption1>
                {d.status === 'Resolved' && (
                  <Caption1 className={styles.muted}>
                    Resolved by {resolvedBy(d)}
                    {d.resolution ? `: ${d.resolution}` : ''}
                  </Caption1>
                )}
              </span>
            ),
          },
          {
            key: 'amount',
            header: 'Amount',
            align: 'end',
            width: 150,
            render: (d) => (
              <span className={styles.amounts}>
                <Amount value={headlineAmount(d)} strong />
                {d.gatewayAmount !== null && d.recordedAmount !== null && d.gatewayAmount !== d.recordedAmount && (
                  <Caption1 className={styles.muted}>
                    HC360 <Amount value={d.recordedAmount} />
                  </Caption1>
                )}
              </span>
            ),
          },
          {
            key: 'when',
            header: status === 'Open' ? 'Found' : 'Resolved',
            width: 170,
            priority: 'low',
            render: (d) => formatDateTime(status === 'Open' ? d.detectedAt : d.resolvedAt),
          },
        ]}
        rowActions={(d) => (
          <Menu>
            <MenuTrigger disableButtonEnhancement>
              <Button
                size="small"
                appearance="subtle"
                icon={<MoreHorizontalRegular />}
                aria-label={`Actions for ${d.key}`}
                disabled={busy}
              />
            </MenuTrigger>
            <MenuPopover>
              <MenuList>
                {d.status === 'Open' && d.canRecordReceipt && (
                  <MenuItem icon={<ReceiptAddRegular />} onClick={() => record.mutate(d)}>
                    Record the receipt
                  </MenuItem>
                )}
                {d.status === 'Open' && (
                  <MenuItem icon={<ArrowClockwiseRegular />} onClick={() => retry.mutate(d)}>
                    {retryLabel(d)}
                  </MenuItem>
                )}
                {d.status === 'Open' && (
                  <MenuItem icon={<CheckmarkCircleRegular />} onClick={() => setResolving(d)}>
                    Mark resolved…
                  </MenuItem>
                )}
                {(d.billId || d.refundId) && d.status === 'Open' && <MenuDivider />}
                {d.billId && (
                  <MenuItem
                    icon={<ReceiptRegular />}
                    onClick={() => void navigate({ to: '/billing/bills/$billId', params: { billId: d.billId! } })}
                  >
                    Open the bill
                  </MenuItem>
                )}
                {d.refundId && (
                  <MenuItem
                    icon={<ReceiptRegular />}
                    onClick={() =>
                      void navigate({ to: '/billing/refunds/$refundId', params: { refundId: d.refundId! } })
                    }
                  >
                    Open the refund
                  </MenuItem>
                )}
              </MenuList>
            </MenuPopover>
          </Menu>
        )}
        emptyTitle={status === 'Open' ? 'Nothing to look at' : 'Nothing resolved yet'}
        emptyDescription={
          status === 'Open'
            ? 'Every online payment and refund HC360 knows of matches what the gateway reports.'
            : 'Exceptions you or HC360 resolve are listed here with what was done.'
        }
        page={filters.page ?? 1}
        pageSize={25}
        totalCount={list.data?.totalCount}
        onPageChange={(page) => onFilters({ ...filters, page: page > 1 ? page : undefined })}
        size="small"
      />
      <ReasonDialog
        open={resolving !== null}
        title="Mark this exception resolved?"
        description={
          resolving ? `${resolving.summary} Say what was done about it. Your note is kept with it.` : undefined
        }
        label="What was done"
        placeholder="For example: refunded by NEFT, UTR 1234…; or the bank statement shows the credit"
        confirmLabel="Mark resolved"
        maxLength={500}
        busy={resolve.isPending}
        error={resolve.error}
        onConfirm={(note) => resolving && resolve.mutate({ d: resolving, note })}
        onCancel={() => {
          setResolving(null);
          resolve.reset();
        }}
      />
    </>
  );
}
