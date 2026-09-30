import {
  Accordion,
  AccordionHeader,
  AccordionItem,
  AccordionPanel,
  Button,
  Caption1,
  DrawerBody,
  DrawerHeader,
  DrawerHeaderTitle,
  makeStyles,
  OverlayDrawer,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { ArrowDownloadRegular, DismissRegular, HistoryRegular } from '@fluentui/react-icons';
import { formatDateTime } from '@hc360/core';
import { EmptyState, ErrorState, LoadingState, Pagination, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { analyticsApi, analyticsQueries, type DeliveryDto, type ScheduleDto } from './api';
import { saveBlob } from './download';
import { describeRange, formatBytes, formatCount } from './format';
import { DELIVERY_STATUS_LABELS, DELIVERY_TRIGGER_LABELS, RECIPIENT_STATUS_LABELS } from './labels';

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: '4px' },
  header: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0, textAlign: 'start' },
  headLine: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  panel: { display: 'flex', flexDirection: 'column', gap: '8px', padding: '4px 0 12px' },
  recipients: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) auto', gap: '4px 12px', alignItems: 'center' },
  error: { color: tokens.colorPaletteRedForeground1 },
  title: { display: 'flex', alignItems: 'center', gap: '8px' },
});

const PAGE_SIZE = 10;

/** Each file a schedule produced, who it reached, and a way to download it again while it is kept. */
export function DeliveriesDrawer({ schedule, onClose }: { schedule: ScheduleDto; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const [page, setPage] = useState(1);
  const deliveries = useQuery(analyticsQueries.deliveries(schedule.id, page));

  const download = useMutation({
    mutationFn: async (delivery: DeliveryDto) => {
      const blob = await analyticsApi.deliveryFile(delivery.id);
      saveBlob(blob, delivery.fileName ?? `${schedule.reportId}.${delivery.format === 'Csv' ? 'csv' : 'xlsx'}`);
    },
    onError: (error) => notify.error(error, 'Could not download the file'),
  });

  return (
    <OverlayDrawer open position="end" size="medium" onOpenChange={(_, data) => !data.open && onClose()}>
      <DrawerHeader>
        <DrawerHeaderTitle
          action={<Button appearance="subtle" aria-label="Close" icon={<DismissRegular />} onClick={onClose} />}
        >
          <span className={styles.title}>
            <HistoryRegular aria-hidden />
            Delivery history
          </span>
        </DrawerHeaderTitle>
        <Caption1 className={styles.muted}>
          {schedule.name} · {schedule.when}
        </Caption1>
      </DrawerHeader>
      <DrawerBody>
        {deliveries.isLoading ? (
          <LoadingState />
        ) : deliveries.error && !deliveries.data ? (
          <ErrorState error={deliveries.error} onRetry={() => void deliveries.refetch()} />
        ) : !deliveries.data || deliveries.data.items.length === 0 ? (
          <EmptyState
            title="Nothing sent yet"
            description={
              schedule.nextRunAt
                ? `The first e-mail goes out ${formatDateTime(schedule.nextRunAt)}.`
                : 'Turn the schedule on to start sending.'
            }
          />
        ) : (
          <>
            <Accordion collapsible multiple className={styles.list}>
              {deliveries.data.items.map((delivery) => (
                <AccordionItem key={delivery.id} value={delivery.id}>
                  <AccordionHeader expandIconPosition="end">
                    <span className={styles.header}>
                      <span className={styles.headLine}>
                        <Text weight="semibold">{formatDateTime(delivery.createdAt)}</Text>
                        <StatusBadge
                          status={
                            delivery.status === 'Ready'
                              ? delivery.pending > 0
                                ? 'Pending'
                                : 'Delivered'
                              : delivery.status
                          }
                          label={statusText(delivery)}
                        />
                      </span>
                      <Caption1 className={styles.muted}>
                        {DELIVERY_TRIGGER_LABELS[delivery.trigger]}
                        {delivery.periodFrom && delivery.periodTo
                          ? ` · ${describeRange(delivery.periodFrom, delivery.periodTo)}`
                          : ''}
                        {delivery.status === 'Ready'
                          ? ` · ${formatCount(delivery.rowCount, 'row')} · ${formatBytes(delivery.sizeBytes)}`
                          : ''}
                      </Caption1>
                    </span>
                  </AccordionHeader>
                  <AccordionPanel>
                    <div className={styles.panel}>
                      {delivery.error && <Text className={styles.error}>{delivery.error}</Text>}
                      {delivery.recipients.length > 0 && (
                        <div className={styles.recipients}>
                          {delivery.recipients.map((recipient) => (
                            <div key={recipient.email} style={{ display: 'contents' }}>
                              <span>
                                <Text>{recipient.email}</Text>
                                {recipient.lastError && (
                                  <Caption1 block className={styles.error}>
                                    {recipient.lastError}
                                  </Caption1>
                                )}
                              </span>
                              <StatusBadge
                                status={recipient.status === 'Cancelled' ? 'Failed' : recipient.status}
                                label={recipientText(recipient.status, recipient.attempts, recipient.sentAt)}
                              />
                            </div>
                          ))}
                        </div>
                      )}
                      <div>
                        <Button
                          icon={
                            download.isPending && download.variables?.id === delivery.id ? (
                              <Spinner size="tiny" />
                            ) : (
                              <ArrowDownloadRegular />
                            )
                          }
                          disabled={!delivery.fileAvailable || download.isPending}
                          onClick={() => download.mutate(delivery)}
                        >
                          {delivery.fileAvailable
                            ? delivery.format === 'Csv'
                              ? 'Download CSV file'
                              : 'Download Excel file'
                            : 'File no longer kept'}
                        </Button>
                      </div>
                    </div>
                  </AccordionPanel>
                </AccordionItem>
              ))}
            </Accordion>
            {deliveries.data.totalCount > PAGE_SIZE && (
              <Pagination
                page={page}
                pageSize={PAGE_SIZE}
                totalCount={deliveries.data.totalCount}
                onPageChange={setPage}
              />
            )}
          </>
        )}
      </DrawerBody>
    </OverlayDrawer>
  );
}

function statusText(delivery: DeliveryDto): string {
  if (delivery.status !== 'Ready') {
    return DELIVERY_STATUS_LABELS[delivery.status];
  }
  const total = delivery.recipients.length;
  return delivery.pending > 0 ? `Sending (${delivery.sent} of ${total})` : `Sent to ${delivery.sent} of ${total}`;
}

function recipientText(
  status: DeliveryDto['recipients'][number]['status'],
  attempts: number,
  sentAt: string | null,
): string {
  if (status === 'Sent') {
    return sentAt ? `Sent ${formatDateTime(sentAt)}` : 'Sent';
  }
  return attempts > 1 ? `${RECIPIENT_STATUS_LABELS[status]} (${attempts} tries)` : RECIPIENT_STATUS_LABELS[status];
}
