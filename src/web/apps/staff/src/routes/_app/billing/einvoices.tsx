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
  MessageBarTitle,
  Select,
  Spinner,
  Text,
  Textarea,
} from '@fluentui/react-components';
import {
  ArrowDownloadRegular,
  ChevronDownRegular,
  ChevronRightRegular,
  DocumentCheckmarkRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, formatInr, todayIso } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useId, useState, type ReactNode } from 'react';
import {
  CANCEL_REASONS,
  documentLabel,
  EINVOICE_PERMISSION,
  einvoiceApi,
  einvoiceKeys,
  einvoiceQueries,
  ewayBillProblems,
  irnAnswer,
  irnProblems,
  localToIso,
  STATE_LABELS,
  type EInvoiceCancelReason,
  type EInvoiceRow,
  type EInvoiceState,
  type IrnAction,
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
            { key: 'number', header: 'Document', width: 190, render: (r) => documentLabel(r) },
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

/** A refused record, generate or cancel: what it means in plain words, then the server's own sentence. */
function IrnAnswerBar({ action, error }: { action: IrnAction; error: unknown }) {
  const answer = irnAnswer(action, error);
  return (
    <MessageBar intent={answer.intent} layout="multiline">
      <MessageBarBody>
        <MessageBarTitle>{answer.title}</MessageBarTitle>
        {answer.detail}
      </MessageBarBody>
    </MessageBar>
  );
}

/** A way round the dialog's main action, folded away under its button until asked for. */
function Disclosure({ label, children }: { label: string; children: ReactNode }) {
  const [open, setOpen] = useState(false);
  const id = useId();
  return (
    <>
      <Button
        appearance="subtle"
        icon={open ? <ChevronDownRegular /> : <ChevronRightRegular />}
        aria-expanded={open}
        aria-controls={id}
        onClick={() => setOpen((shown) => !shown)}
        style={{ alignSelf: 'flex-start' }}
      >
        {label}
      </Button>
      <div
        id={id}
        role="group"
        aria-label={label}
        hidden={!open}
        // Indented under the button's words, past its chevron.
        style={{ display: open ? 'flex' : 'none', flexDirection: 'column', gap: 12, paddingLeft: 38 }}
      >
        {children}
      </div>
    </>
  );
}

/** Why the IRN is cancelled, in the portal's own reasons, and the remark the portal takes with it. */
function CancelFields({
  reason,
  onReason,
  remark,
  onRemark,
}: {
  reason: EInvoiceCancelReason;
  onReason: (value: EInvoiceCancelReason) => void;
  remark: string;
  onRemark: (value: string) => void;
}) {
  return (
    <div style={{ display: 'grid', gridTemplateColumns: '1fr 2fr', gap: 8 }}>
      <Field label="Reason">
        <Select value={reason} onChange={(_, v) => onReason(v.value as EInvoiceCancelReason)}>
          {CANCEL_REASONS.map((r) => (
            <option key={r.value} value={r.value}>
              {r.label}
            </option>
          ))}
        </Select>
      </Field>
      <Field label="Remark" required>
        <Input value={remark} maxLength={100} onChange={(_, v) => onRemark(v.value)} />
      </Field>
    </div>
  );
}

/** The JSON to upload on the portal's own screens, and what the portal gives back for it. */
function HandRecordFields({
  canDownload,
  onDownload,
  ackNo,
  onAckNo,
  qr,
  onQr,
}: {
  canDownload: boolean;
  onDownload: () => void;
  ackNo: string;
  onAckNo: (value: string) => void;
  qr: string;
  onQr: (value: string) => void;
}) {
  return (
    <>
      <Button
        icon={<ArrowDownloadRegular />}
        disabled={!canDownload}
        onClick={onDownload}
        style={{ alignSelf: 'flex-start' }}
      >
        Download e-invoice JSON
      </Button>
      <Field label="Acknowledgement number" required>
        <Input value={ackNo} maxLength={20} inputMode="numeric" onChange={(_, v) => onAckNo(v.value)} />
      </Field>
      <Field
        label="Signed QR code"
        required
        hint="Paste the SignedQRCode the portal returned. It must name this invoice."
      >
        <Textarea value={qr} rows={4} maxLength={8000} onChange={(_, v) => onQr(v.value)} />
      </Field>
    </>
  );
}

/** An e-way bill generated outside HC360, as its print shows it: the number, when it was generated, and its validity. */
function EwayBillPrintFields({
  ewbNo,
  onEwbNo,
  generatedAt,
  onGeneratedAt,
  validUntil,
  onValidUntil,
}: {
  ewbNo: string;
  onEwbNo: (value: string) => void;
  generatedAt: string;
  onGeneratedAt: (value: string) => void;
  validUntil: string;
  onValidUntil: (value: string) => void;
}) {
  return (
    <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', gap: 8 }}>
      <Field label="E-way bill number" required hint="12 digits as printed (14 from 1 January 2027)">
        <Input value={ewbNo} maxLength={17} inputMode="numeric" onChange={(_, v) => onEwbNo(v.value)} />
      </Field>
      <Field label="Generated on" required>
        <Input type="datetime-local" value={generatedAt} onChange={(_, v) => onGeneratedAt(v.value)} />
      </Field>
      <Field label="Valid until" hint="Blank while only Part-A is filled">
        <Input type="datetime-local" value={validUntil} onChange={(_, v) => onValidUntil(v.value)} />
      </Field>
    </div>
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
  // Read the document again after every answer, a refusal included: what the server holds is what the dialog shows
  // (an IRN whose answer was lost on the way here and is now recorded, a cancellation the portal has settled).
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
    onError: refresh,
  });
  const generate = useMutation({
    mutationFn: () => einvoiceApi.generate(row.source, row.id),
    onSuccess: () => {
      notify.success('IRN received', 'The portal registered the document; it now prints its IRN and signed QR code.');
      refresh();
    },
    onError: refresh,
  });
  const [distance, setDistance] = useState('');
  const [vehicle, setVehicle] = useState('');
  const [transporter, setTransporter] = useState('');
  const ewayBill = useMutation({
    mutationFn: () =>
      einvoiceApi.ewayBill(row.source, row.id, {
        distanceKm: Number(distance) || 0,
        mode: 'Road',
        vehicleNumber: vehicle.trim() || null,
        transporterGstin: transporter.trim() || null,
        transporterName: null,
        transportDocumentNumber: null,
        transportDocumentDate: null,
      }),
    onSuccess: () => {
      notify.success('E-way bill generated', 'The goods can move with it until it expires.');
      refresh();
    },
  });
  const [ewbNo, setEwbNo] = useState('');
  const [ewbGeneratedAt, setEwbGeneratedAt] = useState('');
  const [ewbValidUntil, setEwbValidUntil] = useState('');
  const recordEwayBill = useMutation({
    mutationFn: () =>
      einvoiceApi.recordEwayBill(row.source, row.id, {
        ewbNo: ewbNo.trim(),
        generatedAt: localToIso(ewbGeneratedAt)!,
        validUntil: ewbValidUntil ? localToIso(ewbValidUntil) : null,
      }),
    onSuccess: () => {
      notify.success('E-way bill recorded', "It is kept with the invoice's IRN.");
      refresh();
    },
    onError: refresh,
  });
  const cancel = useMutation({
    mutationFn: (version: number) => einvoiceApi.cancel(row.source, row.id, reason, remark.trim(), version),
    onSuccess: () => {
      notify.success('IRN cancellation recorded', 'Cancel the invoice here and issue a new one with a new number.');
      refresh();
    },
    onError: refresh,
  });
  // Past the 24 hours the same request only asks the portal whether a cancellation made in time went through.
  const askCancelled = useMutation({
    mutationFn: (version: number) => einvoiceApi.cancel(row.source, row.id, reason, remark.trim(), version),
    onSuccess: () => {
      notify.success(
        'IRN cancellation recorded',
        'The portal had cancelled it within the 24 hours. Cancel the invoice here and issue a new one with a new number.',
      );
      refresh();
    },
    onError: refresh,
  });

  const d = detail.data;
  const registration = d?.registration;
  const live = registration?.status === 'Registered';
  const problems = irnProblems(ackNo, qr);
  const ewbProblems = d ? ewayBillProblems(ewbNo, ewbGeneratedAt, ewbValidUntil, d.date) : [];
  const ewbFields = (
    <EwayBillPrintFields
      ewbNo={ewbNo}
      onEwbNo={setEwbNo}
      generatedAt={ewbGeneratedAt}
      onGeneratedAt={setEwbGeneratedAt}
      validUntil={ewbValidUntil}
      onValidUntil={setEwbValidUntil}
    />
  );

  return (
    <Dialog open onOpenChange={(_, data) => !data.open && onClose()}>
      <DialogSurface style={{ maxWidth: 720 }}>
        <DialogBody>
          <DialogTitle>
            {row.type === 'CRN' ? 'Credit note' : 'E-invoice'} {row.number} · {formatDateOnly(row.date)}
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
                {d.type === 'CRN' && d.against && (
                  <Text>
                    Credit note against invoice {d.against}
                    {d.againstDate ? ` of ${formatDateOnly(d.againstDate)}` : ''}: it goes to the portal after that
                    invoice&apos;s IRN.
                  </Text>
                )}
                {d.problems.length > 0 && (
                  <MessageBar intent="warning">
                    <MessageBarBody>
                      The portal would refuse this {d.type === 'CRN' ? 'credit note' : 'invoice'}:
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
                      {registration.ewbNo && (
                        <Caption1>
                          E-way bill {registration.ewbNo}
                          {registration.ewbValidTill
                            ? ` · valid until ${formatDateTime(registration.ewbValidTill)}`
                            : ''}
                        </Caption1>
                      )}
                      {registration.cancelledAt && (
                        <Caption1>
                          Cancelled {formatDateTime(registration.cancelledAt)}: {registration.cancelRemark}
                        </Caption1>
                      )}
                    </div>
                  </div>
                )}
                {!live && d.state !== 'NotNeeded' && d.portalConnected && (
                  <>
                    <Text>This facility is connected to the e-invoice portal: HC360 asks it for the IRN.</Text>
                    {generate.error && <IrnAnswerBar action="generate" error={generate.error} />}
                    {/* Recording by hand is the way round the portal's API: folded away under the portal's own button. */}
                    <Disclosure label="Record an IRN generated on the portal">
                      <Caption1>
                        When the IRN was generated on the portal&apos;s own screens: upload this JSON there, then enter
                        what the portal gives back. HC360 checks a recent IRN with the portal before recording it.
                      </Caption1>
                      <HandRecordFields
                        canDownload={d.problems.length === 0 && !download.isPending}
                        onDownload={() => download.mutate()}
                        ackNo={ackNo}
                        onAckNo={setAckNo}
                        qr={qr}
                        onQr={setQr}
                      />
                      {record.error && <IrnAnswerBar action="record" error={record.error} />}
                      <Button
                        icon={<DocumentCheckmarkRegular />}
                        disabled={problems.length > 0 || record.isPending}
                        title={problems.join(' ')}
                        onClick={() => record.mutate()}
                        style={{ alignSelf: 'flex-start' }}
                      >
                        Record IRN
                      </Button>
                    </Disclosure>
                  </>
                )}
                {live && d.ewayBillNeeded && !registration.ewbNo && d.portalConnected && (
                  <>
                    <Text weight="semibold">E-way bill</Text>
                    <div
                      style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))', gap: 8 }}
                    >
                      <Field label="Distance (km)" hint="0: the portal works it out from the PIN codes">
                        <Input
                          value={distance}
                          inputMode="numeric"
                          maxLength={4}
                          onChange={(_, v) => setDistance(v.value)}
                        />
                      </Field>
                      <Field label="Vehicle number">
                        <Input value={vehicle} maxLength={20} onChange={(_, v) => setVehicle(v.value)} />
                      </Field>
                      <Field label="Transporter's GSTIN or id" hint="When the transporter fills the vehicle in">
                        <Input value={transporter} maxLength={15} onChange={(_, v) => setTransporter(v.value)} />
                      </Field>
                    </div>
                    {ewayBill.error && (
                      <MessageBar intent="error">
                        <MessageBarBody>
                          {errorMessage(ewayBill.error, 'The e-way bill was not generated.')}
                        </MessageBarBody>
                      </MessageBar>
                    )}
                    {/* One generated on the e-way bill portal or by the transporter: folded away under the portal's way. */}
                    <Disclosure label="Record an e-way bill generated elsewhere">
                      <Caption1>
                        When the e-way bill was generated on the e-way bill portal or by the transporter: enter it from
                        its print. HC360 asks the e-invoice portal first, which confirms only one generated from the
                        IRN.
                      </Caption1>
                      {ewbFields}
                      {recordEwayBill.error && <IrnAnswerBar action="recordEwayBill" error={recordEwayBill.error} />}
                      <Button
                        disabled={ewbProblems.length > 0 || recordEwayBill.isPending}
                        title={ewbProblems.join(' ')}
                        onClick={() => recordEwayBill.mutate()}
                        style={{ alignSelf: 'flex-start' }}
                      >
                        Record e-way bill
                      </Button>
                    </Disclosure>
                  </>
                )}
                {live && d.ewayBillNeeded && !registration.ewbNo && !d.portalConnected && (
                  <>
                    <Text weight="semibold">E-way bill</Text>
                    <Caption1>
                      This facility is not connected to the e-invoice portal: generate the e-way bill on the e-way bill
                      portal, then enter it here from its print.
                    </Caption1>
                    {ewbFields}
                    {recordEwayBill.error && <IrnAnswerBar action="recordEwayBill" error={recordEwayBill.error} />}
                  </>
                )}
                {!live && d.state !== 'NotNeeded' && !d.portalConnected && (
                  <>
                    <HandRecordFields
                      canDownload={d.problems.length === 0 && !download.isPending}
                      onDownload={() => download.mutate()}
                      ackNo={ackNo}
                      onAckNo={setAckNo}
                      qr={qr}
                      onQr={setQr}
                    />
                    {record.error && <IrnAnswerBar action="record" error={record.error} />}
                  </>
                )}
                {live && registration.canCancel && (
                  <>
                    <Caption1>
                      The portal cancels an IRN only until {formatDateTime(registration.cancelBy)}.{' '}
                      {d.portalConnected
                        ? 'HC360 cancels it on the portal and records it.'
                        : 'Cancel it there first, then record it here.'}
                    </Caption1>
                    <CancelFields reason={reason} onReason={setReason} remark={remark} onRemark={setRemark} />
                    {cancel.error && <IrnAnswerBar action="cancel" error={cancel.error} />}
                  </>
                )}
                {live && !registration.canCancel && (
                  <>
                    <Caption1>
                      More than 24 hours have passed: the IRN stays, and a credit note undoes the invoice.
                    </Caption1>
                    {/* A cancellation made in time whose answer was lost: the portal still says whether it went through. */}
                    {d.portalConnected && (
                      <Disclosure label="Ask the portal whether it was cancelled">
                        <Caption1>
                          A cancellation made on the portal within the 24 hours, whose answer was lost, is recorded here
                          once the portal confirms it. Give the reason and remark it was made with.
                        </Caption1>
                        <CancelFields reason={reason} onReason={setReason} remark={remark} onRemark={setRemark} />
                        {askCancelled.error && <IrnAnswerBar action="askCancelled" error={askCancelled.error} />}
                        <Button
                          disabled={!remark.trim() || askCancelled.isPending}
                          onClick={() => askCancelled.mutate(registration.version)}
                          style={{ alignSelf: 'flex-start' }}
                        >
                          Ask the portal
                        </Button>
                      </Disclosure>
                    )}
                  </>
                )}
              </>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>Close</Button>
            {d && !live && d.state !== 'NotNeeded' && d.portalConnected && (
              <Button
                appearance="primary"
                icon={<DocumentCheckmarkRegular />}
                disabled={d.problems.length > 0 || generate.isPending}
                onClick={() => generate.mutate()}
              >
                Get IRN from the portal
              </Button>
            )}
            {d && live && d.ewayBillNeeded && !registration.ewbNo && d.portalConnected && (
              <Button
                disabled={(!vehicle.trim() && !transporter.trim()) || ewayBill.isPending}
                onClick={() => ewayBill.mutate()}
              >
                Generate e-way bill
              </Button>
            )}
            {d && live && d.ewayBillNeeded && !registration.ewbNo && !d.portalConnected && (
              <Button
                appearance="primary"
                disabled={ewbProblems.length > 0 || recordEwayBill.isPending}
                title={ewbProblems.join(' ')}
                onClick={() => recordEwayBill.mutate()}
              >
                Record e-way bill
              </Button>
            )}
            {d && !live && d.state !== 'NotNeeded' && !d.portalConnected && (
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
                {d?.portalConnected ? 'Cancel IRN on the portal' : 'Record IRN cancellation'}
              </Button>
            )}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
