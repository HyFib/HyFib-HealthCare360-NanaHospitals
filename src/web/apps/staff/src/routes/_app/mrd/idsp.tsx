import {
  Badge,
  Button,
  Caption1,
  Link,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, ArrowDownloadRegular, ArrowLeftRegular, DeleteRegular, SendRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime } from '@hc360/core';
import { ConfirmDialog, DataTable, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { AddCaseDialog, DiscardCaseDialog, ReportWeekDialog } from '@/modules/mrd/IdspDialogs';
import {
  caseSourceLabel,
  caseStatusTone,
  IDSP_PERMISSION,
  idspApi,
  idspFormLabel,
  idspQueries,
  weekLabel,
  type IdspCaseRow,
  type IdspForm,
  type IdspFormWeek,
  type IdspWeekDto,
  type IdspWeekSummary,
} from '@/modules/mrd/idsp';

interface IdspSearch {
  year?: number;
  week?: number;
  case?: string;
}

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export const Route = createFileRoute('/_app/mrd/idsp')({
  validateSearch: (s: Record<string, unknown>): IdspSearch => {
    const year = Number(s.year);
    const week = Number(s.week);
    return {
      year: Number.isInteger(year) && year >= 2000 ? year : undefined,
      week: Number.isInteger(week) && week >= 1 && week <= 53 ? week : undefined,
      case: typeof s.case === 'string' && UUID.test(s.case) ? s.case : undefined,
    };
  },
  component: () => (
    <RequirePermission permission={IDSP_PERMISSION} what="the disease surveillance register">
      <IdspPage />
    </RequirePermission>
  ),
});

/**
 * Notifiable diseases (IDSP): the cases the hospital's diagnoses and lab results raised, each week's P and L forms
 * to upload to IHIP and record as reported (a nil report too), and the outbreak-prone cases to tell the district
 * surveillance unit about at once.
 */
function IdspPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const [adding, setAdding] = useState(false);
  const selected = search.year && search.week ? { year: search.year, week: search.week } : null;

  return (
    <Page>
      <PageHeader
        title="Notifiable diseases (IDSP)"
        subtitle="Cases from diagnoses go on the week's P form, cases confirmed by a released test on the L form. Upload each week's forms to IHIP by the Monday after, a nil report too, and record them here."
        actions={
          <Button icon={<AddRegular />} onClick={() => setAdding(true)}>
            Add a case
          </Button>
        }
      />
      {search.case && (
        <CaseNotice id={search.case} onDone={() => void navigate({ search: { ...search, case: undefined } })} />
      )}
      {selected ? (
        <WeekView year={selected.year} week={selected.week} onBack={() => void navigate({ search: {} })} />
      ) : (
        <Overview onOpen={(w) => void navigate({ search: { year: w.year, week: w.week } })} />
      )}
      {adding && <AddCaseDialog onClose={() => setAdding(false)} />}
    </Page>
  );
}

/** The case an outbreak alert links to, with what to do about it. */
function CaseNotice({ id, onDone }: { id: string; onDone: () => void }) {
  const row = useQuery(idspQueries.case(id));
  const [discarding, setDiscarding] = useState(false);
  if (!row.data) {
    return null;
  }
  const c = row.data;
  return (
    <>
      <MessageBar intent={c.status === 'Open' && c.immediate ? 'warning' : 'info'} layout="multiline">
        <MessageBarBody>
          <MessageBarTitle>
            {c.disease}: {c.patientName} · {c.uhid}
          </MessageBarTitle>
          {c.status === 'Discarded'
            ? `Discarded: ${c.discardReason ?? ''}`
            : c.immediate
              ? 'Outbreak-prone: inform the district surveillance unit within 24 hours, then report it on the week’s form.'
              : 'On the week’s form, to report to IHIP.'}{' '}
          {c.status === 'Open' && <Link onClick={() => setDiscarding(true)}>Discard the case</Link>}{' '}
          <Link onClick={onDone}>Dismiss</Link>
        </MessageBarBody>
      </MessageBar>
      {discarding && <DiscardCaseDialog row={c} onClose={() => setDiscarding(false)} />}
    </>
  );
}

function Overview({ onOpen }: { onOpen: (week: IdspWeekSummary) => void }) {
  const overview = useQuery(idspQueries.overview());
  const data = overview.data;
  const overdue = data?.weeks.filter((w) => w.overdue) ?? [];

  return (
    <>
      {overdue.length > 0 && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Reports overdue</MessageBarTitle>
            {overdue.map((w) => weekLabel(w.year, w.week)).join(', ')}: send the forms to IHIP and record them, a nil
            report where there were no cases.
          </MessageBarBody>
        </MessageBar>
      )}
      {data && data.immediate.length > 0 && (
        <Section title="Outbreak-prone cases, last two weeks">
          <CaseTable rows={data.immediate} ariaLabel="Outbreak-prone cases" form={null} />
        </Section>
      )}
      <Section title="Weeks" flush>
        <DataTable<IdspWeekSummary>
          ariaLabel="IDSP weeks"
          rows={data?.weeks}
          loading={overview.isLoading}
          error={overview.error}
          onRetry={() => void overview.refetch()}
          getRowId={(w) => `${w.year}-${w.week}`}
          onRowClick={onOpen}
          size="small"
          columns={[
            {
              key: 'week',
              header: 'Week',
              width: 170,
              render: (w) => (
                <span>
                  <Link onClick={() => onOpen(w)}>{weekLabel(w.year, w.week)}</Link>
                  <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                    {formatDateOnly(w.monday)} to {formatDateOnly(w.sunday)}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'p',
              header: 'P form',
              render: (w) => <FormState cases={w.presumptiveCases} reported={w.presumptiveReported} />,
            },
            {
              key: 'l',
              header: 'L form',
              render: (w) => <FormState cases={w.laboratoryCases} reported={w.laboratoryReported} />,
            },
            {
              key: 'due',
              header: 'Due',
              width: 170,
              render: (w) => (
                <span>
                  {formatDateOnly(w.dueOn)}
                  {w.overdue && (
                    <Badge size="small" appearance="filled" color="danger" style={{ marginLeft: 6 }}>
                      Overdue
                    </Badge>
                  )}
                  {!w.overdue && w.notReported > 0 && (w.presumptiveReported || w.laboratoryReported) && (
                    <Badge size="small" appearance="outline" color="warning" style={{ marginLeft: 6 }}>
                      {w.notReported} added since
                    </Badge>
                  )}
                </span>
              ),
            },
          ]}
          emptyTitle="No weeks"
          emptyDescription="The current week and the eight before it appear here."
        />
      </Section>
    </>
  );
}

function FormState({ cases, reported }: { cases: number; reported: boolean }) {
  return (
    <span>
      {cases === 0 ? 'No cases' : cases === 1 ? 'One case' : `${cases} cases`}
      {reported && (
        <Badge size="small" appearance="tint" color="success" style={{ marginLeft: 6 }}>
          Reported
        </Badge>
      )}
    </span>
  );
}

function WeekView({ year, week, onBack }: { year: number; week: number; onBack: () => void }) {
  const query = useQuery(idspQueries.week(year, week));
  const [reporting, setReporting] = useState<IdspForm | null>(null);
  const data = query.data;

  return (
    <>
      <div>
        <Button appearance="subtle" icon={<ArrowLeftRegular />} onClick={onBack}>
          All weeks
        </Button>
      </div>
      {query.error && (
        <MessageBar intent="error">
          <MessageBarBody>The week could not be loaded.</MessageBarBody>
        </MessageBar>
      )}
      {data && (
        <>
          <Text size={500} weight="semibold">
            Week {weekLabel(data.year, data.week)}: {formatDateOnly(data.monday)} to {formatDateOnly(data.sunday)}, due{' '}
            {formatDateOnly(data.dueOn)}
          </Text>
          <FormSection week={data} sheet={data.presumptive} onReport={() => setReporting('Presumptive')} />
          <FormSection week={data} sheet={data.laboratory} onReport={() => setReporting('Laboratory')} />
          {data.discarded.length > 0 && (
            <Section title="Discarded">
              <CaseTable rows={data.discarded} ariaLabel="Discarded cases" form={null} />
            </Section>
          )}
          {reporting && <ReportWeekDialog week={data} form={reporting} onClose={() => setReporting(null)} />}
        </>
      )}
    </>
  );
}

function FormSection({ week, sheet, onReport }: { week: IdspWeekDto; sheet: IdspFormWeek; onReport: () => void }) {
  const notify = useNotify();
  const [confirmExport, setConfirmExport] = useState(false);
  const letter = sheet.form === 'Presumptive' ? 'P' : 'L';
  const exportCsv = useMutation({
    mutationFn: () => idspApi.export(week.year, week.week, sheet.form),
    onSuccess: (blob) => {
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `idsp-${letter}-${weekLabel(week.year, week.week)}.csv`;
      document.body.appendChild(link);
      link.click();
      link.remove();
      setTimeout(() => URL.revokeObjectURL(url), 10_000);
      setConfirmExport(false);
      notify.success('Line list downloaded', 'The download was recorded in the audit log.');
    },
    onError: (error) => {
      setConfirmExport(false);
      notify.error(error, 'Could not download the line list');
    },
  });

  const status = sheet.report
    ? `Reported on ${formatDateOnly(sheet.report.reportedOn)} with ${sheet.report.caseCount === 0 ? 'no cases (nil report)' : `${sheet.report.caseCount} cases`}${sheet.report.reference ? `, IHIP ${sheet.report.reference}` : ''}.`
    : week.ended
      ? 'Not yet reported.'
      : 'The week is not over: report it from its Monday after.';

  return (
    <Section
      title={idspFormLabel[sheet.form]}
      actions={
        <>
          <Button
            icon={<ArrowDownloadRegular />}
            disabled={exportCsv.isPending || sheet.cases.length === 0}
            onClick={() => setConfirmExport(true)}
          >
            Download CSV
          </Button>
          <Button appearance="primary" icon={<SendRegular />} disabled={!week.ended} onClick={onReport}>
            {sheet.report ? 'Report again' : 'Record report'}
          </Button>
        </>
      }
    >
      <Caption1 block style={{ marginBottom: 8 }}>
        {status}
        {sheet.overdue && (
          <Badge size="small" appearance="filled" color="danger" style={{ marginLeft: 6 }}>
            Overdue
          </Badge>
        )}
        {sheet.report && sheet.notReported > 0 && (
          <Badge size="small" appearance="outline" color="warning" style={{ marginLeft: 6 }}>
            {sheet.notReported} added since: report again
          </Badge>
        )}
      </Caption1>
      <CaseTable rows={sheet.cases} ariaLabel={idspFormLabel[sheet.form]} form={sheet.form} />
      <ConfirmDialog
        open={confirmExport}
        title="Download the line list?"
        confirmLabel="Download"
        busy={exportCsv.isPending}
        onConfirm={() => exportCsv.mutate()}
        onCancel={() => setConfirmExport(false)}
      >
        The file names the patients, with their addresses and mobile numbers, for the IHIP upload. The download is
        recorded in the audit log with your name. Delete it once uploaded.
      </ConfirmDialog>
    </Section>
  );
}

function CaseTable({ rows, ariaLabel, form }: { rows: IdspCaseRow[]; ariaLabel: string; form: IdspForm | null }) {
  const [discarding, setDiscarding] = useState<IdspCaseRow | null>(null);
  return (
    <>
      <DataTable<IdspCaseRow>
        ariaLabel={ariaLabel}
        rows={rows}
        getRowId={(r) => r.id}
        size="small"
        columns={[
          {
            key: 'disease',
            header: 'Disease',
            render: (r) => (
              <span>
                <Text weight="semibold">{r.disease}</Text>
                {r.immediate && (
                  <Badge size="small" appearance="filled" color="warning" style={{ marginLeft: 6 }}>
                    Outbreak-prone
                  </Badge>
                )}
                <Caption1 block>{caseSourceLabel[r.source]}</Caption1>
              </span>
            ),
          },
          {
            key: 'patient',
            header: 'Patient',
            render: (r) => (
              <span>
                <Text>{r.patientName}</Text>
                <Caption1 block>
                  {[r.uhid, r.sex, r.ageYears !== null ? `${r.ageYears} years` : null].filter(Boolean).join(' · ')}
                </Caption1>
              </span>
            ),
          },
          { key: 'place', header: 'Address', priority: 'low', render: (r) => r.address ?? r.district ?? '' },
          {
            key: 'onset',
            header: 'Onset',
            priority: 'low',
            render: (r) => (r.onsetDate ? formatDateOnly(r.onsetDate) : ''),
          },
          {
            key: 'when',
            header: form === 'Laboratory' ? 'Result' : 'Diagnosed',
            render: (r) =>
              form === 'Laboratory' || (form === null && r.confirmedAt) ? (
                <span>
                  {r.testName}: {r.testResult ?? ''}
                  <Caption1 block>{r.confirmedAt ? formatDateTime(r.confirmedAt) : ''}</Caption1>
                </span>
              ) : (
                <span>
                  {r.icdCode ?? ''}
                  <Caption1 block>{r.presumedAt ? formatDateTime(r.presumedAt) : ''}</Caption1>
                </span>
              ),
          },
          {
            key: 'status',
            header: 'Status',
            width: 120,
            render: (r) => <StatusBadge status={r.status} tone={caseStatusTone[r.status]} label={r.status} />,
          },
        ]}
        rowActions={(r) =>
          r.status === 'Open' && (
            <Button size="small" icon={<DeleteRegular />} onClick={() => setDiscarding(r)}>
              Discard
            </Button>
          )
        }
        emptyTitle="No cases"
        emptyDescription="A nil report is still sent: record it once the week is over."
      />
      {discarding && <DiscardCaseDialog row={discarding} onClose={() => setDiscarding(null)} />}
    </>
  );
}
