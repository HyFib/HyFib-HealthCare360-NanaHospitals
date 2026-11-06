import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  tokens,
} from '@fluentui/react-components';
import { formatDateTime } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { ReasonDialog } from '../common/ReasonDialog';
import { labApi, labKeys, type AnalyserChannelDto, type LabInstrumentDto } from '../api';
import { lastReading } from './channels';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  code: { fontFamily: tokens.fontFamilyMonospace },
  muted: { color: tokens.colorNeutralForeground3 },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
});

/**
 * The channels an analyser sends that no test maps (INT-LAB): what each last read, how often, and how to write its
 * mapping in a test's analyser channels, after which it leaves the list. Whoever manages the lab's masters can decide
 * that a channel goes on no report, with why, and take that back.
 */
export function AnalyserChannelsDialog({
  instrument,
  channels,
  canManage,
  onClose,
}: {
  instrument: LabInstrumentDto;
  channels: readonly AnalyserChannelDto[];
  canManage: boolean;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [ignoring, setIgnoring] = useState<AnalyserChannelDto | null>(null);
  const refresh = () => void queryClient.invalidateQueries({ queryKey: labKeys.masters });

  const ignore = useMutation({
    mutationFn: ({ id, reason }: { id: string; reason: string }) => labApi.ignoreAnalyserChannel(id, reason),
    onSuccess: () => {
      setIgnoring(null);
      notify.success('Channel ignored', 'It goes on no report. The reason is in the audit trail.');
      refresh();
    },
    onError: (error) => notify.error(error, 'Could not ignore the channel'),
  });
  const stopIgnoring = useMutation({
    mutationFn: (id: string) => labApi.stopIgnoringAnalyserChannel(id),
    onSuccess: () => {
      notify.success('Channel waits to be mapped again');
      refresh();
    },
    onError: (error) => notify.error(error, 'Could not take the decision back'),
  });

  return (
    <>
      <Dialog open={!ignoring} onOpenChange={(_, data) => !data.open && onClose()}>
        <DialogSurface style={{ maxWidth: 880 }}>
          <DialogBody>
            <DialogTitle>Channels {instrument.code} sends that no test maps</DialogTitle>
            <DialogContent className={styles.body}>
              <Text>
                Their values go on no report. Map a channel by adding the code under &ldquo;Map it as&rdquo; to the
                right test&apos;s analyser channels in the Tests tab; it then leaves this list. A channel that should go
                on no report (a research parameter, a flag sent as a channel) can be ignored, with why.
              </Text>
              {channels.length === 0 ? (
                <Text className={styles.muted}>Every channel {instrument.code} has sent maps a test.</Text>
              ) : (
                <Table aria-label={`Channels ${instrument.code} sends that no test maps`} size="small">
                  <TableHeader>
                    <TableRow>
                      <TableHeaderCell>Channel</TableHeaderCell>
                      <TableHeaderCell>Last sent</TableHeaderCell>
                      <TableHeaderCell>Times</TableHeaderCell>
                      <TableHeaderCell>Map it as</TableHeaderCell>
                      {canManage && <TableHeaderCell aria-label="Actions" />}
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {channels.map((channel) => (
                      <TableRow key={channel.id}>
                        <TableCell>
                          <span className={styles.code}>{channel.channelCode}</span>
                        </TableCell>
                        <TableCell>
                          <span className={styles.cell}>
                            <span>{lastReading(channel)}</span>
                            <Caption1 className={styles.muted}>{formatDateTime(channel.lastSeenAt)}</Caption1>
                          </span>
                        </TableCell>
                        <TableCell>{channel.timesSeen}</TableCell>
                        <TableCell>
                          {channel.ignored ? (
                            <span className={styles.cell}>
                              <span>Ignored: {channel.ignored.reason}</span>
                              <Caption1 className={styles.muted}>
                                {channel.ignored.ignoredBy ?? 'Someone'}, {formatDateTime(channel.ignored.ignoredAt)}
                              </Caption1>
                            </span>
                          ) : channel.mapping ? (
                            <span className={styles.code}>{channel.mapping}</span>
                          ) : (
                            <span className={styles.muted}>
                              Cannot be mapped: a mapping holds letters, digits and . _ % # ^ - only. Ask the
                              analyser&apos;s engineer to send it under another code.
                            </span>
                          )}
                        </TableCell>
                        {canManage && (
                          <TableCell>
                            {channel.ignored ? (
                              <Button
                                size="small"
                                appearance="subtle"
                                disabled={stopIgnoring.isPending}
                                onClick={() => stopIgnoring.mutate(channel.id)}
                              >
                                Stop ignoring
                              </Button>
                            ) : (
                              <Button size="small" appearance="subtle" onClick={() => setIgnoring(channel)}>
                                Ignore…
                              </Button>
                            )}
                          </TableCell>
                        )}
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
            </DialogContent>
            <DialogActions>
              <Button appearance="primary" onClick={onClose}>
                Close
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
      {ignoring && (
        <ReasonDialog
          key={ignoring.id}
          open
          title={`Ignore ${instrument.code}'s channel ${ignoring.channelCode}?`}
          description={
            <Text>
              It last sent {lastReading(ignoring)}. Its values go on no report, and it is listed as ignored rather than
              waiting to be mapped. The reason is kept in the audit trail.
            </Text>
          }
          minLength={5}
          suggestions={[
            'Research parameter, not reported',
            'A flag the analyser sends as a channel',
            'Calculated on the report from other tests',
          ]}
          confirmLabel="Ignore channel"
          busy={ignore.isPending}
          onCancel={() => setIgnoring(null)}
          onConfirm={(reason) => ignore.mutate({ id: ignoring.id, reason })}
        />
      )}
    </>
  );
}
