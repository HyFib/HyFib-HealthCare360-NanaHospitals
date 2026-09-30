import { makeStyles, tokens } from '@fluentui/react-components';
import {
  BedRegular,
  DocumentAddRegular,
  DocumentTextRegular,
  HeartPulseRegular,
  StethoscopeRegular,
  SyringeRegular,
  VideoRegular,
  WarningRegular,
} from '@fluentui/react-icons';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import type { ReactElement } from 'react';
import { ButtonLink, CardLink } from '@/components/links';
import { ListRow, RowCard } from '@/components/ListRow';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, LoadingCards } from '@/components/States';
import { StatusPill } from '@/components/StatusPill';
import { useT } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import type { EncounterType, PortalHealthRecord } from '@/lib/types';
import { recordQueries } from '@/modules/records/api';
import { ALLERGY_SEVERITY, ALLERGY_TYPE, PROBLEM_STATUS, splitLines, VISIT_TYPE } from '@/modules/records/labels';
import { ReportCard } from '@/modules/reports/ReportParts';
import { useActiveProfile } from '@/shell/profileContext';

export const Route = createFileRoute('/_auth/records/')({
  component: RecordsPage,
});

const VISIT_ICON: Record<EncounterType, ReactElement> = {
  OutPatient: <StethoscopeRegular />,
  InPatient: <BedRegular />,
  Emergency: <HeartPulseRegular />,
  Telehealth: <VideoRegular />,
  HealthCheckup: <HeartPulseRegular />,
};

const useStyles = makeStyles({
  list: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: '10px' },
  item: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: '12px',
    paddingBottom: '10px',
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
    ':last-child': { borderBottom: 'none', paddingBottom: 0 },
  },
  itemText: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  itemTitle: { fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase400, overflowWrap: 'anywhere' },
  itemMeta: { color: tokens.colorNeutralForeground2, overflowWrap: 'anywhere' },
  pills: { display: 'flex', flexWrap: 'wrap', gap: '4px', justifyContent: 'flex-end', flexShrink: 0 },
  none: { margin: 0, color: tokens.colorNeutralForeground2 },
  cards: { display: 'flex', flexDirection: 'column', gap: '10px' },
  note: { margin: 0, color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase200 },
  allergyIcon: { color: tokens.colorStatusDangerForeground1, fontSize: '20px', flexShrink: 0, marginTop: '2px' },
  vaccineIcon: { color: tokens.colorBrandForeground1, fontSize: '20px', flexShrink: 0, marginTop: '2px' },
  allergyRow: { display: 'flex', gap: '10px', minWidth: 0 },
});

function RecordsPage() {
  const styles = useStyles();
  const t = useT();
  const { profile } = useActiveProfile();
  const record = useQuery(recordQueries.summary(profile.id));

  return (
    <PortalPage
      title={t('records.title')}
      subtitle={t('records.subtitle')}
      actions={
        <ButtonLink to="/records/request" appearance="secondary" icon={<DocumentAddRegular />}>
          {t('records.request')}
        </ButtonLink>
      }
    >
      {record.isPending ? (
        <LoadingCards count={4} />
      ) : record.isError ? (
        <ErrorBlock error={record.error} onRetry={() => void record.refetch()} />
      ) : (
        <RecordSummary record={record.data} />
      )}
      <p className={styles.note}>{t('records.note')}</p>
    </PortalPage>
  );
}

function RecordSummary({ record }: { record: PortalHealthRecord }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();

  return (
    <>
      <ReportCard title={t('records.allergies')}>
        {record.allergies.length === 0 ? (
          <p className={styles.none}>{t('records.allergies.none')}</p>
        ) : (
          <ul className={styles.list}>
            {record.allergies.map((allergy) => {
              const severity = ALLERGY_SEVERITY[allergy.severity];
              return (
                <li key={allergy.id} className={styles.item}>
                  <span className={styles.allergyRow}>
                    <WarningRegular className={styles.allergyIcon} aria-hidden />
                    <span className={styles.itemText}>
                      <span className={styles.itemTitle}>{allergy.allergen}</span>
                      <span className={styles.itemMeta}>
                        {[
                          t(ALLERGY_TYPE[allergy.type]),
                          allergy.reaction ? t('records.allergy.reaction', { reaction: allergy.reaction }) : null,
                        ]
                          .filter(Boolean)
                          .join(' · ')}
                      </span>
                    </span>
                  </span>
                  <span className={styles.pills}>
                    <StatusPill tone={severity.tone}>{t(severity.key)}</StatusPill>
                  </span>
                </li>
              );
            })}
          </ul>
        )}
      </ReportCard>

      <ReportCard title={t('records.discharge')}>
        {record.dischargeSummaries.length === 0 ? (
          <p className={styles.none}>{t('records.discharge.none')}</p>
        ) : (
          <div className={styles.cards}>
            {record.dischargeSummaries.map((summary) => (
              <CardLink key={summary.id} to="/records/discharge/$summaryId" params={{ summaryId: summary.id }}>
                <ListRow
                  icon={<DocumentTextRegular />}
                  title={
                    summary.dischargedAt
                      ? t('records.discharge.item', { date: f.date(summary.dischargedAt) })
                      : t('records.discharge.itemUndated')
                  }
                  subtitle={splitLines(summary.diagnoses).slice(0, 2).join(', ') || undefined}
                />
              </CardLink>
            ))}
          </div>
        )}
      </ReportCard>

      <ReportCard title={t('records.problems')}>
        {record.problems.length === 0 ? (
          <p className={styles.none}>{t('records.problems.none')}</p>
        ) : (
          <ul className={styles.list}>
            {record.problems.map((problem) => (
              <li key={problem.id} className={styles.item}>
                <span className={styles.itemText}>
                  <span className={styles.itemTitle}>{problem.title}</span>
                  {problem.onsetDate && (
                    <span className={styles.itemMeta}>
                      {t('records.problem.since', { date: f.calendarDate(problem.onsetDate) })}
                    </span>
                  )}
                </span>
                <span className={styles.pills}>
                  <StatusPill tone={problem.status === 'Resolved' ? 'success' : 'brand'}>
                    {t(PROBLEM_STATUS[problem.status])}
                  </StatusPill>
                  {problem.type === 'Provisional' && (
                    <StatusPill tone="neutral">{t('records.problem.provisional')}</StatusPill>
                  )}
                </span>
              </li>
            ))}
          </ul>
        )}
      </ReportCard>

      <ReportCard title={t('records.visits')}>
        {record.recentEncounters.length === 0 ? (
          <p className={styles.none}>{t('records.visits.none')}</p>
        ) : (
          <div className={styles.cards}>
            {record.recentEncounters.map((visit) => (
              <RowCard key={visit.id}>
                <ListRow
                  icon={VISIT_ICON[visit.type]}
                  tone="neutral"
                  title={`${t(VISIT_TYPE[visit.type])} · ${f.date(visit.startedAt)}`}
                  subtitle={[visit.doctor, visit.department].filter(Boolean).join(' · ') || undefined}
                  meta={
                    [
                      visit.diagnoses.length
                        ? t('records.visit.diagnoses', { list: visit.diagnoses.join(', ') })
                        : null,
                      visit.followUpDate
                        ? t('records.visit.followUp', { date: f.calendarDate(visit.followUpDate) })
                        : null,
                    ]
                      .filter(Boolean)
                      .join(' · ') || undefined
                  }
                  chevron={false}
                />
              </RowCard>
            ))}
          </div>
        )}
      </ReportCard>

      <ReportCard title={t('records.vaccines')}>
        {record.immunizations.length === 0 ? (
          <p className={styles.none}>{t('records.vaccines.none')}</p>
        ) : (
          <ul className={styles.list}>
            {record.immunizations.map((vaccine, index) => (
              <li key={`${vaccine.vaccineName}-${vaccine.administeredOn}-${index}`} className={styles.item}>
                <span className={styles.allergyRow}>
                  <SyringeRegular className={styles.vaccineIcon} aria-hidden />
                  <span className={styles.itemText}>
                    <span className={styles.itemTitle}>
                      {vaccine.vaccineName}
                      {vaccine.doseNumber ? ` · ${t('records.vaccine.dose', { number: vaccine.doseNumber })}` : ''}
                    </span>
                    <span className={styles.itemMeta}>
                      {t('records.vaccine.given', { date: f.calendarDate(vaccine.administeredOn) })}
                    </span>
                    {vaccine.nextDueOn && (
                      <span className={styles.itemMeta}>
                        {t('records.vaccine.nextDue', { date: f.calendarDate(vaccine.nextDueOn) })}
                      </span>
                    )}
                  </span>
                </span>
              </li>
            ))}
          </ul>
        )}
      </ReportCard>
    </>
  );
}
