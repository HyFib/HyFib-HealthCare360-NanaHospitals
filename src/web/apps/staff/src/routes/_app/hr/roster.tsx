import {
  Button,
  Caption1,
  Checkbox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Select,
  Spinner,
  Text,
  ToggleButton,
  tokens,
  Tooltip,
  useArrowNavigationGroup,
} from '@fluentui/react-components';
import {
  ArrowUndoRegular,
  CalendarLtrRegular,
  CheckmarkCircleRegular,
  ChevronLeftRegular,
  ChevronRightRegular,
  CopyArrowRightRegular,
  EraserRegular,
  SaveRegular,
  ShieldCheckmarkRegular,
  WeatherMoonRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, isApiError, todayIso } from '@hc360/core';
import { EmptyState, ErrorState, LoadingState, Page, PageHeader, Section, useLeaveGuard, useNotify } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useMemo, useState, type MouseEvent } from 'react';
import { toOptions, useDepartments } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { hrApi, hrKeys, hrQueries, type RosterConflictDto, type ShiftDto } from '@/modules/hr/api';
import { HR_PERMISSIONS as P, ROSTER_VIEWERS } from '@/modules/hr/labels';
import {
  cellKey,
  cellsFromEntries,
  conflictsByCell,
  coverage,
  diffRoster,
  entryIdsByCell,
  mergeCells,
  paint,
  sameItems,
  type Brush,
  type CellItem,
  type Cells,
} from '@/modules/hr/roster';
import { addDays, datesBetween, dayHeader, formatTimeOfDay, isIsoDate, startOfWeek } from '@/modules/hr/shared/dates';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { Muted, Toolbar } from '@/modules/hr/shared/layout';

interface RosterSearch {
  week?: string;
  departmentId?: string;
}

export const Route = createFileRoute('/_app/hr/roster')({
  validateSearch: (s: Record<string, unknown>): RosterSearch => ({
    week: isIsoDate(s.week) ? startOfWeek(s.week) : undefined,
    departmentId: typeof s.departmentId === 'string' && s.departmentId ? s.departmentId : undefined,
  }),
  component: () => (
    <RequirePermission permission={ROSTER_VIEWERS} what="the duty roster">
      <RosterPage />
    </RequirePermission>
  ),
});

const useStyles = makeStyles({
  wrap: { overflowX: 'auto', maxHeight: 'calc(100vh - 330px)', minHeight: '240px' },
  table: { borderCollapse: 'separate', borderSpacing: 0, width: '100%', minWidth: '860px' },
  th: {
    position: 'sticky',
    top: 0,
    zIndex: 2,
    backgroundColor: tokens.colorNeutralBackground1,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    padding: '6px 4px',
    textAlign: 'center',
    fontWeight: tokens.fontWeightSemibold,
  },
  today: { color: tokens.colorBrandForeground1 },
  nameCell: {
    position: 'sticky',
    left: 0,
    zIndex: 1,
    backgroundColor: tokens.colorNeutralBackground1,
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
    padding: '6px 10px',
    minWidth: '200px',
    maxWidth: '260px',
  },
  td: { borderBottom: `1px solid ${tokens.colorNeutralStroke3}`, padding: '3px' },
  cell: {
    width: '100%',
    minHeight: '40px',
    display: 'flex',
    flexWrap: 'wrap',
    gap: '3px',
    alignItems: 'center',
    justifyContent: 'center',
    padding: '4px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid transparent`,
    backgroundColor: 'transparent',
    cursor: 'pointer',
    color: tokens.colorNeutralForeground3,
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
      border: `1px solid ${tokens.colorNeutralStroke1}`,
    },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
    ':disabled': { cursor: 'default' },
  },
  dirty: { border: `1px dashed ${tokens.colorBrandStroke1}`, backgroundColor: tokens.colorBrandBackground2 },
  error: { border: `2px solid ${tokens.colorPaletteRedBorder2}` },
  warning: { border: `2px solid ${tokens.colorPaletteMarigoldBorder2}` },
  chip: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '3px',
    padding: '1px 6px',
    borderRadius: tokens.borderRadiusMedium,
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
    backgroundColor: tokens.colorNeutralBackground3,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    whiteSpace: 'nowrap',
  },
  off: { fontStyle: 'italic', color: tokens.colorNeutralForeground3 },
  swatch: { width: '8px', height: '8px', borderRadius: '50%' },
  palette: { display: 'flex', gap: '6px', flexWrap: 'wrap', alignItems: 'center' },
  coverage: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
    padding: '6px 4px',
    textAlign: 'center',
    verticalAlign: 'top',
  },
  muted: { color: tokens.colorNeutralForeground3 },
  conflicts: { margin: '4px 0 0', paddingLeft: '18px', maxHeight: '160px', overflowY: 'auto' },
});

function RosterPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const departments = useDepartments();
  const week = search.week ?? startOfWeek(todayIso());
  const departmentId = search.departmentId ?? null;

  const setWeek = (next: string) =>
    void navigate({
      search: (prev) => ({
        ...prev,
        week: startOfWeek(next) === startOfWeek(todayIso()) ? undefined : startOfWeek(next),
      }),
    });

  return (
    <Page>
      <PageHeader
        title="Duty roster"
        subtitle="Choose a shift below, then click the days to assign it. Shift+click adds a second duty the same day."
        icon={<CalendarLtrRegular />}
      />
      <Toolbar spaced={false}>
        <Field label="Department">
          <Select
            value={departmentId ?? ''}
            onChange={(_, d) => void navigate({ search: (prev) => ({ ...prev, departmentId: d.value || undefined }) })}
          >
            <option value="">Choose a department</option>
            {toOptions(departments.data).map((o) => (
              <option key={o.value} value={o.value}>
                {o.label}
              </option>
            ))}
          </Select>
        </Field>
        <Field label="Week">
          <span style={{ display: 'inline-flex', gap: 4, alignItems: 'center' }}>
            <Button
              icon={<ChevronLeftRegular />}
              aria-label="Previous week"
              onClick={() => setWeek(addDays(week, -7))}
            />
            <Input
              type="date"
              value={week}
              onChange={(_, d) => isIsoDate(d.value) && setWeek(d.value)}
              aria-label="Week starting"
            />
            <Button icon={<ChevronRightRegular />} aria-label="Next week" onClick={() => setWeek(addDays(week, 7))} />
            <Button appearance="subtle" onClick={() => setWeek(todayIso())}>
              This week
            </Button>
          </span>
        </Field>
      </Toolbar>
      {departmentId ? (
        <RosterBoard key={`${departmentId}:${week}`} departmentId={departmentId} week={week} />
      ) : (
        <Section>
          <EmptyState
            icon={<CalendarLtrRegular />}
            title="Choose a department"
            description="The roster is planned one department and one week at a time."
          />
        </Section>
      )}
    </Page>
  );
}

interface RowPerson {
  id: string;
  name: string;
  code: string;
  designation: string | null;
}

function RosterBoard({ departmentId, week }: { departmentId: string; week: string }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canEdit = usePermission(P.rosterManage);
  const canListStaff = usePermission(P.employeesRead);
  const dates = useMemo(() => datesBetween(week, addDays(week, 6)), [week]);
  const weekEnd = addDays(week, 6);
  const today = todayIso();
  const shifts = useQuery(hrQueries.shifts());
  const roster = useQuery(hrQueries.roster(week, weekEnd, departmentId));
  const staff = useQuery({ ...hrQueries.employees({ departmentId, sort: 'name' }, 200), enabled: canListStaff });
  const [draft, setDraft] = useState<Cells>({});
  const [brush, setBrush] = useState<Brush | null>(null);
  const [conflicts, setConflicts] = useState<RosterConflictDto[]>([]);
  const [warnings, setWarnings] = useState<RosterConflictDto[] | null>(null);
  const [busy, setBusy] = useState<'check' | 'save' | null>(null);
  const [copyOpen, setCopyOpen] = useState(false);
  const navigation = useArrowNavigationGroup({ axis: 'grid' });

  const saved = useMemo(() => cellsFromEntries(roster.data ?? []), [roster.data]);
  const savedIds = useMemo(() => entryIdsByCell(roster.data ?? []), [roster.data]);
  const cells = useMemo(() => mergeCells(saved, draft), [saved, draft]);
  const diff = useMemo(() => diffRoster(saved, draft), [saved, draft]);
  const dirty = diff.changedKeys.length > 0;
  const byCell = useMemo(() => conflictsByCell(conflicts), [conflicts]);
  const activeShifts = useMemo(() => (shifts.data ?? []).filter((s) => s.isActive), [shifts.data]);
  const shiftById = useMemo(() => new Map((shifts.data ?? []).map((s) => [s.id, s])), [shifts.data]);
  const currentBrush: Brush | null = brush ?? (activeShifts[0] ? { kind: 'shift', shiftId: activeShifts[0].id } : null);

  // Any navigation with unsaved changes asks first (SRS UX-08): another week or department shows other entries.
  useLeaveGuard({
    dirty,
    leaves: () => true,
    leaveQuestion: {
      title: 'Leave without saving?',
      body: `You have ${diff.changedKeys.length} roster change(s) that are not saved.`,
      confirmLabel: 'Discard changes',
      destructive: true,
    },
  });

  const people = useMemo(() => {
    const map = new Map<string, RowPerson>();
    for (const e of staff.data?.items ?? []) {
      map.set(e.id, { id: e.id, name: e.fullName, code: e.employeeCode, designation: e.designationName });
    }
    for (const r of roster.data ?? []) {
      if (!map.has(r.employeeId)) {
        map.set(r.employeeId, { id: r.employeeId, name: r.employeeName, code: r.employeeCode, designation: null });
      }
    }
    return [...map.values()].sort((a, b) => a.name.localeCompare(b.name));
  }, [staff.data, roster.data]);

  const counts = useMemo(() => coverage(cells, dates), [cells, dates]);

  const onCell = (event: MouseEvent, key: string) => {
    if (!canEdit || !currentBrush) {
      return;
    }
    const next = paint(cells[key] ?? [], currentBrush, event.shiftKey);
    setDraft((prev) => ({ ...prev, [key]: next }));
  };

  const check = async () => {
    if (diff.assignments.length === 0) {
      notify.info('Nothing to check', 'Only newly assigned or changed days are checked; cleared days need no check.');
      return;
    }
    setBusy('check');
    try {
      const result = await hrApi.checkRoster({
        assignments: diff.assignments,
        replaceExisting: true,
        acknowledgeWarnings: false,
      });
      setConflicts(result.conflicts);
      if (result.conflicts.length === 0) {
        notify.success('No conflicts', 'The changes can be saved.');
      }
    } catch (error) {
      notify.error(error, 'Could not check the roster');
    } finally {
      setBusy(null);
    }
  };

  const save = async (acknowledgeWarnings: boolean) => {
    setBusy('save');
    try {
      let saved = 0;
      if (diff.assignments.length > 0) {
        const result = await hrApi.saveRoster({
          assignments: diff.assignments,
          replaceExisting: true,
          acknowledgeWarnings,
        });
        saved = result.saved;
      }
      const toDelete = diff.clearedKeys.flatMap((key) => savedIds[key] ?? []);
      for (const id of toDelete) {
        await hrApi.deleteRosterEntry(id);
      }
      setDraft({});
      setConflicts([]);
      setWarnings(null);
      notify.success(
        'Roster saved',
        `${saved} duty assignment(s) saved${toDelete.length ? `, ${toDelete.length} removed` : ''}.`,
      );
      void queryClient.invalidateQueries({ queryKey: hrKeys.roster });
    } catch (error) {
      if (isApiError(error) && error.code === 'roster.warnings' && Array.isArray(error.data)) {
        setWarnings(error.data as RosterConflictDto[]);
      } else if (isApiError(error) && error.code === 'roster.conflicts' && Array.isArray(error.data)) {
        setConflicts(error.data as RosterConflictDto[]);
        notify.warning('Fix the highlighted days', 'Some assignments clash. Nothing was saved.');
      } else {
        notify.error(error, 'Could not save the roster');
      }
      void queryClient.invalidateQueries({ queryKey: hrKeys.roster });
    } finally {
      setBusy(null);
    }
  };

  if (roster.isLoading || shifts.isLoading || (canListStaff && staff.isLoading)) {
    return <LoadingState label="Loading the roster…" />;
  }
  if (roster.error || shifts.error) {
    return <ErrorState error={roster.error ?? shifts.error} onRetry={() => void roster.refetch()} />;
  }

  const errorCount = conflicts.filter((c) => c.severity === 'Error').length;

  return (
    <>
      {canEdit && (
        <Section>
          <Toolbar spaced={false}>
            <div className={styles.palette} role="toolbar" aria-label="Choose what to assign">
              {activeShifts.map((shift) => (
                <ToggleButton
                  key={shift.id}
                  size="small"
                  checked={currentBrush?.kind === 'shift' && currentBrush.shiftId === shift.id}
                  onClick={() => setBrush({ kind: 'shift', shiftId: shift.id })}
                  title={`${shift.name}: ${formatTimeOfDay(shift.startTime)} to ${formatTimeOfDay(shift.endTime)}`}
                >
                  <span
                    className={styles.swatch}
                    style={{ backgroundColor: shift.color ?? tokens.colorBrandBackground, marginRight: 6 }}
                    aria-hidden
                  />
                  {shift.code}
                  {shift.isNightShift && <WeatherMoonRegular aria-label="night" style={{ marginLeft: 2 }} />}
                </ToggleButton>
              ))}
              <ToggleButton
                size="small"
                checked={currentBrush?.kind === 'weekOff'}
                onClick={() => setBrush({ kind: 'weekOff' })}
              >
                Week off
              </ToggleButton>
              <ToggleButton
                size="small"
                icon={<EraserRegular />}
                checked={currentBrush?.kind === 'clear'}
                onClick={() => setBrush({ kind: 'clear' })}
              >
                Clear
              </ToggleButton>
            </div>
            <span style={{ flex: 1 }} />
            <Button icon={<CopyArrowRightRegular />} onClick={() => setCopyOpen(true)} disabled={dirty}>
              Copy last week
            </Button>
            <Button
              icon={busy === 'check' ? <Spinner size="tiny" aria-hidden /> : <ShieldCheckmarkRegular />}
              disabled={!dirty || busy !== null}
              onClick={() => void check()}
            >
              Check conflicts
            </Button>
            <Button
              icon={<ArrowUndoRegular />}
              disabled={!dirty || busy !== null}
              onClick={() => {
                setDraft({});
                setConflicts([]);
              }}
            >
              Undo changes
            </Button>
            <Button
              appearance="primary"
              icon={busy === 'save' ? <Spinner size="tiny" aria-hidden /> : <SaveRegular />}
              disabled={!dirty || busy !== null}
              onClick={() => void save(false)}
            >
              Save roster{dirty ? ` (${diff.changedKeys.length})` : ''}
            </Button>
          </Toolbar>
          {activeShifts.length === 0 && <Muted block>No shifts are set up yet. Add them under Shifts first.</Muted>}
        </Section>
      )}

      {conflicts.length > 0 && (
        <MessageBar intent={errorCount > 0 ? 'error' : 'warning'} layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>
              {errorCount > 0
                ? `${errorCount} clash(es) must be fixed before saving`
                : 'Warnings: you can save after confirming'}
            </MessageBarTitle>
            <ul className={styles.conflicts}>
              {conflicts.map((c) => (
                <li key={`${c.employeeId}-${c.date}-${c.kind}`}>
                  <strong>{c.employeeName ?? 'Employee'}</strong>, {formatDateOnly(c.date)}: {c.message}
                  {c.severity === 'Warning' ? ' (warning)' : ''}
                </li>
              ))}
            </ul>
          </MessageBarBody>
        </MessageBar>
      )}

      <Section flush>
        {people.length === 0 ? (
          <EmptyState
            title="Nobody to roster in this department"
            description={
              canListStaff
                ? 'Assign employees to this department on their records first.'
                : 'Only staff already on the roster are shown: you need access to the employee list to plan new staff.'
            }
          />
        ) : (
          <div className={styles.wrap}>
            <table className={styles.table} {...navigation} aria-label={`Duty roster, week of ${formatDateOnly(week)}`}>
              <thead>
                <tr>
                  <th className={mergeClasses(styles.th, styles.nameCell)} style={{ zIndex: 3, textAlign: 'start' }}>
                    Staff ({people.length})
                  </th>
                  {dates.map((date) => {
                    const head = dayHeader(date);
                    return (
                      <th key={date} className={mergeClasses(styles.th, date === today && styles.today)} scope="col">
                        {head.weekday} {head.day} {head.month}
                      </th>
                    );
                  })}
                </tr>
              </thead>
              <tbody>
                {people.map((person) => (
                  <tr key={person.id}>
                    <th className={styles.nameCell} scope="row" style={{ textAlign: 'start', fontWeight: 'normal' }}>
                      <Text weight="semibold" block truncate>
                        {person.name}
                      </Text>
                      <Caption1 className={styles.muted}>
                        {person.code}
                        {person.designation ? ` · ${person.designation}` : ''}
                      </Caption1>
                    </th>
                    {dates.map((date) => {
                      const key = cellKey(person.id, date);
                      const items = cells[key] ?? [];
                      const changed = key in draft && !sameItems(saved[key] ?? [], items);
                      const cellConflicts = byCell[key] ?? [];
                      const severity = cellConflicts.some((c) => c.severity === 'Error')
                        ? 'error'
                        : cellConflicts.length
                          ? 'warning'
                          : null;
                      const label = describe(items, shiftById);
                      const button = (
                        <button
                          type="button"
                          className={mergeClasses(
                            styles.cell,
                            changed && styles.dirty,
                            severity === 'error' && styles.error,
                            severity === 'warning' && styles.warning,
                          )}
                          onClick={(event) => onCell(event, key)}
                          disabled={!canEdit}
                          aria-label={`${person.name}, ${formatDateOnly(date)}: ${label || 'nothing planned'}${changed ? ' (changed, not saved)' : ''}`}
                        >
                          {items.length === 0 ? (
                            <span aria-hidden>—</span>
                          ) : (
                            items.map((item, index) => (
                              <CellChip
                                key={index}
                                item={item}
                                shift={item.shiftId ? shiftById.get(item.shiftId) : undefined}
                              />
                            ))
                          )}
                        </button>
                      );
                      return (
                        <td key={date} className={styles.td}>
                          {cellConflicts.length > 0 ? (
                            <Tooltip
                              content={cellConflicts.map((c) => c.message).join(' · ')}
                              relationship="description"
                            >
                              {button}
                            </Tooltip>
                          ) : (
                            button
                          )}
                        </td>
                      );
                    })}
                  </tr>
                ))}
                <tr>
                  <th className={styles.nameCell} scope="row" style={{ textAlign: 'start' }}>
                    <Caption1>On duty</Caption1>
                  </th>
                  {dates.map((date) => (
                    <td key={date} className={styles.coverage}>
                      {Object.entries(counts[date] ?? {})
                        .map(([bucket, n]) => `${bucket === 'off' ? 'Off' : (shiftById.get(bucket)?.code ?? '?')} ${n}`)
                        .join(' · ') || '—'}
                    </td>
                  ))}
                </tr>
              </tbody>
            </table>
          </div>
        )}
      </Section>

      <Dialog open={warnings !== null} onOpenChange={(_, d) => !d.open && setWarnings(null)}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Save with these warnings?</DialogTitle>
            <DialogContent>
              <ul className={styles.conflicts}>
                {(warnings ?? []).map((c) => (
                  <li key={`${c.employeeId}-${c.date}-${c.kind}`}>
                    <strong>{c.employeeName ?? 'Employee'}</strong>, {formatDateOnly(c.date)}: {c.message}
                  </li>
                ))}
              </ul>
            </DialogContent>
            <DialogActions>
              <Button onClick={() => setWarnings(null)} disabled={busy !== null}>
                Go back
              </Button>
              <Button appearance="primary" disabled={busy !== null} onClick={() => void save(true)}>
                Save anyway
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>

      <CopyWeekDialog
        open={copyOpen}
        onClose={() => setCopyOpen(false)}
        departmentId={departmentId}
        targetWeek={week}
      />
    </>
  );
}

function describe(items: CellItem[], shifts: Map<string, ShiftDto>): string {
  return items
    .map((item) => (item.isWeekOff ? 'week off' : (shifts.get(item.shiftId ?? '')?.name ?? 'shift')))
    .join(' and ');
}

function CellChip({ item, shift }: { item: CellItem; shift?: ShiftDto }) {
  const styles = useStyles();
  if (item.isWeekOff) {
    return <span className={mergeClasses(styles.chip, styles.off)}>Off</span>;
  }
  return (
    <span className={styles.chip}>
      <span
        className={styles.swatch}
        style={{ backgroundColor: shift?.color ?? tokens.colorBrandBackground }}
        aria-hidden
      />
      {shift?.code ?? '?'}
      {shift?.isNightShift && <WeatherMoonRegular aria-hidden />}
    </span>
  );
}

function CopyWeekDialog({
  open,
  onClose,
  departmentId,
  targetWeek,
}: {
  open: boolean;
  onClose: () => void;
  departmentId: string;
  targetWeek: string;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [source, setSource] = useState(addDays(targetWeek, -7));
  const [warnings, setWarnings] = useState<RosterConflictDto[] | null>(null);
  const [acknowledge, setAcknowledge] = useState(false);
  const [busy, setBusy] = useState(false);

  const copy = async () => {
    setBusy(true);
    try {
      const result = await hrApi.copyRosterWeek({
        sourceWeekStart: startOfWeek(source),
        targetWeekStart: targetWeek,
        departmentId,
        acknowledgeWarnings: acknowledge,
      });
      notify.success(
        'Week copied',
        `${result.saved} duty assignment(s) copied into the week of ${formatDateOnly(targetWeek)}.`,
      );
      void queryClient.invalidateQueries({ queryKey: hrKeys.roster });
      setWarnings(null);
      setAcknowledge(false);
      onClose();
    } catch (error) {
      if (
        isApiError(error) &&
        (error.code === 'roster.warnings' || error.code === 'roster.conflicts') &&
        Array.isArray(error.data)
      ) {
        setWarnings(error.data as RosterConflictDto[]);
      } else {
        notify.error(error, 'Could not copy the week');
      }
    } finally {
      setBusy(false);
    }
  };

  const blockingErrors = (warnings ?? []).filter((w) => w.severity === 'Error');

  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && !busy && onClose()}>
      <DialogSurface style={{ maxWidth: 600 }}>
        <DialogBody>
          <DialogTitle>Copy a week into {formatDateOnly(targetWeek)}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Field
              label="Copy from the week starting"
              hint="Days already planned in this week are replaced for the staff in the copied week."
            >
              <Input type="date" value={source} onChange={(_, d) => setSource(d.value)} />
            </Field>
            {warnings && warnings.length > 0 && (
              <MessageBar intent={blockingErrors.length ? 'error' : 'warning'} layout="multiline">
                <MessageBarBody>
                  <MessageBarTitle>
                    {blockingErrors.length ? 'The copy clashes with existing duties' : 'Please review'}
                  </MessageBarTitle>
                  <ul style={{ margin: '4px 0 0', paddingLeft: 18, maxHeight: 140, overflowY: 'auto' }}>
                    {warnings.map((w) => (
                      <li key={`${w.employeeId}-${w.date}-${w.kind}`}>
                        {w.employeeName ?? 'Employee'}, {formatDateOnly(w.date)}: {w.message}
                      </li>
                    ))}
                  </ul>
                  {blockingErrors.length === 0 && (
                    <Checkbox
                      checked={acknowledge}
                      onChange={(_, d) => setAcknowledge(Boolean(d.checked))}
                      label="Copy anyway"
                    />
                  )}
                </MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={busy}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              icon={busy ? <Spinner size="tiny" aria-hidden /> : <CheckmarkCircleRegular />}
              disabled={
                busy || !isIsoDate(source) || blockingErrors.length > 0 || (Boolean(warnings?.length) && !acknowledge)
              }
              onClick={() => void copy()}
            >
              Copy week
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
