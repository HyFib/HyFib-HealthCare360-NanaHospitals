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
  Dropdown,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Option,
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
import {
  blockProgress,
  continuityApi,
  continuityKeys,
  continuityQueries,
  KIND_LABELS,
  lateRequestOf,
  MAX_NUMBERS,
  windowWords,
  type BackEntryRecord,
  type BackEntryWindowDto,
  type DowntimeNumberBlockDetail,
  type DowntimeNumberKind,
  type DowntimePack,
  type RequestLateBackEntry,
} from './continuityApi';
import { DowntimePackView } from './DowntimePackView';
import { printNumberSheet, printPack } from './packPrint';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', paddingBlock: '6px', whiteSpace: 'normal' },
  muted: { color: tokens.colorNeutralForeground3 },
  mono: { fontFamily: tokens.fontFamilyMonospace },
  toolbar: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'center' },
});

const problemOf = (error: unknown, fallback: string) => (isApiError(error) ? error.message : fallback);

const STATUS_LABELS = { Open: 'Not entered', Entered: 'Entered', Voided: 'Voided' } as const;

// ------------------------------------------------------------------------------------------------ the pack

/** Print the pack now (kept, so the offline copy takes it next), and see what was printed. */
export function PackPanel() {
  const styles = useStyles();
  const notify = useNotify();
  const [pack, setPack] = useState<DowntimePack | null>(null);
  const make = useMutation({
    mutationFn: () => continuityApi.printPack(),
    onSuccess: async (made) => {
      setPack(made);
      try {
        await printPack(made);
      } catch {
        notify.error('The pack was made but could not be printed. Print it from the copy below.');
      }
    },
  });

  return (
    <div className={styles.body}>
      <Text>
        The census by ward with diagnoses, allergies and isolation; each in-patient&apos;s medication sheet with the
        doses due in the next 24 hours; pending lab and imaging orders; the OT list; and the downtime numbers each
        counter holds. HC360 also makes one every night.
      </Text>
      <div className={styles.toolbar}>
        <Button appearance="primary" icon={<PrintRegular />} disabled={make.isPending} onClick={() => make.mutate()}>
          {make.isPending ? 'Making the pack…' : 'Print pack now'}
        </Button>
        {pack && (
          <Button icon={<PrintRegular />} onClick={() => void printPack(pack)}>
            Print again
          </Button>
        )}
      </div>
      {make.error && (
        <MessageBar intent="error">
          <MessageBarBody>{problemOf(make.error, 'The pack could not be made.')}</MessageBarBody>
        </MessageBar>
      )}
      {pack && <DowntimePackView pack={pack} />}
    </div>
  );
}

// ------------------------------------------------------------------------------------------------ number blocks

/** The facility's visit, bill and receipt number blocks, newest first. */
export function NumberBlocks({ onOpen }: { onOpen: (id: string) => void }) {
  const styles = useStyles();
  const blocks = useQuery(continuityQueries.blocks());
  return (
    <DataTable
      ariaLabel="Downtime number blocks"
      rows={blocks.data}
      loading={blocks.isLoading}
      error={blocks.error}
      onRetry={() => void blocks.refetch()}
      getRowId={(b) => b.id}
      onRowClick={(b) => onOpen(b.id)}
      columns={[
        {
          key: 'counter',
          header: 'Counter',
          render: (b) => (
            <div className={styles.cell}>
              <Text weight="semibold">{b.counter}</Text>
              <Caption1 className={styles.muted}>
                {KIND_LABELS[b.kind]} · issued {formatDateTime(b.issuedAt)}
                {b.issuedByName ? ` by ${b.issuedByName}` : ''}
              </Caption1>
            </div>
          ),
        },
        {
          key: 'range',
          header: 'Numbers',
          render: (b) => (
            <Text className={styles.mono}>
              {b.firstNumber} – {b.lastNumber}
            </Text>
          ),
        },
        { key: 'progress', header: 'Entered', render: (b) => blockProgress(b) },
        {
          key: 'state',
          header: 'State',
          render: (b) => (
            <StatusBadge
              status={b.returnedAt ? 'Returned' : b.open > 0 ? 'With the counter' : 'All accounted for'}
              tone={b.returnedAt ? 'success' : b.open > 0 ? 'informative' : 'success'}
            />
          ),
        },
      ]}
    />
  );
}

export function IssueNumbersDialog({
  open,
  onClose,
  onIssued,
}: {
  open: boolean;
  onClose: () => void;
  onIssued: (detail: DowntimeNumberBlockDetail) => void;
}) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const [kind, setKind] = useState<DowntimeNumberKind>('Visit');
  const [counter, setCounter] = useState('');
  const [count, setCount] = useState(50);
  const issue = useMutation({
    mutationFn: () => continuityApi.issue({ kind, counter: counter.trim(), count }),
    onSuccess: (detail) => {
      void queryClient.invalidateQueries({ queryKey: continuityKeys.blocks });
      setCounter('');
      onIssued(detail);
    },
  });
  const busy = issue.isPending;
  const valid = counter.trim().length > 0 && Number.isInteger(count) && count >= 1 && count <= MAX_NUMBERS;
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
          <DialogTitle>Issue downtime numbers</DialogTitle>
          <DialogContent className={styles.body}>
            <Text>
              The numbers are taken from their series now, so HC360 never issues them again. Print the sheet and keep it
              at the counter; return the block when it is no longer needed.
            </Text>
            <Field label="Numbers for" required>
              <Dropdown
                value={KIND_LABELS[kind]}
                selectedOptions={[kind]}
                onOptionSelect={(_, data) => data.optionValue && setKind(data.optionValue as DowntimeNumberKind)}
              >
                {(Object.keys(KIND_LABELS) as DowntimeNumberKind[]).map((k) => (
                  <Option key={k} value={k}>
                    {KIND_LABELS[k]}
                  </Option>
                ))}
              </Dropdown>
            </Field>
            <Field label="Counter" hint='Which counter keeps it, for example "OPD billing counter 1"' required>
              <Input value={counter} maxLength={100} onChange={(_, data) => setCounter(data.value)} />
            </Field>
            <Field label="How many" hint={`About a day's work at that counter; at most ${MAX_NUMBERS}`} required>
              <SpinButton
                value={count}
                min={1}
                max={MAX_NUMBERS}
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
                <MessageBarBody>{problemOf(issue.error, 'The numbers could not be issued.')}</MessageBarBody>
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

/** One block: its numbers and what was entered under each, printing its sheet, and returning it. */
export function NumberBlockDrawer({ blockId, onClose }: { blockId: string; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const detail = useQuery(continuityQueries.block(blockId));
  const [returning, setReturning] = useState(false);
  const [note, setNote] = useState('');
  const block = detail.data;
  const giveBack = useMutation({
    mutationFn: () => continuityApi.return(blockId, { note: note.trim() || null, version: block?.block.version ?? 0 }),
    onSuccess: (returned) => {
      queryClient.setQueryData(continuityKeys.block(blockId), returned);
      void queryClient.invalidateQueries({ queryKey: continuityKeys.blocks });
      setReturning(false);
      notify.success(`Block returned: ${returned.block.voided} numbers voided.`);
    },
  });

  return (
    <OverlayDrawer open position="end" size="large" onOpenChange={(_, data) => !data.open && onClose()}>
      <DrawerHeader>
        <DrawerHeaderTitle
          action={<Button appearance="subtle" aria-label="Close" icon={<DismissRegular />} onClick={onClose} />}
        >
          {block ? `${block.block.counter}: ${KIND_LABELS[block.block.kind]}` : 'Downtime numbers'}
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody className={styles.body}>
        {block && (
          <div className={styles.toolbar}>
            <Text>{blockProgress(block.block)}</Text>
            <Button
              icon={<PrintRegular />}
              onClick={() => void printNumberSheet(block).catch(() => notify.error('The sheet could not be printed.'))}
            >
              Print sheet
            </Button>
            {!block.block.returnedAt && <Button onClick={() => setReturning(true)}>Return block</Button>}
          </div>
        )}
        {block?.block.returnedAt && (
          <MessageBar intent="success">
            <MessageBarBody>
              Returned {formatDateTime(block.block.returnedAt)}
              {block.block.returnedByName ? ` by ${block.block.returnedByName}` : ''}
              {block.block.returnNote ? `: ${block.block.returnNote}` : ''}
            </MessageBarBody>
          </MessageBar>
        )}
        <DataTable
          ariaLabel="Numbers of the block"
          rows={block?.numbers}
          loading={detail.isLoading}
          error={detail.error}
          onRetry={() => void detail.refetch()}
          getRowId={(n) => n.number}
          columns={[
            { key: 'position', header: '#', width: 50, align: 'end', render: (n) => n.position },
            {
              key: 'number',
              header: 'Number',
              width: 170,
              render: (n) => <Text className={styles.mono}>{n.number}</Text>,
            },
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
                      {n.patientName ?? 'Patient'}, on paper {formatDateTime(n.paperAt)}; entered{' '}
                      {formatDateTime(n.enteredAt)}
                      {n.enteredByName ? ` by ${n.enteredByName}` : ''}
                      {n.lateBackEntry ? ' (late, approved)' : ''}
                    </Caption1>
                  )}
                  {n.status === 'Voided' && <Caption1>{n.voidReason}</Caption1>}
                </div>
              ),
            },
          ]}
        />
      </DrawerBody>
      <Dialog open={returning} onOpenChange={(_, data) => !data.open && !giveBack.isPending && setReturning(false)}>
        <DialogSurface style={{ maxWidth: 480 }}>
          <DialogBody>
            <DialogTitle>Return the block</DialogTitle>
            <DialogContent className={styles.body}>
              <Text>
                Every number not entered is voided as returned unused, and the return is recorded in the audit trail.
                Enter any paper still to come first.
              </Text>
              <Field label="Note" hint="What came back with it, if anything to say">
                <Textarea value={note} maxLength={300} onChange={(_, data) => setNote(data.value)} />
              </Field>
              {giveBack.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{problemOf(giveBack.error, 'The block could not be returned.')}</MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={() => setReturning(false)} disabled={giveBack.isPending}>
                Cancel
              </Button>
              <Button appearance="primary" disabled={giveBack.isPending || !block} onClick={() => giveBack.mutate()}>
                Return
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </OverlayDrawer>
  );
}

// ------------------------------------------------------------------------------------------------ back-entry

/** The back-entry windows in force, each with its default and the most an administrator may set. */
export function BackEntryWindows() {
  const windows = useQuery(continuityQueries.windows());
  return (
    <DataTable
      ariaLabel="Back-entry windows"
      rows={windows.data}
      loading={windows.isLoading}
      error={windows.error}
      onRetry={() => void windows.refetch()}
      getRowId={(w) => w.record}
      columns={[
        { key: 'name', header: 'Record', render: (w) => w.name },
        { key: 'hours', header: 'Window', render: (w) => windowWords(w.hours) },
        { key: 'default', header: 'Default', render: (w) => windowWords(w.defaultHours) },
        { key: 'max', header: 'At most', render: (w) => windowWords(w.maxHours) },
      ]}
    />
  );
}

/** Late back-entry approvals of the last 30 days at this facility. */
export function LateBackEntries() {
  const styles = useStyles();
  const grants = useQuery(continuityQueries.grants());
  const windows = useQuery(continuityQueries.windows());
  const names = new Map((windows.data ?? []).map((w) => [w.record, w.name]));
  return (
    <DataTable
      ariaLabel="Late back-entry approvals"
      rows={grants.data}
      loading={grants.isLoading}
      error={grants.error}
      onRetry={() => void grants.refetch()}
      getRowId={(g) => g.id}
      columns={[
        {
          key: 'what',
          header: 'Records',
          render: (g) => (
            <div className={styles.cell}>
              <Text weight="semibold">
                {g.maxRecords} × {names.get(g.record) ?? g.record}
              </Text>
              <Caption1 className={styles.muted}>
                Paper dated {formatDateTime(g.paperFrom)} to {formatDateTime(g.paperTo)}
              </Caption1>
            </div>
          ),
        },
        {
          key: 'who',
          header: 'Asked / approved',
          render: (g) => (
            <div className={styles.cell}>
              <Text>{g.requestedByName}</Text>
              <Caption1 className={styles.muted}>
                approved by {g.approvedByName ?? '—'} {formatDateTime(g.approvedAt)}
              </Caption1>
            </div>
          ),
        },
        { key: 'used', header: 'Used', render: (g) => `${g.used} of ${g.maxRecords}` },
        {
          key: 'state',
          header: 'State',
          render: (g) => (
            <StatusBadge
              status={g.usable ? `Usable until ${formatDateTime(g.expiresAt)}` : 'Used or expired'}
              tone={g.usable ? 'informative' : 'success'}
            />
          ),
        },
      ]}
    />
  );
}

/** Ask a second person to approve entering paper older than its window (answered 202: it waits in the approvals inbox). */
export function RequestLateDialog({
  open,
  onClose,
  windows,
}: {
  open: boolean;
  onClose: () => void;
  windows: BackEntryWindowDto[];
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [record, setRecord] = useState<BackEntryRecord>('NursingChart');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [records, setRecords] = useState('');
  const [reason, setReason] = useState('');
  const [problem, setProblem] = useState<string | null>(null);
  const ask = useMutation({
    mutationFn: (request: RequestLateBackEntry) => continuityApi.requestLate(request),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: continuityKeys.grants });
      notify.success('Asked: it waits for a second person in the approvals inbox.');
      onClose();
    },
  });
  const chosen = windows.find((w) => w.record === record);
  const submit = () => {
    const checked = lateRequestOf({ record, from, to, records, reason });
    setProblem(checked.problem);
    if (checked.request) {
      ask.mutate(checked.request);
    }
  };

  return (
    <Dialog open={open} onOpenChange={(_, data) => !data.open && !ask.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <DialogBody>
          <DialogTitle>Ask for a late back-entry</DialogTitle>
          <DialogContent className={styles.body}>
            <Text>
              Paper older than its window is entered only after someone else approves. Ask once for the batch, with the
              count from the back-entry tally.
            </Text>
            <Field label="Record" required hint={chosen ? `Window: ${windowWords(chosen.hours)}` : undefined}>
              <Dropdown
                value={chosen?.name ?? record}
                selectedOptions={[record]}
                onOptionSelect={(_, data) => data.optionValue && setRecord(data.optionValue as BackEntryRecord)}
              >
                {windows.map((w) => (
                  <Option key={w.record} value={w.record}>
                    {w.name}
                  </Option>
                ))}
              </Dropdown>
            </Field>
            <Field label="Earliest time on the papers" required>
              <Input type="datetime-local" value={from} onChange={(_, data) => setFrom(data.value)} />
            </Field>
            <Field label="Latest time on the papers" required>
              <Input type="datetime-local" value={to} onChange={(_, data) => setTo(data.value)} />
            </Field>
            <Field label="How many records" required>
              <Input type="number" min={1} max={5000} value={records} onChange={(_, data) => setRecords(data.value)} />
            </Field>
            <Field label="Why late" required>
              <Textarea value={reason} maxLength={500} onChange={(_, data) => setReason(data.value)} />
            </Field>
            {(problem || ask.error) && (
              <MessageBar intent="error">
                <MessageBarBody>{problem ?? problemOf(ask.error, 'The request could not be sent.')}</MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={ask.isPending}>
              Cancel
            </Button>
            <Button appearance="primary" disabled={ask.isPending} onClick={submit}>
              Ask for approval
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
