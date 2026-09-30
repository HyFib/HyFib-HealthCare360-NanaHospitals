import {
  Badge,
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  mergeClasses,
  Radio,
  RadioGroup,
  Spinner,
  Switch,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowSyncRegular,
  BarcodeScannerRegular,
  BeakerDismissRegular,
  CheckmarkCircleRegular,
  DeleteRegular,
  ErrorCircleRegular,
  InfoRegular,
  OpenRegular,
} from '@fluentui/react-icons';
import { formatTime, isApiError, useHotkey } from '@hc360/core';
import { EmptyState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useRef, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { LAB_PERMISSIONS, labApi, labKeys, labQueries, type SampleLookup } from '@/modules/lab/api';
import { PriorityBadge, SampleStatusBadge, TubeChip } from '@/modules/lab/badges';
import { beep, scanSoundEnabled, setScanSoundEnabled } from '@/modules/lab/beep';
import { errorMessage } from '@/modules/lab/common/errors';
import { sectionLabel } from '@/modules/lab/labels';
import { RejectSampleDialog, type RejectTarget } from '@/modules/lab/RejectSampleDialog';
import { splitScans } from '@/modules/lab/scan';

export const Route = createFileRoute('/_app/lab/receive')({
  component: ReceivePage,
});

type Mode = 'receive' | 'check';
type RowState = 'sending' | 'received' | 'problem' | 'checked';

interface ScanRow {
  key: string;
  barcode: string;
  mode: Mode;
  state: RowState;
  message: string | null;
  sampleId: string | null;
  sample: SampleLookup | null;
  at: string;
}

const MAX_ROWS = 300;
const FLUSH_DELAY_MS = 250;

const useStyles = makeStyles({
  scanBox: { display: 'flex', gap: '16px', alignItems: 'flex-end', flexWrap: 'wrap' },
  input: { flex: '1 1 360px' },
  big: { fontSize: tokens.fontSizeBase500, height: '48px' },
  last: {
    display: 'grid',
    gridTemplateColumns: 'auto minmax(0, 1fr)',
    gap: '4px 16px',
    alignItems: 'center',
    padding: '14px 18px',
    borderRadius: tokens.borderRadiusLarge,
    boxShadow: tokens.shadow4,
  },
  ok: {
    backgroundColor: tokens.colorPaletteGreenBackground1,
    borderLeft: `6px solid ${tokens.colorPaletteGreenBorder2}`,
  },
  bad: { backgroundColor: tokens.colorPaletteRedBackground1, borderLeft: `6px solid ${tokens.colorPaletteRedBorder2}` },
  info: { backgroundColor: tokens.colorNeutralBackground1, borderLeft: `6px solid ${tokens.colorBrandStroke1}` },
  lastIcon: { fontSize: '36px', display: 'flex' },
  bench: { fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold },
  counters: { display: 'flex', gap: '16px', flexWrap: 'wrap', alignItems: 'center' },
  list: { display: 'flex', flexDirection: 'column' },
  row: {
    display: 'grid',
    gridTemplateColumns: '28px minmax(120px, 160px) minmax(0, 1fr) auto',
    gap: '4px 12px',
    alignItems: 'center',
    padding: '8px 16px',
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
    '@media (max-width: 760px)': { gridTemplateColumns: '28px minmax(0, 1fr)' },
  },
  rowActions: {
    display: 'flex',
    gap: '6px',
    flexWrap: 'wrap',
    justifyContent: 'flex-end',
    '@media (max-width: 760px)': { gridColumn: '1 / -1' },
  },
  mono: { fontFamily: tokens.fontFamilyMonospace, fontWeight: tokens.fontWeightSemibold },
  muted: { color: tokens.colorNeutralForeground3 },
  green: { color: tokens.colorPaletteGreenForeground1 },
  red: { color: tokens.colorPaletteRedForeground1 },
});

/**
 * Sample receipt at the lab (SRS LIS-03): scan tubes one after another, each receipt is confirmed in the list and
 * a bad label never blocks the rack. "Check only" looks a tube up without receiving it, e.g. to reject a haemolysed
 * sample. Rejection asks for a standard reason and requests a new sample.
 */
function ReceivePage() {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const canReceive = usePermission(LAB_PERMISSIONS.receive);
  const canEnter = usePermission([LAB_PERMISSIONS.enter, LAB_PERMISSIONS.verify]);
  const inputRef = useRef<HTMLInputElement>(null);
  const pending = useRef<string[]>([]);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const [mode, setMode] = useState<Mode>('receive');
  const [value, setValue] = useState('');
  const [rows, setRows] = useState<ScanRow[]>([]);
  const [sound, setSound] = useState(scanSoundEnabled);
  const [rejecting, setRejecting] = useState<RejectTarget | null>(null);
  const [recollecting, setRecollecting] = useState<string | null>(null);

  useEffect(
    () => () => {
      if (timer.current) {
        clearTimeout(timer.current);
      }
    },
    [],
  );

  useHotkey('f2', () => inputRef.current?.focus(), { allowInInputs: true });

  const focusInput = () => setTimeout(() => inputRef.current?.focus(), 0);

  const update = (barcodes: readonly string[], patch: (row: ScanRow) => Partial<ScanRow>) =>
    setRows((current) =>
      current.map((row) =>
        barcodes.includes(row.barcode) && row.state === 'sending' ? { ...row, ...patch(row) } : row,
      ),
    );

  const attachLookups = async (barcodes: readonly string[]) => {
    await Promise.all(
      barcodes.map(async (barcode) => {
        try {
          const sample = await queryClient.fetchQuery({ ...labQueries.sampleByBarcode(barcode), staleTime: 0 });
          setRows((current) =>
            current.map((row) =>
              row.barcode === barcode && !row.sample ? { ...row, sample, sampleId: row.sampleId ?? sample.id } : row,
            ),
          );
        } catch {
          // Unknown barcodes stay without details; the outcome message already says why.
        }
      }),
    );
  };

  const flush = async () => {
    timer.current = null;
    const batch = [...new Set(pending.current)].slice(0, 200);
    pending.current = pending.current.slice(batch.length);
    if (batch.length === 0) {
      return;
    }
    try {
      const outcomes = await labApi.receive(batch);
      let problems = 0;
      for (const outcome of outcomes) {
        if (!outcome.received) {
          problems++;
        }
        update([outcome.barcode], () => ({
          state: outcome.received ? 'received' : 'problem',
          message: outcome.received ? null : (outcome.message ?? 'Not received.'),
          sampleId: outcome.sampleId,
        }));
      }
      if (sound) {
        beep(problems > 0 ? 'problem' : 'ok');
      }
      void queryClient.invalidateQueries({ queryKey: labKeys.worklist });
      void queryClient.invalidateQueries({ queryKey: labKeys.samples });
      void attachLookups(batch);
    } catch (error) {
      update(batch, () => ({
        state: 'problem',
        message: `Not sent: ${errorMessage(error, 'the server could not be reached')}. Scan again or use Retry.`,
      }));
      if (sound) {
        beep('problem');
      }
    } finally {
      if (pending.current.length > 0) {
        timer.current = setTimeout(() => void flush(), FLUSH_DELAY_MS);
      }
    }
  };

  const check = async (barcode: string) => {
    try {
      const sample = await queryClient.fetchQuery({ ...labQueries.sampleByBarcode(barcode), staleTime: 0 });
      update([barcode], () => ({ state: 'checked', sample, sampleId: sample.id, message: null }));
    } catch (error) {
      update([barcode], () => ({
        state: 'problem',
        message:
          isApiError(error) && error.status === 404
            ? 'No sample has this barcode.'
            : errorMessage(error, 'Could not look the sample up.'),
      }));
      if (sound) {
        beep('problem');
      }
    }
  };

  const enqueue = (codes: readonly string[], requested: Mode = mode) => {
    if (codes.length === 0) {
      return;
    }
    const scanMode: Mode = canReceive ? requested : 'check';
    const at = new Date().toISOString();
    setRows((current) =>
      [
        ...codes.map((barcode) => ({
          key: crypto.randomUUID(),
          barcode,
          mode: scanMode,
          state: 'sending' as const,
          message: null,
          sampleId: null,
          sample: null,
          at,
        })),
        ...current,
      ].slice(0, MAX_ROWS),
    );
    if (scanMode === 'check') {
      codes.forEach((code) => void check(code));
      return;
    }
    pending.current.push(...codes);
    timer.current ??= setTimeout(() => void flush(), FLUSH_DELAY_MS);
  };

  const submit = (raw: string) => {
    enqueue(splitScans(raw));
    setValue('');
  };

  /** Sends a scan again (after a network failure), or receives a tube that was only checked. */
  const again = (row: ScanRow, requested: Mode) => {
    setRows((current) => current.filter((r) => r.key !== row.key));
    enqueue([row.barcode], requested);
    focusInput();
  };

  const recollect = async (row: ScanRow) => {
    if (!row.sampleId) {
      return;
    }
    setRecollecting(row.key);
    try {
      const result = await labApi.recollect(row.sampleId);
      notify.success(
        'New sample requested',
        result.recollectionBarcode ? `New tube ${result.recollectionBarcode} is on the collection list.` : undefined,
      );
      void queryClient.invalidateQueries({ queryKey: labKeys.samples });
    } catch (error) {
      notify.error(error, 'Could not request a new sample');
    } finally {
      setRecollecting(null);
      focusInput();
    }
  };

  const received = rows.filter((row) => row.state === 'received').length;
  const problems = rows.filter((row) => row.state === 'problem').length;
  const last = rows[0];

  return (
    <Page>
      <PageHeader
        title="Receive samples"
        subtitle="Scan each tube as it arrives. The list below confirms every scan; F2 returns to the scan box."
        actions={
          <Button icon={<OpenRegular />} onClick={() => void navigate({ to: '/lab/worklist' })}>
            Result entry
          </Button>
        }
      />

      <Section>
        <div className={styles.scanBox}>
          <Field
            label={mode === 'receive' && canReceive ? 'Scan to receive' : 'Scan to check (not received)'}
            className={styles.input}
          >
            <Input
              ref={inputRef}
              className={styles.big}
              value={value}
              onChange={(_, data) => setValue(data.value)}
              onKeyDown={(event) => {
                if ((event.key === 'Enter' || (event.key === 'Tab' && value.trim())) && !event.ctrlKey) {
                  event.preventDefault();
                  submit(value);
                }
              }}
              onPaste={(event) => {
                const text = event.clipboardData.getData('text');
                if (/[\r\n\t,;]/.test(text.trim())) {
                  event.preventDefault();
                  submit(text);
                }
              }}
              contentBefore={<BarcodeScannerRegular />}
              placeholder="Sample barcode"
              autoFocus
              autoComplete="off"
              aria-keyshortcuts="F2"
              aria-describedby="receive-help"
            />
          </Field>
          {canReceive && (
            <Field label="On scan">
              <RadioGroup
                layout="horizontal"
                value={mode}
                onChange={(_, data) => {
                  setMode(data.value as Mode);
                  focusInput();
                }}
              >
                <Radio value="receive" label="Receive" />
                <Radio value="check" label="Check only" />
              </RadioGroup>
            </Field>
          )}
          <Switch
            checked={sound}
            onChange={(_, data) => {
              setSound(data.checked);
              setScanSoundEnabled(data.checked);
              focusInput();
            }}
            label="Sound"
          />
        </div>
        <Caption1 id="receive-help" className={styles.muted} block style={{ marginTop: 8 }}>
          A rack can be scanned without pausing: scans are sent together and each tube gets its own answer.
        </Caption1>
      </Section>

      {last && <LastScan row={last} />}

      <Section
        title="This session"
        actions={
          rows.length > 0 && (
            <div className={styles.counters}>
              <Caption1>
                <span className={styles.green}>{received} received</span>
                {problems > 0 && <span className={styles.red}> · {problems} with problems</span>}
              </Caption1>
              <Button
                size="small"
                appearance="subtle"
                icon={<DeleteRegular />}
                onClick={() => {
                  setRows([]);
                  focusInput();
                }}
              >
                Clear list
              </Button>
            </div>
          )
        }
        flush
      >
        {rows.length === 0 ? (
          <EmptyState
            icon={<BarcodeScannerRegular />}
            title="No samples scanned yet"
            description="Scan the first tube. Received tubes appear on the result entry worklist straight away."
          />
        ) : (
          <div className={styles.list} role="log" aria-live="polite" aria-label="Scanned samples">
            {rows.map((row) => (
              <div key={row.key} className={styles.row}>
                <span aria-hidden>
                  {row.state === 'sending' ? (
                    <Spinner size="extra-tiny" />
                  ) : row.state === 'received' ? (
                    <CheckmarkCircleRegular className={styles.green} fontSize={20} />
                  ) : row.state === 'problem' ? (
                    <ErrorCircleRegular className={styles.red} fontSize={20} />
                  ) : (
                    <InfoRegular fontSize={20} />
                  )}
                </span>
                <span>
                  <Text className={styles.mono} block>
                    {row.barcode}
                  </Text>
                  <Caption1 className={styles.muted}>
                    {formatTime(row.at)} ·{' '}
                    {row.state === 'sending'
                      ? 'Sending…'
                      : row.state === 'received'
                        ? 'Received'
                        : row.state === 'checked'
                          ? 'Checked'
                          : 'Not received'}
                  </Caption1>
                </span>
                <span>
                  {row.sample ? (
                    <>
                      <Text weight="semibold">{row.sample.patientName}</Text>{' '}
                      <Caption1 className={styles.muted}>
                        {row.sample.ageSex} · {row.sample.uhid}
                      </Caption1>{' '}
                      <PriorityBadge priority={row.sample.priority} /> <SampleStatusBadge status={row.sample.status} />
                      <Caption1 block>
                        <TubeChip container={row.sample.container} /> {sectionLabel[row.sample.section]} ·{' '}
                        {row.sample.tests.join(', ')}
                      </Caption1>
                    </>
                  ) : null}
                  {row.message && (
                    <Caption1 block className={styles.red}>
                      {row.message}
                    </Caption1>
                  )}
                </span>
                <span className={styles.rowActions}>
                  {row.state === 'problem' && row.message?.startsWith('Not sent') && (
                    <Button size="small" icon={<ArrowSyncRegular />} onClick={() => again(row, row.mode)}>
                      Retry
                    </Button>
                  )}
                  {canReceive && row.mode === 'check' && row.sample?.status === 'Collected' && (
                    <Button size="small" onClick={() => again(row, 'receive')}>
                      Receive now
                    </Button>
                  )}
                  {canReceive && row.sample && ['Collected', 'Received', 'InProcess'].includes(row.sample.status) && (
                    <Button
                      size="small"
                      icon={<BeakerDismissRegular />}
                      onClick={() =>
                        setRejecting({
                          sampleId: row.sample!.id,
                          barcode: row.barcode,
                          patientName: row.sample!.patientName,
                          tests: row.sample!.tests,
                        })
                      }
                    >
                      Reject
                    </Button>
                  )}
                  {canReceive && row.sample?.status === 'Rejected' && (
                    <Button
                      size="small"
                      icon={recollecting === row.key ? <Spinner size="tiny" /> : undefined}
                      disabled={recollecting !== null}
                      onClick={() => void recollect(row)}
                    >
                      Request new sample
                    </Button>
                  )}
                  {canEnter &&
                    row.sampleId &&
                    (row.state === 'received' ||
                      row.sample?.status === 'Received' ||
                      row.sample?.status === 'InProcess') && (
                      <Button
                        size="small"
                        appearance="subtle"
                        onClick={() => void navigate({ to: '/lab/worklist', search: { sampleId: row.sampleId! } })}
                      >
                        Results
                      </Button>
                    )}
                </span>
              </div>
            ))}
          </div>
        )}
      </Section>

      {rejecting && (
        <RejectSampleDialog
          target={rejecting}
          onClose={() => {
            setRejecting(null);
            focusInput();
          }}
          onDone={() => {
            setRows((current) =>
              current.map((row) =>
                row.barcode === rejecting.barcode && row.sample
                  ? { ...row, sample: { ...row.sample, status: 'Rejected' } }
                  : row,
              ),
            );
          }}
        />
      )}
    </Page>
  );
}

/** Big confirmation of the last scan, readable from arm's length at the bench. */
function LastScan({ row }: { row: ScanRow }) {
  const styles = useStyles();
  const tone = row.state === 'received' ? styles.ok : row.state === 'problem' ? styles.bad : styles.info;
  return (
    <div className={mergeClasses(styles.last, tone)} role="status" aria-live="assertive">
      <span className={styles.lastIcon} aria-hidden>
        {row.state === 'received' ? (
          <CheckmarkCircleRegular className={styles.green} />
        ) : row.state === 'problem' ? (
          <ErrorCircleRegular className={styles.red} />
        ) : row.state === 'sending' ? (
          <Spinner size="small" />
        ) : (
          <InfoRegular />
        )}
      </span>
      <span>
        <Text weight="semibold" size={500}>
          {row.state === 'received'
            ? 'Received'
            : row.state === 'problem'
              ? 'Not received'
              : row.state === 'sending'
                ? 'Sending…'
                : 'Checked'}{' '}
          <span className={styles.mono}>{row.barcode}</span>
        </Text>
        {row.sample && (
          <Text block>
            {row.sample.patientName} · <span className={styles.bench}>{sectionLabel[row.sample.section]}</span>
            {row.sample.priority !== 'Routine' && (
              <>
                {' '}
                <Badge color={row.sample.priority === 'Stat' ? 'danger' : 'warning'}>
                  {row.sample.priority === 'Stat' ? 'STAT' : 'Urgent'}
                </Badge>
              </>
            )}
          </Text>
        )}
        {row.message && <Text block>{row.message}</Text>}
      </span>
    </div>
  );
}
