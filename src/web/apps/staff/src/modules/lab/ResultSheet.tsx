import {
  Badge,
  Button,
  Caption1,
  Checkbox,
  Input,
  makeStyles,
  mergeClasses,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Select,
  Spinner,
  Switch,
  Text,
  tokens,
  Tooltip,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  ArrowUndoRegular,
  BeakerDismissRegular,
  CallRegular,
  CheckmarkCircleRegular,
  CheckmarkRegular,
  DocumentBulletListRegular,
  MoreHorizontalRegular,
  SaveRegular,
  WarningRegular,
} from '@fluentui/react-icons';
import { draftStorageKey, formatDate, formatTime, useHotkey, useSession } from '@hc360/core';
import { ErrorState, LoadingState, Section, useNotify } from '@hc360/ui';
import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useEffect, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import { usePermission } from '@/lib/permissions';
import { PatientBannerWithFlags } from '@/modules/emr/PatientBannerWithFlags';
import { parseAgeSex } from './common/banner';
import { CriticalCallDialog, type CriticalFinding } from './common/CriticalCallDialog';
import { errorData, hasErrorCode, indexedFieldErrors } from './common/errors';
import { ReasonDialog } from './common/ReasonDialog';
import type { CriticalCallDetails } from './common/types';
import {
  LAB_PERMISSIONS,
  labApi,
  labKeys,
  labQueries,
  type CriticalCallRequiredResult,
  type LabTestDetail,
  type QcLockout,
  type ResultEntry,
  type ResultSheet as ResultSheetDto,
  type ResultSheetLine,
  type SusceptibilityDto,
  type VerifyResultsRequest,
} from './api';
import { FlagChip, SampleStatusBadge } from './badges';
import { CultureDialog, type CultureValue } from './CultureDialog';
import {
  flagDisplay,
  formatDelta,
  interpretLine,
  isCriticalFlag,
  isReadyForBulkVerify,
  reviewHints,
  type LineInterpretation,
} from './flags';
import { QcOverrideDialog } from './QcOverrideDialog';
import { RejectSampleDialog } from './RejectSampleDialog';
import { isSampleBarcode, KeystrokeTimer, normalizeScan } from './scan';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '12px', minWidth: 0 },
  info: {
    display: 'flex',
    gap: '8px 16px',
    flexWrap: 'wrap',
    alignItems: 'center',
    color: tokens.colorNeutralForeground2,
  },
  mono: { fontFamily: tokens.fontFamilyMonospace },
  tableWrap: { overflowX: 'auto' },
  table: { width: '100%', borderCollapse: 'collapse', fontSize: tokens.fontSizeBase300 },
  th: {
    textAlign: 'start',
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground3,
    padding: '6px 8px',
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    whiteSpace: 'nowrap',
    fontSize: tokens.fontSizeBase200,
  },
  group: {
    textAlign: 'start',
    padding: '10px 8px 4px',
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorBrandForeground1,
  },
  subgroup: {
    textAlign: 'start',
    padding: '6px 8px 2px 36px',
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground2,
  },
  td: { padding: '6px 8px', borderBottom: `1px solid ${tokens.colorNeutralStroke3}`, verticalAlign: 'top' },
  critical: {
    boxShadow: `inset 3px 0 0 ${tokens.colorPaletteRedBorder2}`,
    backgroundColor: tokens.colorPaletteRedBackground1,
  },
  abnormal: { boxShadow: `inset 3px 0 0 ${tokens.colorPaletteMarigoldBorder2}` },
  testName: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: '150px' },
  hints: { color: tokens.colorPaletteMarigoldForeground1, fontSize: tokens.fontSizeBase200 },
  ready: { color: tokens.colorPaletteGreenForeground1, fontSize: tokens.fontSizeBase200 },
  valueCell: { minWidth: '140px', maxWidth: '220px' },
  invalid: {
    color: tokens.colorPaletteRedForeground1,
    fontSize: tokens.fontSizeBase200,
    display: 'block',
    marginTop: '2px',
  },
  valueText: { fontWeight: tokens.fontWeightSemibold },
  critText: { fontWeight: tokens.fontWeightBold, color: tokens.colorPaletteRedForeground1 },
  muted: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  delta: { display: 'inline-flex', alignItems: 'center', gap: '4px', fontSize: tokens.fontSizeBase200 },
  deltaBad: { color: tokens.colorPaletteMarigoldForeground1, fontWeight: tokens.fontWeightSemibold },
  remarks: { marginTop: '4px' },
  footer: {
    position: 'sticky',
    bottom: 0,
    zIndex: 1,
    display: 'flex',
    gap: '8px 12px',
    flexWrap: 'wrap',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: '10px 12px',
    backgroundColor: tokens.colorNeutralBackground1,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    boxShadow: tokens.shadow8,
    borderRadius: tokens.borderRadiusLarge,
  },
  footerActions: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'center' },
  selectionBar: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'center' },
});

interface SheetEdit {
  value: string;
  remarks?: string;
  organism?: string;
  colonyCount?: string;
  susceptibilities?: SusceptibilityDto[];
}

type Edits = Record<string, SheetEdit>;

interface SavedDraft {
  value: Edits;
  savedAt: number;
}

function readDraft(key: string | null): SavedDraft | null {
  if (!key) {
    return null;
  }
  try {
    const raw = localStorage.getItem(key);
    const parsed = raw ? (JSON.parse(raw) as SavedDraft) : null;
    return parsed && Object.keys(parsed.value ?? {}).length > 0 ? parsed : null;
  } catch {
    return null;
  }
}

function writeDraft(key: string, edits: Edits): void {
  try {
    localStorage.setItem(key, JSON.stringify({ value: edits, savedAt: new Date().getTime() }));
  } catch {
    // Drafts are best effort (quota, private mode).
  }
}

function removeDraft(key: string | null): void {
  if (!key) {
    return;
  }
  try {
    localStorage.removeItem(key);
  } catch {
    // Nothing to remove.
  }
}

function same(a: string | null | undefined, b: string | null | undefined): boolean {
  return (a ?? '').trim() === (b ?? '').trim();
}

function isDirty(line: ResultSheetLine, edit: SheetEdit | undefined): boolean {
  if (!edit) {
    return false;
  }
  return (
    !same(edit.value, line.value) ||
    (edit.remarks !== undefined && !same(edit.remarks, line.remarks)) ||
    (edit.organism !== undefined && !same(edit.organism, line.organism)) ||
    (edit.colonyCount !== undefined && !same(edit.colonyCount, line.colonyCount)) ||
    (edit.susceptibilities !== undefined &&
      JSON.stringify(edit.susceptibilities) !== JSON.stringify(line.susceptibilities))
  );
}

function findingOf(line: Pick<ResultSheetLine, 'resultId' | 'testName' | 'value' | 'unit' | 'flag'>): CriticalFinding {
  const flag = flagDisplay(line.flag);
  return {
    id: line.resultId,
    label: `${line.testName}: ${line.value ?? ''}${line.unit ? ` ${line.unit}` : ''}${flag?.code ? ` (${flag.label.toLowerCase()})` : ''}`,
  };
}

interface CallState {
  mode: 'entry' | 'verify';
  findings: CriticalFinding[];
  request?: VerifyResultsRequest;
}

interface QcState {
  lockouts: QcLockout[];
  request: VerifyResultsRequest;
}

interface Group {
  orderedTest: string;
  parts: { groupName: string | null; lines: ResultSheetLine[] }[];
}

function groupLines(lines: readonly ResultSheetLine[]): Group[] {
  const groups: Group[] = [];
  for (const line of lines) {
    let group = groups.find((g) => g.orderedTest === line.orderedTest);
    if (!group) {
      group = { orderedTest: line.orderedTest, parts: [] };
      groups.push(group);
    }
    const last = group.parts.at(-1);
    if (last && last.groupName === line.groupName) {
      last.lines.push(line);
    } else {
      group.parts.push({ groupName: line.groupName, lines: [line] });
    }
  }
  return groups;
}

/**
 * Result entry and verification for one sample (SRS LIS-02/04/05/06). Loads the sheet, then hands over to the editor,
 * which is keyed by sample so switching tubes always starts clean.
 */
export function ResultSheetPanel({
  sampleId,
  onScanBarcode,
}: {
  sampleId: string;
  onScanBarcode?: (barcode: string) => void;
}) {
  const sheet = useQuery(labQueries.sheet(sampleId));
  if (sheet.isLoading) {
    return (
      <Section>
        <LoadingState label="Opening the results…" />
      </Section>
    );
  }
  if (sheet.error || !sheet.data) {
    return (
      <Section>
        <ErrorState error={sheet.error} onRetry={() => void sheet.refetch()} title="Could not open the results" />
      </Section>
    );
  }
  return <SheetEditor key={sampleId} sheet={sheet.data} onScanBarcode={onScanBarcode} />;
}

function SheetEditor({ sheet, onScanBarcode }: { sheet: ResultSheetDto; onScanBarcode?: (barcode: string) => void }) {
  const styles = useStyles();
  // Closing a dialog opened from a menu returns focus to that menu's button.
  const restoreFocusTarget = useRestoreFocusTarget();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const userId = useSession((s) => s.user?.id);
  const canEnter = usePermission(LAB_PERMISSIONS.enter);
  const canVerify = usePermission(LAB_PERMISSIONS.verify);
  const canReject = usePermission(LAB_PERMISSIONS.receive);
  const canCall = canEnter || canVerify;
  const sheetKey = labQueries.sheet(sheet.sampleId).queryKey;
  const patient = parseAgeSex(sheet.ageSex);
  const inLab = sheet.sampleStatus === 'Received' || sheet.sampleStatus === 'InProcess';

  // Reference intervals of each analyte for live flags (cached; a failure falls back to the printed interval).
  const testIds = [...new Set(sheet.lines.map((line) => line.labTestId))];
  const masters = useQueries({ queries: testIds.map((id) => ({ ...labQueries.test(id), retry: false })) });
  const masterById = new Map<string, LabTestDetail>();
  for (const master of masters) {
    if (master.data) {
      masterById.set(master.data.id, master.data);
    }
  }

  const draftKey = userId ? draftStorageKey(userId, `lab-results:${sheet.sampleId}`) : null;
  const [edits, setEdits] = useState<Edits>({});
  const [restorable, setRestorable] = useState<SavedDraft | null>(() => readDraft(draftKey));
  const [lineErrors, setLineErrors] = useState<Record<string, string>>({});
  const [selected, setSelected] = useState<string[]>([]);
  const [release, setRelease] = useState(true);
  const [cultureFor, setCultureFor] = useState<string | null>(null);
  const [remarksOpen, setRemarksOpen] = useState<string[]>([]);
  const [callState, setCallState] = useState<CallState | null>(null);
  const [qcState, setQcState] = useState<QcState | null>(null);
  const [reopenLine, setReopenLine] = useState<ResultSheetLine | null>(null);
  const [rejecting, setRejecting] = useState(false);
  const inputs = useRef(new Map<string, HTMLElement>());
  const scanState = useRef<{ timer: KeystrokeTimer; before: string } | null>(null);

  // Typing is kept on this computer as it happens, so switching tubes, a crash or a WiFi drop never loses results.
  useEffect(() => {
    if (!draftKey) {
      return;
    }
    if (Object.keys(edits).length > 0) {
      writeDraft(draftKey, edits);
    } else if (!restorable) {
      removeDraft(draftKey);
    }
  }, [draftKey, edits, restorable]);

  const lines = sheet.lines;
  const interpretation = new Map<string, LineInterpretation>();
  for (const line of lines) {
    const master = masterById.get(line.labTestId);
    interpretation.set(
      line.resultId,
      interpretLine(line, edits[line.resultId], {
        gender: patient.gender,
        ageDays: patient.ageDays,
        ranges: master ? master.ranges : undefined,
        deltaThresholdPercent: master?.deltaThresholdPercent,
        options: master?.options,
      }),
    );
  }

  const editable = (line: ResultSheetLine) => canEnter && inLab && line.status !== 'Verified';
  const dirtyLines = lines.filter((line) => isDirty(line, edits[line.resultId]));
  const editableIds = lines.filter(editable).map((line) => line.resultId);
  const selectable = (line: ResultSheetLine) =>
    canVerify && line.status === 'Entered' && !isDirty(line, edits[line.resultId]);
  const selectedLines = lines.filter((line) => selected.includes(line.resultId) && selectable(line));
  const readyLines = lines.filter(
    (line) => selectable(line) && isReadyForBulkVerify(line, interpretation.get(line.resultId)!),
  );
  const uncalled = lines.filter((line) => line.isCritical && !line.hasCriticalCall && line.status !== 'Pending');
  const hasInvalid = dirtyLines.some((line) => interpretation.get(line.resultId)?.invalid);

  const setEdit = (line: ResultSheetLine, patch: Partial<SheetEdit>) => {
    setEdits((current) => ({
      ...current,
      [line.resultId]: {
        value: current[line.resultId]?.value ?? line.value ?? '',
        ...current[line.resultId],
        ...patch,
      },
    }));
    if (lineErrors[line.resultId]) {
      setLineErrors((current) => {
        const next = { ...current };
        delete next[line.resultId];
        return next;
      });
    }
  };

  const invalidateLists = () => {
    void queryClient.invalidateQueries({ queryKey: labKeys.worklist });
    void queryClient.invalidateQueries({ queryKey: labKeys.orders });
    void queryClient.invalidateQueries({ queryKey: labKeys.reports });
  };

  // ------------------------------------------------------------------------------------------------ save

  const save = useMutation({
    mutationFn: (entries: ResultEntry[]) => labApi.enterResults(entries),
    onSuccess: (updated, entries) => {
      const byId = new Map(updated.map((line) => [line.resultId, line]));
      queryClient.setQueryData(sheetKey, (old) =>
        old ? { ...old, lines: old.lines.map((line) => byId.get(line.resultId) ?? line) } : old,
      );
      const savedIds = new Set(entries.map((entry) => entry.resultId));
      setEdits((current) => Object.fromEntries(Object.entries(current).filter(([id]) => !savedIds.has(id))));
      setLineErrors({});
      setRestorable(null);
      void queryClient.invalidateQueries({ queryKey: sheetKey });
      invalidateLists();
      notify.success(entries.length === 1 ? 'Result saved' : `${entries.length} results saved`);
      const critical = updated.filter((line) => line.isCritical && !line.hasCriticalCall);
      if (critical.length > 0 && canCall) {
        setCallState({ mode: 'entry', findings: critical.map(findingOf) });
      }
    },
    onError: (error, entries) => {
      const indexed = indexedFieldErrors(error, 'entries');
      if (indexed.size > 0) {
        const next: Record<string, string> = {};
        indexed.forEach((message, index) => {
          const entry = entries[index];
          if (entry) {
            next[entry.resultId] = message;
          }
        });
        setLineErrors(next);
        notify.warning(
          'Some results were not saved',
          'Correct the highlighted values and save again. Nothing was saved.',
        );
      } else {
        notify.error(error, 'Could not save the results');
      }
    },
  });

  const saveAll = () => {
    if (save.isPending || dirtyLines.length === 0) {
      return;
    }
    if (hasInvalid) {
      notify.warning('Check the highlighted values', 'Some values are not valid for their test.');
      return;
    }
    save.mutate(
      dirtyLines.map((line) => {
        const edit = edits[line.resultId]!;
        return {
          resultId: line.resultId,
          value: edit.value.trim() || null,
          remarks: (edit.remarks ?? line.remarks ?? '').trim() || null,
          organism: (edit.organism ?? line.organism ?? '').trim() || null,
          colonyCount: (edit.colonyCount ?? line.colonyCount ?? '').trim() || null,
          susceptibilities: edit.susceptibilities ?? null,
        };
      }),
    );
  };

  useHotkey('mod+s', saveAll, { allowInInputs: true, enabled: canEnter });

  // ------------------------------------------------------------------------------------------------ verify

  const verify = useMutation({
    mutationFn: (request: VerifyResultsRequest) => labApi.verifyResults(request),
    onSuccess: (outcome, request) => {
      setSelected([]);
      setCallState(null);
      setQcState(null);
      void queryClient.invalidateQueries({ queryKey: sheetKey });
      invalidateLists();
      const released = outcome.releasedItemIds.length;
      notify.success(
        outcome.verified === 1 ? 'Result verified' : `${outcome.verified} results verified`,
        released > 0
          ? `${released} ${released === 1 ? 'test is' : 'tests are'} now released to the doctor and the patient.`
          : request.release
            ? 'The report is released when all results of a test are verified.'
            : 'Release the report when you are ready.',
      );
    },
    onError: (error, request) => {
      if (hasErrorCode(error, 'lab.qc_lockout')) {
        setCallState(null);
        setQcState({ lockouts: errorData<{ lockouts?: QcLockout[] }>(error)?.lockouts ?? [], request });
        return;
      }
      if (hasErrorCode(error, 'lab.critical_call_required')) {
        const results = errorData<{ results?: CriticalCallRequiredResult[] }>(error)?.results ?? [];
        setQcState(null);
        setCallState({
          mode: 'verify',
          findings: results.map((r) => ({
            id: r.id,
            label: `${r.testName}: ${r.value}${r.flag ? ` (${flagDisplay(r.flag)?.label.toLowerCase() ?? r.flag})` : ''}`,
          })),
          request,
        });
        return;
      }
      notify.error(error, 'Could not verify the results');
      void queryClient.invalidateQueries({ queryKey: sheetKey });
    },
  });

  const startVerify = (ids: string[]) => {
    const chosen = lines.filter((line) => ids.includes(line.resultId) && selectable(line));
    if (chosen.length === 0) {
      return;
    }
    const request: VerifyResultsRequest = {
      resultIds: chosen.map((line) => line.resultId),
      qcOverrideReason: null,
      criticalCall: null,
      release,
    };
    const needCall = chosen.filter((line) => line.isCritical && !line.hasCriticalCall);
    if (needCall.length > 0) {
      setCallState({ mode: 'verify', findings: needCall.map(findingOf), request });
    } else {
      verify.mutate(request);
    }
  };

  // ------------------------------------------------------------------------------------------------ critical calls, reopen

  const recordCalls = useMutation({
    mutationFn: async ({ ids, call }: { ids: string[]; call: CriticalCallDetails }) => {
      for (const id of ids) {
        await labApi.recordCriticalCall(id, call);
      }
    },
    onSuccess: () => {
      setCallState(null);
      notify.success('Critical call recorded', 'It is in the critical call register.');
      void queryClient.invalidateQueries({ queryKey: sheetKey });
      void queryClient.invalidateQueries({ queryKey: ['lab', 'critical-calls'] });
    },
    onError: (error) => notify.error(error, 'Could not record the call'),
  });

  const reopen = useMutation({
    mutationFn: ({ line, reason }: { line: ResultSheetLine; reason: string }) =>
      labApi.reopenResult(line.resultId, reason),
    onSuccess: (_, { line }) => {
      setReopenLine(null);
      notify.success(`${line.testName} reopened`, 'Correct the value, save and verify it again.');
      void queryClient.invalidateQueries({ queryKey: sheetKey });
      invalidateLists();
    },
    onError: (error) => notify.error(error, 'Could not reopen the result'),
  });

  // ------------------------------------------------------------------------------------------------ keyboard

  const focusRelative = (resultId: string, step: number) => {
    const index = editableIds.indexOf(resultId);
    const target = index < 0 ? undefined : editableIds[index + step];
    if (!target) {
      return false;
    }
    const element = inputs.current.get(target);
    element?.focus();
    if (element instanceof HTMLInputElement) {
      element.select();
    }
    return true;
  };

  const onValueKeyDown = (line: ResultSheetLine, event: KeyboardEvent<HTMLElement>) => {
    const isSelect = event.currentTarget instanceof HTMLSelectElement;
    if (
      event.key.length === 1 &&
      !event.ctrlKey &&
      !event.metaKey &&
      !event.altKey &&
      event.currentTarget instanceof HTMLInputElement
    ) {
      const state = (scanState.current ??= { timer: new KeystrokeTimer(35), before: '' });
      if (state.timer.record(event.timeStamp)) {
        state.before = event.currentTarget.value;
      }
      return;
    }
    if (event.key === 'Enter' && event.currentTarget instanceof HTMLButtonElement) {
      return;
    }
    if (event.key === 'Enter') {
      // A tube barcode scanned into a result field by mistake: undo the typing and open that tube instead.
      const state = scanState.current;
      if (state && state.timer.isScannerBurst(6) && event.currentTarget instanceof HTMLInputElement) {
        const typed = event.currentTarget.value;
        const scanned = normalizeScan(typed.startsWith(state.before) ? typed.slice(state.before.length) : typed);
        state.timer.reset();
        if (isSampleBarcode(scanned) && onScanBarcode) {
          event.preventDefault();
          setEdit(line, { value: state.before });
          notify.info('Barcode scanned', `Opening sample ${scanned}. The result field was left unchanged.`);
          onScanBarcode(scanned);
          return;
        }
      }
      event.preventDefault();
      focusRelative(line.resultId, 1);
      return;
    }
    if (event.key === 'Tab') {
      if (focusRelative(line.resultId, event.shiftKey ? -1 : 1)) {
        event.preventDefault();
      }
      return;
    }
    if (!isSelect && (event.key === 'ArrowDown' || event.key === 'ArrowUp')) {
      if (focusRelative(line.resultId, event.key === 'ArrowDown' ? 1 : -1)) {
        event.preventDefault();
      }
    }
  };

  const register = (resultId: string) => (element: HTMLElement | null) => {
    if (element) {
      inputs.current.set(resultId, element);
    } else {
      inputs.current.delete(resultId);
    }
  };

  // ------------------------------------------------------------------------------------------------ render

  const groups = groupLines(lines);
  const cultureLine = cultureFor ? lines.find((line) => line.resultId === cultureFor) : undefined;
  const orderedTests = [...new Set(lines.map((line) => line.orderedTest))];

  const valueEditor = (line: ResultSheetLine, interp: LineInterpretation) => {
    const edit = edits[line.resultId];
    const value = edit?.value ?? line.value ?? '';
    const error = lineErrors[line.resultId] ?? interp.invalid;
    const common = {
      'aria-label': `${line.testName} result${line.unit ? ` in ${line.unit}` : ''}`,
      'aria-invalid': Boolean(error),
      onKeyDown: (event: KeyboardEvent<HTMLElement>) => onValueKeyDown(line, event),
    };
    let control;
    if (line.resultType === 'Options' && line.options.length > 0) {
      control = (
        <Select
          ref={register(line.resultId)}
          value={value}
          onChange={(_, data) => setEdit(line, { value: data.value })}
          {...common}
        >
          <option value="">—</option>
          {line.options.map((option) => (
            <option key={option} value={option}>
              {option}
            </option>
          ))}
        </Select>
      );
    } else if (line.resultType === 'Culture') {
      const organism = edit?.organism ?? line.organism;
      control = (
        <Button
          ref={register(line.resultId)}
          onClick={() => setCultureFor(line.resultId)}
          icon={<BeakerDismissRegular />}
          {...common}
        >
          {value ? `${value}${organism ? `: ${organism}` : ''}` : 'Enter culture result'}
        </Button>
      );
    } else {
      control = (
        <Input
          ref={register(line.resultId)}
          value={value}
          onChange={(_, data) => setEdit(line, { value: data.value.slice(0, 2000) })}
          contentAfter={line.unit ? <Caption1>{line.unit}</Caption1> : undefined}
          inputMode={line.resultType === 'Numeric' ? 'decimal' : 'text'}
          autoComplete="off"
          {...common}
        />
      );
    }
    return (
      <>
        {control}
        {error && (
          <span className={styles.invalid} role="alert">
            {error}
          </span>
        )}
      </>
    );
  };

  const valueDisplay = (line: ResultSheetLine) => {
    const critical = isCriticalFlag(line.flag);
    if (!line.value) {
      return <Caption1 className={styles.muted}>{line.status === 'Pending' ? 'Awaited' : '—'}</Caption1>;
    }
    return (
      <span>
        <Text className={critical ? styles.critText : styles.valueText}>{line.value}</Text>{' '}
        {line.unit && <Caption1>{line.unit}</Caption1>}
        {line.resultType === 'Culture' && line.organism && <Caption1 block>{line.organism}</Caption1>}
        {line.resultType === 'Culture' && (line.susceptibilities.length > 0 || line.organism) && (
          <Button size="small" appearance="transparent" onClick={() => setCultureFor(line.resultId)}>
            View sensitivity
          </Button>
        )}
      </span>
    );
  };

  const statusCell = (line: ResultSheetLine) => {
    if (line.status === 'Verified') {
      return (
        <Tooltip
          content={
            line.verifiedAt ? `Verified ${formatDate(line.verifiedAt)} ${formatTime(line.verifiedAt)}` : 'Verified'
          }
          relationship="description"
        >
          <Badge appearance="tint" color="success" icon={<CheckmarkRegular />} shape="rounded">
            {line.autoValidated ? 'Auto-verified' : 'Verified'}
          </Badge>
        </Tooltip>
      );
    }
    if (line.status === 'Entered') {
      return (
        <Badge appearance="tint" color="brand" shape="rounded">
          {line.instrumentCode ? `From ${line.instrumentCode}` : 'Entered'}
        </Badge>
      );
    }
    return (
      <Badge appearance="outline" color="informative" shape="rounded">
        Pending
      </Badge>
    );
  };

  const previousCell = (line: ResultSheetLine, interp: LineInterpretation) => {
    if (line.previousValue === null) {
      return <Caption1 className={styles.muted}>—</Caption1>;
    }
    const typed = interp.dirty
      ? Number.parseFloat((edits[line.resultId]?.value ?? '').replace(/,/g, ''))
      : Number.parseFloat((line.value ?? '').replace(/,/g, ''));
    const delta = formatDelta(Number.isFinite(typed) ? typed : null, line.previousValue, interp.deltaPercent);
    return (
      <span>
        <Text>{line.previousValue.toLocaleString('en-IN')}</Text>
        {line.previousResultAt && (
          <Caption1 block className={styles.muted}>
            {formatDate(line.previousResultAt)}
          </Caption1>
        )}
        {delta && (
          <span className={mergeClasses(styles.delta, interp.deltaExceeded && styles.deltaBad)}>
            {interp.deltaExceeded && <WarningRegular aria-hidden />}
            {delta}
            {interp.deltaExceeded && ' — check'}
          </span>
        )}
      </span>
    );
  };

  return (
    <div className={styles.root}>
      <PatientBannerWithFlags
        patientId={sheet.patientId}
        patient={{
          id: sheet.patientId,
          uhid: sheet.uhid,
          fullName: sheet.patientName,
          gender: patient.gender,
          age: patient.age,
        }}
        actions={
          <Menu>
            <MenuTrigger disableButtonEnhancement>
              <Button
                icon={<MoreHorizontalRegular />}
                aria-label="More actions for this sample"
                {...restoreFocusTarget}
              />
            </MenuTrigger>
            <MenuPopover>
              <MenuList>
                <MenuItem
                  icon={<DocumentBulletListRegular />}
                  onClick={() => void navigate({ to: '/lab/orders/$orderId', params: { orderId: sheet.labOrderId } })}
                >
                  Open the order
                </MenuItem>
                {canReject &&
                  sheet.sampleStatus !== 'Rejected' &&
                  sheet.sampleStatus !== 'Completed' &&
                  sheet.sampleStatus !== 'Cancelled' && (
                    <MenuItem icon={<BeakerDismissRegular />} onClick={() => setRejecting(true)}>
                      Reject this sample
                    </MenuItem>
                  )}
              </MenuList>
            </MenuPopover>
          </Menu>
        }
      />

      <div className={styles.info}>
        <span>
          Sample <Text className={styles.mono}>{sheet.barcode}</Text>
        </span>
        <span>
          Accession <Text className={styles.mono}>{sheet.accessionNumber}</Text>
        </span>
        <SampleStatusBadge status={sheet.sampleStatus} />
        <Caption1>{orderedTests.join(' · ')}</Caption1>
      </div>

      {sheet.clinicalNotes && (
        <MessageBar intent="info" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Clinical notes</MessageBarTitle>
            {sheet.clinicalNotes}
          </MessageBarBody>
        </MessageBar>
      )}

      {!inLab && sheet.sampleStatus !== 'Completed' && (
        <MessageBar intent="warning">
          <MessageBarBody>
            {sheet.sampleStatus === 'Collected'
              ? 'This sample has not been received in the lab yet. Receive it before entering results.'
              : sheet.sampleStatus === 'Rejected'
                ? 'This sample was rejected. Results are entered on the new sample.'
                : 'Results cannot be entered for this sample.'}
          </MessageBarBody>
        </MessageBar>
      )}

      {restorable && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Unsaved results found</MessageBarTitle>
            Values typed on this computer at {formatTime(new Date(restorable.savedAt).toISOString())} were not saved.
          </MessageBarBody>
          <MessageBarActions>
            <Button
              appearance="primary"
              size="small"
              onClick={() => {
                const saved = restorable.value;
                setEdits((current) => ({ ...saved, ...current }));
                setRestorable(null);
              }}
            >
              Restore
            </Button>
            <Button
              size="small"
              onClick={() => {
                setRestorable(null);
                if (Object.keys(edits).length === 0) {
                  removeDraft(draftKey);
                }
              }}
            >
              Discard
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}

      {uncalled.length > 0 && canCall && (
        <MessageBar intent="error" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Critical {uncalled.length === 1 ? 'value' : 'values'}: inform the doctor</MessageBarTitle>
            {uncalled.map((line) => findingOf(line).label).join(' · ')}
          </MessageBarBody>
          <MessageBarActions>
            <Button
              appearance="primary"
              size="small"
              icon={<CallRegular />}
              onClick={() => setCallState({ mode: 'entry', findings: uncalled.map(findingOf) })}
            >
              Log the call
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}

      <Section flush>
        <div className={styles.tableWrap}>
          <table className={styles.table} aria-label={`Results for sample ${sheet.barcode}`}>
            <thead>
              <tr>
                {canVerify && <th className={styles.th} style={{ width: 32 }} aria-label="Select for verification" />}
                <th className={styles.th}>Test</th>
                <th className={styles.th}>Result</th>
                <th className={styles.th} style={{ width: 48 }}>
                  Flag
                </th>
                <th className={styles.th}>Reference range</th>
                <th className={styles.th}>Previous</th>
                <th className={styles.th} style={{ width: 110 }}>
                  Status
                </th>
                <th className={styles.th} style={{ width: 40 }} aria-label="Actions" />
              </tr>
            </thead>
            <tbody>
              {groups.map((group) => (
                <GroupRows key={group.orderedTest} group={group} columns={canVerify ? 8 : 7} styles={styles}>
                  {(line) => {
                    const interp = interpretation.get(line.resultId)!;
                    const hints = line.status === 'Pending' && !interp.dirty ? [] : reviewHints(line, interp);
                    const ready = selectable(line) && isReadyForBulkVerify(line, interp);
                    const critical = isCriticalFlag(interp.flag);
                    const abnormal = !critical && interp.flag !== null && interp.flag !== 'Normal';
                    const showRemarks =
                      remarksOpen.includes(line.resultId) ||
                      Boolean(line.remarks) ||
                      edits[line.resultId]?.remarks !== undefined;
                    return (
                      <tr
                        key={line.resultId}
                        className={critical ? styles.critical : abnormal ? styles.abnormal : undefined}
                      >
                        {canVerify && (
                          <td className={styles.td}>
                            {selectable(line) && (
                              <Checkbox
                                checked={selected.includes(line.resultId)}
                                onChange={(_, data) =>
                                  setSelected((current) =>
                                    data.checked
                                      ? [...current, line.resultId]
                                      : current.filter((id) => id !== line.resultId),
                                  )
                                }
                                aria-label={`Select ${line.testName} for verification`}
                              />
                            )}
                          </td>
                        )}
                        <td className={styles.td}>
                          <div className={styles.testName}>
                            <Text weight="semibold">{line.testName}</Text>
                            {line.method && <Caption1 className={styles.muted}>{line.method}</Caption1>}
                            {canVerify &&
                              line.status === 'Entered' &&
                              (hints.length > 0 ? (
                                <span className={styles.hints}>{hints.join(' · ')}</span>
                              ) : ready ? (
                                <span className={styles.ready}>Ready to verify</span>
                              ) : null)}
                            {!canVerify && interp.deltaExceeded && (
                              <span className={styles.hints}>
                                Large change from the previous result: check the sample
                              </span>
                            )}
                          </div>
                        </td>
                        <td className={mergeClasses(styles.td, styles.valueCell)}>
                          {editable(line) ? valueEditor(line, interp) : valueDisplay(line)}
                          {showRemarks && (
                            <div className={styles.remarks}>
                              {editable(line) ? (
                                <Input
                                  size="small"
                                  value={edits[line.resultId]?.remarks ?? line.remarks ?? ''}
                                  onChange={(_, data) => setEdit(line, { remarks: data.value.slice(0, 1000) })}
                                  placeholder="Remarks"
                                  aria-label={`Remarks for ${line.testName}`}
                                  style={{ width: '100%' }}
                                />
                              ) : (
                                <Caption1 className={styles.muted}>{line.remarks}</Caption1>
                              )}
                            </div>
                          )}
                        </td>
                        <td className={styles.td}>
                          <FlagChip flag={interp.flag} preview={interp.source === 'preview'} />
                        </td>
                        <td className={styles.td}>
                          <Caption1>{line.referenceText ?? '—'}</Caption1>
                        </td>
                        <td className={styles.td}>{previousCell(line, interp)}</td>
                        <td className={styles.td}>
                          {statusCell(line)}
                          {line.isCritical && line.hasCriticalCall && (
                            <Caption1 block className={styles.muted}>
                              Call logged
                            </Caption1>
                          )}
                        </td>
                        <td className={styles.td}>
                          <Menu>
                            <MenuTrigger disableButtonEnhancement>
                              <Button
                                size="small"
                                appearance="subtle"
                                icon={<MoreHorizontalRegular />}
                                aria-label={`More for ${line.testName}`}
                                {...restoreFocusTarget}
                              />
                            </MenuTrigger>
                            <MenuPopover>
                              <MenuList>
                                {editable(line) && !showRemarks && (
                                  <MenuItem onClick={() => setRemarksOpen((current) => [...current, line.resultId])}>
                                    Add remarks
                                  </MenuItem>
                                )}
                                {canCall && line.isCritical && (
                                  <MenuItem
                                    icon={<CallRegular />}
                                    onClick={() => setCallState({ mode: 'entry', findings: [findingOf(line)] })}
                                  >
                                    {line.hasCriticalCall ? 'Log another call' : 'Log critical call'}
                                  </MenuItem>
                                )}
                                {canVerify && line.status === 'Verified' && (
                                  <MenuItem icon={<ArrowUndoRegular />} onClick={() => setReopenLine(line)}>
                                    Reopen for correction
                                  </MenuItem>
                                )}
                                {canVerify && selectable(line) && (
                                  <MenuItem
                                    icon={<CheckmarkCircleRegular />}
                                    onClick={() => startVerify([line.resultId])}
                                  >
                                    Verify this result
                                  </MenuItem>
                                )}
                                {!editable(line) &&
                                  !(canCall && line.isCritical) &&
                                  !(canVerify && (line.status === 'Verified' || selectable(line))) && (
                                    <MenuItem disabled>No actions</MenuItem>
                                  )}
                              </MenuList>
                            </MenuPopover>
                          </Menu>
                        </td>
                      </tr>
                    );
                  }}
                </GroupRows>
              ))}
            </tbody>
          </table>
        </div>
      </Section>

      {(canEnter || canVerify) && (
        <div className={styles.footer}>
          <Caption1 aria-live="polite">
            {dirtyLines.length > 0
              ? `${dirtyLines.length} unsaved ${dirtyLines.length === 1 ? 'change' : 'changes'} · kept on this computer until saved`
              : 'All results saved'}
            {canEnter && inLab && ' · Tab or Enter moves down, Ctrl+S saves'}
          </Caption1>
          <div className={styles.footerActions}>
            {canVerify && (
              <div className={styles.selectionBar}>
                <Button
                  size="small"
                  appearance="subtle"
                  disabled={readyLines.length === 0}
                  onClick={() => setSelected(readyLines.map((line) => line.resultId))}
                >
                  Select normal results ({readyLines.length})
                </Button>
                <Button
                  size="small"
                  appearance="subtle"
                  disabled={lines.filter(selectable).length === 0}
                  onClick={() => setSelected(lines.filter(selectable).map((line) => line.resultId))}
                >
                  Select all entered
                </Button>
                <Switch
                  checked={release}
                  onChange={(_, data) => setRelease(data.checked)}
                  label="Release after verifying"
                />
              </div>
            )}
            {canEnter && inLab && (
              <Button
                appearance={canVerify && selectedLines.length > 0 ? 'secondary' : 'primary'}
                icon={save.isPending ? <Spinner size="tiny" /> : <SaveRegular />}
                disabled={dirtyLines.length === 0 || save.isPending}
                onClick={saveAll}
                aria-keyshortcuts="Control+S"
              >
                Save results
              </Button>
            )}
            {canVerify && (
              <Button
                appearance={selectedLines.length > 0 ? 'primary' : 'secondary'}
                icon={verify.isPending ? <Spinner size="tiny" /> : <CheckmarkCircleRegular />}
                disabled={selectedLines.length === 0 || verify.isPending}
                onClick={() => startVerify(selectedLines.map((line) => line.resultId))}
              >
                {release ? 'Verify and release' : 'Verify'} ({selectedLines.length})
              </Button>
            )}
          </div>
        </div>
      )}

      {cultureLine && (
        <CultureDialog
          testName={cultureLine.testName}
          readOnly={!editable(cultureLine)}
          options={cultureLine.options}
          initial={{
            value: edits[cultureLine.resultId]?.value ?? cultureLine.value ?? '',
            organism: edits[cultureLine.resultId]?.organism ?? cultureLine.organism ?? '',
            colonyCount: edits[cultureLine.resultId]?.colonyCount ?? cultureLine.colonyCount ?? '',
            susceptibilities: edits[cultureLine.resultId]?.susceptibilities ?? cultureLine.susceptibilities,
          }}
          onClose={() => setCultureFor(null)}
          onApply={(value: CultureValue) => {
            setEdit(cultureLine, value);
            setCultureFor(null);
          }}
        />
      )}

      {callState && (
        <CriticalCallDialog
          open
          findings={callState.findings}
          busy={verify.isPending || recordCalls.isPending}
          title={
            callState.mode === 'verify'
              ? 'Document the critical call before verifying'
              : 'Inform the doctor about the critical result'
          }
          submitLabel={
            callState.mode === 'verify'
              ? release
                ? 'Save call, verify and release'
                : 'Save call and verify'
              : 'Save the call'
          }
          cancelLabel={callState.mode === 'entry' ? 'Later' : 'Cancel'}
          intro={
            callState.mode === 'entry'
              ? 'Call the treating doctor now. If you cannot, the pathologist must document the call before verifying.'
              : undefined
          }
          onCancel={() => setCallState(null)}
          onSubmit={(call) => {
            if (callState.mode === 'verify' && callState.request) {
              verify.mutate({ ...callState.request, criticalCall: call });
            } else {
              recordCalls.mutate({ ids: callState.findings.map((finding) => finding.id), call });
            }
          }}
        />
      )}

      {qcState && (
        <QcOverrideDialog
          assays={qcState.lockouts.map((lockout) => ({ testName: lockout.testName, violations: lockout.violations }))}
          busy={verify.isPending}
          confirmLabel={qcState.request.release ? 'Override, verify and release' : 'Override and verify'}
          onCancel={() => setQcState(null)}
          onConfirm={(reason) => verify.mutate({ ...qcState.request, qcOverrideReason: reason })}
        />
      )}

      {reopenLine && (
        <ReasonDialog
          key={reopenLine.resultId}
          open
          title={`Reopen ${reopenLine.testName}?`}
          description={
            <Text>
              The verified value {reopenLine.value} {reopenLine.unit ?? ''} goes back for correction. Released results
              cannot be reopened: amend the report instead.
            </Text>
          }
          minLength={5}
          suggestions={[
            'Repeat test ordered',
            'Transcription error',
            'Wrong sample suspected',
            'Clinically inconsistent',
          ]}
          confirmLabel="Reopen result"
          busy={reopen.isPending}
          onCancel={() => setReopenLine(null)}
          onConfirm={(reason) => reopen.mutate({ line: reopenLine, reason })}
        />
      )}

      {rejecting && (
        <RejectSampleDialog
          target={{
            sampleId: sheet.sampleId,
            barcode: sheet.barcode,
            patientName: sheet.patientName,
            tests: orderedTests,
          }}
          onClose={() => setRejecting(false)}
          onDone={() => void queryClient.invalidateQueries({ queryKey: sheetKey })}
        />
      )}
    </div>
  );
}

/** One ordered test (panel) with its sub-groups, e.g. "Complete blood count" → "Differential count". */
function GroupRows({
  group,
  columns,
  styles,
  children,
}: {
  group: Group;
  columns: number;
  styles: ReturnType<typeof useStyles>;
  children: (line: ResultSheetLine) => ReactNode;
}) {
  return (
    <>
      <tr>
        <th className={styles.group} colSpan={columns} scope="colgroup">
          {group.orderedTest}
        </th>
      </tr>
      {group.parts.map((part, index) => (
        <PartRows key={`${part.groupName ?? 'main'}-${index}`} part={part} columns={columns} styles={styles}>
          {children}
        </PartRows>
      ))}
    </>
  );
}

function PartRows({
  part,
  columns,
  styles,
  children,
}: {
  part: Group['parts'][number];
  columns: number;
  styles: ReturnType<typeof useStyles>;
  children: (line: ResultSheetLine) => ReactNode;
}) {
  return (
    <>
      {part.groupName && (
        <tr>
          <th className={styles.subgroup} colSpan={columns} scope="colgroup">
            {part.groupName}
          </th>
        </tr>
      )}
      {part.lines.map((line) => children(line))}
    </>
  );
}
