import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DrawerBody,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  SpinButton,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { DismissRegular, PrintRegular } from '@fluentui/react-icons';
import { formatDateTime, isApiError } from '@hc360/core';
import { DataTable, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { printDowntimeUhidSheet } from '@/lib/print';
import {
  downtimeApi,
  downtimeKeys,
  downtimeQueries,
  MAX_BLOCK,
  progressText,
  STATUS_LABELS,
  type DowntimeUhidBlockDetail,
  type DowntimeUhidDto,
} from './downtimeApi';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', paddingBlock: '6px', whiteSpace: 'normal' },
  muted: { color: tokens.colorNeutralForeground3 },
  mono: { fontFamily: tokens.fontFamilyMonospace },
  toolbar: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'center' },
});

const problemOf = (error: unknown, fallback: string) => (isApiError(error) ? error.message : fallback);

/** The facility's downtime UHID blocks, newest first, with how far each has been entered. */
export function DowntimeBlocks({ onOpen }: { onOpen: (id: string) => void }) {
  const styles = useStyles();
  const blocks = useQuery(downtimeQueries.list());
  return (
    <DataTable
      ariaLabel="Downtime UHID blocks"
      rows={blocks.data}
      loading={blocks.isLoading}
      error={blocks.error}
      onRetry={() => void blocks.refetch()}
      getRowId={(b) => b.id}
      onRowClick={(b) => onOpen(b.id)}
      columns={[
        {
          key: 'kit',
          header: 'Kit',
          render: (b) => (
            <div className={styles.cell}>
              <Text weight="semibold">{b.label}</Text>
              <Caption1 className={styles.muted}>
                Issued {formatDateTime(b.issuedAt)}
                {b.issuedByName ? ` by ${b.issuedByName}` : ''}
              </Caption1>
            </div>
          ),
        },
        {
          key: 'uhids',
          header: 'UHIDs',
          width: 230,
          render: (b) => (
            <Text className={styles.mono}>
              {b.firstUhid}
              {b.count > 1 ? ` – ${b.lastUhid}` : ''}
            </Text>
          ),
        },
        { key: 'progress', header: 'Entered', width: 200, render: (b) => progressText(b) },
        {
          key: 'open',
          header: 'Status',
          width: 140,
          priority: 'low',
          render: (b) =>
            b.open === 0 ? (
              <StatusBadge status="Accounted for" tone="success" />
            ) : (
              <StatusBadge status={`${b.open} not entered`} tone="informative" />
            ),
        },
      ]}
      emptyTitle="No downtime UHIDs issued yet"
      emptyDescription="Issue a block for each registration desk's downtime kit, print its sheet and keep it with the paper forms."
      size="small"
    />
  );
}

/** Issuing a block of UHIDs for a desk's downtime kit; the new block opens so its sheet can be printed. */
export function IssueDowntimeUhidsDialog({
  open,
  onClose,
  onIssued,
}: {
  open: boolean;
  onClose: () => void;
  onIssued: (detail: DowntimeUhidBlockDetail) => void;
}) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const [label, setLabel] = useState('');
  const [count, setCount] = useState(25);
  const issue = useMutation({
    mutationFn: () => downtimeApi.issue({ label: label.trim(), count }),
    onSuccess: (detail) => {
      void queryClient.invalidateQueries({ queryKey: downtimeKeys.all });
      setLabel('');
      onIssued(detail);
    },
  });
  const busy = issue.isPending;
  const valid = label.trim().length > 0 && Number.isInteger(count) && count >= 1 && count <= MAX_BLOCK;
  const close = () => {
    if (!busy) {
      issue.reset();
      onClose();
    }
  };

  return (
    <Dialog open={open} onOpenChange={(_, data) => !data.open && close()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <DialogBody>
          <DialogTitle>Issue downtime UHIDs</DialogTitle>
          <DialogContent className={styles.body}>
            <Text>
              The UHIDs are taken from the UHID series now, so HC360 will never give them to anyone else. Print the
              sheet and keep it with the desk&apos;s paper registration forms.
            </Text>
            <Field label="Kit" hint='Where it is kept, for example "Main reception, kit 2"' required>
              <Input value={label} maxLength={100} onChange={(_, data) => setLabel(data.value)} />
            </Field>
            <Field label="Number of UHIDs" hint={`A day's registrations at that desk; at most ${MAX_BLOCK}`} required>
              <SpinButton
                value={count}
                min={1}
                max={MAX_BLOCK}
                onChange={(_, data) => {
                  const next = data.value ?? Number(data.displayValue);
                  if (Number.isFinite(next)) {
                    setCount(Math.trunc(next));
                  }
                }}
              />
            </Field>
            {issue.error && (
              <MessageBar intent="error">
                <MessageBarBody>{problemOf(issue.error, 'The UHIDs could not be issued.')}</MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={close} disabled={busy}>
              Cancel
            </Button>
            <Button appearance="primary" disabled={!valid || busy} onClick={() => issue.mutate()}>
              Issue
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

/** One block: its UHIDs, the patient entered under each, printing its sheet, and voiding a number that will not be used. */
export function DowntimeBlockDrawer({ blockId, onClose }: { blockId: string; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const detail = useQuery(downtimeQueries.block(blockId));
  const [voiding, setVoiding] = useState<DowntimeUhidDto | null>(null);
  const block = detail.data;

  const print = async () => {
    if (!block) {
      return;
    }

    try {
      await printDowntimeUhidSheet({
        facilityName: block.facilityName,
        label: block.block.label,
        issuedAt: block.block.issuedAt,
        numbers: block.numbers.map((n) => ({ position: n.position, uhid: n.uhid })),
      });
    } catch {
      notify.error('The sheet could not be printed.');
    }
  };

  return (
    <OverlayDrawer open position="end" size="large" onOpenChange={(_, data) => !data.open && onClose()}>
      <DrawerHeader>
        <DrawerHeaderTitle
          action={<Button appearance="subtle" aria-label="Close" icon={<DismissRegular />} onClick={onClose} />}
        >
          {block?.block.label ?? 'Downtime UHIDs'}
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody className={styles.body}>
        {block && (
          <div className={styles.toolbar}>
            <Text>{progressText(block.block)}</Text>
            <Button icon={<PrintRegular />} onClick={() => void print()}>
              Print sheet
            </Button>
          </div>
        )}
        <DataTable
          ariaLabel="UHIDs of the block"
          rows={block?.numbers}
          loading={detail.isLoading}
          error={detail.error}
          onRetry={() => void detail.refetch()}
          getRowId={(n) => n.uhid}
          columns={[
            { key: 'position', header: '#', width: 50, align: 'end', render: (n) => n.position },
            { key: 'uhid', header: 'UHID', width: 150, render: (n) => <Text className={styles.mono}>{n.uhid}</Text> },
            {
              key: 'status',
              header: 'Status',
              render: (n) => (
                <div className={styles.cell}>
                  <StatusBadge
                    status={STATUS_LABELS[n.status]}
                    tone={n.status === 'Entered' ? 'success' : n.status === 'Voided' ? 'warning' : 'informative'}
                  />
                  {n.status === 'Entered' && (
                    <Caption1>
                      {n.patientName ?? 'Patient'}, registered {formatDateTime(n.paperRegisteredAt)}; entered{' '}
                      {formatDateTime(n.enteredAt)}
                      {n.enteredByName ? ` by ${n.enteredByName}` : ''}
                    </Caption1>
                  )}
                  {n.status === 'Voided' && (
                    <Caption1>
                      {n.voidReason}
                      {n.voidedByName ? ` (${n.voidedByName})` : ''}
                    </Caption1>
                  )}
                </div>
              ),
            },
            {
              key: 'actions',
              header: '',
              width: 90,
              render: (n) =>
                n.status === 'Open' ? (
                  <Button size="small" onClick={() => setVoiding(n)}>
                    Void
                  </Button>
                ) : null,
            },
          ]}
          size="small"
        />
      </DrawerBody>
      {voiding && <VoidDowntimeUhidDialog blockId={blockId} number={voiding} onClose={() => setVoiding(null)} />}
    </OverlayDrawer>
  );
}

function VoidDowntimeUhidDialog({
  blockId,
  number,
  onClose,
}: {
  blockId: string;
  number: DowntimeUhidDto;
  onClose: () => void;
}) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const [reason, setReason] = useState('');
  const voidNumber = useMutation({
    mutationFn: () => downtimeApi.void(blockId, { uhid: number.uhid, reason: reason.trim(), version: number.version }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: downtimeKeys.all });
      onClose();
    },
  });
  const busy = voidNumber.isPending;
  return (
    <Dialog open onOpenChange={(_, data) => !data.open && !busy && onClose()}>
      <DialogSurface style={{ maxWidth: 480 }}>
        <DialogBody>
          <DialogTitle>Void {number.uhid}</DialogTitle>
          <DialogContent className={styles.body}>
            <Text>
              A voided UHID can no longer be entered. Void only a number that was not given to a patient: a spoilt or
              lost label, or a kit withdrawn.
            </Text>
            <Field label="Reason" required>
              <Textarea value={reason} maxLength={300} onChange={(_, data) => setReason(data.value)} />
            </Field>
            {voidNumber.error && (
              <MessageBar intent="error">
                <MessageBarBody>{problemOf(voidNumber.error, 'The UHID could not be voided.')}</MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={busy}>
              Cancel
            </Button>
            <Button appearance="primary" disabled={!reason.trim() || busy} onClick={() => voidNumber.mutate()}>
              Void
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
