import { Badge, Button, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import { BeakerRegular, DocumentEditRegular, NoteRegular, PeopleQueueRegular, PillRegular, StethoscopeRegular } from '@fluentui/react-icons';
import { formatRelative, useSession } from '@hc360/core';
import { ErrorState, KpiCard, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { emrQueries, type WorklistResult } from './api';
import { ResultReviewDialog } from './ResultReviewDialog';

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: '4px', margin: 0, padding: 0, listStyle: 'none' },
  item: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) auto',
    gap: '2px 8px',
    padding: '6px 8px',
    borderRadius: tokens.borderRadiusMedium,
    cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
  muted: { color: tokens.colorNeutralForeground3 },
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(150px, 1fr))' },
});

/** Released reports waiting for the signed-in doctor's review, critical first (EMR-01). */
export function ResultsToReviewWidget() {
  const styles = useStyles();
  const navigate = useNavigate();
  const isDoctor = useSession((s) => Boolean(s.user?.practitionerId));
  const worklist = useQuery({ ...emrQueries.worklist({}), enabled: isDoctor });
  const [reviewing, setReviewing] = useState<WorklistResult | null>(null);
  const results = worklist.data?.resultsToReview ?? [];
  const critical = results.filter((r) => r.hasCriticalFindings).length;

  if (!isDoctor) {
    return null;
  }

  return (
    <Section
      title={`Results to review (${results.length})`}
      description={critical > 0 ? `${critical} with critical findings` : 'Newest released reports for your patients'}
      actions={
        <Button size="small" appearance="subtle" onClick={() => void navigate({ to: '/emr' })}>
          Open worklist
        </Button>
      }
    >
      {worklist.error ? (
        <ErrorState error={worklist.error} onRetry={() => void worklist.refetch()} />
      ) : results.length === 0 ? (
        <Text className={styles.muted}>{worklist.isLoading ? 'Loading…' : 'Nothing to review. New reports appear here when the lab releases them.'}</Text>
      ) : (
        <ul className={styles.list}>
          {results.slice(0, 6).map((r) => (
            <li
              key={r.id}
              className={styles.item}
              role="button"
              tabIndex={0}
              onClick={() => setReviewing(r)}
              onKeyDown={(event) => {
                if (event.key === 'Enter' || event.key === ' ') {
                  event.preventDefault();
                  setReviewing(r);
                }
              }}
            >
              <Text weight="semibold">{r.title}</Text>
              {r.hasCriticalFindings ? (
                <Badge color="danger" size="small">
                  Critical
                </Badge>
              ) : (
                <span />
              )}
              <Caption1 className={styles.muted}>
                {r.patientName} · {r.uhid}
              </Caption1>
              <Caption1 className={styles.muted}>{formatRelative(r.releasedAt)}</Caption1>
            </li>
          ))}
        </ul>
      )}
      <ResultReviewDialog result={reviewing} onClose={() => setReviewing(null)} />
    </Section>
  );
}

/** Today's clinical work at a glance for the signed-in doctor. */
export function ClinicalTasksWidget() {
  const styles = useStyles();
  const navigate = useNavigate();
  const isDoctor = useSession((s) => Boolean(s.user?.practitionerId));
  const worklist = useQuery({ ...emrQueries.worklist({}), enabled: isDoctor });
  if (!isDoctor) {
    return null;
  }
  const w = worklist.data;
  const value = (n: number | undefined) => (w ? String(n ?? 0) : '…');
  const open = () => void navigate({ to: '/emr' });
  return (
    <div className={styles.kpis}>
      <KpiCard label="Waiting for you" value={value(w?.queue.filter((q) => q.tokenStatus === 'Waiting' || q.tokenStatus === 'Called').length)} icon={<PeopleQueueRegular />} hint="Today's queue" onClick={open} />
      <KpiCard label="Unsigned encounters" value={value(w?.openEncounters.length)} icon={<StethoscopeRegular />} onClick={open} />
      <KpiCard label="Unsigned notes" value={value(w?.unsignedNotes.length)} icon={<NoteRegular />} onClick={open} />
      <KpiCard label="Draft prescriptions" value={value(w?.draftPrescriptions.length)} icon={<PillRegular />} onClick={open} />
      <KpiCard label="Results to review" value={value(w?.resultsToReview.length)} icon={<BeakerRegular />} onClick={open} />
      <KpiCard label="Chart deficiencies" value={value(w?.chartDeficiencies.length)} icon={<DocumentEditRegular />} hint="Assigned by medical records" onClick={open} />
    </div>
  );
}
