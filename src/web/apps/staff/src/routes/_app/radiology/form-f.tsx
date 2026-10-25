import {
  Button,
  Caption1,
  Field,
  Input,
  Link,
  MessageBar,
  MessageBarBody,
  Text,
  tokens,
} from '@fluentui/react-components';
import { ArrowDownloadRegular } from '@fluentui/react-icons';
import { formatDate, todayIso } from '@hc360/core';
import { ConfirmDialog, DataTable, FilterBar, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { radiologyApi, radiologyQueries, RADIOLOGY_PERMISSIONS, type FormFRegisterRow } from '@/modules/radiology/api';
import { formFIndicationLabel } from '@/modules/radiology/labels';

const MONTH = /^\d{4}-\d{2}$/;

export const Route = createFileRoute('/_app/radiology/form-f')({
  validateSearch: (s: Record<string, unknown>): { month?: string } => ({
    month: typeof s.month === 'string' && MONTH.test(s.month) ? s.month : undefined,
  }),
  component: () => (
    <RequirePermission permission={RADIOLOGY_PERMISSIONS.formFRegister} what="the Form F register">
      <FormFRegisterPage />
    </RequirePermission>
  ),
});

/**
 * The month's PCPNDT Form F register: every ultrasound in pregnancy, as the Appropriate Authority receives it by the
 * 5th of the next month (PCPNDT Rules, rule 9(8)), with the forms still to be completed.
 */
function FormFRegisterPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const month = search.month ?? todayIso().slice(0, 7);
  const [year, monthNumber] = [Number(month.slice(0, 4)), Number(month.slice(5, 7))];
  const register = useQuery(radiologyQueries.formFRegister(year, monthNumber));
  const [confirmExport, setConfirmExport] = useState(false);

  const exportCsv = useMutation({
    mutationFn: () => radiologyApi.exportFormFRegister(year, monthNumber),
    onSuccess: (blob) => {
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `form-f-register-${month}.csv`;
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
  return (
    <Page>
      <PageHeader
        title="PCPNDT Form F register"
        subtitle={`Every ultrasound in pregnancy in the month. Send the month's forms to the Appropriate Authority by the 5th of the next month.${data?.registrationNumber ? ` Registration ${data.registrationNumber}.` : ''}`}
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
          {data && data.incomplete > 0 && (
            <MessageBar intent="warning" style={{ marginTop: 8 }}>
              <MessageBarBody>
                {data.incomplete === 1 ? 'One form is' : `${data.incomplete} forms are`} not completed. The doctors who
                scanned complete them, with the result and their declaration, before the month&apos;s forms are sent.
              </MessageBarBody>
            </MessageBar>
          )}
        </div>
        <DataTable<FormFRegisterRow>
          ariaLabel="Form F register"
          rows={data?.rows}
          loading={register.isLoading}
          error={register.error}
          onRetry={() => void register.refetch()}
          getRowId={(r) => r.id}
          size="small"
          columns={[
            {
              key: 'form',
              header: 'Form',
              width: 150,
              render: (r) => (
                <span>
                  <Link
                    onClick={() => void navigate({ to: '/radiology/studies/$studyId', params: { studyId: r.studyId } })}
                  >
                    {r.formNumber}
                  </Link>
                  <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                    {r.procedureDate ? formatDate(r.procedureDate) : 'Not completed'}
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
                    {r.uhid} · {r.ageYears} years · {r.livingSons} son(s), {r.livingDaughters} daughter(s)
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'pregnancy',
              header: 'Pregnancy',
              priority: 'low',
              render: (r) =>
                r.weeksOfPregnancy
                  ? `${r.weeksOfPregnancy} weeks`
                  : r.lastMenstrualPeriod
                    ? `LMP ${formatDate(r.lastMenstrualPeriod)}`
                    : '',
            },
            { key: 'referred', header: 'Referred by', priority: 'low', render: (r) => r.referredBy },
            {
              key: 'indications',
              header: 'Indications',
              priority: 'low',
              render: (r) => r.indications.map((i) => formFIndicationLabel[i]).join('; '),
            },
            { key: 'result', header: 'Result', render: (r) => r.result ?? '' },
            {
              key: 'mtp',
              header: 'MTP indicated',
              priority: 'low',
              render: (r) => (r.status === 'Completed' ? (r.mtpIndicated ? 'Yes' : 'No') : ''),
            },
            { key: 'doctor', header: 'Doctor', priority: 'low', render: (r) => r.doctorName ?? '' },
          ]}
          emptyTitle="No Form F this month"
          emptyDescription="Ultrasounds in pregnancy appear here once the woman's Form F details are recorded."
        />
      </Section>
      <ConfirmDialog
        open={confirmExport}
        title="Download the register?"
        confirmLabel="Download"
        busy={exportCsv.isPending}
        onConfirm={() => exportCsv.mutate()}
        onCancel={() => setConfirmExport(false)}
      >
        The file contains the women&apos;s names, UHIDs and the results of their scans. The download is recorded in the
        audit log with your name. Keep it on a secure computer and delete it once it has been sent.
      </ConfirmDialog>
    </Page>
  );
}
