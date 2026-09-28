import { Badge, Button, Caption1, Field, Input, makeStyles, mergeClasses, Spinner, Text, tokens } from '@fluentui/react-components';
import { AlertUrgentRegular, BotRegular, CheckmarkRegular, DismissRegular, TextBulletListAddRegular } from '@fluentui/react-icons';
import { formatDateTime } from '@hc360/core';
import { ErrorState, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { radiologyApi, radiologyQueries, type AiFindingDto } from './api';
import { aiStatusLabel } from './labels';
import { formatConfidence } from './workflow';

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: '10px', margin: 0, padding: 0, listStyleType: 'none' },
  item: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  urgent: { borderLeft: `4px solid ${tokens.colorPaletteRedBorder2}` },
  head: { display: 'flex', gap: '6px', alignItems: 'center', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  actions: { display: 'flex', gap: '6px', flexWrap: 'wrap', alignItems: 'flex-end' },
});

/**
 * Preliminary findings from an AI triage engine (SRS IMG-05). They are suggestions only: the radiologist accepts or
 * rejects each one, and an accepted finding can be copied into the report. Urgent findings move the study up the
 * reporting worklist.
 */
export function AiFindingsPanel({ studyId, canReview, onInsert }: { studyId: string; canReview: boolean; onInsert?: (text: string) => void }) {
  const styles = useStyles();
  const findings = useQuery(radiologyQueries.aiFindings(studyId));

  return (
    <Section title="AI findings" description="Preliminary suggestions from the AI engine. Review each before relying on it.">
      {findings.error ? (
        <ErrorState error={findings.error} onRetry={() => void findings.refetch()} />
      ) : !findings.data ? (
        <Caption1 className={styles.muted}>Loading…</Caption1>
      ) : findings.data.length === 0 ? (
        <Caption1 className={styles.muted}>No AI findings for this study.</Caption1>
      ) : (
        <ul className={styles.list}>
          {findings.data.map((finding) => (
            <FindingItem key={finding.id} finding={finding} studyId={studyId} canReview={canReview} onInsert={onInsert} />
          ))}
        </ul>
      )}
    </Section>
  );
}

function FindingItem({ finding, studyId, canReview, onInsert }: { finding: AiFindingDto; studyId: string; canReview: boolean; onInsert?: (text: string) => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [note, setNote] = useState('');
  const confidence = formatConfidence(finding.confidence);

  const review = useMutation({
    mutationFn: (acknowledge: boolean) => radiologyApi.reviewAiFinding(finding.id, acknowledge, note.trim() || null),
    onSuccess: (updated) => {
      notify.success(updated.status === 'Acknowledged' ? 'AI finding accepted' : 'AI finding rejected');
      void queryClient.invalidateQueries({ queryKey: radiologyQueries.aiFindings(studyId).queryKey });
      void queryClient.invalidateQueries({ queryKey: ['radiology', 'studies'] });
    },
    onError: (error) => notify.error(error, 'Could not review the AI finding'),
  });

  return (
    <li className={mergeClasses(styles.item, finding.isUrgent ? styles.urgent : '')}>
      <div className={styles.head}>
        <BotRegular aria-hidden />
        <Text weight="semibold">{finding.engine}</Text>
        {finding.engineVersion && <Caption1 className={styles.muted}>v{finding.engineVersion}</Caption1>}
        {finding.isUrgent && (
          <Badge color="danger" icon={<AlertUrgentRegular />} shape="rounded">
            Urgent
          </Badge>
        )}
        {confidence && <Caption1>Confidence {confidence}</Caption1>}
        <Badge appearance="tint" shape="rounded" color={finding.status === 'Acknowledged' ? 'success' : finding.status === 'Dismissed' ? 'informative' : 'warning'}>
          {aiStatusLabel[finding.status]}
        </Badge>
      </div>
      <Text>{finding.finding}</Text>
      <Caption1 className={styles.muted}>
        Received {formatDateTime(finding.receivedAt)}
        {finding.reviewedAt ? ` · reviewed by ${finding.reviewedBy ?? 'staff'} ${formatDateTime(finding.reviewedAt)}` : ''}
        {finding.reviewNote ? ` · ${finding.reviewNote}` : ''}
      </Caption1>
      {finding.status === 'Preliminary' && canReview && (
        <div className={styles.actions}>
          <Field label="Note" size="small" style={{ flex: '1 1 160px' }}>
            <Input size="small" value={note} onChange={(_, data) => setNote(data.value.slice(0, 500))} placeholder="Optional" />
          </Field>
          <Button size="small" icon={review.isPending ? <Spinner size="tiny" /> : <CheckmarkRegular />} disabled={review.isPending} onClick={() => review.mutate(true)}>
            Accept
          </Button>
          <Button size="small" icon={<DismissRegular />} disabled={review.isPending} onClick={() => review.mutate(false)}>
            Reject
          </Button>
        </div>
      )}
      {finding.status === 'Acknowledged' && onInsert && (
        <div>
          <Button size="small" appearance="subtle" icon={<TextBulletListAddRegular />} onClick={() => onInsert(finding.finding)}>
            Add to findings
          </Button>
        </div>
      )}
    </li>
  );
}
