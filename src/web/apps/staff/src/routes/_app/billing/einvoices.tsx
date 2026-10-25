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
  Input,
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
  Text,
  Textarea,
} from '@fluentui/react-components';
import { ArrowDownloadRegular, DocumentCheckmarkRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, formatInr, todayIso } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import {
  CANCEL_REASONS,
  EINVOICE_PERMISSION,
  einvoiceApi,
  einvoiceKeys,
  einvoiceQueries,
  irnProblems,
  STATE_LABELS,
  type EInvoiceCancelReason,
  type EInvoiceRow,
  type EInvoiceSource,
  type EInvoiceState,
} from '@/modules/billing/einvoiceApi';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { gstStateName } from '@/modules/inventory/gst';
import { errorMessage } from '@/modules/lab/common/errors';

const DAY = /^\d{4}-\d{2}-\d{2}$/;

export const Route = createFileRoute('/_app/billing/einvoices')({
  validateSearch: (s: Record<string, unknown>): { from?: string; to?: string } => ({
    from: typeof s.from === 'string' && DAY.test(s.from) ? s.from : undefined,
    to: typeof s.to === 'string' && DAY.test(s.to) ? s.to : undefined,
  }),
  component: () => (
    <RequirePermission permission={EINVOICE_PERMISSION} what="GST e-invoices">
      <EInvoicesPage />
    </RequirePermission>
  ),
});

const TONES: Record<EInvoiceState, 'warning' | 'success' | 'danger' | 'subtle'> = {
  Pending: 'warning',
  Registered: 'success',
  IrnCancelled: 'danger',
  NotNeeded: 'subtle',
};

const SOURCES: Record<EInvoiceSource, string> = { bill: 'Bill', pharmacy: 'Pharmacy' };

function EInvoicesPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const register = useQuery(einvoiceQueries.register(search.from, search.to));
  const [open, setOpen] = useState<EInvoiceRow | null>(null);
  const data = register.data;
  const pending = data?.rows.filter((r) => r.state === 'Pending').length ?? 0;
  const overdue = data?.rows.filter((r) => r.isOverdue).length ?? 0;

  return (
    <Page>
      <PageHeader
        title="GST e-invoices"
        subtitle="B2B tax invoices need an IRN from the Invoice Registration Portal (GST rule 48(4)): download each invoice's JSON, upload it to the portal, and record the IRN it gives back."
      />
      {data && !data.applicable && (
        <MessageBar intent="info">
          <MessageBarBody>
            E-invoicing is not switched on for this hospital. When its turnover crosses the notified limit, set the date
            it applies from (gst.einvoice.from) under Hospital setup.
          </MessageBarBody>
        </MessageBar>
      )}
      <Section flush>
        <div style={{ padding: '8px 16px 12px' }}>
          <FilterBar>
            <Field label="From">
              <Input
                type="date"
                value={data?.from ?? search.from ?? ''}
                max={todayIso()}
                onChange={(_, d) =>
                  void navigate({ search: { ...search, from: DAY.test(d.value) ? d.value : undefined } })
                }
              />
            </Field>
            <Field label="To">
              <Input
                type="date"
                value={data?.to ?? search.to ?? ''}
                max={todayIso()}
                onChange={(_, d) =>
                  void navigate({ search: { ...search, to: DAY.test(d.value) ? d.value : undefined } })
                }
              />
            </Field>
            {data?.applicable && (
              <Caption1>
                {pending} need an IRN{overdue > 0 ? `, ${overdue} past the 30 days` : ''} · e-invoicing from{' '}
                {data.applicableFrom ? formatDateOnly(data.applicableFrom) : '—'}
              </Caption1>
            )}
          </FilterBar>
        </div>
        <DataTable<EInvoiceRow>
          ariaLabel="B2B tax invoices and their IRNs"
          rows={data?.rows}
          loading={register.isLoading}
          error={register.error}
          onRetry={() => void register.refetch()}
          getRowId={(r) => `${r.source}-${r.id}`}
          onRowClick={setOpen}
          size="small"
          emptyTitle="No B2B tax invoices in these days"
          columns={[
            { key: 'date', header: 'Date', width: 110, render: (r) => formatDateOnly(r.date) },
            { key: 'number', header: 'Invoice', width: 150, render: (r) => `${r.number} (${SOURCES[r.source]})` },
            { key: 'buyer', header: 'Buyer', render: (r) => `${r.buyerName} · ${r.buyerGstin}` },
            { key: 'total', header: 'Value', align: 'end', width: 110, render: (r) => formatInr(r.total) },
            {
              key: 'state',
              header: 'IRN',
              width: 170,
              render: (r) => (
                <StatusBadge
                  status={r.state}
                  tone={r.isOverdue ? 'danger' : TONES[r.state]}
                  label={r.isOverdue ? `Overdue (by ${formatDateOnly(r.reportBy)})` : STATE_LABELS[r.state]}
                />
              ),
            },
          ]}
        />
      </Section>
      {open && <EInvoiceDialog row={open} onClose={() => setOpen(null)} />}
    </Page>
  );
}

function EInvoiceDialog({ row, onClose }: { row: EInvoiceRow; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const detail = useQuery(einvoiceQueries.detail(row.source, row.id));
  const [ackNo, setAckNo] = useState('');
  const [qr, setQr] = useState('');
  const [reason, setReason] = useState<EInvoiceCancelReason>('DataEntryMistake');
  const [remark, setRemark] = useState('');
  const refresh = () => void queryClient.invalidateQueries({ queryKey: einvoiceKeys.all });

  const download = useMutation({
    mutationFn: () => einvoiceApi.json(row.source, row.id),
    onSuccess: (blob) => {
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `einvoice-${row.number}.json`;
      document.body.appendChild(link);
      link.click();
      link.remove();
      setTimeout(() => URL.revokeObjectURL(url), 10_000);
    },
    onError: (error) => notify.error(error, 'Could not download the e-invoice'),
  });
  const record = useMutation({
    mutationFn: () => einvoiceApi.record(row.source, row.id, ackNo.trim(), qr.trim()),
    onSuccess: () => {
      notify.success('IRN recorded', 'The invoice now prints its IRN and signed QR code.');
      refresh();
    },
  });
  const cancel = useMutation({
    mutationFn: (version: number) => einvoiceApi.cancel(row.source, row.id, reason, remark.trim(), version),
    onSuccess: () => {
      notify.success('IRN cancellation recorded', 'Cancel the invoice here and issue a new one with a new number.');
      refresh();
    },
  });

  const d = detail.data;
  const registration = d?.registration;
  const live = registration?.status === 'Registered';
  const problems = irnProblems(ackNo, qr);

  return (
    <Dialog open onOpenChange={(_, data) => !data.open && onClose()}>
      <DialogSurface style={{ maxWidth: 720 }}>
        <DialogBody>
          <DialogTitle>
            E-invoice {row.number} · {formatDateOnly(row.date)}
          </DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            {detail.isLoading && <Spinner size="small" label="Loading the invoice…" />}
            {detail.error && (
              <MessageBar intent="error">
                <MessageBarBody>{errorMessage(detail.error, 'The invoice could not be loaded.')}</MessageBarBody>
              </MessageBar>
            )}
            {d && (
              <>
                <Text>
                  {d.buyerName} · GSTIN {d.buyerGstin} · place of supply{' '}
                  {gstStateName(d.placeOfSupply) ?? d.placeOfSupply} · {d.lines} line(s) · {formatInr(d.total)}
                </Text>
                {d.problems.length > 0 && (
                  <MessageBar intent="warning">
                    <MessageBarBody>
                      The portal would refuse this invoice:
                      <ul style={{ margin: '4px 0 0', paddingLeft: 20 }}>
                        {d.problems.map((p) => (
                          <li key={p}>{p}</li>
                        ))}
                      </ul>
                    </MessageBarBody>
                  </MessageBar>
                )}
                {registration && (
                  <div style={{ display: 'flex', gap: 16, alignItems: 'center' }}>
                    <img src={registration.qrCodeSvg} alt="Signed QR code of the e-invoice" width={120} height={120} />
                    <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
                      <Text weight="semibold">{live ? 'IRN recorded' : 'IRN cancelled'}</Text>
                      <Caption1 style={{ fontFamily: 'monospace', wordBreak: 'break-all' }}>
                        {registration.irn}
                      </Caption1>
                      <Caption1>
                        Ack no. {registration.ackNo} · {formatDateTime(registration.ackDate)} · recorded by{' '}
                        {registration.recordedByName}
                      </Caption1>
                      {registration.cancelledAt && (
                        <Caption1>
                          Cancelled {formatDateTime(registration.cancelledAt)}: {registration.cancelRemark}
                        </Caption1>
                      )}
                    </div>
                  </div>
                )}
                {!live && d.state !== 'NotNeeded' && (
                  <>
                    <Button
                      icon={<ArrowDownloadRegular />}
                      disabled={d.problems.length > 0 || download.isPending}
                      onClick={() => download.mutate()}
                      style={{ alignSelf: 'flex-start' }}
                    >
                      Download e-invoice JSON
                    </Button>
                    <Field label="Acknowledgement number" required>
                      <Input value={ackNo} maxLength={20} inputMode="numeric" onChange={(_, v) => setAckNo(v.value)} />
                    </Field>
                    <Field
                      label="Signed QR code"
                      required
                      hint="Paste the SignedQRCode the portal returned. It must name this invoice."
                    >
                      <Textarea value={qr} rows={4} maxLength={8000} onChange={(_, v) => setQr(v.value)} />
                    </Field>
                    {record.error && (
                      <MessageBar intent="error">
                        <MessageBarBody>{errorMessage(record.error, 'The IRN could not be recorded.')}</MessageBarBody>
                      </MessageBar>
                    )}
                  </>
                )}
                {live && registration.canCancel && (
                  <>
                    <Caption1>
                      The portal cancels an IRN only until {formatDateTime(registration.cancelBy)}. Cancel it there
                      first, then record it here.
                    </Caption1>
                    <div style={{ display: 'grid', gridTemplateColumns: '1fr 2fr', gap: 8 }}>
                      <Field label="Reason">
                        <Select value={reason} onChange={(_, v) => setReason(v.value as EInvoiceCancelReason)}>
                          {CANCEL_REASONS.map((r) => (
                            <option key={r.value} value={r.value}>
                              {r.label}
                            </option>
                          ))}
                        </Select>
                      </Field>
                      <Field label="Remark" required>
                        <Input value={remark} maxLength={100} onChange={(_, v) => setRemark(v.value)} />
                      </Field>
                    </div>
                    {cancel.error && (
                      <MessageBar intent="error">
                        <MessageBarBody>
                          {errorMessage(cancel.error, 'The cancellation could not be recorded.')}
                        </MessageBarBody>
                      </MessageBar>
                    )}
                  </>
                )}
                {live && !registration.canCancel && (
                  <Caption1>
                    More than 24 hours have passed: the IRN stays, and a credit note undoes the invoice.
                  </Caption1>
                )}
              </>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>Close</Button>
            {d && !live && d.state !== 'NotNeeded' && (
              <Button
                appearance="primary"
                icon={<DocumentCheckmarkRegular />}
                disabled={problems.length > 0 || record.isPending}
                title={problems.join(' ')}
                onClick={() => record.mutate()}
              >
                Record IRN
              </Button>
            )}
            {live && registration.canCancel && (
              <Button
                appearance="primary"
                disabled={!remark.trim() || cancel.isPending}
                onClick={() => cancel.mutate(registration.version)}
              >
                Record IRN cancellation
              </Button>
            )}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
