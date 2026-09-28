import {
  Badge,
  Body1,
  Button,
  Caption1,
  DrawerBody,
  DrawerHeader,
  DrawerHeaderTitle,
  Link,
  makeStyles,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  RatingDisplay,
  Spinner,
  Subtitle2,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import { CallRegular, DismissRegular, LockClosedRegular } from '@fluentui/react-icons';
import { formatDateTime, formatPhone } from '@hc360/core';
import { ErrorState } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useDepartments } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { Patient360Link } from '@/modules/crm/Patient360Link';
import { Detail, DetailGrid } from '@/modules/hr/shared/DetailGrid';
import { Stack } from '@/modules/hr/shared/layout';
import { feedbackQueries, type FeedbackResponseDetail } from './api';
import { NpsAnswer, SentimentBadge } from './badges';
import { ASPECTS, aspectLabel, channelLabel, FEEDBACK_PERMISSIONS, ratingWord } from './labels';
import { ratingKey } from './rules';

const useStyles = makeStyles({
  badges: { display: 'flex', gap: '6px', flexWrap: 'wrap' },
  ratings: { display: 'grid', gridTemplateColumns: 'minmax(140px, auto) auto 1fr', gap: '8px 12px', alignItems: 'center' },
  word: { color: tokens.colorNeutralForeground3 },
  comment: {
    margin: 0,
    padding: '10px 14px',
    borderLeft: `3px solid ${tokens.colorBrandStroke1}`,
    backgroundColor: tokens.colorNeutralBackground2,
    whiteSpace: 'pre-wrap',
    overflowWrap: 'anywhere',
  },
});

/** One feedback response. Opening a response linked to a patient is recorded in the audit log by the server. */
export function ResponseDrawer({ responseId, onClose }: { responseId: string | null; onClose: () => void }) {
  return (
    <OverlayDrawer open={responseId !== null} position="end" size="medium" onOpenChange={(_, d) => !d.open && onClose()}>
      {responseId && <Content responseId={responseId} onClose={onClose} />}
    </OverlayDrawer>
  );
}

function Content({ responseId, onClose }: { responseId: string; onClose: () => void }) {
  const response = useQuery(feedbackQueries.response(responseId));
  return (
    <>
      <DrawerHeader>
        <DrawerHeaderTitle action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}>
          {response.data?.formTitle ?? 'Feedback'}
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        {response.data ? (
          <Details response={response.data} />
        ) : response.error ? (
          <ErrorState error={response.error} onRetry={() => void response.refetch()} />
        ) : (
          <Spinner label="Loading…" />
        )}
      </DrawerBody>
    </>
  );
}

function Details({ response }: { response: FeedbackResponseDetail }) {
  const styles = useStyles();
  const navigate = useNavigate();
  const departments = useDepartments();
  const canComplaints = usePermission(FEEDBACK_PERMISSIONS.complaintsManage);
  const department = response.departmentId ? (departments.data?.find((d) => d.id === response.departmentId)?.name ?? 'Other department') : null;
  const answered = ASPECTS.filter((aspect) => response.ratings[ratingKey(aspect)] !== null && response.ratings[ratingKey(aspect)] !== undefined);

  return (
    <Stack gap={16}>
      <div className={styles.badges}>
        <Badge appearance="outline" shape="rounded">
          {channelLabel[response.channel]}
        </Badge>
        <Tooltip content="Worked out on the server from the comment's words, the ratings and the recommendation score. No AI service is used." relationship="description">
          <span>
            <SentimentBadge sentiment={response.sentiment} />
          </span>
        </Tooltip>
        {response.isLowScore && (
          <Badge color="danger" appearance="tint" shape="rounded">
            Low score
          </Badge>
        )}
        {!response.patientId && (
          <Badge appearance="tint" color="informative" shape="rounded" icon={<LockClosedRegular />}>
            Anonymous
          </Badge>
        )}
      </div>

      <DetailGrid minColumnWidth={180}>
        <Detail label="Received">{formatDateTime(response.submittedAt)}</Detail>
        <Detail label="Department">{department}</Detail>
        {response.patientId && (
          <Detail label="Patient" wide>
            <span style={{ display: 'inline-flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
              {response.patientName} · {response.uhid}
              <Patient360Link patientId={response.patientId} size="small" appearance="subtle" />
            </span>
          </Detail>
        )}
      </DetailGrid>

      <Stack gap={8}>
        <Subtitle2>Ratings</Subtitle2>
        {answered.length === 0 ? (
          <Caption1>No aspect was rated.</Caption1>
        ) : (
          <div className={styles.ratings}>
            {answered.map((aspect) => {
              const value = response.ratings[ratingKey(aspect)] ?? 0;
              return (
                <div key={aspect} style={{ display: 'contents' }}>
                  <Body1>{aspectLabel[aspect]}</Body1>
                  <RatingDisplay value={value} color="marigold" size="medium" aria-label={`${value} out of 5`} />
                  <Caption1 className={styles.word}>{ratingWord(value)}</Caption1>
                </div>
              );
            })}
          </div>
        )}
      </Stack>

      <Stack gap={4}>
        <Subtitle2>Would recommend us</Subtitle2>
        <div>
          <NpsAnswer value={response.nps} /> {response.nps !== null && <Caption1>out of 10</Caption1>}
        </div>
      </Stack>

      {response.comment && (
        <Stack gap={4}>
          <Subtitle2>Comment</Subtitle2>
          <blockquote className={styles.comment}>{response.comment}</blockquote>
        </Stack>
      )}

      {response.wantsCallback && (
        <MessageBar intent="info" icon={<CallRegular />}>
          <MessageBarBody>The patient asked for a call back on {response.contactPhone ? formatPhone(response.contactPhone) : 'no number given'}.</MessageBarBody>
        </MessageBar>
      )}

      {response.complaintNumber && (
        <MessageBar intent="warning">
          <MessageBarBody>
            The low score raised complaint{' '}
            {canComplaints && response.complaintId ? (
              <Link
                onClick={() => void navigate({ to: '/feedback/complaints/$complaintId', params: { complaintId: response.complaintId ?? '' } })}
              >
                {response.complaintNumber}
              </Link>
            ) : (
              <strong>{response.complaintNumber}</strong>
            )}
            .
          </MessageBarBody>
        </MessageBar>
      )}

      {response.patientId && <Caption1>Opening a patient's feedback is recorded in the audit log.</Caption1>}
    </Stack>
  );
}
