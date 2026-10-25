import {
  Button,
  Caption1,
  Field,
  Input,
  Link,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  tokens,
} from '@fluentui/react-components';
import { ArrowDownloadRegular } from '@fluentui/react-icons';
import { formatDate, todayIso } from '@hc360/core';
import { ConfirmDialog, DataTable, ErrorState, FilterBar, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import {
  MTP_PERMISSIONS,
  mtpApi,
  mtpCategoryLabel,
  mtpGroundLabel,
  mtpMethodLabel,
  mtpQueries,
  type MtpCount,
  type MtpRegisterRow,
} from '@/modules/mtp/api';
import { MtpConfidential } from '@/modules/mtp/MtpConfidential';

const MONTH = /^\d{4}-\d{2}$/;

export const Route = createFileRoute('/_app/mtp/register')({
  validateSearch: (s: Record<string, unknown>): { month?: string } => ({
    month: typeof s.month === 'string' && MONTH.test(s.month) ? s.month : undefined,
  }),
  component: () => (
    <RequirePermission permission={MTP_PERMISSIONS.register} what="the MTP register">
      <MtpRegisterPage />
    </RequirePermission>
  ),
});

const useStyles = makeStyles({
  counts: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(240px, 1fr))', gap: '16px 24px' },
  count: { textAlign: 'end', width: '64px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/**
 * The month's admission register (Form III): every termination done in it, by serial number, with the cases certified
 * and not yet done; and the month's report to the Chief Medical Officer of the district (Form II).
 */
function MtpRegisterPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const month = search.month ?? todayIso().slice(0, 7);
  const [year, monthNumber] = [Number(month.slice(0, 4)), Number(month.slice(5, 7))];
  const register = useQuery(mtpQueries.register(year, monthNumber));
  const report = useQuery(mtpQueries.monthlyReport(year, monthNumber));
  const [confirmExport, setConfirmExport] = useState(false);

  const exportCsv = useMutation({
    mutationFn: () => mtpApi.exportRegister(year, monthNumber),
    onSuccess: (blob) => {
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `mtp-register-${month}.csv`;
      document.body.appendChild(link);
      link.click();
      link.remove();
      setTimeout(() => URL.revokeObjectURL(url), 10_000);
      setConfirmExport(false);
      notify.success('Register downloaded', 'The download was recorded in the audit log.');
    },
    onError: (error) => {
      setConfirmExport(false);
      notify.error(error, 'Could not download the register');
    },
  });

  const data = register.data;
  const approval = data?.approvalNumber || report.data?.approvalNumber;
  return (
    <Page>
      <PageHeader
        title="MTP admission register"
        subtitle={`Form III: every termination of pregnancy done in the month, and the month's report to the Chief Medical Officer (Form II).${approval ? ` Certificate of approval ${approval}.` : ''}`}
        actions={
          <Button
            appearance="primary"
            icon={<ArrowDownloadRegular />}
            disabled={exportCsv.isPending || !data || data.rows.length === 0}
            onClick={() => setConfirmExport(true)}
          >
            Download CSV
          </Button>
        }
      />
      <MtpConfidential />
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
          </FilterBar>
          {data && data.awaitingTermination > 0 && (
            <MessageBar intent="warning" style={{ marginTop: 8 }}>
              <MessageBarBody>
                {data.awaitingTermination === 1 ? 'One case certified' : `${data.awaitingTermination} cases certified`}{' '}
                this month {data.awaitingTermination === 1 ? 'is' : 'are'} not yet done. Record the termination, or
                cancel the case, so the register is complete.
              </MessageBarBody>
            </MessageBar>
          )}
        </div>
        <DataTable<MtpRegisterRow>
          ariaLabel="MTP admission register"
          rows={data?.rows}
          loading={register.isLoading}
          error={register.error}
          onRetry={() => void register.refetch()}
          getRowId={(r) => r.id}
          size="small"
          columns={[
            {
              key: 'serial',
              header: 'Serial no.',
              width: 140,
              render: (r) => (
                <span>
                  <Link onClick={() => void navigate({ to: '/mtp/$caseId', params: { caseId: r.id } })}>
                    {r.serialNumber}
                  </Link>
                  <Caption1 block className={styles.muted}>
                    {r.admittedOn ? `Admitted ${formatDate(r.admittedOn)}` : ''}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'woman',
              header: 'Woman',
              render: (r) => (
                <span>
                  <Text weight="semibold">{r.patientName}</Text>
                  <Caption1 block>
                    {[r.uhid, `${r.ageYears} years`, r.religion, r.guardianName ? `w/d/o ${r.guardianName}` : null]
                      .filter(Boolean)
                      .join(' · ')}
                  </Caption1>
                </span>
              ),
            },
            { key: 'address', header: 'Address', priority: 'low', render: (r) => r.address },
            { key: 'weeks', header: 'Pregnancy', render: (r) => `${r.weeksOfGestation} weeks` },
            {
              key: 'reasons',
              header: 'Reasons',
              priority: 'low',
              render: (r) =>
                [r.grounds.map((g) => mtpGroundLabel[g]).join('; '), r.category ? mtpCategoryLabel[r.category] : null]
                  .filter(Boolean)
                  .join(' · '),
            },
            {
              key: 'terminated',
              header: 'Terminated',
              render: (r) =>
                r.terminatedOn ? (
                  <span>
                    {formatDate(r.terminatedOn)}
                    <Caption1 block>{r.method ? mtpMethodLabel[r.method] : ''}</Caption1>
                  </span>
                ) : (
                  'Not yet done'
                ),
            },
            {
              key: 'discharged',
              header: 'Discharged',
              priority: 'low',
              render: (r) =>
                [r.dischargedOn ? formatDate(r.dischargedOn) : null, r.outcome].filter(Boolean).join(' · '),
            },
            { key: 'opinion', header: 'Opinion formed by', priority: 'low', render: (r) => r.opinionBy },
            { key: 'performed', header: 'Performed by', priority: 'low', render: (r) => r.performedBy ?? '' },
            { key: 'remarks', header: 'Remarks', priority: 'low', render: (r) => r.remarks ?? '' },
          ]}
          emptyTitle="No terminations this month"
          emptyDescription="A case enters the register once it is certified; its termination is recorded on the case."
        />
      </Section>
      <Section
        title="Monthly report (Form II)"
        description={
          report.data
            ? `${report.data.total === 1 ? 'One termination' : `${report.data.total} terminations`} done in the month, for the Chief Medical Officer of the district.`
            : 'The terminations done in the month, for the Chief Medical Officer of the district.'
        }
      >
        {report.error ? (
          <ErrorState error={report.error} onRetry={() => void report.refetch()} />
        ) : (
          <div className={styles.counts}>
            <Counts title="Length of pregnancy" counts={report.data?.byGestation} />
            <Counts title="Religion" counts={report.data?.byReligion} />
            <Counts title="Age" counts={report.data?.byAge} />
            <Counts title="Method" counts={report.data?.byMethod} />
            <Counts title="Reasons" counts={report.data?.byGround} />
          </div>
        )}
      </Section>
      <ConfirmDialog
        open={confirmExport}
        title="Download the register?"
        confirmLabel="Download"
        busy={exportCsv.isPending}
        onConfirm={() => exportCsv.mutate()}
        onCancel={() => setConfirmExport(false)}
      >
        The file contains the women&apos;s names, UHIDs and addresses, which the MTP Act keeps secret. The download is
        recorded in the audit log with your name. Keep it on a secure computer and delete it once it has been used.
      </ConfirmDialog>
    </Page>
  );
}

/** One count of Form II, as a small two-column table. */
function Counts({ title, counts }: { title: string; counts: MtpCount[] | undefined }) {
  const styles = useStyles();
  return (
    <Table size="small" aria-label={title}>
      <TableHeader>
        <TableRow>
          <TableHeaderCell>{title}</TableHeaderCell>
          <TableHeaderCell className={styles.count}>Cases</TableHeaderCell>
        </TableRow>
      </TableHeader>
      <TableBody>
        {(counts ?? []).map((c) => (
          <TableRow key={c.label}>
            <TableCell>{c.label}</TableCell>
            <TableCell className={styles.count}>{c.count}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
