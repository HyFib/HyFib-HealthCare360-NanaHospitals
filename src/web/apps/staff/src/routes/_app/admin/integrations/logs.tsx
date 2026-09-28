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
  makeStyles,
  SearchBox,
  Select,
  Text,
  tokens,
} from '@fluentui/react-components';
import { formatDateTime, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { IntegrationsTabs } from '@/modules/admin/IntegrationsTabs';
import {
  formatDuration,
  INTEGRATION_LABELS,
  INTEGRATION_NAMES,
  integrationLabel,
  integrationQueries,
  isIntegrationName,
  isIsoDay,
  LOG_PAGE_SIZE,
  pageFromUrl,
  type IntegrationLogDto,
  type IntegrationName,
} from '@/modules/admin/integrationsApi';
import { NoAccess } from '@/modules/admin/NoAccess';

interface LogSearch {
  integration?: IntegrationName;
  result?: 'ok' | 'failed';
  from?: string;
  to?: string;
  q?: string;
  page?: number;
}

export const Route = createFileRoute('/_app/admin/integrations/logs')({
  validateSearch: (s: Record<string, unknown>): LogSearch => ({
    integration: isIntegrationName(s.integration) ? s.integration : undefined,
    result: s.result === 'ok' || s.result === 'failed' ? s.result : undefined,
    from: isIsoDay(s.from) ? s.from : undefined,
    to: isIsoDay(s.to) ? s.to : undefined,
    q: typeof s.q === 'string' && s.q.trim() ? s.q.trim().slice(0, 100) : undefined,
    page: pageFromUrl(s.page),
  }),
  component: IntegrationLogPage,
});

const useStyles = makeStyles({
  toolbar: { padding: '0 16px 12px' },
  muted: { color: tokens.colorNeutralForeground3 },
  mono: { fontFamily: tokens.fontFamilyMonospace },
  error: { color: tokens.colorPaletteRedForeground1 },
  detail: { display: 'grid', gridTemplateColumns: 'auto 1fr', gap: '6px 16px', alignItems: 'baseline' },
  block: {
    gridColumn: '1 / -1',
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
    fontFamily: tokens.fontFamilyMonospace,
    backgroundColor: tokens.colorNeutralBackground3,
    padding: '8px 12px',
    borderRadius: tokens.borderRadiusMedium,
    margin: 0,
    maxHeight: '220px',
    overflow: 'auto',
  },
});

/** Every call to and from an external system (SEC-10: phone numbers, e-mails and secrets are masked by the server). */
function IntegrationLogPage() {
  const styles = useStyles();
  const allowed = usePermission('admin.integrations.manage');
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  const [detail, setDetail] = useState<IntegrationLogDto | null>(null);
  const page = search.page ?? 1;

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const logs = useQuery({
    ...integrationQueries.logs({
      integration: search.integration,
      success: search.result === undefined ? undefined : search.result === 'ok',
      from: search.from,
      to: search.to,
      search: search.q,
      page,
    }),
    enabled: allowed,
  });

  const filter = (patch: Partial<LogSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  if (!allowed) {
    return (
      <Page>
        <PageHeader title="Integrations" />
        <NoAccess what="the integration log" />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader title="Integrations" subtitle="Every message, payment call and analyser message, newest first. Phone numbers, e-mail addresses and credentials are masked." />
      <IntegrationsTabs selected="logs" />
      <Section flush>
        <div className={styles.toolbar}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 220px' }}>
              <SearchBox value={term} onChange={(_, data) => setTerm(data.value)} placeholder="Reference, operation or provider" />
            </Field>
            <Field label="Integration">
              <Select value={search.integration ?? ''} onChange={(_, data) => filter({ integration: isIntegrationName(data.value) ? data.value : undefined })}>
                <option value="">All</option>
                {INTEGRATION_NAMES.map((name) => (
                  <option key={name} value={name}>
                    {INTEGRATION_LABELS[name]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Result">
              <Select value={search.result ?? ''} onChange={(_, data) => filter({ result: data.value === 'ok' || data.value === 'failed' ? data.value : undefined })}>
                <option value="">All</option>
                <option value="ok">Succeeded</option>
                <option value="failed">Failed</option>
              </Select>
            </Field>
            <Field label="From">
              <Input type="date" value={search.from ?? ''} max={search.to} onChange={(_, data) => filter({ from: isIsoDay(data.value) ? data.value : undefined })} />
            </Field>
            <Field label="To">
              <Input type="date" value={search.to ?? ''} min={search.from} onChange={(_, data) => filter({ to: isIsoDay(data.value) ? data.value : undefined })} />
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Integration log"
          rows={logs.data?.items}
          loading={logs.isLoading}
          error={logs.error}
          onRetry={() => void logs.refetch()}
          getRowId={(row) => row.id}
          onRowClick={setDetail}
          size="small"
          columns={[
            { key: 'when', header: 'When', width: 180, render: (row) => formatDateTime(row.createdAt) },
            { key: 'integration', header: 'Integration', render: (row) => integrationLabel(row.integration) },
            { key: 'operation', header: 'Operation', render: (row) => <Text font="monospace">{row.operation}</Text> },
            { key: 'direction', header: 'Direction', width: 100, priority: 'low', render: (row) => (row.direction === 'Inbound' ? 'Received' : 'Sent') },
            {
              key: 'result',
              header: 'Result',
              width: 110,
              render: (row) => <StatusBadge status={row.success ? 'Succeeded' : 'Failed'} label={row.success ? 'Succeeded' : 'Failed'} />,
            },
            { key: 'duration', header: 'Took', width: 90, align: 'end', priority: 'low', render: (row) => formatDuration(row.durationMs) },
            { key: 'reference', header: 'Reference', priority: 'low', render: (row) => (row.reference ? <Text font="monospace">{row.reference}</Text> : '—') },
          ]}
          emptyTitle="Nothing in the log for these filters"
          emptyDescription="Every SMS, WhatsApp message, e-mail, payment call and analyser message appears here. Send a test from the Status tab to add one."
          page={page}
          pageSize={LOG_PAGE_SIZE}
          totalCount={logs.data?.totalCount}
          onPageChange={(next) => void navigate({ search: (prev) => ({ ...prev, page: next > 1 ? next : undefined }) })}
        />
      </Section>

      <Dialog open={detail !== null} onOpenChange={(_, data) => !data.open && setDetail(null)}>
        <DialogSurface style={{ maxWidth: 760 }}>
          <DialogBody>
            <DialogTitle>{detail ? `${integrationLabel(detail.integration)}: ${detail.operation}` : ''}</DialogTitle>
            <DialogContent className={styles.detail}>
              {detail && (
                <>
                  <Caption1 className={styles.muted}>When</Caption1>
                  <Text>{formatDateTime(detail.createdAt)}</Text>
                  <Caption1 className={styles.muted}>Provider</Caption1>
                  <Text>{detail.provider}</Text>
                  <Caption1 className={styles.muted}>Direction</Caption1>
                  <Text>{detail.direction === 'Inbound' ? 'Received from outside' : 'Sent from HC360'}</Text>
                  <Caption1 className={styles.muted}>Result</Caption1>
                  <Text className={detail.success ? undefined : styles.error}>{detail.success ? 'Succeeded' : 'Failed'}</Text>
                  <Caption1 className={styles.muted}>Took</Caption1>
                  <Text>{formatDuration(detail.durationMs)}</Text>
                  <Caption1 className={styles.muted}>Reference</Caption1>
                  <Text className={styles.mono}>{detail.reference ?? '—'}</Text>
                  <Caption1 className={styles.muted}>Correlation id</Caption1>
                  <Text className={styles.mono}>{detail.correlationId ?? '—'}</Text>
                  {detail.error && (
                    <>
                      <Caption1 className={styles.muted}>Error</Caption1>
                      <pre className={styles.block}>{detail.error}</pre>
                    </>
                  )}
                  {detail.request && (
                    <>
                      <Caption1 className={styles.muted}>Request summary</Caption1>
                      <pre className={styles.block}>{detail.request}</pre>
                    </>
                  )}
                  {detail.response && (
                    <>
                      <Caption1 className={styles.muted}>Response summary</Caption1>
                      <pre className={styles.block}>{detail.response}</pre>
                    </>
                  )}
                </>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={() => setDetail(null)}>Close</Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </Page>
  );
}
