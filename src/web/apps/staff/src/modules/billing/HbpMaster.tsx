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
  tokens,
} from '@fluentui/react-components';
import { ArrowUploadRegular, DocumentRegular } from '@fluentui/react-icons';
import { formatInr } from '@hc360/core';
import { DataTable, StatusBadge } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { type ChangeEvent, useRef, useState } from 'react';
import { hbpApi, hbpKeys, hbpQueries, type HbpImportResult } from './hbpApi';
import { ActionError } from './ui';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  file: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  list: { margin: 0, paddingInlineStart: '20px', maxHeight: '200px', overflowY: 'auto' },
  muted: { color: tokens.colorNeutralForeground3 },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', paddingBlock: '6px', whiteSpace: 'normal' },
});

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

/** Importing NHA's HBP master: the file, its version name, and what the import changed. */
export function ImportHbpDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const input = useRef<HTMLInputElement>(null);
  const [file, setFile] = useState<File | null>(null);
  const [version, setVersion] = useState('HBP 2022');
  const [result, setResult] = useState<HbpImportResult | null>(null);

  const upload = useMutation({
    mutationFn: () => hbpApi.importMaster(file!, version.trim()),
    onSuccess: (imported) => {
      setResult(imported);
      void queryClient.invalidateQueries({ queryKey: hbpKeys.all });
    },
  });

  const choose = (event: ChangeEvent<HTMLInputElement>) => {
    setFile(event.target.files?.[0] ?? null);
    setResult(null);
    upload.reset();
    event.target.value = '';
  };

  const busy = upload.isPending;
  const close = () => {
    if (!busy) {
      setFile(null);
      setResult(null);
      upload.reset();
      onClose();
    }
  };

  return (
    <Dialog open={open} onOpenChange={(_, data) => !data.open && close()}>
      <DialogSurface style={{ maxWidth: 620 }}>
        <DialogBody>
          <DialogTitle>Import the HBP master</DialogTitle>
          <DialogContent className={styles.body}>
            <Text>
              Import AB PM-JAY&apos;s Health Benefit Package master as NHA or the State Health Agency sends it, as Excel
              (.xlsx) or CSV, at most 10 MB. HC360 finds the columns by their headings. Procedures the new master no
              longer lists are retired, not deleted: packages mapped to them keep their code until changed.
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
            <ActionError error={upload.error} title="Could not import the master" />
            {busy && <Spinner size="small" labelPosition="after" label="Reading the master…" />}
            {result && (
              <MessageBar intent={result.problems.length > 0 ? 'warning' : 'success'} layout="multiline">
                <MessageBarBody>
                  <MessageBarTitle>Imported {result.masterVersion}</MessageBarTitle>
                  {result.read} procedures read: {result.added} new, {result.updated} changed, {result.retired} retired.
                  {result.problems.length > 0 && (
                    <ul className={styles.list} aria-label="Rows not read">
                      {result.problems.map((problem) => (
                        <li key={problem}>{problem}</li>
                      ))}
                    </ul>
                  )}
                </MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={close} disabled={busy}>
              {result ? 'Close' : 'Cancel'}
            </Button>
            {!result && (
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
