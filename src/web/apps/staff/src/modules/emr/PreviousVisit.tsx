import { makeStyles, Text, tokens } from '@fluentui/react-components';
import { formatDateOnly } from '@hc360/core';
import { ErrorState, LoadingState, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { EncounterGlance, VitalChipList } from './ChartSummary';
import { emrQueries, type PreviousVisitMedicine, type PreviousVisitSummary } from './api';
import { vitalChips } from './vitals';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
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
  empty: { color: tokens.colorNeutralForeground3 },
});

/** "Paracetamol 500 mg Tablet · 1 tablet · TDS · 5 days · After food", without repeating a strength the name has. */
function medicineLine(m: PreviousVisitMedicine): string {
  const name = m.strength && !m.drugName.includes(m.strength) ? `${m.drugName} ${m.strength}` : m.drugName;
  const days = `${m.durationDays} ${m.durationDays === 1 ? 'day' : 'days'}`;
  return [name, m.dose, m.frequency, days, m.instructions].filter(Boolean).join(' · ');
}

/**
 * The previous visit beside the note being written (EMR-02..07): the patient's latest signed encounter that started
 * before this one, at any facility of the hospital, as the server finds it. At a glance, when and with whom and its
 * diagnoses (the doctor opens it to read it in full); below, what was decided, prescribed and measured then, so the
 * consultation starts from it. The medicines are those of its signed prescriptions.
 */
export function PreviousVisit({
  patientId,
  encounterId,
  onOpen,
}: {
  patientId: string;
  encounterId: string;
  onOpen: (encounterId: string) => void;
}) {
  const styles = useStyles();
  const visit = useQuery(emrQueries.previousVisit(patientId, encounterId));

  return (
    <Section title="Previous visit" description="The last signed visit before this one.">
      {/* A visit already shown stays up while a refresh fails (a WiFi blip); only a first load can fail visibly. */}
      {!visit.data ? (
        visit.error ? (
          <ErrorState
            error={visit.error}
            title="Could not load the previous visit"
            onRetry={() => void visit.refetch()}
          />
        ) : (
          <LoadingState label="Loading the previous visit…" />
        )
      ) : visit.data.previous ? (
        <div className={styles.stack}>
          <EncounterGlance
            encounter={{ ...visit.data.previous, id: visit.data.previous.encounterId }}
            onOpen={onOpen}
          />
          <VisitDetails visit={visit.data.previous} />
        </div>
      ) : (
        <Text className={styles.empty}>No signed visit before this one.</Text>
      )}
    </Section>
  );
}

/** What the glance leaves out: the complaint (it shows the diagnoses when there are any), the decisions, medicines and vitals. */
function VisitDetails({ visit }: { visit: PreviousVisitSummary }) {
  const styles = useStyles();
  const followUp = [
    visit.followUpDate ? `On ${formatDateOnly(visit.followUpDate)}` : null,
    visit.followUpAdvice,
  ].filter(Boolean);

  return (
    <dl className={styles.facts}>
      {visit.chiefComplaint && visit.diagnoses.length > 0 && <Fact term="Complaint">{visit.chiefComplaint}</Fact>}
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
