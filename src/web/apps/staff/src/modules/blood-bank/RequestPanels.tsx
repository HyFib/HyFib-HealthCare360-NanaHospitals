import {
  Badge,
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
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Radio,
  RadioGroup,
  SearchBox,
  Select,
  Spinner,
  Switch,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowUndoRegular,
  BarcodeScannerRegular,
  BeakerRegular,
  HeartPulseWarningRegular,
  PrintRegular,
  ShieldErrorRegular,
  WarningRegular,
} from '@fluentui/react-icons';
import { formatDateTime, formatTime, isApiError, type BloodGroup } from '@hc360/core';
import { ConfirmDialog, DataTable, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type KeyboardEvent } from 'react';
import { useLetterhead } from '@/lib/letterhead';
import { formatDuration } from '@/modules/ot/shared/datetime';
import { ActionError, ChargeBadge, WhoWhen } from '@/modules/ot/shared/feedback';
import {
  bloodApi,
  bloodKeys,
  bloodQueries,
  CROSSMATCH_METHODS,
  KNOWN_GROUPS,
  type BloodIssueDto,
  type BloodRequestDetail,
  type CompatibleUnitDto,
  type CrossMatchDto,
  type CrossmatchMethod,
  type CrossmatchResult,
} from './api';
import { BloodGroupBadge, ExpiryText, TtiBadge, UnitStatusBadge } from './BloodBadges';
import { componentLabel, componentShort, crossmatchMethodLabel, groupLabel } from './labels';
import { printIssueSlip } from './print';
import { compatibilityText, crossmatchValidity, isKnownGroup, issueCheck, returnOutcome, ttiState } from './rules';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  row: { display: 'flex', gap: '12px', alignItems: 'center', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  mono: { fontFamily: tokens.fontFamilyMonospace },
  emergency: {
    padding: '12px 14px',
    borderRadius: tokens.borderRadiusLarge,
    border: `2px solid ${tokens.colorPaletteRedBorder2}`,
    backgroundColor: tokens.colorPaletteRedBackground1,
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  unit: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  picks: { display: 'flex', gap: '6px', flexWrap: 'wrap' },
  valid: { color: tokens.colorPaletteGreenForeground1 },
  invalid: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
});

// ---------------------------------------------------------------- grouping

/** The patient's group from the blood bank's own grouping of the request sample (never the registration group). */
export function GroupingCard({ request, canGroup }: { request: BloodRequestDetail; canGroup: boolean }) {
  const styles = useStyles();
  const [open, setOpen] = useState(false);
  const grouped = isKnownGroup(request.patientBloodGroup) && request.groupedAt !== null;
  const closed = request.status === 'Issued' || request.status === 'Cancelled';
  return (
    <div className={styles.stack}>
      <div className={styles.row}>
        <BloodGroupBadge
          group={grouped ? request.patientBloodGroup : 'Unknown'}
          size="extra-large"
          note="blood bank grouping"
        />
        {grouped ? (
          <WhoWhen prefix="Grouped by" by={request.groupedByName} at={request.groupedAt} />
        ) : (
          <Text>Not grouped yet. Group the request sample before cross-matching.</Text>
        )}
      </div>
      <Caption1 className={styles.muted}>The group recorded at registration is never used for transfusion.</Caption1>
      {canGroup && !closed && (
        <div>
          <Button icon={<BeakerRegular />} appearance={grouped ? 'secondary' : 'primary'} onClick={() => setOpen(true)}>
            {grouped ? 'Group a second sample' : 'Record the patient’s group'}
          </Button>
        </div>
      )}
      {open && <GroupDialog request={request} onClose={() => setOpen(false)} />}
    </div>
  );
}

function GroupDialog({ request, onClose }: { request: BloodRequestDetail; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [group, setGroup] = useState<BloodGroup | ''>('');
  const record = useMutation({
    mutationFn: () => bloodApi.groupPatient(request.id, group || 'Unknown'),
    onSuccess: () => {
      notify.success('Patient grouped');
      void queryClient.invalidateQueries({ queryKey: bloodKeys.request(request.id) });
      void queryClient.invalidateQueries({ queryKey: bloodKeys.compatible(request.id) });
      onClose();
    },
  });
  const discrepancy = isApiError(record.error) && record.error.code === 'bloodbank.grouping_discrepancy';
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <DialogBody>
          <DialogTitle>
            Group the patient’s sample
            <Caption1 block className={styles.muted}>
              {request.patientName} · {request.uhid} · {request.requestNumber}
            </Caption1>
          </DialogTitle>
          <DialogContent className={styles.stack}>
            <Field label="ABO and Rh(D) group of this sample" required>
              <Select value={group} onChange={(_, d) => setGroup(d.value as BloodGroup | '')} autoFocus>
                <option value="">Choose the group</option>
                {KNOWN_GROUPS.map((g) => (
                  <option key={g} value={g}>
                    {groupLabel(g)}
                  </option>
                ))}
              </Select>
            </Field>
            <Caption1 className={styles.muted}>
              A result that disagrees with an earlier grouping is refused: it may be the wrong blood in the tube.
              Collect a fresh sample and investigate.
            </Caption1>
            {discrepancy ? (
              <MessageBar intent="error" layout="multiline" politeness="assertive" icon={<ShieldErrorRegular />}>
                <MessageBarBody>
                  <MessageBarTitle>Grouping does not match — possible wrong blood in tube</MessageBarTitle>
                  {isApiError(record.error) ? record.error.userMessage : ''} Do not cross-match or issue until this is
                  resolved.
                </MessageBarBody>
              </MessageBar>
            ) : (
              <ActionError error={record.error} />
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={record.isPending}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={!group || record.isPending}
              icon={record.isPending ? <Spinner size="tiny" /> : undefined}
              onClick={() => record.mutate()}
            >
              Save group
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

export function CompatibilityHint({ request }: { request: BloodRequestDetail }) {
  return (
    <MessageBar intent="info" layout="multiline">
      <MessageBarBody>{compatibilityText(request.componentType, request.patientBloodGroup)}</MessageBarBody>
    </MessageBar>
  );
}

// ---------------------------------------------------------------- compatible units and cross-matching

/** Units the server offers: compatible, in date, first-expiry-first-out; a compatible cross-match reserves one. */
export function CompatibleUnitsPanel({ request, now }: { request: BloodRequestDetail; now: number }) {
  const styles = useStyles();
  const grouped = isKnownGroup(request.patientBloodGroup);
  const units = useQuery({ ...bloodQueries.compatibleUnits(request.id), enabled: grouped });
  const [crossMatching, setCrossMatching] = useState<CompatibleUnitDto | null>(null);
  if (!grouped) {
    return <Text className={styles.muted}>Compatible units appear once the patient’s sample is grouped.</Text>;
  }
  return (
    <>
      <DataTable
        ariaLabel="Compatible units"
        rows={units.data}
        loading={units.isLoading}
        error={units.error}
        onRetry={() => void units.refetch()}
        getRowId={(u) => u.id}
        size="small"
        columns={[
          { key: 'number', header: 'Unit', render: (u) => <Text className={styles.mono}>{u.unitNumber}</Text> },
          { key: 'group', header: 'Group', width: 110, render: (u) => groupLabel(u.bloodGroup) },
          { key: 'volume', header: 'Volume', width: 90, align: 'end', render: (u) => `${u.volumeMl} ml` },
          {
            key: 'expiry',
            header: 'Expiry (use first)',
            render: (u) => <ExpiryText expiresAt={u.expiresAt} now={now} />,
          },
          {
            key: 'state',
            header: 'For this request',
            render: (u) =>
              u.reservedForThisRequest ? (
                <Badge appearance="tint" color="brand">
                  Reserved
                </Badge>
              ) : (
                '—'
              ),
          },
        ]}
        rowActions={(u) => (
          <Button size="small" onClick={() => setCrossMatching(u)}>
            {u.reservedForThisRequest ? 'Cross-match again' : 'Record cross-match'}
          </Button>
        )}
        emptyTitle={`No compatible ${componentLabel[request.componentType].toLowerCase()} in stock`}
        emptyDescription="Check units in quarantine awaiting results, arrange replacement donors, or contact another blood bank."
      />
      {crossMatching && (
        <CrossMatchDialog request={request} unit={crossMatching} onClose={() => setCrossMatching(null)} />
      )}
    </>
  );
}

function CrossMatchDialog({
  request,
  unit,
  onClose,
}: {
  request: BloodRequestDetail;
  unit: CompatibleUnitDto;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [method, setMethod] = useState<CrossmatchMethod>('AntiHumanGlobulin');
  const [result, setResult] = useState<CrossmatchResult | ''>('');
  const [remarks, setRemarks] = useState('');
  const save = useMutation({
    mutationFn: () =>
      bloodApi.crossMatch(request.id, unit.id, method, result || 'Incompatible', remarks.trim() || null),
    onSuccess: (dto) => {
      notify.success(
        dto.result === 'Compatible'
          ? `Unit ${dto.unitNumber} compatible and reserved`
          : `Unit ${dto.unitNumber} incompatible`,
      );
      void queryClient.invalidateQueries({ queryKey: bloodKeys.request(request.id) });
      void queryClient.invalidateQueries({ queryKey: bloodKeys.compatible(request.id) });
      void queryClient.invalidateQueries({ queryKey: bloodKeys.inventory });
      onClose();
    },
  });
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <DialogBody>
          <DialogTitle>
            Cross-match unit {unit.unitNumber}
            <Caption1 block className={styles.muted}>
              {groupLabel(unit.bloodGroup)} {componentShort[request.componentType]} for {request.patientName} (
              {groupLabel(request.patientBloodGroup)})
            </Caption1>
          </DialogTitle>
          <DialogContent className={styles.stack}>
            <Field label="Method">
              <Select value={method} onChange={(_, d) => setMethod(d.value as CrossmatchMethod)}>
                {CROSSMATCH_METHODS.map((m) => (
                  <option key={m} value={m}>
                    {crossmatchMethodLabel[m]}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Result" required>
              <RadioGroup
                layout="horizontal"
                value={result}
                onChange={(_, d) => setResult(d.value as CrossmatchResult)}
              >
                <Radio value="Compatible" label="Compatible" />
                <Radio value="Incompatible" label="Incompatible" />
              </RadioGroup>
            </Field>
            <Field label="Remarks">
              <Textarea value={remarks} onChange={(_, d) => setRemarks(d.value)} rows={2} maxLength={500} />
            </Field>
            <Caption1 className={styles.muted}>
              A compatible result reserves the unit for this patient for 72 hours.
            </Caption1>
            <ActionError error={save.error} />
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={save.isPending}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={!result || save.isPending}
              icon={save.isPending ? <Spinner size="tiny" /> : undefined}
              onClick={() => save.mutate()}
            >
              Save cross-match
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

export function CrossMatchesTable({ crossMatches, now }: { crossMatches: CrossMatchDto[]; now: number }) {
  const styles = useStyles();
  return (
    <DataTable
      ariaLabel="Cross-matches"
      rows={crossMatches}
      getRowId={(c) => c.id}
      size="small"
      columns={[
        { key: 'unit', header: 'Unit', render: (c) => <Text className={styles.mono}>{c.unitNumber}</Text> },
        {
          key: 'result',
          header: 'Result',
          render: (c) => <StatusBadge status={c.result} tone={c.result === 'Compatible' ? 'success' : 'danger'} />,
        },
        { key: 'method', header: 'Method', priority: 'low', render: (c) => crossmatchMethodLabel[c.method] },
        {
          key: 'when',
          header: 'Done',
          render: (c) => `${formatDateTime(c.performedAt)}${c.performedByName ? ` · ${c.performedByName}` : ''}`,
        },
        {
          key: 'validity',
          header: 'Valid for issue',
          render: (c) => {
            if (c.result !== 'Compatible') {
              return '—';
            }
            const validity = crossmatchValidity(c.performedAt, now);
            return c.isValid && validity.valid ? (
              <span className={styles.valid}>
                Until {formatDateTime(validity.expiresAt)} ({validity.hoursLeft} h left)
              </span>
            ) : (
              <span className={styles.invalid}>Older than 72 h — repeat before issue</span>
            );
          },
        },
        { key: 'bill', header: 'Billing', priority: 'low', render: (c) => <ChargeBadge status={c.chargeStatus} /> },
      ]}
      emptyTitle="No cross-matches yet"
    />
  );
}

// ---------------------------------------------------------------- emergency release

/**
 * Doctor-authorised release of uncross-matched blood for life-threatening bleeding: only group O Rh(D)-negative
 * packed red cells, or AB plasma if the patient is not grouped yet. Recorded in the audit log.
 */
export function EmergencyReleasePanel({ request, canIssue }: { request: BloodRequestDetail; canIssue: boolean }) {
  const styles = useStyles();
  const [open, setOpen] = useState(false);
  if (request.urgency !== 'Emergency' || request.status === 'Issued' || request.status === 'Cancelled') {
    return null;
  }
  if (request.emergencyReleaseAuthorisedBy) {
    return (
      <MessageBar intent="error" layout="multiline" icon={<WarningRegular />}>
        <MessageBarBody>
          <MessageBarTitle>Emergency release authorised by {request.emergencyReleaseAuthorisedBy}</MessageBarTitle>
          {request.emergencyReleaseReason} Uncross-matched issue is limited to O Rh(D)-negative packed red cells, or AB
          plasma while the patient is ungrouped. Group and cross-match as soon as the sample arrives.
        </MessageBarBody>
      </MessageBar>
    );
  }
  return (
    <div className={styles.emergency}>
      <Text weight="semibold">
        <WarningRegular /> Life-threatening bleeding and no time to cross-match?
      </Text>
      <Text>
        A doctor can authorise release of uncross-matched blood: only group O Rh(D)-negative packed red cells, or AB
        plasma if the patient is not grouped yet. The decision is recorded with the doctor’s name and audited.
      </Text>
      {canIssue && (
        <div>
          <Button
            appearance="primary"
            icon={<WarningRegular />}
            style={{ backgroundColor: tokens.colorPaletteRedBackground3 }}
            onClick={() => setOpen(true)}
          >
            Authorise emergency release
          </Button>
        </div>
      )}
      {open && <EmergencyReleaseDialog request={request} onClose={() => setOpen(false)} />}
    </div>
  );
}

function EmergencyReleaseDialog({ request, onClose }: { request: BloodRequestDetail; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [doctor, setDoctor] = useState('');
  const [reason, setReason] = useState('');
  const [confirmed, setConfirmed] = useState(false);
  const authorise = useMutation({
    mutationFn: () => bloodApi.emergencyRelease(request.id, doctor.trim(), reason.trim()),
    onSuccess: () => {
      notify.warning(
        'Emergency release authorised',
        'Issue only O Rh(D)-negative red cells or AB plasma until the patient is cross-matched.',
      );
      void queryClient.invalidateQueries({ queryKey: bloodKeys.request(request.id) });
      onClose();
    },
  });
  const valid = doctor.trim().length > 2 && reason.trim().length >= 10 && confirmed;
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()} modalType="alert">
      <DialogSurface style={{ maxWidth: 600 }}>
        <DialogBody>
          <DialogTitle>Authorise uncross-matched blood</DialogTitle>
          <DialogContent className={styles.stack}>
            <MessageBar intent="error" layout="multiline" icon={<WarningRegular />}>
              <MessageBarBody>
                <MessageBarTitle>Risk of a fatal transfusion reaction</MessageBarTitle>
                Uncross-matched blood is given only when waiting for a cross-match would endanger life. Only O
                Rh(D)-negative packed red cells (or AB plasma for an ungrouped patient) can be issued this way. Send the
                patient’s sample for grouping and cross-matching at once.
              </MessageBarBody>
            </MessageBar>
            <Text>
              Patient: <Text weight="semibold">{request.patientName}</Text> ({request.uhid}) · request{' '}
              {request.requestNumber}
            </Text>
            <Field label="Doctor authorising the release" required>
              <Input
                value={doctor}
                onChange={(_, d) => setDoctor(d.value)}
                maxLength={200}
                autoFocus
                placeholder="Name of the treating doctor"
              />
            </Field>
            <Field
              label="Clinical reason"
              required
              hint="At least 10 characters, e.g. haemorrhagic shock after trauma, BP 70/40."
            >
              <Textarea value={reason} onChange={(_, d) => setReason(d.value)} rows={3} maxLength={500} />
            </Field>
            <Checkbox
              checked={confirmed}
              onChange={(_, d) => setConfirmed(d.checked === true)}
              label="I confirm the doctor above has authorised release of uncross-matched blood for this life-threatening situation."
            />
            <ActionError error={authorise.error} />
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={authorise.isPending}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={!valid || authorise.isPending}
              icon={authorise.isPending ? <Spinner size="tiny" /> : <WarningRegular />}
              style={{ backgroundColor: tokens.colorPaletteRedBackground3 }}
              onClick={() => authorise.mutate()}
            >
              Authorise release
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

// ---------------------------------------------------------------- issue

/**
 * The counter check: scan the unit, see every safety check the server will apply (cross-match within 72 h or a
 * valid emergency release, ABO/Rh, expiry, screening, reservation), name the person collecting it, and issue.
 */
export function IssuePanel({ request, now }: { request: BloodRequestDetail; now: number }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const letterhead = useLetterhead();
  const [term, setTerm] = useState('');
  const [number, setNumber] = useState('');
  const [collector, setCollector] = useState('');
  const [confirmEmergency, setConfirmEmergency] = useState(false);
  const unit = useQuery(bloodQueries.unitByNumber(number));
  const u = unit.data && unit.data.unitNumber === number ? unit.data : null;

  const issuedIds = new Set(request.issues.filter((i) => !i.returnedAt).map((i) => i.componentId));
  const ready = request.crossMatches.filter(
    (c) =>
      c.result === 'Compatible' &&
      c.isValid &&
      crossmatchValidity(c.performedAt, now).valid &&
      !issuedIds.has(c.componentId),
  );
  const latestCrossMatch = u
    ? request.crossMatches
        .filter((c) => c.componentId === u.id)
        .sort((a, b) => Date.parse(b.performedAt) - Date.parse(a.performedAt))[0]
    : undefined;
  const validCrossMatch =
    latestCrossMatch &&
    latestCrossMatch.result === 'Compatible' &&
    crossmatchValidity(latestCrossMatch.performedAt, now).valid
      ? latestCrossMatch
      : undefined;
  const check = u
    ? u.componentType !== request.componentType
      ? {
          allowed: false,
          emergency: false,
          reasons: [
            `This is ${componentLabel[u.componentType].toLowerCase()}; the request is for ${componentLabel[request.componentType].toLowerCase()}.`,
          ],
        }
      : issueCheck({
          component: u.componentType,
          unitGroup: u.bloodGroup,
          patientGroup: request.patientBloodGroup,
          hasValidCompatibleCrossmatch: Boolean(validCrossMatch),
          emergencyReleaseAuthorised: Boolean(request.emergencyReleaseAuthorisedBy),
          unitStatus: u.status,
          unitExpired: u.isExpired || Date.parse(u.expiresAt) <= now,
          ttiCleared: ttiState(u.tti).cleared,
          reservedForOtherRequest: u.status === 'Reserved' && u.reservedForRequestId !== request.id,
        })
    : null;

  const issue = useMutation({
    mutationFn: () => bloodApi.issue(request.id, u?.id ?? '', collector.trim()),
    onSuccess: (dto) => {
      setConfirmEmergency(false);
      notify.success(
        `Unit ${dto.unitNumber} issued${dto.isEmergencyRelease ? ' under emergency release' : ''}`,
        'Printing the issue slip.',
      );
      void queryClient.invalidateQueries({ queryKey: bloodKeys.all });
      const refreshed = queryClient.getQueryData<BloodRequestDetail>(bloodKeys.request(request.id)) ?? request;
      void printIssueSlip(
        letterhead,
        refreshed,
        dto,
        validCrossMatch ?? null,
        u ? { expiresAt: u.expiresAt, volumeMl: u.volumeMl, componentType: u.componentType } : null,
      ).catch((error: unknown) => notify.error(error, 'Printing the issue slip failed'));
      setTerm('');
      setNumber('');
      setCollector('');
    },
    onError: () => setConfirmEmergency(false),
  });

  const scan = (value: string) => {
    setTerm(value);
    setNumber(value.trim().toUpperCase());
  };
  const onKeyDown = (event: KeyboardEvent) => {
    if (event.key === 'Enter') {
      event.preventDefault();
      scan(term);
    }
  };
  const submit = () => (check?.emergency ? setConfirmEmergency(true) : issue.mutate());
  const notFound = isApiError(unit.error) && unit.error.status === 404;

  return (
    <div className={styles.stack}>
      {ready.length > 0 && (
        <div>
          <Caption1 className={styles.muted}>Cross-matched and ready:</Caption1>
          <div className={styles.picks}>
            {ready.map((c) => (
              <Button key={c.id} size="small" onClick={() => scan(c.unitNumber)}>
                {c.unitNumber}
              </Button>
            ))}
          </div>
        </div>
      )}
      <Field
        label="Scan the unit label"
        hint="Always scan the bag in hand: the check runs on the unit you are about to hand over."
      >
        <SearchBox
          value={term}
          onChange={(_, d) => setTerm(d.value)}
          onKeyDown={onKeyDown}
          contentBefore={<BarcodeScannerRegular />}
          placeholder="Unit number"
          aria-label="Unit number to issue"
        />
      </Field>
      {unit.isFetching && <Spinner size="tiny" label="Checking the unit…" labelPosition="after" />}
      {notFound && <Text className={styles.invalid}>No unit with the number {number}. Check the label.</Text>}
      {u && (
        <div className={styles.unit}>
          <div className={styles.row}>
            <BloodGroupBadge group={u.bloodGroup} />
            <Text weight="semibold" className={styles.mono}>
              {u.unitNumber}
            </Text>
            <Text>
              {componentLabel[u.componentType]} · {u.volumeMl} ml
            </Text>
            <UnitStatusBadge status={u.status} />
            <TtiBadge tti={u.tti} />
          </div>
          <Caption1>
            Expiry: <ExpiryText expiresAt={u.expiresAt} now={now} />
            {validCrossMatch
              ? ` · cross-matched compatible ${formatDateTime(validCrossMatch.performedAt)}`
              : ' · no valid cross-match'}
          </Caption1>
          {check && !check.allowed && (
            <MessageBar intent="error" layout="multiline" politeness="assertive">
              <MessageBarBody>
                <MessageBarTitle>Do not issue this unit</MessageBarTitle>
                <ul style={{ margin: 0, paddingLeft: 18 }}>
                  {check.reasons.map((reason) => (
                    <li key={reason}>{reason}</li>
                  ))}
                </ul>
              </MessageBarBody>
            </MessageBar>
          )}
          {check?.allowed && check.emergency && (
            <MessageBar intent="warning" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>Uncross-matched — emergency release</MessageBarTitle>
                Allowed under the release authorised by {request.emergencyReleaseAuthorisedBy}. The slip is marked NOT
                CROSS-MATCHED.
              </MessageBarBody>
            </MessageBar>
          )}
          {check?.allowed && !check.emergency && (
            <MessageBar intent="success">
              <MessageBarBody>All checks passed for {request.patientName}.</MessageBarBody>
            </MessageBar>
          )}
        </div>
      )}
      <Field
        label="Handed to (ward staff collecting the unit)"
        required
        hint="Check their identity card at the counter."
      >
        <Input value={collector} onChange={(_, d) => setCollector(d.value)} maxLength={200} />
      </Field>
      <ActionError error={issue.error} />
      <div className={styles.row}>
        <Button
          appearance="primary"
          size="large"
          disabled={!u || !check?.allowed || !collector.trim() || issue.isPending}
          icon={issue.isPending ? <Spinner size="tiny" /> : <PrintRegular />}
          onClick={submit}
        >
          Issue unit and print slip
        </Button>
      </div>
      <ConfirmDialog
        open={confirmEmergency}
        title="Issue uncross-matched blood?"
        confirmLabel="Issue under emergency release"
        destructive
        busy={issue.isPending}
        onConfirm={() => issue.mutate()}
        onCancel={() => setConfirmEmergency(false)}
      >
        {u ? `${groupLabel(u.bloodGroup)} ${componentShort[u.componentType]} ${u.unitNumber}` : 'This unit'} goes to{' '}
        {request.patientName} without a cross-match, as authorised by {request.emergencyReleaseAuthorisedBy}. This is
        recorded in the audit log.
      </ConfirmDialog>
    </div>
  );
}

// ---------------------------------------------------------------- issued units: slip, return, reaction

export function IssuesTable({
  request,
  canIssue,
  canReport,
  onReportReaction,
}: {
  request: BloodRequestDetail;
  canIssue: boolean;
  canReport: boolean;
  onReportReaction: (issue: BloodIssueDto) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const letterhead = useLetterhead();
  const [returning, setReturning] = useState<BloodIssueDto | null>(null);

  const reprint = (issue: BloodIssueDto) => {
    const crossMatch =
      request.crossMatches.find((c) => c.componentId === issue.componentId && c.result === 'Compatible') ?? null;
    void printIssueSlip(letterhead, request, issue, crossMatch, null).catch((error: unknown) =>
      notify.error(error, 'Printing failed'),
    );
  };

  return (
    <>
      <DataTable
        ariaLabel="Issued units"
        rows={request.issues}
        getRowId={(i) => i.id}
        size="small"
        columns={[
          { key: 'unit', header: 'Unit', render: (i) => <Text className={styles.mono}>{i.unitNumber}</Text> },
          {
            key: 'what',
            header: 'Component',
            render: (i) => `${componentShort[i.componentType]} · ${groupLabel(i.unitBloodGroup)}`,
          },
          {
            key: 'issued',
            header: 'Issued',
            render: (i) => (
              <span>
                {formatDateTime(i.issuedAt)}
                <Caption1 block className={styles.muted}>
                  by {i.issuedByName ?? '—'} to {i.issuedToName}
                </Caption1>
              </span>
            ),
          },
          {
            key: 'state',
            header: 'Status',
            render: (i) => (
              <span className={styles.row}>
                {i.isEmergencyRelease && (
                  <Badge color="danger" appearance="filled">
                    Uncross-matched
                  </Badge>
                )}
                {i.returnedAt ? (
                  <StatusBadge
                    status={i.restocked ? 'Restocked' : 'Discarded'}
                    tone={i.restocked ? 'success' : 'danger'}
                    label={i.restocked ? 'Returned to stock' : 'Returned, discarded'}
                  />
                ) : (
                  <StatusBadge status="Issued" tone="brand" label="With the patient" />
                )}
              </span>
            ),
          },
          { key: 'bill', header: 'Billing', priority: 'low', render: (i) => <ChargeBadge status={i.chargeStatus} /> },
        ]}
        rowActions={(i) => (
          <span style={{ display: 'inline-flex', gap: 4 }}>
            <Button
              size="small"
              appearance="subtle"
              icon={<PrintRegular />}
              aria-label={`Print issue slip for ${i.unitNumber}`}
              onClick={() => reprint(i)}
            />
            {canIssue && !i.returnedAt && (
              <Button size="small" icon={<ArrowUndoRegular />} onClick={() => setReturning(i)}>
                Return
              </Button>
            )}
            {canReport && (
              <Button size="small" icon={<HeartPulseWarningRegular />} onClick={() => onReportReaction(i)}>
                Reaction
              </Button>
            )}
          </span>
        )}
        emptyTitle="No units issued yet"
      />
      {returning && <ReturnDialog issue={returning} onClose={() => setReturning(null)} />}
    </>
  );
}

function ReturnDialog({ issue, onClose }: { issue: BloodIssueDto; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [maintained, setMaintained] = useState(false);
  const [notes, setNotes] = useState('');
  // The dialog reads the clock once when it opens: the outcome shown is for returning now.
  const [openedAt] = useState(() => Date.now());
  const outcome = returnOutcome(issue.componentType, issue.issuedAt, openedAt, maintained);
  const minutesOut = Math.max(0, Math.round((openedAt - Date.parse(issue.issuedAt)) / 60_000));
  const accept = useMutation({
    mutationFn: () => bloodApi.returnIssue(issue.id, maintained, notes.trim() || null),
    onSuccess: (dto) => {
      notify.success(
        dto.restocked ? `Unit ${dto.unitNumber} back in stock` : `Unit ${dto.unitNumber} discarded`,
        dto.restocked ? 'The charge was withdrawn.' : 'Recorded as wastage.',
      );
      void queryClient.invalidateQueries({ queryKey: bloodKeys.all });
      onClose();
    },
  });
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <DialogBody>
          <DialogTitle>
            Return unit {issue.unitNumber}
            <Caption1 block className={styles.muted}>
              Issued at {formatTime(issue.issuedAt)} · out for {formatDuration(minutesOut)}
            </Caption1>
          </DialogTitle>
          <DialogContent className={styles.stack}>
            <Switch
              checked={maintained}
              onChange={(_, d) => setMaintained(d.checked)}
              label="Kept at the right temperature (approved transport box, not left out)"
            />
            <MessageBar intent={outcome.restock ? 'success' : 'warning'} layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>{outcome.restock ? 'Goes back to stock' : 'Will be discarded'}</MessageBarTitle>
                {outcome.reason}
              </MessageBarBody>
            </MessageBar>
            <Field label="Notes">
              <Textarea value={notes} onChange={(_, d) => setNotes(d.value)} rows={2} maxLength={500} />
            </Field>
            <ActionError error={accept.error} />
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={accept.isPending}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={accept.isPending}
              icon={accept.isPending ? <Spinner size="tiny" /> : <ArrowUndoRegular />}
              onClick={() => accept.mutate()}
            >
              Accept return
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
