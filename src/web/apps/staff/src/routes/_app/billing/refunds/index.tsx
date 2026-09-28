import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Radio,
  RadioGroup,
  Tab,
  TabList,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowUndoRegular,
  CheckmarkRegular,
  DismissRegular,
  MoneyHandRegular,
  MoreHorizontalRegular,
  PrintRegular,
} from '@fluentui/react-icons';
import { formatDateTime, formatInr } from '@hc360/core';
import { DataTable, EmptyState, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState, type ReactElement } from 'react';
import { usePermission } from '@/lib/permissions';
import { billingQueries, type RefundDto } from '@/modules/billing/api';
import { useBillingLive, useBillingPrint } from '@/modules/billing/hooks';
import { REFUND_PURPOSE_LABEL, REFUND_STATUSES, type RefundStatus } from '@/modules/billing/labels';
import { fromPaise, toPaise } from '@/modules/billing/money';
import { DecideRefundDialog, PayRefundDialog, RequestRefundDialog } from '@/modules/billing/RefundDialogs';
import { enumParam, idParam, pageParam } from '@/modules/billing/searchParams';
import { Amount, NoAccess, RefundStatusBadge } from '@/modules/billing/ui';
import { patientQueries } from '@/modules/patients/api';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';

interface RefundsSearch {
  status?: RefundStatus | 'all';
  patientId?: string;
  page?: number;
}

export const Route = createFileRoute('/_app/billing/refunds/')({
  validateSearch: (s: Record<string, unknown>): RefundsSearch => ({
    status: s.status === 'all' ? 'all' : enumParam(s.status, REFUND_STATUSES),
    patientId: idParam(s.patientId),
    page: pageParam(s.page),
  }),
  component: RefundsPage,
});

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  advances: { display: 'flex', flexDirection: 'column', gap: '10px' },
});

const TAB_LABEL: Record<RefundStatus | 'all', string> = {
  Requested: 'Awaiting approval',
  Approved: 'To pay out',
  Paid: 'Paid',
  Rejected: 'Rejected',
  all: 'All',
};

/** Refunds (MB-05): request → approve or reject (by another person) → pay out, with the refund voucher. */
function RefundsPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canRead = usePermission(['billing.refunds.request', 'billing.refunds.approve', 'billing.charges.read']);
  const canApprove = usePermission('billing.refunds.approve');
  const canPay = usePermission('billing.payments.collect');
  const canRequest = usePermission('billing.refunds.request');
  const { printRefund } = useBillingPrint();
  const [picked, setPicked] = useState<PickedPatient | null>(null);
  const [action, setAction] = useState<{ refund: RefundDto; kind: 'approve' | 'reject' | 'pay' } | null>(null);
  const [requesting, setRequesting] = useState(false);
  useBillingLive();

  const patientDetail = useQuery({
    ...patientQueries.detail(search.patientId ?? ''),
    enabled: Boolean(search.patientId) && picked === null,
  });
  const pickerValue =
    picked ??
    (patientDetail.data && search.patientId
      ? { id: patientDetail.data.id, uhid: patientDetail.data.uhid, fullName: patientDetail.data.fullName }
      : null);
  const status = search.status ?? (canApprove ? 'Requested' : canPay ? 'Approved' : 'all');
  const refunds = useQuery({
    ...billingQueries.refunds({
      status: status === 'all' ? undefined : status,
      patientId: search.patientId,
      page: search.page ?? 1,
    }),
    enabled: canRead,
  });

  if (!canRead) {
    return (
      <Page>
        <PageHeader title="Refunds" />
        <NoAccess what="refunds" />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader
        title="Refunds"
        subtitle="Every refund is requested by one person, approved by another, then paid out at the counter"
        actions={
          canRequest && (
            <Button appearance="primary" icon={<ArrowUndoRegular />} onClick={() => setRequesting(true)}>
              Refund a patient advance
            </Button>
          )
        }
      />
      <Section flush>
        <div style={{ padding: '0 8px' }}>
          <TabList
            selectedValue={status}
            onTabSelect={(_, d) =>
              void navigate({
                search: (prev) => ({ ...prev, status: d.value as RefundsSearch['status'], page: undefined }),
              })
            }
          >
            {(['Requested', 'Approved', 'Paid', 'Rejected', 'all'] as const).map((s) => (
              <Tab key={s} value={s}>
                {TAB_LABEL[s]}
              </Tab>
            ))}
          </TabList>
        </div>
        <div style={{ padding: '8px 16px 12px' }}>
          <FilterBar>
            <div style={{ flex: '1 1 320px', maxWidth: 480 }}>
              <PatientPicker
                value={pickerValue}
                onChange={(p) => {
                  setPicked(p);
                  void navigate({ search: (prev) => ({ ...prev, patientId: p?.id, page: undefined }) });
                }}
                label="Patient"
                hint="Leave empty to see everyone"
              />
            </div>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Refunds"
          rows={refunds.data?.items}
          loading={refunds.isLoading}
          error={refunds.error}
          onRetry={() => void refunds.refetch()}
          getRowId={(r) => r.id}
          onRowClick={(r) => void navigate({ to: '/billing/refunds/$refundId', params: { refundId: r.id } })}
          columns={[
            { key: 'no', header: 'Refund', width: 150, render: (r) => <Text font="monospace">{r.refundNumber}</Text> },
            {
              key: 'patient',
              header: 'Patient',
              render: (r) => (
                <span>
                  <Text weight="semibold">{r.patientName ?? '—'}</Text>
                  <Caption1 block className={styles.muted}>
                    {r.uhid}
                  </Caption1>
                </span>
              ),
            },
            { key: 'purpose', header: 'For', priority: 'low', render: (r) => REFUND_PURPOSE_LABEL[r.purpose] },
            { key: 'reason', header: 'Reason', priority: 'low', render: (r) => r.reason },
            {
              key: 'requested',
              header: 'Requested',
              priority: 'low',
              render: (r) => (
                <span>
                  {formatDateTime(r.requestedAt)}
                  <Caption1 block className={styles.muted}>
                    {r.requestedByName ?? 'System'}
                  </Caption1>
                </span>
              ),
            },
            { key: 'amount', header: 'Amount', align: 'end', render: (r) => <Amount value={r.amount} strong /> },
            { key: 'status', header: 'Status', width: 160, render: (r) => <RefundStatusBadge status={r.status} /> },
          ]}
          rowActions={(r) => (
            <RefundRowActions
              refund={r}
              canApprove={canApprove}
              canPay={canPay}
              onAction={(kind) => setAction({ refund: r, kind })}
              onPrint={() => void printRefund(r)}
            />
          )}
          emptyTitle={
            status === 'Requested'
              ? 'No refund is waiting for approval'
              : status === 'Approved'
                ? 'No approved refund to pay out'
                : 'No refunds here'
          }
          emptyDescription="Refunds of bills are requested from the bill; unused IP advances from the IP account."
          page={search.page ?? 1}
          pageSize={25}
          totalCount={refunds.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>

      {action?.kind === 'pay' && <PayRefundDialog refund={action.refund} onClose={() => setAction(null)} />}
      {action && action.kind !== 'pay' && (
        <DecideRefundDialog refund={action.refund} decision={action.kind} onClose={() => setAction(null)} />
      )}
      {requesting && <AdvanceRefundDialog onClose={() => setRequesting(false)} />}
    </Page>
  );
}

function RefundRowActions({
  refund,
  canApprove,
  canPay,
  onAction,
  onPrint,
}: {
  refund: RefundDto;
  canApprove: boolean;
  canPay: boolean;
  onAction: (kind: 'approve' | 'reject' | 'pay') => void;
  onPrint: () => void;
}) {
  const items: ReactElement[] = [];
  if (refund.status === 'Requested' && canApprove) {
    items.push(
      <MenuItem key="approve" icon={<CheckmarkRegular />} onClick={() => onAction('approve')}>
        Approve
      </MenuItem>,
      <MenuItem key="reject" icon={<DismissRegular />} onClick={() => onAction('reject')}>
        Reject
      </MenuItem>,
    );
  }
  if (refund.status === 'Approved' && canPay) {
    items.push(
      <MenuItem key="pay" icon={<MoneyHandRegular />} onClick={() => onAction('pay')}>
        Pay out {formatInr(refund.amount)}
      </MenuItem>,
    );
  }
  if (refund.status === 'Paid') {
    items.push(
      <MenuItem key="print" icon={<PrintRegular />} onClick={onPrint}>
        Print refund voucher
      </MenuItem>,
    );
  }
  if (items.length === 0) {
    return null;
  }
  return (
    <Menu>
      <MenuTrigger disableButtonEnhancement>
        <Button
          size="small"
          appearance="subtle"
          icon={<MoreHorizontalRegular />}
          aria-label={`Actions for refund ${refund.refundNumber}`}
        />
      </MenuTrigger>
      <MenuPopover>
        <MenuList>{items}</MenuList>
      </MenuPopover>
    </Menu>
  );
}

/** Refund of a patient advance (e.g. the online payment of an appointment that did not happen). */
function AdvanceRefundDialog({ onClose }: { onClose: () => void }) {
  const styles = useStyles();
  const [patient, setPatient] = useState<PickedPatient | null>(null);
  const [advanceId, setAdvanceId] = useState('');
  const [confirming, setConfirming] = useState(false);
  const advances = useQuery({ ...billingQueries.advances(patient?.id ?? '', true), enabled: patient !== null });
  const open = (advances.data ?? [])
    .map((a) => ({ advance: a, available: fromPaise(toPaise(a.balance) - toPaise(a.refundPending)) }))
    .filter((a) => a.available > 0);
  const chosen = open.find((a) => a.advance.id === advanceId);

  if (confirming && chosen && patient) {
    return (
      <RequestRefundDialog
        open
        target={{
          kind: 'advance',
          advanceId: chosen.advance.id,
          max: chosen.available,
          label: `Advance ${chosen.advance.receiptNumber} of ${patient.fullName}`,
        }}
        onClose={onClose}
      />
    );
  }

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <DialogBody>
          <DialogTitle>Refund a patient advance</DialogTitle>
          <DialogContent className={styles.advances}>
            <Text>For bills use Request refund on the bill; for in-patient advances use the IP account.</Text>
            <PatientPicker
              value={patient}
              onChange={(p) => {
                setPatient(p);
                setAdvanceId('');
              }}
              required
            />
            {patient &&
              (advances.isLoading ? (
                <Caption1>Looking for advances…</Caption1>
              ) : open.length === 0 ? (
                <EmptyState title="No advance to refund" description="This patient has no unused advance." />
              ) : (
                <RadioGroup value={advanceId} onChange={(_, d) => setAdvanceId(d.value)} aria-label="Advance">
                  {open.map(({ advance, available }) => (
                    <Radio
                      key={advance.id}
                      value={advance.id}
                      label={`${advance.receiptNumber} · ${formatDateTime(advance.receivedAt)} · ${formatInr(available)} available${advance.appointmentNumber ? ` · appointment ${advance.appointmentNumber}` : ''}`}
                    />
                  ))}
                </RadioGroup>
              ))}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>Cancel</Button>
            <Button appearance="primary" disabled={!chosen} onClick={() => setConfirming(true)}>
              Continue
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
