import { Caption1, makeStyles, MessageBar, MessageBarBody, Text, tokens } from '@fluentui/react-components';
import { useRealtimeEvent, useSession } from '@hc360/core';
import { ErrorState, LoadingState, Pipeline, Section, StatusBadge, type StatusTone } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { usePermission } from '@/lib/permissions';
import { JOURNEY_KIND_LABEL, journeyKeys, journeyQueries, type JourneyDto, type JourneyStatus } from './journeys';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '20px' },
  journey: { display: 'flex', flexDirection: 'column', gap: '8px', minWidth: 0 },
  heading: { display: 'flex', flexWrap: 'wrap', alignItems: 'baseline', gap: '4px 8px' },
  title: { margin: 0 },
  lines: { display: 'flex', flexDirection: 'column', gap: '10px' },
  line: { display: 'flex', flexDirection: 'column', gap: '4px', minWidth: 0, overflowWrap: 'anywhere' },
  muted: { color: tokens.colorNeutralForeground3 },
});

const STATUS_LABEL: Record<JourneyStatus, string> = {
  Active: 'In progress',
  Completed: 'Completed',
  Cancelled: 'Cancelled',
};

const STATUS_TONE: Record<JourneyStatus, StatusTone> = { Active: 'brand', Completed: 'success', Cancelled: 'danger' };

/**
 * Where the patient is, on their page: the current (or latest) out-patient visit and the current stay as pipelines, with
 * their operations and lab orders beneath as lines. Every stage comes from the hospital's records; nobody sets one. For
 * staff who see visits (`visits.read`); it refreshes every minute and whenever a visit of the facility changes.
 */
export function PatientJourneyCard({ patientId }: { patientId: string }) {
  return usePermission('visits.read') ? <JourneyCard patientId={patientId} /> : null;
}

function JourneyCard({ patientId }: { patientId: string }) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const facilityId = useSession((s) => s.user?.facilityId);
  const journeys = useQuery(journeyQueries.forPatient(patientId));

  useRealtimeEvent(facilityId ? `facility:${facilityId}` : null, 'visits.changed', () => {
    void queryClient.invalidateQueries({ queryKey: journeyKeys.patient(patientId) });
  });

  const data = journeys.data;
  const main = [data?.outpatient, data?.inpatient].filter((j): j is JourneyDto => Boolean(j));
  return (
    <Section
      title="Journey"
      description="Where the patient is in their visit, stay, operations and tests, from what has been recorded."
    >
      {journeys.isLoading ? (
        <LoadingState label="Loading the journey…" />
      ) : journeys.error ? (
        <ErrorState error={journeys.error} onRetry={() => void journeys.refetch()} />
      ) : data ? (
        <div className={styles.stack}>
          {main.map((journey) => (
            <section key={journey.id} className={styles.journey} aria-labelledby={`journey-${journey.id}`}>
              <div className={styles.heading}>
                <h3 id={`journey-${journey.id}`} className={styles.title}>
                  <Text weight="semibold">
                    {JOURNEY_KIND_LABEL[journey.kind]} {journey.number}
                  </Text>
                </h3>
                <StatusBadge
                  status={journey.status}
                  tone={STATUS_TONE[journey.status]}
                  label={STATUS_LABEL[journey.status]}
                />
                <Caption1 className={styles.muted}>{journey.title}</Caption1>
              </div>
              <Pipeline stages={journey.stages} label={`${JOURNEY_KIND_LABEL[journey.kind]} ${journey.number}`} />
            </section>
          ))}
          <JourneyLines title="Operations" journeys={data.surgeries} />
          {data.surgeriesWithheld && (
            <MessageBar intent="info">
              <MessageBarBody>
                Operations are not shown: the record is confidential, and you do not hold its category.
              </MessageBarBody>
            </MessageBar>
          )}
          <JourneyLines title="Lab orders" journeys={data.labOrders} />
          {main.length === 0 && data.surgeries.length === 0 && data.labOrders.length === 0 && (
            <Text className={styles.muted}>No visit, stay, operation or lab order to follow yet.</Text>
          )}
        </div>
      ) : null}
    </Section>
  );
}

/** The operations or lab orders of the visit and stay (or still open), one compact line each. */
function JourneyLines({ title, journeys }: { title: string; journeys: JourneyDto[] }) {
  const styles = useStyles();
  if (journeys.length === 0) {
    return null;
  }
  return (
    <section className={styles.journey} aria-label={title}>
      <h3 className={styles.title}>
        <Text weight="semibold">{title}</Text>
      </h3>
      <ul className={styles.lines} style={{ listStyleType: 'none', margin: 0, padding: 0 }}>
        {journeys.map((journey) => (
          <li key={journey.id} className={styles.line}>
            <Text size={200}>
              <Text size={200} weight="semibold">
                {journey.number}
              </Text>{' '}
              {journey.title}
              {journey.status !== 'Active' && ` · ${STATUS_LABEL[journey.status]}`}
            </Text>
            <Pipeline stages={journey.stages} label={`${JOURNEY_KIND_LABEL[journey.kind]} ${journey.number}`} compact />
          </li>
        ))}
      </ul>
    </section>
  );
}
