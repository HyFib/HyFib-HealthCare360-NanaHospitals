import {
  Badge,
  Button,
  Caption1,
  Divider,
  makeStyles,
  Subtitle2,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  tokens,
} from '@fluentui/react-components';
import { PlayRegular, RecordRegular, SendRegular } from '@fluentui/react-icons';
import { formatDateTime, formatTime } from '@hc360/core';
import { StatusBadge } from '@hc360/ui';
import type { ReactNode } from 'react';
import { formatDuration } from '@/modules/scheduling/dates';
import { useNow } from '@/modules/scheduling/useNow';
import type { TelehealthSessionDetail } from './api';
import { isSessionOpen, roleLabel, sessionStatusText } from './labels';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  grid: { display: 'grid', gridTemplateColumns: 'minmax(120px, auto) 1fr', gap: '6px 12px', alignItems: 'baseline' },
  label: { color: tokens.colorNeutralForeground3 },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
  tableWrap: { overflowX: 'auto' },
});

function Row({ label, children }: { label: string; children: ReactNode }) {
  const styles = useStyles();
  return (
    <>
      <Caption1 className={styles.label}>{label}</Caption1>
      <Text>{children ?? '—'}</Text>
    </>
  );
}

export interface SessionDetailsProps {
  session: TelehealthSessionDetail;
  canConsult: boolean;
  starting?: boolean;
  onStart?: () => void;
  onAskConsent?: () => void;
  canResend?: boolean;
  resending?: boolean;
  onResend?: () => void;
}

/** When, who, visit, recording consent, link and connection quality of a consultation (TEL-05 metadata). */
export function SessionDetails({ session: s, canConsult, starting, onStart, onAskConsent, canResend, resending, onResend }: SessionDetailsProps) {
  const styles = useStyles();
  const now = useNow();
  const open = isSessionOpen(s.status);
  const status = sessionStatusText[s.status];
  const liveMinutes = s.status === 'InProgress' && s.startedAt ? (now - Date.parse(s.startedAt)) / 60_000 : null;

  return (
    <div className={styles.body}>
      <div className={styles.grid}>
        <Row label="Status">
          <StatusBadge status={s.status} tone={status.tone} label={status.label} />
        </Row>
        <Row label="Booked for">
          {formatDateTime(s.scheduledStartAt)} – {formatTime(s.scheduledEndAt)}
        </Row>
        {s.appointmentNumber && <Row label="Appointment">{s.appointmentNumber}</Row>}
        {s.startedAt && <Row label="Started">{formatDateTime(s.startedAt)}</Row>}
        {s.endedAt && <Row label="Ended">{formatDateTime(s.endedAt)}</Row>}
        {(liveMinutes !== null || s.durationSeconds !== null) && (
          <Row label="Duration">{formatDuration(liveMinutes ?? (s.durationSeconds ?? 0) / 60)}</Row>
        )}
        <Row label="Visit">{s.visitId ? 'Created: write notes and the prescription against it' : open ? 'Created when the consultation starts' : 'No visit'}</Row>
        <Row label="Recording consent">
          {s.recordingConsentAt ? `${s.recordingConsent ? 'Patient agreed' : 'Patient declined'} at ${formatTime(s.recordingConsentAt)}` : 'Not asked'}
        </Row>
        {s.audioOnlyUsed && (
          <Row label="Audio only">
            <Badge appearance="tint" color="warning">
              Used during the call
            </Badge>
          </Row>
        )}
        {s.qualityNotes && <Row label="Quality notes">{s.qualityNotes}</Row>}
        {s.cancelReason && <Row label="Cancelled because">{s.cancelReason}</Row>}
        <Row label="Join link">
          {s.linkSentAt ? `Sent ${s.linkSendCount} time${s.linkSendCount === 1 ? '' : 's'}, last ${formatDateTime(s.linkSentAt)}` : 'Not sent'}
          {s.joinTokenExpiresAt && open ? ` · works until ${formatTime(s.joinTokenExpiresAt)}` : ''}
        </Row>
        <Row label="Chat messages">{s.messageCount}</Row>
      </div>

      {open && (canConsult || canResend) && (
        <div className={styles.actions}>
          {canConsult && !s.visitId && onStart && (
            <Button icon={<PlayRegular />} disabled={starting} onClick={onStart}>
              Start consultation
            </Button>
          )}
          {canConsult && onAskConsent && (
            <Button icon={<RecordRegular />} onClick={onAskConsent}>
              Recording consent
            </Button>
          )}
          {canResend && onResend && (
            <Button icon={<SendRegular />} disabled={resending} onClick={onResend}>
              Send a new link
            </Button>
          )}
        </div>
      )}

      {s.participants.length > 0 && (
        <>
          <Divider />
          <Subtitle2 as="h3">Who joined</Subtitle2>
          <div className={styles.tableWrap}>
            <Table size="extra-small" aria-label="Participants">
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>Name</TableHeaderCell>
                  <TableHeaderCell>Joined</TableHeaderCell>
                  <TableHeaderCell>Left</TableHeaderCell>
                  <TableHeaderCell>Worst connection</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {s.participants.map((p) => (
                  <TableRow key={p.id}>
                    <TableCell>
                      <Text size={200}>{p.displayName}</Text>
                      <Caption1 block className={styles.label}>
                        {roleLabel[p.role]}
                        {p.admittedAt && p.role !== 'Clinician' ? ` · admitted ${formatTime(p.admittedAt)}` : ''}
                      </Caption1>
                    </TableCell>
                    <TableCell>{formatTime(p.joinedAt)}</TableCell>
                    <TableCell>{p.leftAt ? formatTime(p.leftAt) : 'In the call'}</TableCell>
                    <TableCell>
                      {[
                        p.worstRoundTripMs !== null ? `${p.worstRoundTripMs} ms` : null,
                        p.maxPacketLossPercent !== null ? `${p.maxPacketLossPercent}% lost` : null,
                        p.lowestBitrateKbps !== null ? `${p.lowestBitrateKbps} kbps` : null,
                        p.audioOnly ? 'audio only' : null,
                      ]
                        .filter(Boolean)
                        .join(' · ') || '—'}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        </>
      )}
    </div>
  );
}
