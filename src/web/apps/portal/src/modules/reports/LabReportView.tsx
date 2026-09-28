import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { CheckmarkCircleRegular, ErrorCircleRegular, InfoRegular, WarningRegular } from '@fluentui/react-icons';
import { InfoList } from '@/components/InfoList';
import { Notice } from '@/components/States';
import { StatusPill } from '@/components/StatusPill';
import { useT } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import type { LabReportResultLine, LabReportTest, LabReportView as LabReport } from '@/lib/types';
import { flagInfo, parseRange, parseResultNumber, SECTION_KEY, summarizeResults, SUSCEPTIBILITY_KEY } from './labResults';
import { RangeBar, ReportCard, Signatories, VerificationCode } from './ReportParts';
import { reportVerifyUrl } from './verifyLink';

const useStyles = makeStyles({
  summary: { display: 'flex', alignItems: 'center', gap: '10px', fontSize: tokens.fontSizeBase400, fontWeight: tokens.fontWeightSemibold },
  summaryIcon: { fontSize: '28px', flexShrink: 0 },
  good: { color: tokens.colorStatusSuccessForeground1 },
  warn: { color: tokens.colorStatusWarningForeground3 },
  test: { display: 'flex', flexDirection: 'column', gap: '8px', paddingTop: '12px', borderTop: `1px solid ${tokens.colorNeutralStroke2}` },
  testFirst: { paddingTop: 0, borderTop: 'none' },
  testName: { margin: 0, fontSize: tokens.fontSizeBase400, fontWeight: tokens.fontWeightSemibold },
  testMeta: { margin: 0, color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  lines: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: '8px' },
  line: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  lineWarn: { backgroundColor: tokens.colorStatusWarningBackground1 },
  lineDanger: { backgroundColor: tokens.colorStatusDangerBackground1 },
  lineHead: { display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', gap: '8px', flexWrap: 'wrap' },
  lineName: { fontWeight: tokens.fontWeightSemibold, overflowWrap: 'anywhere' },
  group: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  value: { fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold, overflowWrap: 'anywhere' },
  unit: { fontSize: tokens.fontSizeBase300, fontWeight: tokens.fontWeightRegular, color: tokens.colorNeutralForeground2 },
  detail: { color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase300, margin: 0 },
  pending: { color: tokens.colorNeutralForeground3, fontStyle: 'italic' },
  table: { width: '100%', borderCollapse: 'collapse' },
  th: { textAlign: 'start', padding: '6px 8px', color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  td: { padding: '6px 8px', borderBottom: `1px solid ${tokens.colorNeutralStroke3}` },
  note: { margin: 0, whiteSpace: 'pre-line' },
  changes: { margin: 0, paddingLeft: '20px', display: 'flex', flexDirection: 'column', gap: '4px' },
});

/** A released lab report: a one-line summary first, then each result with its range and meaning in plain words. */
export function LabReportView({ report }: { report: LabReport }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const summary = summarizeResults(report.sections);

  return (
    <>
      {summary.critical && <Notice intent="error">{t('reports.critical')}</Notice>}
      {summary.total > 0 && (
        <ReportCard>
          <p className={mergeClasses(styles.summary, summary.outOfRange > 0 ? styles.warn : styles.good)} style={{ margin: 0 }}>
            {summary.outOfRange > 0 ? (
              <WarningRegular className={styles.summaryIcon} aria-hidden />
            ) : (
              <CheckmarkCircleRegular className={styles.summaryIcon} aria-hidden />
            )}
            {summary.outOfRange > 0
              ? t('lab.summary.someOut', { count: summary.outOfRange, total: summary.total })
              : t('lab.summary.allNormal', { total: summary.total })}
          </p>
          <p className={styles.detail}>
            <InfoRegular aria-hidden /> {t('reports.doctorNote')}
          </p>
        </ReportCard>
      )}

      <ReportCard>
        <InfoList
          items={[
            { label: t('reports.numberLabel'), value: report.reportNumber },
            { label: t('reports.releasedOn'), value: f.dateTime(report.releasedAt), hideEmpty: !report.releasedAt },
            { label: t('reports.ordered'), value: f.dateTime(report.orderedAt) },
            { label: t('reports.referredBy'), value: report.referredBy, hideEmpty: true },
            { label: t('common.hospital'), value: report.letterhead.facilityName },
          ]}
        />
        {report.isPartial && <Notice intent="info">{t('reports.partial')}</Notice>}
      </ReportCard>

      {report.sections.map((section) => (
        <ReportCard key={section.section} title={t(SECTION_KEY[section.section])}>
          {section.tests.map((test, index) => (
            <TestBlock key={test.orderItemId} test={test} first={index === 0} />
          ))}
        </ReportCard>
      ))}

      {report.amendments.length > 0 && (
        <ReportCard title={t('reports.changes')}>
          <ul className={styles.changes}>
            {report.amendments.map((amendment) => (
              <li key={amendment.revision}>
                {t('reports.change', { revision: amendment.revision, date: f.dateTime(amendment.amendedAt), reason: amendment.reason })}
              </li>
            ))}
          </ul>
        </ReportCard>
      )}

      <Signatories signatories={report.signatories} />
      <VerificationCode code={report.verificationCode} url={reportVerifyUrl('lab', report, window.location.origin)} />
    </>
  );
}

function TestBlock({ test, first }: { test: LabReportTest; first: boolean }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const meta = [
    test.collectedAt ? t('lab.collected', { date: f.dateTime(test.collectedAt) }) : null,
    test.referralLabName ? t('lab.referral', { lab: test.referralLabName }) : null,
    test.method ? t('lab.method', { method: test.method }) : null,
  ].filter(Boolean);

  return (
    <div className={mergeClasses(styles.test, first && styles.testFirst)}>
      <h3 className={styles.testName}>{test.name}</h3>
      {meta.length > 0 && <p className={styles.testMeta}>{meta.join(' · ')}</p>}
      <ul className={styles.lines}>
        {test.results.map((line) => (
          <ResultLine key={line.resultId} line={line} />
        ))}
      </ul>
      {test.interpretation && (
        <div>
          <strong>{t('lab.note')}</strong>
          <p className={styles.note}>{test.interpretation}</p>
        </div>
      )}
    </div>
  );
}

function ResultLine({ line }: { line: LabReportResultLine }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const info = flagInfo(line.flag);
  const numeric = parseResultNumber(line.value);
  const range = parseRange(line.referenceText);
  const hasValue = line.value !== null && line.value.trim() !== '';
  const microbiology = line.organism || line.colonyCount || line.susceptibilities.length > 0;

  return (
    <li className={mergeClasses(styles.line, info?.severity === 1 && styles.lineWarn, info?.severity === 2 && styles.lineDanger)}>
      <div className={styles.lineHead}>
        <span>
          <span className={styles.lineName}>{line.name}</span>
          {line.groupName && <span className={styles.group}> · {line.groupName}</span>}
        </span>
        {info && hasValue && (
          <StatusPill tone={info.tone} icon={info.severity === 2 ? <ErrorCircleRegular /> : info.severity === 1 ? <WarningRegular /> : <CheckmarkCircleRegular />}>
            {t(info.key)}
          </StatusPill>
        )}
      </div>
      {hasValue ? (
        <span className={styles.value}>
          {line.value} {line.unit && <span className={styles.unit}>{line.unit}</span>}
        </span>
      ) : (
        <span className={styles.pending}>{t('lab.pending')}</span>
      )}
      {line.referenceText && (
        <p className={styles.detail}>
          {t('lab.range')}: {line.referenceText}
          {line.unit && !line.referenceText.includes(line.unit) ? ` ${line.unit}` : ''}
        </p>
      )}
      {numeric !== null && range && <RangeBar value={numeric} range={range} />}
      {line.previousValue !== null && line.previousResultAt && (
        <p className={styles.detail}>
          {t('lab.previous', { value: `${f.number(line.previousValue)}${line.unit ? ` ${line.unit}` : ''}`, date: f.date(line.previousResultAt) })}
        </p>
      )}
      {line.remarks && <p className={styles.detail}>{line.remarks}</p>}
      {microbiology && <Microbiology line={line} />}
    </li>
  );
}

function Microbiology({ line }: { line: LabReportResultLine }) {
  const styles = useStyles();
  const t = useT();
  return (
    <>
      {(line.organism || line.colonyCount) && (
        <InfoList
          items={[
            { label: t('lab.organism'), value: line.organism, hideEmpty: true },
            { label: t('lab.colonyCount'), value: line.colonyCount, hideEmpty: true },
          ]}
        />
      )}
      {line.susceptibilities.length > 0 && (
        <table className={styles.table}>
          <caption style={{ textAlign: 'start', fontWeight: 600, padding: '4px 0' }}>{t('lab.susceptibility')}</caption>
          <thead>
            <tr>
              <th className={styles.th} scope="col">
                {t('lab.antibiotic')}
              </th>
              <th className={styles.th} scope="col">
                {t('lab.result')}
              </th>
            </tr>
          </thead>
          <tbody>
            {line.susceptibilities.map((item) => (
              <tr key={item.antibiotic}>
                <td className={styles.td}>{item.antibiotic}</td>
                <td className={styles.td}>
                  {t(SUSCEPTIBILITY_KEY[item.interpretation])}
                  {item.mic ? ` (MIC ${item.mic})` : ''}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}
