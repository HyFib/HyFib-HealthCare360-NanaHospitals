import { makeStyles, Text, tokens } from '@fluentui/react-components';
import { ErrorState, LoadingState, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { emrQueries } from './api';
import { EncounterGlance } from './ChartSummary';

const useStyles = makeStyles({
  empty: { color: tokens.colorNeutralForeground3 },
});

/**
 * The previous visit beside the note being written (EMR-02..07): the patient's latest signed encounter that started
 * before this one, at any facility of the hospital, as the server finds it. The doctor opens it to read it in full.
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
      {visit.isLoading ? (
        <LoadingState label="Loading the previous visit…" />
      ) : visit.error ? (
        <ErrorState
          error={visit.error}
          title="Could not load the previous visit"
          onRetry={() => void visit.refetch()}
        />
      ) : visit.data?.previous ? (
        <EncounterGlance encounter={visit.data.previous} onOpen={onOpen} />
      ) : (
        <Text className={styles.empty}>No signed visit before this one.</Text>
      )}
    </Section>
  );
}
