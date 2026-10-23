import { Button, Caption1, makeStyles, Spinner, Text, tokens } from '@fluentui/react-components';
import { OpenRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime } from '@hc360/core';
import { ErrorState, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { VitalChipList } from './ChartSummary';
import { emrQueries, encounterTypeLabel, type PreviousVisitMedicine } from './api';
import { vitalChips } from './vitals';

const useStyles = makeStyles({
  facts: {
    display: 'grid',
    gridTemplateColumns: 'minmax(7rem, max-content) minmax(0, 1fr)',
    columnGap: '16px',
    rowGap: '8px',
    margin: 0,
    '@media (max-width: 600px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  term: { color: tokens.colorNeutralForeground3 },
  detail: { margin: 0, minWidth: 0, overflowWrap: 'anywhere', whiteSpace: 'pre-line' },
  list: { display: 'flex', flexDirection: 'column', gap: '4px', margin: 0, padding: 0, listStyle: 'none' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** "Paracetamol 500 mg Tablet · 1 tablet · TDS · 5 days · After food", without repeating a strength the name has. */
function medicineLine(m: PreviousVisitMedicine): string {
  const name = m.strength && !m.drugName.includes(m.strength) ? `${m.drugName} ${m.strength}` : m.drugName;
  const days = `${m.durationDays} ${m.durationDays === 1 ? 'day' : 'days'}`;
  return [name, m.dose, m.frequency, days, m.instructions].filter(Boolean).join(' · ');
}

/**
 * The previous visit summary (EMR-07) at the top of the encounter workspace: the patient's encounter before this one,
 * whichever doctor or facility it was with, so the consultation starts from what was found, decided and prescribed then.
 * The server picks it and lists the medicines of its signed prescriptions only.
 */
export function PreviousVisit({ encounterId, onOpen }: { encounterId: string; onOpen: (encounterId: string) => void }) {
  const styles = useStyles();
  const query = useQuery(emrQueries.previousVisit(encounterId));

  // A summary already shown stays up while a refresh fails (a WiFi blip); only a first load can fail visibly.
  if (!query.data) {
    return (
      <Section title="Previous visit">
        {query.error ? (
          <ErrorState
            error={query.error}
            title="Could not load the previous visit"
            onRetry={() => void query.refetch()}
          />
        ) : (
          <Spinner size="tiny" labelPosition="after" label="Loading the previous visit…" />
        )}
      </Section>
    );
  }

  const visit = query.data.previous;
  if (!visit) {
    return (
      <Section title="Previous visit">
        <Text className={styles.muted}>This is the patient's first encounter on record.</Text>
      </Section>
    );
  }

  const where = [encounterTypeLabel[visit.type], visit.departmentName, visit.practitionerName].filter(Boolean);
  const followUp = [
    visit.followUpDate ? `On ${formatDateOnly(visit.followUpDate)}` : null,
    visit.followUpAdvice,
  ].filter(Boolean);

  return (
    <Section
      title={`Previous visit · ${formatDateTime(visit.startedAt)}`}
      description={where.join(' · ')}
      actions={
        <>
          {visit.status === 'InProgress' && <StatusBadge status="Draft" label="Not signed" />}
          <Button size="small" appearance="subtle" icon={<OpenRegular />} onClick={() => onOpen(visit.encounterId)}>
            Open
          </Button>
        </>
      }
    >
      <dl className={styles.facts}>
        {visit.chiefComplaint && <Fact term="Complaint">{visit.chiefComplaint}</Fact>}
        <Fact term="Diagnoses">{visit.diagnoses.length > 0 ? visit.diagnoses.join('; ') : 'None recorded'}</Fact>
        {visit.assessment && <Fact term="Assessment">{visit.assessment}</Fact>}
        {visit.plan && <Fact term="Plan">{visit.plan}</Fact>}
        {followUp.length > 0 && <Fact term="Follow-up">{followUp.join(' · ')}</Fact>}
        <Fact term="Medicines">
          {visit.medicines.length === 0 ? (
            'None prescribed'
          ) : (
            <ul className={styles.list}>
              {visit.medicines.map((m, index) => (
                <li key={`${index}-${m.drugName}`}>{medicineLine(m)}</li>
              ))}
            </ul>
          )}
        </Fact>
        {visit.vitals && (
          <Fact term="Vitals">
            <VitalChipList chips={vitalChips(visit.vitals)} />
          </Fact>
        )}
      </dl>
      {visit.status === 'InProgress' && (
        <Caption1 className={styles.muted}>The note of that visit is not signed yet and may still change.</Caption1>
      )}
    </Section>
  );
}

function Fact({ term, children }: { term: string; children: ReactNode }) {
  const styles = useStyles();
  return (
    <>
      <dt className={styles.term}>
        <Text size={200}>{term}</Text>
      </dt>
      <dd className={styles.detail}>{children}</dd>
    </>
  );
}
