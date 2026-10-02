import {
  Badge,
  Button,
  Caption1,
  Field,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Select,
  Text,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import {
  CheckmarkCircleRegular,
  ClockRegular,
  HeartPulseWarningRegular,
  StethoscopeRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, formatRelative, formatTime, type LookupItem } from '@hc360/core';
import { DataTable } from '@hc360/ui';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { inpatientKeys, type WardRound, type WardRoundPatient } from '@/modules/inpatient/api';
import { RecordVisitDialog } from '@/modules/inpatient/ConsultantVisits';
import { dayOfStayLabel, roundSummaryText, sortRound, summariseRound, type RoundOrder } from './worklist';
import { WorklistPatient } from './WorklistParts';

const useStyles = makeStyles({
  toolbar: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'flex-end',
    gap: '12px',
    flexWrap: 'wrap',
    padding: '12px 16px 8px',
  },
  stack: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: '2px',
    minWidth: 0,
    whiteSpace: 'normal',
  },
  chips: { display: 'flex', gap: '4px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  seen: { display: 'inline-flex', gap: '4px', alignItems: 'center', color: tokens.colorPaletteGreenForeground1 },
  notSeen: { display: 'inline-flex', gap: '4px', alignItems: 'center', color: tokens.colorPaletteMarigoldForeground1 },
  message: { margin: '0 16px 8px' },
});

/**
 * The ward round (EMR-01 "IP rounds"): the doctor's in-patients in the bed board's walking order, or the most urgent
 * first, with whether they have been seen today, the latest note, results waiting and worrying vital signs. A row
 * opens the chart; "Record visit" records the consultant visit, which is charged to the in-patient bill.
 */
export function WardRoundList({
  round,
  loading,
  error,
  onRetry,
  isToday,
  isDepartment,
  canRecordVisit,
  practitioner,
  onOpenChart,
}: {
  round: WardRound | undefined;
  loading: boolean;
  error: unknown;
  onRetry: () => void;
  isToday: boolean;
  /** No doctor chosen: the department's in-patients, seen by anyone. */
  isDepartment: boolean;
  canRecordVisit: boolean;
  /** The doctor whose round this is, offered first when recording a visit. */
  practitioner: LookupItem | null;
  onOpenChart: (patientId: string) => void;
}) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const [order, setOrder] = useState<RoundOrder>('bed');
  const [recording, setRecording] = useState<WardRoundPatient | null>(null);
  const patients = round?.patients;
  const rows = patients ? sortRound(patients, order) : undefined;

  return (
    <>
      <div className={styles.toolbar}>
        <Text aria-live="polite">{patients ? roundSummaryText(summariseRound(patients)) : ' '}</Text>
        <Field label="Order">
          <Select value={order} onChange={(_, d) => setOrder(d.value as RoundOrder)}>
            <option value="bed">Ward and bed</option>
            <option value="attention">Most urgent first</option>
          </Select>
        </Field>
      </div>
      {!isToday && (
        <MessageBar intent="info" className={styles.message}>
          <MessageBarBody>
            The ward round always shows the patients in hospital now, and who has been seen today.
          </MessageBarBody>
        </MessageBar>
      )}
      <DataTable
        ariaLabel="Ward round"
        rows={rows}
        loading={loading}
        error={error}
        onRetry={onRetry}
        getRowId={(p) => p.admissionId}
        onRowClick={(p) => onOpenChart(p.patientId)}
        size="small"
        columns={[
          {
            key: 'bed',
            header: 'Bed',
            width: 120,
            render: (p) => (
              <span className={styles.stack}>
                <Text weight="semibold">{p.bedLabel ?? 'No bed'}</Text>
                <Caption1 className={styles.muted}>{p.wardName ?? '—'}</Caption1>
                {p.isCriticalCare && (
                  <Badge size="small" appearance="tint" color="severe">
                    Critical care
                  </Badge>
                )}
              </span>
            ),
          },
          {
            key: 'patient',
            header: 'Patient',
            render: (p) => (
              <span className={styles.stack}>
                <WorklistPatient
                  name={p.patientName}
                  age={p.age}
                  gender={p.gender}
                  uhid={p.uhid}
                  hasActiveAllergies={p.hasActiveAllergies}
                  hasCriticalAlerts={p.hasCriticalAlerts}
                  isRestricted={p.isRestricted}
                >
                  {p.isMlc && (
                    <Tooltip content="Medico-legal case" relationship="label">
                      <Badge size="small" appearance="tint" color="warning">
                        MLC
                      </Badge>
                    </Tooltip>
                  )}
                  {p.status === 'DischargeInitiated' && (
                    <Badge size="small" appearance="tint" color="informative">
                      Discharge in progress
                    </Badge>
                  )}
                  {!p.isPrimaryConsultant && (
                    <Tooltip
                      content={`Admitted under ${p.consultantName ?? 'another consultant'}. You have seen this patient during the stay.`}
                      relationship="description"
                    >
                      <Badge size="small" appearance="outline">
                        Cross-consult
                      </Badge>
                    </Tooltip>
                  )}
                </WorklistPatient>
                <Caption1 className={styles.muted}>
                  {[
                    dayOfStayLabel(p.dayOfStay),
                    p.provisionalDiagnosis,
                    p.expectedDischargeDate ? `going home ${formatDateOnly(p.expectedDischargeDate)}` : null,
                  ]
                    .filter(Boolean)
                    .join(' · ')}
                </Caption1>
              </span>
            ),
          },
          {
            key: 'today',
            header: isDepartment ? 'Today (any doctor)' : 'Today',
            width: 200,
            render: (p) => <Today patient={p} />,
          },
        ]}
        rowActions={
          canRecordVisit && isToday
            ? (p) => (
                <Button
                  size="small"
                  icon={<StethoscopeRegular />}
                  onClick={() => setRecording(p)}
                  aria-label={`Record visit to ${p.patientName}`}
                >
                  Record visit
                </Button>
              )
            : undefined
        }
        emptyTitle={isDepartment ? 'No in-patients in this department' : 'No in-patients under your care'}
        emptyDescription={
          isDepartment
            ? 'Patients admitted to the department appear here.'
            : 'Patients admitted under you, and patients you have seen during their stay, appear here.'
        }
      />
      {recording && (
        <RecordVisitDialog
          admissionId={recording.admissionId}
          defaultPractitioner={practitioner}
          description={
            <Text>
              {recording.patientName}, {recording.bedLabel ?? 'no bed'}
              {recording.wardName ? ` in ${recording.wardName}` : ''}. The visit is charged to the in-patient bill.
            </Text>
          }
          onClose={() => setRecording(null)}
          onSaved={() => {
            setRecording(null);
            void queryClient.invalidateQueries({ queryKey: inpatientKeys.roundsAll });
            void queryClient.invalidateQueries({ queryKey: inpatientKeys.admission(recording.admissionId) });
          }}
        />
      )}
    </>
  );
}

/**
 * The round's state for the patient: seen today or not, then what needs the doctor (worrying vital signs, results
 * waiting), then when the last note was written.
 */
function Today({ patient: p }: { patient: WardRoundPatient }) {
  const styles = useStyles();
  const notes = [
    p.lastNoteAt ? `Note ${formatRelative(p.lastNoteAt)}` : 'No note this stay',
    p.vitalsAt ? null : 'no vitals this stay',
  ]
    .filter(Boolean)
    .join(' · ');
  return (
    <span className={styles.stack}>
      {p.seenToday ? (
        <span className={styles.seen}>
          <CheckmarkCircleRegular aria-hidden />
          <Text>Seen at {formatTime(p.lastVisitAt)}</Text>
        </span>
      ) : (
        <span className={styles.notSeen}>
          <ClockRegular aria-hidden />
          <Text>Not seen today</Text>
        </span>
      )}
      {(p.vitalsSeverity || p.resultsToReview > 0) && (
        <span className={styles.chips}>
          {p.vitalsSeverity && (
            <Tooltip
              content={`${p.vitalsFlags.join(' ')} Recorded ${formatRelative(p.vitalsAt)}.`}
              relationship="description"
            >
              <Badge
                size="small"
                appearance="tint"
                color={p.vitalsSeverity === 'Critical' ? 'danger' : 'warning'}
                icon={<HeartPulseWarningRegular />}
              >
                {p.vitalsSeverity === 'Critical' ? 'Vitals critical' : 'Vitals abnormal'}
              </Badge>
            </Tooltip>
          )}
          {p.resultsToReview > 0 && (
            <Tooltip
              content={`${p.resultsToReview} ${p.resultsToReview === 1 ? 'result' : 'results'} of this stay not yet reviewed${p.hasCriticalResults ? ', with a critical finding' : ''}.`}
              relationship="description"
            >
              <Badge size="small" appearance="tint" color={p.hasCriticalResults ? 'danger' : 'informative'}>
                {p.hasCriticalResults
                  ? 'Critical result'
                  : `${p.resultsToReview} ${p.resultsToReview === 1 ? 'result' : 'results'} to review`}
              </Badge>
            </Tooltip>
          )}
        </span>
      )}
      <Caption1 className={styles.muted}>{notes}</Caption1>
    </span>
  );
}
