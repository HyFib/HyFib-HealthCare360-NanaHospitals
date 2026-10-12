import {
  Badge,
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
  Select,
  Spinner,
  Subtitle2,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular, WarningRegular } from '@fluentui/react-icons';
import { formatDate, isApiError } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { toOptions, useDepartments } from '@/lib/lookups';
import { cssdApi, cssdKeys, cssdQueries, type InstrumentSetDto } from './api';
import { compareCounts } from './rules';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  tray: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    padding: '12px',
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  trayHead: { display: 'flex', gap: '8px', alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap' },
  counts: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) 70px 90px 90px',
    gap: '6px 8px',
    alignItems: 'center',
  },
  head: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  short: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
  row: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: '12px',
    '@media (max-width: 560px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
});

interface CountRow {
  itemName: string;
  expected: number;
  received: number | '';
  damaged: number | '';
  extra: boolean;
}

interface Tray {
  key: number;
  setId: string;
  issueId: string;
  rows: CountRow[];
}

function rowsFor(set: InstrumentSetDto | undefined): CountRow[] {
  return (set?.items ?? []).map((item) => ({
    itemName: item.name,
    expected: item.quantity,
    received: item.quantity,
    damaged: 0,
    extra: false,
  }));
}

const num = (value: number | '') => (value === '' ? 0 : value);

/**
 * Receive used trays from a department and count every instrument against the set's count sheet. Differences are
 * shown while counting; a missing instrument after surgery is a patient-safety signal and alerts the recall team.
 */
export function SoiledReceiptDialog({ onClose }: { onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const departments = useDepartments();
  const sets = useQuery(cssdQueries.sets());
  const [departmentId, setDepartmentId] = useState('');
  const [handedOverBy, setHandedOverBy] = useState('');
  const [notes, setNotes] = useState('');
  const [trays, setTrays] = useState<Tray[]>([]);
  const [nextKey, setNextKey] = useState(1);
  const issues = useQuery({ ...cssdQueries.issues({ departmentId, pageSize: 100 }), enabled: Boolean(departmentId) });
  const openIssues = (issues.data?.items ?? []).filter(
    (i) => i.status === 'Issued' || i.status === 'Used' || i.status === 'Recalled',
  );
  const setById = new Map((sets.data ?? []).map((s) => [s.id, s]));

  const addTray = (setId: string) => {
    if (!setId) {
      return;
    }
    setTrays((current) => [...current, { key: nextKey, setId, issueId: '', rows: rowsFor(setById.get(setId)) }]);
    setNextKey(nextKey + 1);
  };
  const updateTray = (key: number, patch: Partial<Tray>) =>
    setTrays((current) => current.map((t) => (t.key === key ? { ...t, ...patch } : t)));
  const updateRow = (key: number, index: number, patch: Partial<CountRow>) =>
    setTrays((current) =>
      current.map((t) =>
        t.key === key ? { ...t, rows: t.rows.map((r, i) => (i === index ? { ...r, ...patch } : r)) } : t,
      ),
    );

  const checks = trays.map((tray) => ({
    tray,
    check: compareCounts(
      (setById.get(tray.setId)?.items ?? []).map((i) => ({ name: i.name, quantity: i.quantity })),
      tray.rows
        .filter((r) => r.itemName.trim())
        .map((r) => ({ itemName: r.itemName, received: num(r.received), damaged: num(r.damaged) })),
    ),
  }));
  const anyDiscrepancy = checks.some((c) => c.check.hasDiscrepancy);
  const invalidRow = trays.some((t) =>
    t.rows.some((r) => num(r.damaged) > num(r.received) || (r.extra && !r.itemName.trim())),
  );
  const valid = Boolean(departmentId) && trays.length > 0 && !invalidRow;

  const receive = useMutation({
    mutationFn: () =>
      cssdApi.receiveSoiled({
        departmentId,
        handedOverByName: handedOverBy.trim() || null,
        notes: notes.trim() || null,
        lines: trays.map((t) => ({
          instrumentSetId: t.setId,
          setIssueId: t.issueId || null,
          counts: t.rows
            .filter((r) => r.itemName.trim())
            .map((r) => ({ itemName: r.itemName.trim(), received: num(r.received), damaged: num(r.damaged) })),
        })),
      }),
    onSuccess: (receipt) => {
      if (receipt.hasDiscrepancy) {
        notify.warning('Received with count differences', 'The recall team has been alerted.');
      } else {
        notify.success('Trays received', 'All instruments accounted for.');
      }
      void queryClient.invalidateQueries({ queryKey: cssdKeys.all });
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !receive.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 860, width: '100%' }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !receive.isPending) {
              receive.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Receive soiled trays</DialogTitle>
            <DialogContent className={styles.stack}>
              <div className={styles.row}>
                <Field label="From department" required>
                  <Select
                    value={departmentId}
                    onChange={(_, d) => {
                      setDepartmentId(d.value);
                      setTrays((current) => current.map((t) => ({ ...t, issueId: '' })));
                    }}
                  >
                    <option value="">Choose department</option>
                    {toOptions(departments.data).map((o) => (
                      <option key={o.value} value={o.value}>
                        {o.label}
                      </option>
                    ))}
                  </Select>
                </Field>
                <Field label="Handed over by">
                  <Input
                    value={handedOverBy}
                    onChange={(_, d) => setHandedOverBy(d.value)}
                    maxLength={200}
                    placeholder="e.g. OT nurse Latha"
                  />
                </Field>
              </div>

              {checks.map(({ tray, check }) => {
                const set = setById.get(tray.setId);
                const trayIssues = openIssues.filter((i) => i.instrumentSetId === tray.setId);
                return (
                  <section key={tray.key} className={styles.tray} aria-label={set?.name ?? 'Tray'}>
                    <div className={styles.trayHead}>
                      <Subtitle2 as="h3">{set?.name ?? 'Tray'}</Subtitle2>
                      <span style={{ display: 'inline-flex', gap: 8, alignItems: 'center' }}>
                        {check.hasDiscrepancy ? (
                          <Badge appearance="tint" color="danger" icon={<WarningRegular />}>
                            {check.discrepancies.length} differences
                          </Badge>
                        ) : (
                          <Badge appearance="tint" color="success">
                            Count matches ({check.received}/{check.expected})
                          </Badge>
                        )}
                        <Button
                          size="small"
                          appearance="subtle"
                          icon={<DeleteRegular />}
                          aria-label={`Remove ${set?.name ?? 'tray'}`}
                          onClick={() => setTrays(trays.filter((t) => t.key !== tray.key))}
                        />
                      </span>
                    </div>
                    {departmentId && trayIssues.length > 0 && (
                      <Field label="Returned pack (links the return to its issue)">
                        <Select value={tray.issueId} onChange={(_, d) => updateTray(tray.key, { issueId: d.value })}>
                          <option value="">Not linked</option>
                          {trayIssues.map((i) => (
                            <option key={i.id} value={i.id}>
                              {i.packNumber ?? i.id.slice(0, 8)} · issued {formatDate(i.issuedAt)}
                              {i.status === 'Recalled' ? ' · recalled' : ''}
                            </option>
                          ))}
                        </Select>
                      </Field>
                    )}
                    <div className={styles.counts} role="table" aria-label={`Count sheet of ${set?.name ?? 'tray'}`}>
                      <span className={styles.head} role="columnheader">
                        Instrument
                      </span>
                      <span className={styles.head} role="columnheader">
                        Expected
                      </span>
                      <span className={styles.head} role="columnheader">
                        Received
                      </span>
                      <span className={styles.head} role="columnheader">
                        Damaged
                      </span>
                      {tray.rows.map((row, index) => {
                        const short = num(row.received) < row.expected;
                        return (
                          <div key={index} role="row" style={{ display: 'contents' }}>
                            {row.extra ? (
                              <Input
                                aria-label="Instrument not on the count sheet"
                                value={row.itemName}
                                onChange={(_, d) => updateRow(tray.key, index, { itemName: d.value })}
                                placeholder="Instrument name"
                                maxLength={128}
                              />
                            ) : (
                              <Text role="cell">{row.itemName}</Text>
                            )}
                            <Text role="cell">{row.extra ? '—' : row.expected}</Text>
                            <Input
                              type="number"
                              min={0}
                              max={500}
                              aria-label={`Received ${row.itemName}`}
                              className={short ? styles.short : undefined}
                              value={String(row.received)}
                              onChange={(_, d) =>
                                updateRow(tray.key, index, {
                                  received: d.value === '' ? '' : Math.max(0, Number(d.value)),
                                })
                              }
                            />
                            <Input
                              type="number"
                              min={0}
                              max={500}
                              aria-label={`Damaged ${row.itemName}`}
                              value={String(row.damaged)}
                              onChange={(_, d) =>
                                updateRow(tray.key, index, {
                                  damaged: d.value === '' ? '' : Math.max(0, Number(d.value)),
                                })
                              }
                            />
                          </div>
                        );
                      })}
                    </div>
                    <div>
                      <Button
                        size="small"
                        appearance="subtle"
                        icon={<AddRegular />}
                        onClick={() =>
                          updateTray(tray.key, {
                            rows: [...tray.rows, { itemName: '', expected: 0, received: 1, damaged: 0, extra: true }],
                          })
                        }
                      >
                        Instrument not on the sheet
                      </Button>
                    </div>
                    {check.hasDiscrepancy && (
                      <Caption1 style={{ color: tokens.colorPaletteRedForeground1 }}>
                        {check.discrepancies.join(' · ')}
                      </Caption1>
                    )}
                  </section>
                );
              })}

              <Field label={trays.length ? 'Add another tray' : 'Add a tray'} required={trays.length === 0}>
                <Select value="" onChange={(_, d) => addTray(d.value)}>
                  <option value="">Choose the instrument set</option>
                  {(sets.data ?? []).map((s) => (
                    <option key={s.id} value={s.id}>
                      {s.name} ({s.code}) · {s.instrumentCount} instruments
                    </option>
                  ))}
                </Select>
              </Field>
              <Field label="Notes">
                <Textarea value={notes} onChange={(_, d) => setNotes(d.value)} maxLength={500} rows={2} />
              </Field>
              {invalidRow && (
                <Caption1 style={{ color: tokens.colorPaletteRedForeground1 }}>
                  Damaged pieces are part of the received count, and extra instruments need a name.
                </Caption1>
              )}
              {anyDiscrepancy && (
                <MessageBar intent="warning" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>Count differences</MessageBarTitle>
                    Recount before saving. If an instrument is really missing after surgery, inform the OT in charge
                    now: it may be retained in a patient.
                  </MessageBarBody>
                </MessageBar>
              )}
              {receive.error && (
                <MessageBar intent="error">
                  <MessageBarBody>
                    {isApiError(receive.error) ? receive.error.userMessage : 'The receipt could not be saved.'}
                  </MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={receive.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || receive.isPending}
                icon={receive.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                {anyDiscrepancy ? 'Receive with differences' : 'Receive trays'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
