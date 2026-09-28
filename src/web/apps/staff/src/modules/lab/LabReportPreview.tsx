import { Caption1, makeStyles, MessageBar, MessageBarBody, Subtitle1, Subtitle2, Text, tokens } from '@fluentui/react-components';
import { formatDateTime } from '@hc360/core';
import { QRCodeSVG } from 'qrcode.react';
import { Fragment } from 'react';
import { verificationLink } from '@/lib/verification';
import type { LabReportResultLine, LabReportView } from './api';
import { FlagChip } from './badges';
import { flagDisplay } from './flags';
import { sectionLabel, specimenLabel } from './labels';
import { labReportNotes, labReportPatientRows } from './reportBody';

const useStyles = makeStyles({
  paper: {
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusLarge,
    boxShadow: tokens.shadow4,
    padding: '20px 24px',
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    maxWidth: '980px',
  },
  letterhead: { borderBottom: `2px solid ${tokens.colorBrandStroke1}`, paddingBottom: '8px' },
  facility: { color: tokens.colorBrandForeground1 },
  muted: { color: tokens.colorNeutralForeground3 },
  patient: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(260px, 1fr))',
    gap: '4px 16px',
    padding: '8px 12px',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  pair: { display: 'flex', gap: '8px' },
  label: { color: tokens.colorNeutralForeground3, minWidth: '90px' },
  table: { width: '100%', borderCollapse: 'collapse', marginTop: '4px' },
  th: { textAlign: 'start', fontWeight: tokens.fontWeightSemibold, color: tokens.colorNeutralForeground3, padding: '4px 6px', borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  td: { padding: '4px 6px', borderBottom: `1px solid ${tokens.colorNeutralStroke3}`, verticalAlign: 'top' },
  group: { padding: '6px 6px 2px', fontWeight: tokens.fontWeightSemibold },
  abnormal: { fontWeight: tokens.fontWeightBold },
  critical: { fontWeight: tokens.fontWeightBold, color: tokens.colorPaletteRedForeground1 },
  test: { marginTop: '8px' },
  signs: { display: 'flex', gap: '32px', flexWrap: 'wrap', justifyContent: 'flex-end', marginTop: '12px' },
  sign: { textAlign: 'center', minWidth: '180px', borderTop: `1px solid ${tokens.colorNeutralStroke1}`, paddingTop: '4px' },
  qr: { display: 'flex', gap: '12px', alignItems: 'center' },
});

function ResultValue({ line }: { line: LabReportResultLine }) {
  const styles = useStyles();
  if (line.status === 'Pending') {
    return <Caption1 className={styles.muted}>Awaited</Caption1>;
  }
  const display = flagDisplay(line.flag);
  return (
    <span>
      <Text className={display?.critical ? styles.critical : display?.code ? styles.abnormal : undefined}>
        {line.value}
        {line.autoValidated ? '*' : ''}
      </Text>
      {line.status !== 'Verified' && <Caption1 className={styles.muted}> (not verified)</Caption1>}
      {line.organism && <Caption1 block>Organism: {line.organism}{line.colonyCount ? ` · ${line.colonyCount}` : ''}</Caption1>}
      {line.susceptibilities.length > 0 && (
        <Caption1 block>{line.susceptibilities.map((s) => `${s.antibiotic} ${s.interpretation.charAt(0)}${s.mic ? ` (MIC ${s.mic})` : ''}`).join(' · ')}</Caption1>
      )}
      {line.remarks && <Caption1 block className={styles.muted}>{line.remarks}</Caption1>}
    </span>
  );
}

/** On-screen copy of the lab report, laid out like the print (letterhead, patient block, results, signatories, QR). */
export function LabReportPreview({ view }: { view: LabReportView }) {
  const styles = useStyles();
  const url = view.verificationCode ? verificationLink(view.verificationUrl, view.verificationPath) : null;
  const notes = labReportNotes(view);

  return (
    <article className={styles.paper} aria-label={`Lab report ${view.reportNumber}`}>
      <header className={styles.letterhead}>
        <Subtitle1 className={styles.facility}>{view.letterhead.facilityName}</Subtitle1>
        {view.letterhead.address && <Caption1 block className={styles.muted}>{view.letterhead.address}</Caption1>}
        {view.letterhead.nabhNumber && <Caption1 block className={styles.muted}>NABH accreditation {view.letterhead.nabhNumber}</Caption1>}
      </header>

      {view.status !== 'Released' && view.status !== 'Amended' && (
        <MessageBar intent="warning">
          <MessageBarBody>Not released yet. Results marked "not verified" are for the laboratory only.</MessageBarBody>
        </MessageBar>
      )}

      <section className={styles.patient} aria-label="Patient and report details">
        {labReportPatientRows(view).map(([label, value]) => (
          <div key={label} className={styles.pair}>
            <Caption1 className={styles.label}>{label}</Caption1>
            <Text weight="semibold">{value}</Text>
          </div>
        ))}
      </section>

      {view.clinicalNotes && (
        <Caption1>
          <strong>Clinical notes:</strong> {view.clinicalNotes}
        </Caption1>
      )}

      {view.sections.map((section) => (
        <section key={section.section}>
          <Subtitle2 as="h2">{sectionLabel[section.section]}</Subtitle2>
          {section.tests.map((test) => (
            <div key={test.orderItemId} className={styles.test}>
              <Text weight="semibold">{test.name}</Text>{' '}
              <Caption1 className={styles.muted}>
                {specimenLabel[test.specimenType]}
                {test.method ? ` · ${test.method}` : ''}
                {test.referralLabName ? ` · tested at ${test.referralLabName}` : ''}
                {test.status === 'Released' ? (test.releasedAt ? ` · released ${formatDateTime(test.releasedAt)}` : '') : test.status === 'Verified' ? ' · verified, not released' : ' · in progress'}
              </Caption1>
              <table className={styles.table}>
                <thead>
                  <tr>
                    <th className={styles.th}>Investigation</th>
                    <th className={styles.th}>Result</th>
                    <th className={styles.th}>Flag</th>
                    <th className={styles.th}>Unit</th>
                    <th className={styles.th}>Reference interval</th>
                  </tr>
                </thead>
                <tbody>
                  {test.results.map((line, index) => (
                    <Fragment key={line.resultId}>
                      {line.groupName && line.groupName !== test.results[index - 1]?.groupName && (
                        <tr>
                          <td className={styles.group} colSpan={5}>
                            {line.groupName}
                          </td>
                        </tr>
                      )}
                      <tr>
                        <td className={styles.td}>{line.name}</td>
                        <td className={styles.td}>
                          <ResultValue line={line} />
                        </td>
                        <td className={styles.td}>
                          <FlagChip flag={line.flag} />
                        </td>
                        <td className={styles.td}>{line.unit}</td>
                        <td className={styles.td}>{line.referenceText}</td>
                      </tr>
                    </Fragment>
                  ))}
                </tbody>
              </table>
              {test.interpretation && (
                <Caption1 block>
                  <strong>Interpretation:</strong> {test.interpretation}
                </Caption1>
              )}
            </div>
          ))}
        </section>
      ))}

      {view.amendments.length > 0 && (
        <section>
          <Subtitle2 as="h2">Amendments</Subtitle2>
          {view.amendments.map((amendment) => (
            <Caption1 key={amendment.revision} block>
              Revision {amendment.revision} · {formatDateTime(amendment.amendedAt)}
              {amendment.amendedBy ? ` · ${amendment.amendedBy}` : ''}: {amendment.reason}. {amendment.changes}
            </Caption1>
          ))}
        </section>
      )}

      {notes.map((note) => (
        <Caption1 key={note} block className={styles.muted}>
          {note}
        </Caption1>
      ))}

      <footer>
        {view.signatories.length > 0 && (
          <div className={styles.signs}>
            {view.signatories.map((signatory) => (
              <div key={signatory.userId} className={styles.sign}>
                <Text weight="semibold" block>
                  {signatory.name}
                </Text>
                {signatory.qualification && <Caption1 block>{signatory.qualification}</Caption1>}
                {signatory.registrationNumber && <Caption1 block>Reg. no. {signatory.registrationNumber}</Caption1>}
              </div>
            ))}
          </div>
        )}
        {url && view.verificationCode && (
          <div className={styles.qr}>
            <QRCodeSVG value={url} size={96} aria-label="QR code to verify this report" role="img" />
            <span>
              <Text weight="semibold" block>
                Scan to verify this report
              </Text>
              <Caption1 block className={styles.muted}>
                Verification code {view.verificationCode}
              </Caption1>
            </span>
          </div>
        )}
      </footer>
    </article>
  );
}
