import { Badge, Button, Caption1, makeStyles, Select, Text, tokens } from '@fluentui/react-components';
import { AddRegular, ClipboardTaskListLtrRegular } from '@fluentui/react-icons';
import { formatDate } from '@hc360/core';
import { DataTable, FilterBar, KpiCard, Page, PageHeader } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { Can, usePermission } from '@/lib/permissions';
import { LAB_PERMISSIONS } from '@/modules/lab/api';
import { searchFlag, searchOneOf, searchPage } from '@/modules/lab/common/search';
import { EqasStepDialog, ScheduleEqasRoundDialog } from '@/modules/lab/EqasDialogs';
import { eqasQueries, nextStep, performanceTone, type EqasPerformance, type EqasRoundDto } from '@/modules/lab/eqas';

const PERFORMANCES = ['Acceptable', 'Warning', 'Unacceptable'] as const;

interface EqasSearch {
  performance?: EqasPerformance;
  overdue?: boolean;
  needsCapa?: boolean;
  page?: number;
}

export const Route = createFileRoute('/_app/lab/eqas')({
  validateSearch: (s: Record<string, unknown>): EqasSearch => ({
    performance: searchOneOf(s.performance, PERFORMANCES),
    overdue: searchFlag(s.overdue),
    needsCapa: searchFlag(s.needsCapa),
    page: searchPage(s.page),
  }),
  component: EqasPage,
});

const useStyles = makeStyles({
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(180px, 1fr))' },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px' },
  muted: { color: tokens.colorNeutralForeground3 },
  red: { color: tokens.colorPaletteRedForeground1 },
});

const stepLabel = {
  report: 'Record result',
  evaluate: 'Record evaluation',
  capa: 'Link CAPA',
  accept: 'Accept',
  done: '',
};

/**
 * The external quality assessment register (NABL 112, ISO 15189 7.3.7.3): every round per analyte and scheme with the
 * result reported, the provider's assigned value and SD, the z-score and its band, and the corrective action an
 * unacceptable round needs before it is accepted.
 */
function EqasPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canManage = usePermission(LAB_PERMISSIONS.qc);
  const canAccept = usePermission(LAB_PERMISSIONS.verify);
  const summary = useQuery(eqasQueries.summary());
  const rounds = useQuery(eqasQueries.rounds(search));
  const [scheduling, setScheduling] = useState(false);
  const [open, setOpen] = useState<EqasRoundDto | null>(null);
  const may = (round: EqasRoundDto) => {
    const step = nextStep(round);
    return step === 'done' ? false : step === 'accept' ? canAccept : canManage;
  };

  return (
    <Page>
      <PageHeader
        title="External quality assessment"
        subtitle="Proficiency testing rounds, z-scores and corrective actions"
        actions={
          <Can permission={LAB_PERMISSIONS.qc}>
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setScheduling(true)}>
              Schedule round
            </Button>
          </Can>
        }
      />
      <div className={styles.kpis}>
        <KpiCard
          label="Overdue"
          value={
            summary.data ? (
              <span className={summary.data.overdue > 0 ? styles.red : undefined}>{summary.data.overdue}</span>
            ) : (
              '…'
            )
          }
          hint={summary.data ? `${summary.data.dueThisWeek} due this week` : undefined}
          icon={<ClipboardTaskListLtrRegular />}
          onClick={() => void navigate({ search: { overdue: true } })}
        />
        <KpiCard
          label="Unacceptable without CAPA"
          value={
            summary.data ? (
              <span className={summary.data.needCapa > 0 ? styles.red : undefined}>{summary.data.needCapa}</span>
            ) : (
              '…'
            )
          }
          onClick={() => void navigate({ search: { needsCapa: true } })}
        />
        <KpiCard
          label="Last 12 months"
          value={summary.data ? `${summary.data.acceptable} acceptable` : '…'}
          hint={
            summary.data ? `${summary.data.warning} warning · ${summary.data.unacceptable} unacceptable` : undefined
          }
        />
      </div>
      <FilterBar>
        <Select
          aria-label="Performance"
          value={search.performance ?? ''}
          onChange={(_, data) =>
            void navigate({
              search: (s) => ({
                ...s,
                performance: (data.value || undefined) as EqasPerformance | undefined,
                page: undefined,
              }),
            })
          }
        >
          <option value="">Any performance</option>
          {PERFORMANCES.map((p) => (
            <option key={p} value={p}>
              {p}
            </option>
          ))}
        </Select>
      </FilterBar>
      <DataTable
        ariaLabel="EQAS rounds"
        rows={rounds.data?.items}
        loading={rounds.isLoading}
        error={rounds.error}
        onRetry={() => void rounds.refetch()}
        getRowId={(round) => round.id}
        size="small"
        columns={[
          {
            key: 'test',
            header: 'Analyte and round',
            render: (round: EqasRoundDto) => (
              <span className={styles.cell}>
                <Text weight="semibold">{round.testName}</Text>
                <Caption1 className={styles.muted}>
                  {round.roundCode} · {round.schemeName}
                </Caption1>
              </span>
            ),
          },
          {
            key: 'due',
            header: 'Due',
            width: 120,
            render: (round: EqasRoundDto) => (
              <span className={round.isOverdue ? styles.red : undefined}>
                {formatDate(round.dueDate)}
                {round.isOverdue && ' (overdue)'}
              </span>
            ),
          },
          {
            key: 'result',
            header: 'Reported / assigned',
            width: 170,
            priority: 'low',
            render: (round: EqasRoundDto) =>
              round.reportedValue === null
                ? '—'
                : `${round.reportedValue}${round.assignedValue !== null ? ` / ${round.assignedValue} ± ${round.assignedSd}` : ''}`,
          },
          {
            key: 'z',
            header: 'z-score',
            width: 150,
            render: (round: EqasRoundDto) =>
              round.performance ? (
                <Badge appearance="tint" color={performanceTone[round.performance]}>
                  {round.zScore?.toFixed(2)} {round.performance}
                </Badge>
              ) : (
                '—'
              ),
          },
          {
            key: 'capa',
            header: 'CAPA',
            width: 150,
            priority: 'low',
            render: (round: EqasRoundDto) =>
              round.capaNumber
                ? `${round.capaNumber}${round.capaStatus ? ` (${round.capaStatus})` : ''}`
                : round.needsCapa
                  ? 'Needed'
                  : '—',
          },
          {
            key: 'next',
            header: 'Next',
            width: 160,
            render: (round: EqasRoundDto) =>
              may(round) ? (
                <Button size="small" onClick={() => setOpen(round)}>
                  {stepLabel[nextStep(round)]}
                </Button>
              ) : round.acceptedAt ? (
                <Caption1 className={styles.muted}>Accepted {formatDate(round.acceptedAt)}</Caption1>
              ) : null,
          },
        ]}
      />
      {scheduling && <ScheduleEqasRoundDialog onClose={() => setScheduling(false)} />}
      {open && <EqasStepDialog key={open.id} round={open} onClose={() => setOpen(null)} />}
    </Page>
  );
}
