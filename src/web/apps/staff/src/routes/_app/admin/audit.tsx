import {
  Badge,
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
  SearchBox,
  Select,
  Text,
  tokens,
} from '@fluentui/react-components';
import { ShieldCheckmarkRegular } from '@fluentui/react-icons';
import { formatDateTime, humanize, useDebouncedValue } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { api } from '@/lib/api';
import {
  adminQueries,
  type AuditEventDto,
  type AuditFilters,
  type AuditSeverity,
  type AuditVerification,
} from '@/modules/admin/api';

export const Route = createFileRoute('/_app/admin/audit')({
  component: AuditPage,
});

const CATEGORIES = ['Security', 'PatientAccess', 'DataChange', 'Admin', 'Clinical', 'Billing', 'Integration', 'System'];

function severityBadge(severity: AuditSeverity) {
  return (
    <Badge
      appearance="tint"
      color={severity === 'Critical' ? 'danger' : severity === 'Warning' ? 'warning' : 'informative'}
    >
      {severity}
    </Badge>
  );
}

/** Tamper-evident audit trail (SRS SEC-09, NABH): who did what, when, from where; chain verification on demand. */
function AuditPage() {
  const notify = useNotify();
  const [filters, setFilters] = useState<AuditFilters>({});
  const [term, setTerm] = useState('');
  const search = useDebouncedValue(term.trim(), 300);
  const [page, setPage] = useState(1);
  const [detail, setDetail] = useState<AuditEventDto | null>(null);
  const query = { ...filters, search: search || undefined, page };
  const events = useQuery(adminQueries.audit(query));

  const verify = useMutation({
    mutationFn: () => api.get<AuditVerification>('/api/v1/admin/audit/verify', { timeoutMs: 120_000 }),
    onError: (error) => notify.error(error, 'Verification could not run'),
  });

  const set = (patch: Partial<AuditFilters>) => {
    setFilters((f) => ({ ...f, ...patch }));
    setPage(1);
  };

  return (
    <Page>
      <PageHeader
        title="Audit log"
        subtitle="Every sign-in, record access and change. Entries cannot be edited or deleted."
        actions={
          <Button icon={<ShieldCheckmarkRegular />} onClick={() => verify.mutate()} disabled={verify.isPending}>
            {verify.isPending ? 'Verifying…' : 'Verify integrity'}
          </Button>
        }
      />

      {verify.data && (
        <MessageBar intent={verify.data.valid ? 'success' : 'error'} layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>
              {verify.data.valid ? 'The audit log is intact' : 'The audit log has been altered'}
            </MessageBarTitle>
            {verify.data.message ??
              `${verify.data.checkedCount.toLocaleString('en-IN')} sealed entries checked${verify.data.unsealedCount ? `, ${verify.data.unsealedCount} awaiting sealing` : ''}.`}
            {verify.data.firstInvalidChainIndex !== null &&
              ` First broken link at entry #${verify.data.firstInvalidChainIndex}.`}
          </MessageBarBody>
        </MessageBar>
      )}

      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 220px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="User, action or summary" />
            </Field>
            <Field label="From">
              <Input
                type="date"
                value={filters.from?.slice(0, 10) ?? ''}
                onChange={(_, d) => set({ from: d.value ? `${d.value}T00:00:00+05:30` : undefined })}
              />
            </Field>
            <Field label="To">
              <Input
                type="date"
                value={filters.to?.slice(0, 10) ?? ''}
                onChange={(_, d) => set({ to: d.value ? `${d.value}T23:59:59+05:30` : undefined })}
              />
            </Field>
            <Field label="Category">
              <Select value={filters.category ?? ''} onChange={(_, d) => set({ category: d.value || undefined })}>
                <option value="">All</option>
                {CATEGORIES.map((c) => (
                  <option key={c} value={c}>
                    {humanize(c)}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Severity">
              <Select
                value={filters.minSeverity ?? ''}
                onChange={(_, d) => set({ minSeverity: (d.value || undefined) as AuditSeverity | undefined })}
              >
                <option value="">All</option>
                <option value="Warning">Warning and above</option>
                <option value="Critical">Critical only</option>
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Audit events"
          rows={events.data?.items}
          loading={events.isLoading}
          error={events.error}
          onRetry={() => void events.refetch()}
          getRowId={(e) => String(e.id)}
          onRowClick={setDetail}
          columns={[
            { key: 'when', header: 'When', width: 170, render: (e) => formatDateTime(e.occurredAt) },
            { key: 'user', header: 'User', render: (e) => e.userName ?? 'System' },
            { key: 'action', header: 'Action', render: (e) => <Text font="monospace">{e.action}</Text> },
            { key: 'summary', header: 'Summary', priority: 'low', render: (e) => e.summary ?? '—' },
            { key: 'ip', header: 'From', priority: 'low', render: (e) => e.ipAddress ?? '—' },
            { key: 'severity', header: 'Severity', width: 100, render: (e) => severityBadge(e.severity) },
          ]}
          emptyTitle="No audit entries match"
          page={page}
          pageSize={50}
          totalCount={events.data?.totalCount}
          onPageChange={setPage}
          size="small"
        />
      </Section>

      <Dialog open={detail !== null} onOpenChange={(_, d) => !d.open && setDetail(null)}>
        <DialogSurface style={{ maxWidth: 720 }}>
          <DialogBody>
            <DialogTitle>{detail?.action}</DialogTitle>
            <DialogContent style={{ display: 'grid', gridTemplateColumns: 'auto 1fr', gap: '6px 16px' }}>
              {detail && (
                <>
                  <Caption1>When</Caption1>
                  <Text>{formatDateTime(detail.occurredAt)}</Text>
                  <Caption1>User</Caption1>
                  <Text>{detail.userName ?? 'System'}</Text>
                  <Caption1>Category</Caption1>
                  <Text>{humanize(detail.category)}</Text>
                  <Caption1>Severity</Caption1>
                  <span>{severityBadge(detail.severity)}</span>
                  <Caption1>Record</Caption1>
                  <Text>{detail.entityType ? `${detail.entityType} ${detail.entityId ?? ''}` : '—'}</Text>
                  <Caption1>Patient</Caption1>
                  <Text>{detail.patientId ?? '—'}</Text>
                  <Caption1>Network address</Caption1>
                  <Text>{detail.ipAddress ?? '—'}</Text>
                  <Caption1>Summary</Caption1>
                  <Text>{detail.summary ?? '—'}</Text>
                  <Caption1>Sealed</Caption1>
                  <Text>
                    {detail.sealed ? 'Yes, part of the tamper-evident chain' : 'Not yet (sealed within a minute)'}
                  </Text>
                  {detail.details && (
                    <pre
                      style={{
                        gridColumn: '1 / -1',
                        whiteSpace: 'pre-wrap',
                        wordBreak: 'break-word',
                        background: tokens.colorNeutralBackground3,
                        padding: 12,
                        borderRadius: 6,
                        margin: 0,
                        maxHeight: 320,
                        overflow: 'auto',
                      }}
                    >
                      {prettyJson(detail.details)}
                    </pre>
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

function prettyJson(value: string): string {
  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch {
    return value;
  }
}
