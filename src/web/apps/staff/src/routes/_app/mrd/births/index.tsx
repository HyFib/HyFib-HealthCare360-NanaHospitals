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
  BIRTH_READERS,
  birthOutcomeLabel,
  birthSexLabel,
  VITAL_PERMISSIONS,
  vitalApi,
  vitalQueries,
  vitalStatusLabel,
  vitalStatusTone,
  type BirthRegisterRow,
} from '@/modules/mrd/vital';
import { BirthDialog, ReportDialog } from '@/modules/mrd/VitalDialogs';
import { REPORT_WITHIN_DAYS } from '@/modules/mrd/vitalRules';

const MONTH = /^\d{4}-\d{2}$/;

export const Route = createFileRoute('/_app/mrd/births/')({
  validateSearch: (s: Record<string, unknown>): { month?: string } => ({
    month: typeof s.month === 'string' && MONTH.test(s.month) ? s.month : undefined,
  }),
  component: () => (
    <RequirePermission permission={BIRTH_READERS} what="births">
      <BirthsPage />
    </RequirePermission>
  ),
});

/**
 * Births in the hospital: recording one (Form 1, Form 3 for a still birth), and for medical records the month's birth
 * register, each birth's report to the Registrar within 21 days, and the register's CSV for that report.
 */
function BirthsPage() {
  const canRecord = usePermission(VITAL_PERMISSIONS.birthsRecord);
  const canRegister = usePermission(VITAL_PERMISSIONS.register);
  const [recording, setRecording] = useState(false);

  return (
    <>
      {canRegister ? (
        <BirthRegisterPage onRecord={canRecord ? () => setRecording(true) : undefined} />
      ) : (
        <Page>
          <PageHeader
            title="Births"
            subtitle="Record each birth in the hospital, live or still, against the mother's record."
            actions={
              canRecord && (
                <Button appearance="primary" icon={<AddRegular />} onClick={() => setRecording(true)}>
                  Record a birth
                </Button>
              )
            }
          />
          <MessageBar intent="info" layout="multiline">
            <MessageBarBody>
              <MessageBarTitle>The birth register is kept by medical records</MessageBarTitle>
              Once recorded, the birth opens on its own page: print Form 1 (Form 3 for a still birth) there, and correct
              it until medical records report it to the Registrar, within {REPORT_WITHIN_DAYS} days of the birth.
            </MessageBarBody>
          </MessageBar>
        </Page>
      )}
      {recording && <BirthDialog existing={null} onClose={() => setRecording(false)} />}
    </>
  );
}

function BirthRegisterPage({ onRecord }: { onRecord?: () => void }) {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const month = search.month ?? todayIso().slice(0, 7);
  const [year, monthNumber] = [Number(month.slice(0, 4)), Number(month.slice(5, 7))];
  const register = useQuery(vitalQueries.birthRegister(year, monthNumber));
  const [confirmExport, setConfirmExport] = useState(false);
  const [reporting, setReporting] = useState<string | null>(null);

  const exportCsv = useMutation({
    mutationFn: () => vitalApi.exportBirths(year, monthNumber),
    onSuccess: (blob) => {
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `birth-register-${month}.csv`;
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
  const open = (r: BirthRegisterRow) => void navigate({ to: '/mrd/births/$birthId', params: { birthId: r.id } });
  return (
    <Page>
      <PageHeader
        title="Birth register"
        subtitle={`Every birth in the hospital in the month, live and still. Report each to the Registrar within ${REPORT_WITHIN_DAYS} days, and enter the registration number it gives.`}
        actions={
          <>
            {onRecord && (
              <Button icon={<AddRegular />} onClick={onRecord}>
                Record a birth
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
                {data.liveBirths === 1 ? 'One live birth' : `${data.liveBirths} live births`},{' '}
                {data.stillBirths === 1 ? 'one still birth' : `${data.stillBirths} still births`}
              </Text>
            )}
          </FilterBar>
          {data && data.overdue > 0 && (
            <MessageBar intent="warning" style={{ marginTop: 8 }}>
              <MessageBarBody>
                {data.overdue === 1 ? 'One birth is' : `${data.overdue} births are`} past the {REPORT_WITHIN_DAYS} days
                and not yet reported. Report {data.overdue === 1 ? 'it' : 'them'} now: a late registration needs a late
                fee, and after 30 days the permission of the prescribed authority (RBD Act s.13).
              </MessageBarBody>
            </MessageBar>
          )}
        </div>
        <DataTable<BirthRegisterRow>
          ariaLabel="Birth register"
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
                    {formatDateTime(r.bornAt)}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'birth',
              header: 'Birth',
              render: (r) => (
                <span>
                  <Text weight="semibold">{birthOutcomeLabel[r.outcome]}</Text>
                  <Caption1 block>
                    {[birthSexLabel[r.sex], r.weightGrams ? `${r.weightGrams} g` : null, r.childName]
                      .filter(Boolean)
                      .join(' · ')}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'mother',
              header: 'Mother',
              render: (r) => (
                <span>
                  <Text weight="semibold">{r.motherName}</Text>
                  <Caption1 block>{r.motherUhid}</Caption1>
                </span>
              ),
            },
            { key: 'father', header: 'Father', priority: 'low', render: (r) => r.fatherName ?? '' },
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
          emptyTitle="No births this month"
          emptyDescription="Births appear here once they are recorded against the mother."
        />
      </Section>
      {reporting && <ReportDialog kind="birth" id={reporting} onClose={() => setReporting(null)} />}
      <ConfirmDialog
        open={confirmExport}
        title="Download the register?"
        confirmLabel="Download"
        busy={exportCsv.isPending}
        onConfirm={() => exportCsv.mutate()}
        onCancel={() => setConfirmExport(false)}
      >
        The file contains the mothers&apos; names and UHIDs and the children&apos;s details. The download is recorded in
        the audit log with your name. Keep it on a secure computer and delete it once the births are reported.
      </ConfirmDialog>
    </Page>
  );
}
