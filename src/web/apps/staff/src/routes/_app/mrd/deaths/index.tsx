import {
  Badge,
  Button,
  Caption1,
  Field,
  Input,
  Link,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, ArrowDownloadRegular, SendRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, todayIso } from '@hc360/core';
import { ConfirmDialog, DataTable, FilterBar, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import {
  DEATH_READERS,
  deathMannerLabel,
  VITAL_PERMISSIONS,
  vitalApi,
  vitalQueries,
  vitalStatusLabel,
  vitalStatusTone,
  type DeathRegisterRow,
} from '@/modules/mrd/vital';
import { DeathDialog, ReportDialog } from '@/modules/mrd/VitalDialogs';
import { REPORT_WITHIN_DAYS } from '@/modules/mrd/vitalRules';

const MONTH = /^\d{4}-\d{2}$/;

export const Route = createFileRoute('/_app/mrd/deaths/')({
  validateSearch: (s: Record<string, unknown>): { month?: string } => ({
    month: typeof s.month === 'string' && MONTH.test(s.month) ? s.month : undefined,
  }),
  component: () => (
    <RequirePermission permission={DEATH_READERS} what="deaths">
      <DeathsPage />
    </RequirePermission>
  ),
});

/**
 * Deaths in the hospital: the doctor's medical certificate of cause of death (MCCD, Form 4), and for medical records
 * the month's death register, each death's report to the Registrar within 21 days, and the register's CSV.
 */
function DeathsPage() {
  const canCertify = usePermission(VITAL_PERMISSIONS.deathsCertify);
  const canRegister = usePermission(VITAL_PERMISSIONS.register);
  const [certifying, setCertifying] = useState(false);

  return (
    <>
      {canRegister ? (
        <DeathRegisterPage onCertify={canCertify ? () => setCertifying(true) : undefined} />
      ) : (
        <Page>
          <PageHeader
            title="Deaths"
            subtitle="Certify the cause of a death in the hospital (MCCD, Form 4). Certifying it marks the patient deceased."
            actions={
              canCertify && (
                <Button appearance="primary" icon={<AddRegular />} onClick={() => setCertifying(true)}>
                  Certify a death
                </Button>
              )
            }
          />
          <MessageBar intent="info" layout="multiline">
            <MessageBarBody>
              <MessageBarTitle>The death register is kept by medical records</MessageBarTitle>
              Once certified, the death opens on its own page: print the certificate there, and correct it until medical
              records report it to the Registrar, within {REPORT_WITHIN_DAYS} days of the death.
            </MessageBarBody>
          </MessageBar>
        </Page>
      )}
      {certifying && <DeathDialog existing={null} onClose={() => setCertifying(false)} />}
    </>
  );
}

function DeathRegisterPage({ onCertify }: { onCertify?: () => void }) {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const month = search.month ?? todayIso().slice(0, 7);
  const [year, monthNumber] = [Number(month.slice(0, 4)), Number(month.slice(5, 7))];
  const register = useQuery(vitalQueries.deathRegister(year, monthNumber));
  const [confirmExport, setConfirmExport] = useState(false);
  const [reporting, setReporting] = useState<string | null>(null);

  const exportCsv = useMutation({
    mutationFn: () => vitalApi.exportDeaths(year, monthNumber),
    onSuccess: (blob) => {
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `death-register-${month}.csv`;
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
  const open = (r: DeathRegisterRow) => void navigate({ to: '/mrd/deaths/$deathId', params: { deathId: r.id } });
  return (
    <Page>
      <PageHeader
        title="Death register"
        subtitle={`Every death in the hospital in the month, with its certified cause. Report each to the Registrar within ${REPORT_WITHIN_DAYS} days, and enter the registration number it gives.`}
        actions={
          <>
            {onCertify && (
              <Button icon={<AddRegular />} onClick={onCertify}>
                Certify a death
              </Button>
            )}
            <Button
              appearance="primary"
              icon={<ArrowDownloadRegular />}
              disabled={exportCsv.isPending || !data || data.rows.length === 0}
              onClick={() => setConfirmExport(true)}
            >
              Download CSV
            </Button>
          </>
        }
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
            {data && (
              <Text>
                {data.rows.length === 1 ? 'One death' : `${data.rows.length} deaths`},{' '}
                {data.medicoLegal === 1 ? 'one medico-legal' : `${data.medicoLegal} medico-legal`}
              </Text>
            )}
          </FilterBar>
          {data && data.overdue > 0 && (
            <MessageBar intent="warning" style={{ marginTop: 8 }}>
              <MessageBarBody>
                {data.overdue === 1 ? 'One death is' : `${data.overdue} deaths are`} past the {REPORT_WITHIN_DAYS} days
                and not yet reported. Report {data.overdue === 1 ? 'it' : 'them'} now: a late registration needs a late
                fee, and after 30 days the permission of the prescribed authority (RBD Act s.13).
              </MessageBarBody>
            </MessageBar>
          )}
        </div>
        <DataTable<DeathRegisterRow>
          ariaLabel="Death register"
          rows={data?.rows}
          loading={register.isLoading}
          error={register.error}
          onRetry={() => void register.refetch()}
          getRowId={(r) => r.id}
          onRowClick={open}
          size="small"
          columns={[
            {
              key: 'serial',
              header: 'Serial no.',
              width: 150,
              render: (r) => (
                <span>
                  <Link onClick={() => open(r)}>{r.serialNumber}</Link>
                  <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                    {formatDateTime(r.diedAt)}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'deceased',
              header: 'Deceased',
              render: (r) => (
                <span>
                  <Text weight="semibold">{r.patientName}</Text>
                  <Caption1 block>
                    {[r.uhid, r.gender, r.ageYears !== null ? `${r.ageYears} years` : null].filter(Boolean).join(' · ')}
                  </Caption1>
                </span>
              ),
            },
            { key: 'cause', header: 'Underlying cause', render: (r) => r.underlyingCause },
            {
              key: 'manner',
              header: 'Manner',
              priority: 'low',
              render: (r) => (
                <span>
                  {deathMannerLabel[r.manner]}
                  {r.medicoLegal && (
                    <Badge size="small" appearance="outline" style={{ marginLeft: 6 }}>
                      MLC
                    </Badge>
                  )}
                </span>
              ),
            },
            { key: 'certified', header: 'Certified by', priority: 'low', render: (r) => r.certifiedBy },
            {
              key: 'due',
              header: 'Report by',
              width: 150,
              render: (r) => (
                <span>
                  {formatDateOnly(r.dueBy)}
                  {r.overdue && (
                    <Badge size="small" appearance="filled" color="danger" style={{ marginLeft: 6 }}>
                      Overdue
                    </Badge>
                  )}
                </span>
              ),
            },
            {
              key: 'status',
              header: 'Status',
              width: 140,
              render: (r) => (
                <StatusBadge status={r.status} tone={vitalStatusTone[r.status]} label={vitalStatusLabel[r.status]} />
              ),
            },
            {
              key: 'reported',
              header: 'Reported on',
              priority: 'low',
              render: (r) => (r.reportedOn ? formatDateOnly(r.reportedOn) : ''),
            },
            { key: 'registration', header: 'Registration no.', render: (r) => r.registrationNumber ?? '' },
          ]}
          rowActions={(r) =>
            r.status === 'Recorded' && (
              <Button size="small" appearance="primary" icon={<SendRegular />} onClick={() => setReporting(r.id)}>
                Report to Registrar
              </Button>
            )
          }
          emptyTitle="No deaths this month"
          emptyDescription="Deaths appear here once the doctor certifies the cause (Form 4)."
        />
      </Section>
      {reporting && <ReportDialog kind="death" id={reporting} onClose={() => setReporting(null)} />}
      <ConfirmDialog
        open={confirmExport}
        title="Download the register?"
        confirmLabel="Download"
        busy={exportCsv.isPending}
        onConfirm={() => exportCsv.mutate()}
        onCancel={() => setConfirmExport(false)}
      >
        The file contains the names and UHIDs of the deceased and the causes of their death. The download is recorded in
        the audit log with your name. Keep it on a secure computer and delete it once the deaths are reported.
      </ConfirmDialog>
    </Page>
  );
}
