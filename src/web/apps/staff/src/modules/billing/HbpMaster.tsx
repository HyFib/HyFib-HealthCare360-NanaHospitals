import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { ArrowUploadRegular, DocumentRegular } from '@fluentui/react-icons';
import { formatDateTime, formatInr } from '@hc360/core';
import { DataTable, StatusBadge, type StatusTone } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { type ChangeEvent, useRef, useState } from 'react';
import { approvalKeys } from '@/modules/approvals/api';
import { hbpApi, hbpKeys, hbpQueries, type HbpImport, type HbpImportAsked, type HbpImportStatus } from './hbpApi';
import { ActionError } from './ui';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  file: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  list: { margin: 0, paddingInlineStart: '20px', maxHeight: '200px', overflowY: 'auto' },
  muted: { color: tokens.colorNeutralForeground3 },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', paddingBlock: '6px', whiteSpace: 'normal' },
  hash: { fontFamily: tokens.fontFamilyMonospace, overflowWrap: 'anywhere' },
});

/** What each state of an import is called, and its colour. */
const IMPORT_STATUS: Record<HbpImportStatus, { label: string; tone: StatusTone }> = {
  Waiting: { label: 'Waiting for approval', tone: 'warning' },
  Applied: { label: 'Applied', tone: 'success' },
  Rejected: { label: 'Refused', tone: 'danger' },
  Withdrawn: { label: 'Withdrawn', tone: 'informative' },
  Expired: { label: 'Expired', tone: 'informative' },
  NotApplied: { label: 'Not applied', tone: 'danger' },
};

/** What an import reads and changes, in a line. */
function changes(i: Pick<HbpImport, 'read' | 'added' | 'updated' | 'retired'>) {
  return `${i.read} procedures read: ${i.added} new, ${i.updated} changed, ${i.retired} retired`;
}

const MASTER_FILE_TYPES = '.xlsx,.csv,text/csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';

/** The HBP master's procedures matching the search, with their scheme rate, stay and reservation. */
export function HbpProcedures({ search, includeRetired }: { search: string; includeRetired: boolean }) {
  const styles = useStyles();
  const procedures = useQuery(hbpQueries.search(search, includeRetired));
  return (
    <DataTable
      ariaLabel="HBP procedures"
      rows={procedures.data}
      loading={procedures.isLoading}
      error={procedures.error}
      onRetry={() => void procedures.refetch()}
      getRowId={(p) => p.code}
      columns={[
        { key: 'code', header: 'Code', width: 110, render: (p) => <Text weight="semibold">{p.code}</Text> },
        {
          key: 'procedure',
          header: 'Procedure',
          render: (p) => (
            <div className={styles.cell}>
              <Text>{p.procedureName}</Text>
              <Caption1 className={styles.muted}>
                {[p.specialty ?? p.specialtyCode, p.packageName, p.stratification].filter(Boolean).join(' · ')}
              </Caption1>
            </div>
          ),
        },
        {
          key: 'rate',
          header: 'Scheme rate',
          align: 'end',
          width: 130,
          render: (p) => (p.rate !== null ? formatInr(p.rate) : '—'),
        },
        {
          key: 'stay',
          header: 'Stay',
          align: 'end',
          width: 90,
          priority: 'low',
          render: (p) => (p.lengthOfStayDays ? `${p.lengthOfStayDays} days` : '—'),
        },
        {
          key: 'status',
          header: 'Status',
          width: 190,
          render: (p) => (
            <div className={styles.cell}>
              <StatusBadge status={p.isActive ? 'Active' : 'Retired'} tone={p.isActive ? 'success' : 'informative'} />
              {p.reservedForPublic && <Caption1>Reserved for public hospitals</Caption1>}
              <Caption1 className={styles.muted}>{p.masterVersion}</Caption1>
            </div>
          ),
        },
      ]}
      emptyTitle={search ? 'No procedure matches' : 'No HBP master imported yet'}
      emptyDescription={search ? undefined : "Import NHA's HBP master to map packages to the scheme's procedures."}
      size="small"
    />
  );
}

/**
 * Importing NHA's HBP master: the file, its version name and the reason for the approver. The import is staged: it
 * changes the master only once another person who approves prices approves it, and says what it would change.
 */
export function ImportHbpDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const input = useRef<HTMLInputElement>(null);
  const [file, setFile] = useState<File | null>(null);
  const [version, setVersion] = useState('HBP 2022');
  const [reason, setReason] = useState('');
  const [asked, setAsked] = useState<HbpImportAsked | null>(null);

  const upload = useMutation({
    mutationFn: () => hbpApi.importMaster(file!, version.trim(), reason.trim()),
    onSuccess: (staged) => {
      setAsked(staged);
      void queryClient.invalidateQueries({ queryKey: hbpKeys.all });
      void queryClient.invalidateQueries({ queryKey: approvalKeys.all });
    },
  });

  const choose = (event: ChangeEvent<HTMLInputElement>) => {
    setFile(event.target.files?.[0] ?? null);
    setAsked(null);
    upload.reset();
    event.target.value = '';
  };

  const busy = upload.isPending;
  const close = () => {
    if (!busy) {
      setFile(null);
      setReason('');
      setAsked(null);
      upload.reset();
      onClose();
    }
  };

  const staged = asked?.import;
  const unread = staged ? staged.problemCount - staged.problems.length : 0;
  return (
    <Dialog open={open} onOpenChange={(_, data) => !data.open && close()}>
      <DialogSurface style={{ maxWidth: 620 }}>
        <DialogBody>
          <DialogTitle>Import the HBP master</DialogTitle>
          <DialogContent className={styles.body}>
            <Text>
              Import AB PM-JAY&apos;s Health Benefit Package master as NHA or the State Health Agency sends it, as Excel
              (.xlsx) or CSV, at most 10 MB. HC360 finds the columns by their headings. The master changes only when
              another person who approves prices approves the import. Procedures the new master no longer lists are then
              retired, not deleted: packages mapped to them keep their code until changed.
            </Text>
            <div className={styles.file}>
              <Button icon={<DocumentRegular />} onClick={() => input.current?.click()} disabled={busy}>
                {file ? 'Choose another file' : 'Choose file'}
              </Button>
              {file && <Text>{file.name}</Text>}
              <input
                ref={input}
                type="file"
                accept={MASTER_FILE_TYPES}
                hidden
                tabIndex={-1}
                aria-label="HBP master file"
                onChange={choose}
              />
            </div>
            <Field label="Master version" hint="As NHA names it, for example HBP 2022" required>
              <Input value={version} maxLength={64} onChange={(_, data) => setVersion(data.value)} />
            </Field>
            <Field
              label="Reason, for the approver"
              hint="For example, the NHA or State Health Agency circular that issued this master"
            >
              <Textarea value={reason} onChange={(_, d) => setReason(d.value)} maxLength={500} rows={2} />
            </Field>
            <ActionError error={upload.error} title="Could not import the master" />
            {busy && <Spinner size="small" labelPosition="after" label="Reading the master…" />}
            {staged && (
              <MessageBar intent={staged.problemCount > 0 ? 'warning' : 'info'} layout="multiline">
                <MessageBarBody>
                  <MessageBarTitle>Asked for approval</MessageBarTitle>
                  {staged.masterVersion}: {changes(staged)}. Nothing in the master changes until another person who
                  approves prices approves it.
                  <div>
                    <Caption1 className={styles.hash}>File SHA-256 {staged.fileSha256}</Caption1>
                  </div>
                  {staged.problems.length > 0 && (
                    <ul className={styles.list} aria-label="Rows not read">
                      {staged.problems.map((problem) => (
                        <li key={problem}>{problem}</li>
                      ))}
                      {unread > 0 && <li>…and {unread} more rows not read.</li>}
                    </ul>
                  )}
                </MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={close} disabled={busy}>
              {staged ? 'Close' : 'Cancel'}
            </Button>
            {!staged && (
              <Button
                appearance="primary"
                icon={busy ? <Spinner size="tiny" aria-hidden /> : <ArrowUploadRegular />}
                disabled={!file || !version.trim() || busy}
                disabledFocusable={busy}
                onClick={() => upload.mutate()}
              >
                Import
              </Button>
            )}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

/**
 * The master's imports, newest first: which master was put in force when, from which file (its SHA-256), asked for and
 * approved by whom, and an import still waiting for approval.
 */
export function HbpImports() {
  const styles = useStyles();
  const imports = useQuery(hbpQueries.imports());
  return (
    <DataTable
      ariaLabel="Imports of the HBP master"
      rows={imports.data}
      loading={imports.isLoading}
      error={imports.error}
      onRetry={() => void imports.refetch()}
      getRowId={(i) => i.id}
      columns={[
        {
          key: 'master',
          header: 'Master',
          render: (i) => (
            <div className={styles.cell}>
              <Text weight="semibold">{i.masterVersion}</Text>
              <Caption1 className={styles.muted}>{changes(i)}</Caption1>
              <Caption1 className={styles.hash} title={`SHA-256 of ${i.fileName}`}>
                {i.fileName} · SHA-256 {i.fileSha256.slice(0, 16)}…
              </Caption1>
            </div>
          ),
        },
        {
          key: 'status',
          header: 'Status',
          width: 170,
          render: (i) => (
            <StatusBadge status={i.status} label={IMPORT_STATUS[i.status].label} tone={IMPORT_STATUS[i.status].tone} />
          ),
        },
        {
          key: 'asked',
          header: 'Asked for',
          width: 190,
          priority: 'low',
          render: (i) => (
            <div className={styles.cell}>
              <Text>{i.askedBy}</Text>
              <Caption1 className={styles.muted}>{formatDateTime(i.askedAt)}</Caption1>
            </div>
          ),
        },
        {
          key: 'decided',
          header: 'Decided',
          width: 220,
          render: (i) =>
            i.decidedBy ? (
              <div className={styles.cell}>
                <Text>{i.decidedBy}</Text>
                <Caption1 className={styles.muted}>{formatDateTime(i.decidedAt)}</Caption1>
                {i.note && <Caption1>{i.note}</Caption1>}
              </div>
            ) : (
              '—'
            ),
        },
      ]}
      emptyTitle="No import yet"
      emptyDescription="An import of NHA's master shows here, waiting for approval and then applied."
      size="small"
    />
  );
}
