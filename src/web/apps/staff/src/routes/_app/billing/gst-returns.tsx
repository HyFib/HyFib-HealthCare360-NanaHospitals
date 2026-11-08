import { Button, Caption1, Field, Input, MessageBar, MessageBarBody, Text } from '@fluentui/react-components';
import { ArrowDownloadRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatInr, todayIso } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import {
  GST_RETURNS_PERMISSION,
  gstReturnApi,
  gstReturnQueries,
  lastMonth,
  taxOf,
  type GstAmounts,
  type Gstr1B2csRow,
  type Gstr1Document,
  type Gstr1DocumentsIssued,
  type Gstr1HsnRow,
} from '@/modules/billing/gstReturnsApi';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { gstStateName } from '@/modules/inventory/gst';
import { errorMessage } from '@/modules/lab/common/errors';

const MONTH = /^\d{4}-\d{2}$/;

export const Route = createFileRoute('/_app/billing/gst-returns')({
  validateSearch: (s: Record<string, unknown>): { month?: string } => ({
    month: typeof s.month === 'string' && MONTH.test(s.month) ? s.month : undefined,
  }),
  component: () => (
    <RequirePermission permission={GST_RETURNS_PERMISSION} what="GST returns">
      <GstReturnsPage />
    </RequirePermission>
  ),
});

function money(a: GstAmounts) {
  return `${formatInr(a.taxable)} + tax ${formatInr(taxOf(a))}`;
}

function documentTable(label: string, rows: Gstr1Document[] | undefined, withBuyer: boolean) {
  return (
    <Section title={label} flush>
      <DataTable<Gstr1Document>
        ariaLabel={label}
        rows={rows}
        size="small"
        getRowId={(r) => r.number}
        emptyTitle="None this month"
        columns={[
          { key: 'number', header: 'Number', width: 150, render: (r) => r.number },
          { key: 'date', header: 'Date', width: 110, render: (r) => formatDateOnly(r.date) },
          ...(withBuyer
            ? [{ key: 'buyer', header: 'Buyer', render: (r: Gstr1Document) => `${r.buyerName} · ${r.buyerGstin ?? ''}` }]
            : []),
          { key: 'pos', header: 'Place of supply', width: 140, render: (r) => gstStateName(r.placeOfSupply) ?? r.placeOfSupply },
          {
            key: 'rates',
            header: 'By rate',
            render: (r) => r.items.map((i) => `${i.rate}%: ${money(i.amounts)}`).join('; '),
          },
          { key: 'value', header: 'Value', align: 'end', width: 120, render: (r) => formatInr(r.value) },
        ]}
      />
    </Section>
  );
}

function GstReturnsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const month = search.month ?? lastMonth(todayIso());
  const gstr1 = useQuery(gstReturnQueries.gstr1(month));
  const gstr3b = useQuery(gstReturnQueries.gstr3b(month));
  const download = useMutation({
    mutationFn: () => gstReturnApi.gstr1Json(month),
    onSuccess: (blob) => {
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `GSTR1_${gstr1.data?.gstin ?? ''}_${gstr1.data?.period ?? month}.json`;
      document.body.appendChild(link);
      link.click();
      link.remove();
      setTimeout(() => URL.revokeObjectURL(url), 10_000);
    },
    onError: (error) => notify.error(error, 'Could not download the GSTR-1 file'),
  });
  const r = gstr1.data;
  const b = gstr3b.data;

  return (
    <Page>
      <PageHeader
        title="GST returns"
        subtitle="GSTR-1 and GSTR-3B of this facility's GSTIN, with every facility registered under it. Check the figures, then import the GSTR-1 file into the GSTN offline tool or upload it on the portal."
      />
      <Section flush>
        <div style={{ padding: '8px 16px 12px' }}>
          <FilterBar>
            <Field label="Month">
              <Input
                type="month"
                value={month}
                max={todayIso().slice(0, 7)}
                onChange={(_, d) => void navigate({ search: { month: MONTH.test(d.value) ? d.value : undefined } })}
              />
            </Field>
            {r && <Caption1>GSTIN {r.gstin} · return period {r.period}</Caption1>}
            <Button
              icon={<ArrowDownloadRegular />}
              disabled={!r || download.isPending}
              onClick={() => download.mutate()}
            >
              Download GSTR-1 JSON
            </Button>
          </FilterBar>
        </div>
      </Section>
      {gstr1.error && (
        <MessageBar intent="error">
          <MessageBarBody>{errorMessage(gstr1.error, 'The returns could not be built.')}</MessageBarBody>
        </MessageBar>
      )}
      {r && r.problems.length > 0 && (
        <MessageBar intent="warning">
          <MessageBarBody>
            Put these right before filing:
            <ul style={{ margin: '4px 0 0', paddingLeft: 20 }}>
              {r.problems.map((p) => (
                <li key={p}>{p}</li>
              ))}
            </ul>
          </MessageBarBody>
        </MessageBar>
      )}
      {b && (
        <Section title="GSTR-3B">
          <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
            <Text>3.1(a) Outward taxable supplies: {money(b.outwardTaxable)}</Text>
            <Text>
              3.1(c) Nil-rated and exempt: {formatInr(b.nilAndExempt)} · 3.1(e) Non-GST: {formatInr(b.nonGst)}
            </Text>
            <Text>
              3.2 Inter-state supplies to unregistered persons:{' '}
              {b.interStateToUnregistered.length === 0
                ? 'none'
                : b.interStateToUnregistered
                    .map((p) => `${gstStateName(p.placeOfSupply) ?? p.placeOfSupply} ${money(p.amounts)}`)
                    .join('; ')}
            </Text>
            <Text>
              4(A)(5) Input tax credit from {b.purchaseInvoices - b.purchasesWithoutGstin} purchase invoice(s): IGST{' '}
              {formatInr(b.itcAllOther.igst)}, CGST {formatInr(b.itcAllOther.cgst)}, SGST {formatInr(b.itcAllOther.sgst)}
            </Text>
            {b.notes.map((n) => (
              <Caption1 key={n}>{n}</Caption1>
            ))}
          </div>
        </Section>
      )}
      {documentTable('B2B invoices (table 4A)', r?.b2b, true)}
      {documentTable('B2C large invoices (table 5)', r?.b2cl, false)}
      <Section title="B2C others (table 7)" flush>
        <DataTable<Gstr1B2csRow>
          ariaLabel="B2C others"
          rows={r?.b2cs}
          size="small"
          loading={gstr1.isLoading}
          getRowId={(x) => `${x.placeOfSupply}-${x.rate}-${x.interState}`}
          emptyTitle="None this month"
          columns={[
            { key: 'pos', header: 'Place of supply', render: (x) => gstStateName(x.placeOfSupply) ?? x.placeOfSupply },
            { key: 'type', header: 'Supply', width: 120, render: (x) => (x.interState ? 'Inter-state' : 'Intra-state') },
            { key: 'rate', header: 'Rate', width: 80, render: (x) => `${x.rate}%` },
            { key: 'amounts', header: 'Taxable and tax', render: (x) => money(x.amounts) },
          ]}
        />
      </Section>
      {documentTable('Credit and debit notes to registered buyers (table 9B)', r?.cdnr, true)}
      {documentTable('Notes to unregistered buyers on B2C large invoices (table 9B)', r?.cdnur, false)}
      <Section title="Nil-rated, exempt and non-GST supplies (table 8)">
        {(r?.nil ?? []).length === 0 ? (
          <Text>None this month</Text>
        ) : (
          r?.nil.map((n) => (
            <Text key={n.supplyType} block>
              {n.supplyType}: nil-rated {formatInr(n.nilRated)}, exempt {formatInr(n.exempt)}, non-GST {formatInr(n.nonGst)}
            </Text>
          ))
        )}
      </Section>
      {(['hsnB2b', 'hsnB2c'] as const).map((key) => (
        <Section key={key} title={key === 'hsnB2b' ? 'HSN summary, B2B (table 12)' : 'HSN summary, B2C (table 12)'} flush>
          <DataTable<Gstr1HsnRow>
            ariaLabel={key === 'hsnB2b' ? 'HSN summary of B2B supplies' : 'HSN summary of B2C supplies'}
            rows={r?.[key]}
            size="small"
            getRowId={(x) => `${x.hsn}-${x.uqc}-${x.rate}`}
            emptyTitle="None this month"
            columns={[
              { key: 'hsn', header: 'HSN or SAC', width: 110, render: (x) => x.hsn || '—' },
              { key: 'desc', header: 'Description', render: (x) => x.description },
              { key: 'qty', header: 'Quantity', width: 110, render: (x) => `${x.quantity} ${x.uqc}` },
              { key: 'rate', header: 'Rate', width: 80, render: (x) => `${x.rate}%` },
              { key: 'amounts', header: 'Taxable and tax', render: (x) => money(x.amounts) },
            ]}
          />
        </Section>
      ))}
      <Section title="Documents issued (table 13)" flush>
        <DataTable<Gstr1DocumentsIssued>
          ariaLabel="Documents issued"
          rows={r?.documentsIssued}
          size="small"
          getRowId={(x) => `${x.nature}-${x.series}`}
          emptyTitle="None this month"
          columns={[
            {
              key: 'nature',
              header: 'Nature',
              width: 200,
              render: (x) => (x.nature === 1 ? 'Invoices' : x.nature === 4 ? 'Debit notes' : 'Credit notes'),
            },
            { key: 'series', header: 'Series', render: (x) => x.series },
            { key: 'range', header: 'From – to', render: (x) => `${x.from} – ${x.to}` },
            { key: 'total', header: 'Issued', align: 'end', width: 90, render: (x) => String(x.total) },
            { key: 'cancelled', header: 'Cancelled', align: 'end', width: 90, render: (x) => String(x.cancelled) },
          ]}
        />
      </Section>
    </Page>
  );
}
