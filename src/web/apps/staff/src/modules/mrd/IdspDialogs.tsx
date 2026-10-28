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
  Select,
  Spinner,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { formatDateOnly, todayIso } from '@hc360/core';
import { useNotify, useUnsavedChanges, useValuesChanged } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { errorMessage } from '@/modules/lab/common/errors';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';
import {
  addCaseProblems,
  caseFirstDay,
  idspApi,
  idspFormLabel,
  idspKeys,
  idspQueries,
  nikshayProblems,
  reportProblems,
  weekLabel,
  type IdspCaseRow,
  type IdspForm,
  type IdspFormWeek,
  type IdspWeekDto,
} from './idsp';

const useStyles = makeStyles({
  content: { display: 'flex', flexDirection: 'column', gap: '12px' },
  row: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** The week's P or L form as sent to IHIP, with the cases it then holds; none is a nil report. */
export function ReportWeekDialog({ week, form, onClose }: { week: IdspWeekDto; form: IdspForm; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const sheet: IdspFormWeek = form === 'Presumptive' ? week.presumptive : week.laboratory;
  const [reportedOn, setReportedOn] = useState(sheet.report?.reportedOn ?? todayIso());
  const [reference, setReference] = useState(sheet.report?.reference ?? '');
  const problem = reportProblems(reportedOn, week.sunday, todayIso());
  const [tried, setTried] = useState(false);
  const count = sheet.cases.length;

  const report = useMutation({
    mutationFn: () =>
      idspApi.report(week.year, week.week, form, {
        reportedOn,
        reference: reference.trim() || null,
        version: sheet.report?.version,
      }),
    onSuccess: (saved) => {
      queryClient.setQueryData(idspKeys.week(week.year, week.week), saved);
      void queryClient.invalidateQueries({ queryKey: idspKeys.overview() });
      notify.success(
        'Report recorded',
        `${idspFormLabel[form]} of ${weekLabel(week.year, week.week)}: ${count === 0 ? 'a nil report' : `${count} cases`}.`,
      );
      onClose();
    },
  });
  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ reportedOn, reference }),
    busy: report.isPending,
    onClose,
  });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            setTried(true);
            if (!problem && !report.isPending) {
              report.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>
              {sheet.report ? 'Report again' : 'Record the report'}: {idspFormLabel[form]}
              <Caption1 block className={styles.muted}>
                Week {weekLabel(week.year, week.week)}, {formatDateOnly(week.monday)} to {formatDateOnly(week.sunday)}
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.content}>
              {report.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(report.error, 'The report could not be recorded.')}</MessageBarBody>
                </MessageBar>
              )}
              <MessageBar intent={count === 0 ? 'info' : 'warning'} layout="multiline">
                <MessageBarBody>
                  {count === 0
                    ? 'No cases this week: record the nil report you sent. The district unit counts a missing report as a silent facility.'
                    : `Upload the ${count === 1 ? 'case' : `${count} cases`} to IHIP first (Download CSV), then record the report here.`}
                </MessageBarBody>
              </MessageBar>
              <div className={styles.row}>
                <Field label="Sent on" required validationMessage={tried ? (problem ?? undefined) : undefined}>
                  <Input type="date" value={reportedOn} max={todayIso()} onChange={(_, d) => setReportedOn(d.value)} />
                </Field>
                <Field label="IHIP reference" hint="The upload or acknowledgement number, if the portal gave one.">
                  <Input value={reference} maxLength={64} onChange={(_, d) => setReference(d.value)} autoFocus />
                </Field>
              </div>
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={report.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={report.isPending}
                disabledFocusable={report.isPending}
                icon={report.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Record report
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

/** A case ruled out, or recorded on the wrong patient: it leaves the forms, the reason kept. */
export function DiscardCaseDialog({ row, onClose }: { row: IdspCaseRow; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [reason, setReason] = useState('');
  const [tried, setTried] = useState(false);
  const problem = reason.trim() ? null : 'Say why the case is discarded.';

  const discard = useMutation({
    mutationFn: () => idspApi.discard(row.id, reason.trim(), row.version),
    onSuccess: (saved) => {
      queryClient.setQueryData(idspKeys.case(row.id), saved);
      void queryClient.invalidateQueries({ queryKey: idspKeys.all });
      notify.success('Case discarded', `${row.disease}: ${row.patientName}.`);
      onClose();
    },
  });
  const guard = useUnsavedChanges({ dirty: useValuesChanged({ reason }), busy: discard.isPending, onClose });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            setTried(true);
            if (!problem && !discard.isPending) {
              discard.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>
              Discard the case
              <Caption1 block className={styles.muted}>
                {row.disease} · {row.patientName} · {row.uhid}
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.content}>
              {discard.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(discard.error, 'The case could not be discarded.')}</MessageBarBody>
                </MessageBar>
              )}
              <Field label="Reason" required validationMessage={tried ? (problem ?? undefined) : undefined}>
                <Textarea value={reason} maxLength={500} onChange={(_, d) => setReason(d.value)} autoFocus />
              </Field>
              <Caption1 className={styles.muted}>
                If the week was already reported, correct the case on IHIP too.
              </Caption1>
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={discard.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={discard.isPending}
                disabledFocusable={discard.isPending}
                icon={discard.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Discard
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

/**
 * A case the system did not catch: acute flaccid paralysis, a disease diagnosed without its code, or a result from
 * an outside laboratory (which makes it laboratory-confirmed).
 */
export function AddCaseDialog({ onClose }: { onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const diseases = useQuery(idspQueries.diseases());
  const [patient, setPatient] = useState<PickedPatient | null>(null);
  const [diseaseCode, setDiseaseCode] = useState('');
  const [onsetDate, setOnsetDate] = useState('');
  const [testName, setTestName] = useState('');
  const [testResult, setTestResult] = useState('');
  const [tried, setTried] = useState(false);
  const today = todayIso();
  const problems = addCaseProblems({
    patientId: patient?.id ?? null,
    diseaseCode,
    onsetDate,
    testName,
    testResult,
    today,
  });
  const valid = Object.values(problems).every((p) => p === null);
  const show = (p: string | null) => (tried ? (p ?? undefined) : undefined);

  const add = useMutation({
    mutationFn: () =>
      idspApi.add({
        patientId: patient!.id,
        diseaseCode,
        onsetDate: onsetDate || null,
        testName: testName.trim() || null,
        testResult: testResult.trim() || null,
      }),
    onSuccess: (saved) => {
      void queryClient.invalidateQueries({ queryKey: idspKeys.all });
      notify.success('Case added', `${saved.disease}: ${saved.patientName}.`);
      onClose();
    },
  });
  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ patient: patient?.id ?? '', diseaseCode, onsetDate, testName, testResult }),
    busy: add.isPending,
    onClose,
  });
  const chosen = diseases.data?.find((d) => d.code === diseaseCode);

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 600 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            setTried(true);
            if (valid && !add.isPending) {
              add.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Add a notifiable case</DialogTitle>
            <DialogContent className={styles.content}>
              {add.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(add.error, 'The case could not be added.')}</MessageBarBody>
                </MessageBar>
              )}
              <PatientPicker
                value={patient}
                onChange={setPatient}
                label="Patient"
                required
                validationMessage={show(problems.patient)}
                autoFocus
              />
              <div className={styles.row}>
                <Field label="Disease" required validationMessage={show(problems.disease)}>
                  <Select value={diseaseCode} onChange={(_, d) => setDiseaseCode(d.value)} disabled={!diseases.data}>
                    <option value="">Choose…</option>
                    {diseases.data?.map((d) => (
                      <option key={d.code} value={d.code}>
                        {d.name}
                      </option>
                    ))}
                  </Select>
                </Field>
                <Field label="Date of onset" validationMessage={show(problems.onset)}>
                  <Input type="date" value={onsetDate} max={today} onChange={(_, d) => setOnsetDate(d.value)} />
                </Field>
              </div>
              {chosen?.immediate && (
                <MessageBar intent="warning">
                  <MessageBarBody>
                    {chosen.name} is outbreak-prone: inform the district surveillance unit within 24 hours.
                  </MessageBarBody>
                </MessageBar>
              )}
              {chosen?.nikshay && (
                <MessageBar intent="info">
                  <MessageBarBody>
                    {chosen.name} is notified on Ni-kshay, not on the IHIP forms: the case waits under Tuberculosis
                    until its Ni-kshay ID is recorded.
                  </MessageBarBody>
                </MessageBar>
              )}
              <div className={styles.row}>
                <Field
                  label="Test"
                  hint="Only for a result from an outside laboratory."
                  validationMessage={show(problems.test)}
                >
                  <Input value={testName} maxLength={200} onChange={(_, d) => setTestName(d.value)} />
                </Field>
                <Field label="Result">
                  <Input value={testResult} maxLength={200} onChange={(_, d) => setTestResult(d.value)} />
                </Field>
              </div>
              {!chosen?.nikshay && (
                <Caption1 className={styles.muted}>
                  With a result, the case goes on this week&apos;s L form; without one, on the P form.
                </Caption1>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={add.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={add.isPending}
                disabledFocusable={add.isPending}
                icon={add.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Add case
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

/**
 * A tuberculosis case was notified on Ni-kshay: its Ni-kshay ID and the day, from the case's first diagnosis to today.
 * Recording it again corrects them.
 */
export function RecordNikshayDialog({ row, onClose }: { row: IdspCaseRow; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const today = todayIso();
  const firstDay = caseFirstDay(row);
  const [nikshayId, setNikshayId] = useState(row.nikshayId ?? '');
  const [notifiedOn, setNotifiedOn] = useState(row.nikshayNotifiedOn ?? today);
  const [tried, setTried] = useState(false);
  const problems = nikshayProblems(nikshayId, notifiedOn, firstDay, today);
  const valid = problems.id === null && problems.date === null;

  const record = useMutation({
    mutationFn: () => idspApi.recordNikshay(row.id, { nikshayId: nikshayId.trim(), notifiedOn, version: row.version }),
    onSuccess: (saved) => {
      queryClient.setQueryData(idspKeys.case(row.id), saved);
      void queryClient.invalidateQueries({ queryKey: idspKeys.all });
      notify.success('Ni-kshay ID recorded', `${row.patientName}: ${saved.nikshayId ?? ''}.`);
      onClose();
    },
  });
  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ nikshayId, notifiedOn }),
    busy: record.isPending,
    onClose,
  });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            setTried(true);
            if (valid && !record.isPending) {
              record.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>
              {row.nikshayId ? 'Correct the Ni-kshay notification' : 'Record the Ni-kshay notification'}
              <Caption1 block className={styles.muted}>
                {row.disease} · {row.patientName} · {row.uhid}
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.content}>
              {record.error && (
                <MessageBar intent="error">
                  <MessageBarBody>
                    {errorMessage(record.error, 'The Ni-kshay ID could not be recorded.')}
                  </MessageBarBody>
                </MessageBar>
              )}
              <Caption1 className={styles.muted}>
                Notify the case on Ni-kshay first, then record the ID Ni-kshay gave the patient.
              </Caption1>
              <div className={styles.row}>
                <Field label="Ni-kshay ID" required validationMessage={tried ? (problems.id ?? undefined) : undefined}>
                  <Input value={nikshayId} maxLength={32} onChange={(_, d) => setNikshayId(d.value)} autoFocus />
                </Field>
                <Field
                  label="Notified on"
                  required
                  validationMessage={tried ? (problems.date ?? undefined) : undefined}
                >
                  <Input
                    type="date"
                    value={notifiedOn}
                    min={firstDay ?? undefined}
                    max={today}
                    onChange={(_, d) => setNotifiedOn(d.value)}
                  />
                </Field>
              </div>
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={record.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={record.isPending}
                disabledFocusable={record.isPending}
                icon={record.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Record
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
