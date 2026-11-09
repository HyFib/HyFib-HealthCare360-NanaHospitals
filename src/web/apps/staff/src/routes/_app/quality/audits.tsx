import { Button, Field, Select } from '@fluentui/react-components';
import { AddRegular, ClipboardTaskListLtrRegular } from '@fluentui/react-icons';
import { Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { AUDIT_STATUS_LABELS, AUDITS_MANAGE, auditQueries, type AuditStatus } from '@/modules/quality/auditApi';
import { AuditDialog, AuditsTable, ScheduleAuditDialog } from '@/modules/quality/Audits';
import { QUALITY_PERMISSIONS as Q } from '@/modules/quality/labels';

const STATUSES: readonly AuditStatus[] = ['Scheduled', 'InProgress', 'Completed', 'Closed', 'Cancelled'];

export const Route = createFileRoute('/_app/quality/audits')({
  validateSearch: (s: Record<string, unknown>): { audit?: string; status?: AuditStatus } => ({
    audit: typeof s.audit === 'string' && s.audit ? s.audit : undefined,
    status: STATUSES.includes(s.status as AuditStatus) ? (s.status as AuditStatus) : undefined,
  }),
  component: AuditsPage,
});

/**
 * NABH internal audits: the quality team's schedule (checklists by chapter, recurring), and every member of staff's own
 * audits to lead, recorded item by item.
 */
function AuditsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const manage = usePermission(AUDITS_MANAGE);
  const planCapa = usePermission(Q.capaManage);
  const [scheduling, setScheduling] = useState(false);
  const all = useQuery({ ...auditQueries.list(search.status), enabled: manage });
  const mine = useQuery(auditQueries.mine());
  const open = (id: string | undefined) => void navigate({ search: (prev) => ({ ...prev, audit: id }), replace: true });

  return (
    <Page>
      <PageHeader
        title="Internal audits"
        subtitle="Audits against the NABH chapter checklists, their findings and corrective actions"
        icon={<ClipboardTaskListLtrRegular />}
        actions={
          manage && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setScheduling(true)}>
              Schedule an audit
            </Button>
          )
        }
      />
      {manage && (
        <Section
          flush
          title="Schedule"
          actions={
            <Field label="Show" orientation="horizontal">
              <Select
                value={search.status ?? ''}
                onChange={(_, d) =>
                  void navigate({
                    search: (prev) => ({ ...prev, status: (d.value || undefined) as AuditStatus | undefined }),
                    replace: true,
                  })
                }
              >
                <option value="">Not cancelled</option>
                {STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {AUDIT_STATUS_LABELS[s]}
                  </option>
                ))}
              </Select>
            </Field>
          }
        >
          <AuditsTable
            rows={all.data}
            loading={all.isLoading}
            error={all.error}
            onRetry={() => void all.refetch()}
            onOpen={open}
          />
        </Section>
      )}
      <Section flush title="Audits I lead">
        <AuditsTable
          rows={mine.data}
          loading={mine.isLoading}
          error={mine.error}
          onRetry={() => void mine.refetch()}
          onOpen={open}
        />
      </Section>
      {scheduling && (
        <ScheduleAuditDialog
          onClose={() => setScheduling(false)}
          onScheduled={(id) => {
            setScheduling(false);
            open(id);
          }}
        />
      )}
      {search.audit && <AuditDialog auditId={search.audit} canPlanCapa={planCapa} onClose={() => open(undefined)} />}
    </Page>
  );
}
