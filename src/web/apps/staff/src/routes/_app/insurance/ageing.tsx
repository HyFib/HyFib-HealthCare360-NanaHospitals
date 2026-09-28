import { Button, Field, Input, makeStyles, Text, tokens } from '@fluentui/react-components';
import { DataBarVerticalRegular, PrintRegular } from '@fluentui/react-icons';
import {
  escapeHtml,
  formatDateOnly,
  formatDateTime,
  formatInr,
  printDocument,
  todayIso,
  useSession,
} from '@hc360/core';
import {
  DataTable,
  ErrorState,
  FilterBar,
  KpiCard,
  LoadingState,
  Page,
  PageHeader,
  Section,
  useNotify,
} from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useMemo } from 'react';
import { EChart, type ChartOption } from '@/lib/EChart';
import { usePermission } from '@/lib/permissions';
import { usePrintFacility } from '@/modules/billing/hooks';
import { sendToPrinter, type PrintFacility } from '@/modules/billing/print';
import { dateParam } from '@/modules/billing/searchParams';
import { Amount, NoAccess } from '@/modules/billing/ui';
import { insuranceQueries, type ReceivablesAgeingReport } from '@/modules/insurance/api';
import { PAYER_CATEGORY_LABEL } from '@/modules/insurance/labels';

export const Route = createFileRoute('/_app/insurance/ageing')({
  validateSearch: (s: Record<string, unknown>): { asOf?: string } => ({ asOf: dateParam(s.asOf) }),
  component: AgeingPage,
});

const useStyles = makeStyles({
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(170px, 1fr))' },
  muted: { color: tokens.colorNeutralForeground3 },
});

const BUCKETS = [
  { key: 'days0To30', label: '0–30 days' },
  { key: 'days31To60', label: '31–60 days' },
  { key: 'days61To90', label: '61–90 days' },
  { key: 'over90', label: 'Over 90 days' },
] as const;

function buildAgeingHtml(report: ReceivablesAgeingReport, facility: PrintFacility, printedBy: string): string {
  const e = escapeHtml;
  const money = (value: number) => e(formatInr(value));
  const rows = report.payers
    .map(
      (p) =>
        `<tr><td>${e(p.payerName)}</td><td>${e(p.category ? PAYER_CATEGORY_LABEL[p.category] : '')}</td><td class="right">${e(p.creditDays)}</td>${BUCKETS.map((b) => `<td class="right">${money(p[b.key])}</td>`).join('')}<td class="right"><b>${money(p.total)}</b></td></tr>`,
    )
    .join('');
  const body = `<h2>${e(facility.legalName || facility.name)}</h2>
    <h3>Insurance receivables ageing as on ${e(formatDateOnly(report.asOf))}</h3>
    <table style="margin-top:8px"><tr><th>Payer</th><th>Kind</th><th class="right">Credit days</th>${BUCKETS.map((b) => `<th class="right">${e(b.label)}</th>`).join('')}<th class="right">Total</th></tr>
    ${rows}
    <tr><td colspan="3"><b>Total</b></td>${BUCKETS.map((b) => `<td class="right"><b>${money(report[b.key])}</b></td>`).join('')}<td class="right"><b>${money(report.total)}</b></td></tr></table>
    <p class="muted">Printed by ${e(printedBy)} on ${e(formatDateTime(new Date().toISOString()))}. Ageing counts from the bill date.</p>`;
  return printDocument(body, {
    title: `Receivables ageing ${report.asOf}`,
    pageSize: 'A4',
    styles:
      'th { border-bottom: 1px solid #555; background: #f2f2f2; } td { border-bottom: 1px solid #e3e3e3; } h2 { color: #045E80; }',
  });
}

function AgeingChart({ report }: { report: ReceivablesAgeingReport }) {
  const option = useMemo<ChartOption>(
    () => ({
      grid: { left: 150, right: 24, top: 36, bottom: 24 },
      legend: { top: 0 },
      tooltip: {
        trigger: 'axis',
        axisPointer: { type: 'shadow' },
        valueFormatter: (value) => formatInr(Number(value)),
      },
      xAxis: { type: 'value', axisLabel: { formatter: (value: number) => formatInr(value, { whole: true }) } },
      yAxis: { type: 'category', data: report.payers.map((p) => p.payerName), inverse: true },
      series: BUCKETS.map((bucket) => ({
        type: 'bar' as const,
        name: bucket.label,
        stack: 'total',
        data: report.payers.map((p) => p[bucket.key]),
        barMaxWidth: 28,
      })),
    }),
    [report.payers],
  );
  return (
    <EChart
      option={option}
      height={Math.max(220, 60 + report.payers.length * 36)}
      ariaLabel="Receivables by payer and age"
    />
  );
}

/** Receivable ageing by payer (INS-07): how long insurers have owed money, 0–30 / 31–60 / 61–90 / 90+ days. */
function AgeingPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const facility = usePrintFacility();
  const printedBy = useSession((s) => s.user?.displayName) ?? '';
  const canClaims = usePermission('insurance.claims.manage');
  const report = useQuery({ ...insuranceQueries.ageing(search.asOf), enabled: canClaims });

  if (!canClaims) {
    return (
      <Page>
        <PageHeader title="Receivables ageing" />
        <NoAccess what="receivables" />
      </Page>
    );
  }

  const r = report.data;
  return (
    <Page>
      <PageHeader
        title="Receivables ageing"
        subtitle="What insurers, TPAs and corporates owe, by how long it has been outstanding"
        icon={<DataBarVerticalRegular />}
        actions={
          <Button
            appearance="primary"
            icon={<PrintRegular />}
            disabled={!r}
            onClick={() =>
              r &&
              void sendToPrinter(buildAgeingHtml(r, facility, printedBy), 'A4').catch((error: unknown) =>
                notify.error(error, 'Printing failed'),
              )
            }
          >
            Print
          </Button>
        }
      />
      <Section>
        <FilterBar>
          <Field label="As on">
            <Input
              type="date"
              value={search.asOf ?? todayIso()}
              max={todayIso()}
              onChange={(_, d) => void navigate({ search: { asOf: d.value || undefined } })}
            />
          </Field>
        </FilterBar>
      </Section>
      {report.isLoading ? (
        <LoadingState />
      ) : report.error || !r ? (
        <ErrorState error={report.error} onRetry={() => void report.refetch()} />
      ) : (
        <>
          <div className={styles.kpis}>
            <KpiCard label="Total outstanding" value={<Amount value={r.total} />} />
            {BUCKETS.map((b) => (
              <KpiCard
                key={b.key}
                label={b.label}
                value={<Amount value={r[b.key]} tone={b.key === 'over90' && r.over90 > 0 ? 'negative' : undefined} />}
              />
            ))}
          </div>
          {r.payers.length > 0 ? (
            <Section title="By payer">
              <AgeingChart report={r} />
            </Section>
          ) : (
            <Text className={styles.muted}>Nothing is outstanding from insurers as on {formatDateOnly(r.asOf)}.</Text>
          )}
          <Section flush>
            <DataTable
              ariaLabel="Receivables by payer"
              rows={r.payers}
              getRowId={(p) => p.payerId}
              onRowClick={(p) => void navigate({ to: '/insurance/claims', search: { payerId: p.payerId } })}
              size="small"
              columns={[
                { key: 'payer', header: 'Payer', render: (p) => <Text weight="semibold">{p.payerName}</Text> },
                {
                  key: 'kind',
                  header: 'Kind',
                  priority: 'low',
                  render: (p) => (p.category ? PAYER_CATEGORY_LABEL[p.category] : '—'),
                },
                { key: 'credit', header: 'Credit days', align: 'end', priority: 'low', render: (p) => p.creditDays },
                ...BUCKETS.map((b) => ({
                  key: b.key,
                  header: b.label,
                  align: 'end' as const,
                  render: (p: (typeof r.payers)[number]) => (
                    <Amount value={p[b.key]} tone={b.key === 'over90' && p.over90 > 0 ? 'negative' : undefined} />
                  ),
                })),
                { key: 'total', header: 'Total', align: 'end', render: (p) => <Amount value={p.total} strong /> },
              ]}
              emptyTitle="No receivables"
            />
          </Section>
        </>
      )}
    </Page>
  );
}
