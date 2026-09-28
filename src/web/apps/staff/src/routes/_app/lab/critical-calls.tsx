import { Badge, Caption1, Field, makeStyles, Select, Text, tokens } from '@fluentui/react-components';
import { CheckmarkRegular, WarningRegular } from '@fluentui/react-icons';
import { formatDate, formatDateTime, todayIso } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { labQueries } from '@/modules/lab/api';
import { DateRangeFields } from '@/modules/lab/common/DateRangeFields';
import { searchDate, searchOneOf, searchPage } from '@/modules/lab/common/search';
import type { CommunicationMethod, CriticalCallDto, DiagnosticKind } from '@/modules/lab/common/types';
import { useLabLive } from '@/modules/lab/useLabLive';

const KINDS = ['Laboratory', 'Radiology', 'Cardiology'] as const satisfies readonly DiagnosticKind[];

const METHOD_LABEL: Record<CommunicationMethod, string> = { Phone: 'Phone', InPerson: 'In person', VideoCall: 'Video call', SecureMessage: 'Secure message' };

interface CallsSearch {
  from?: string;
  to?: string;
  kind?: DiagnosticKind;
  page?: number;
}

export const Route = createFileRoute('/_app/lab/critical-calls')({
  validateSearch: (s: Record<string, unknown>): CallsSearch => ({
    from: searchDate(s.from),
    to: searchDate(s.to),
    kind: searchOneOf(s.kind, KINDS),
    page: searchPage(s.page),
  }),
  component: CriticalCallsPage,
});

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  ok: { color: tokens.colorPaletteGreenForeground1 },
  bad: { color: tokens.colorPaletteRedForeground1 },
});

/** Critical call register (NABH): every documented communication of a critical lab value or imaging/cardiology finding. */
function CriticalCallsPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  useLabLive();
  const calls = useQuery(labQueries.criticalCalls({ from: search.from, to: search.to, kind: search.kind, page: search.page }));
  const set = (patch: Partial<CallsSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });
  const today = todayIso();

  return (
    <Page>
      <PageHeader
        title="Critical call register"
        subtitle={
          search.from && search.from !== today ? `From ${formatDate(search.from)}${search.to && search.to !== search.from ? ` to ${formatDate(search.to)}` : ''}` : 'Today: who was told, how and when, with read-back'
        }
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <DateRangeFields value={{ from: search.from, to: search.to }} onChange={(range) => set(range)} />
            <Field label="Department">
              <Select value={search.kind ?? ''} onChange={(_, data) => set({ kind: (data.value || undefined) as DiagnosticKind | undefined })}>
                <option value="">All</option>
                {KINDS.map((kind) => (
                  <option key={kind} value={kind}>
                    {kind}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Critical call register"
          rows={calls.data?.items}
          loading={calls.isLoading}
          error={calls.error}
          onRetry={() => void calls.refetch()}
          getRowId={(call) => call.id}
          size="small"
          columns={[
            { key: 'at', header: 'When', width: 170, render: (call: CriticalCallDto) => formatDateTime(call.informedAt) },
            {
              key: 'patient',
              header: 'Patient',
              render: (call: CriticalCallDto) => (
                <span className={styles.cell}>
                  <Text weight="semibold">{call.patientName}</Text>
                  <Caption1 className={styles.muted}>{call.uhid}</Caption1>
                </span>
              ),
            },
            {
              key: 'finding',
              header: 'Critical result',
              render: (call: CriticalCallDto) => (
                <span className={styles.cell}>
                  <Text>{call.finding}</Text>
                  <Caption1 className={styles.muted}>{call.kind}</Caption1>
                </span>
              ),
            },
            {
              key: 'informed',
              header: 'Informed',
              render: (call: CriticalCallDto) => (
                <span className={styles.cell}>
                  <Text>{call.informedPersonName}</Text>
                  <Caption1 className={styles.muted}>
                    {[call.informedPersonRole, METHOD_LABEL[call.method]].filter(Boolean).join(' · ')}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'readback',
              header: 'Read back',
              width: 100,
              render: (call: CriticalCallDto) =>
                call.readBackConfirmed ? (
                  <Badge appearance="tint" color="success" icon={<CheckmarkRegular />} shape="rounded">
                    Yes
                  </Badge>
                ) : (
                  <Badge appearance="tint" color="danger" icon={<WarningRegular />} shape="rounded">
                    No
                  </Badge>
                ),
            },
            {
              key: 'by',
              header: 'Called by',
              priority: 'low',
              render: (call: CriticalCallDto) => (
                <span className={styles.cell}>
                  <Text>{call.calledBy ?? '—'}</Text>
                  {call.notes && <Caption1 className={styles.muted}>{call.notes}</Caption1>}
                </span>
              ),
            },
          ]}
          emptyTitle="No critical calls in this period"
          emptyDescription="Calls are recorded from result entry and report sign-off."
          page={search.page ?? 1}
          pageSize={25}
          totalCount={calls.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
        />
      </Section>
    </Page>
  );
}
