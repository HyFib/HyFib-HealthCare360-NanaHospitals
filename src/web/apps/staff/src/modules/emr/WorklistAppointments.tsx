import { Badge, Button, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import { DocumentTextRegular, PlayRegular, VideoRegular } from '@fluentui/react-icons';
import { formatTime } from '@hc360/core';
import { DataTable, StatusBadge } from '@hc360/ui';
import type { WorklistAppointment } from './api';
import { appointmentAction, appointmentStatusLabel, isLate } from './worklist';
import { WorklistPatient } from './WorklistParts';

const useStyles = makeStyles({
  time: { display: 'flex', flexDirection: 'column', alignItems: 'flex-start', gap: '2px' },
  status: { display: 'flex', flexDirection: 'column', alignItems: 'flex-start', gap: '2px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/**
 * The doctor's appointments for the day (EMR-01), in slot order: who is still to come (late ones marked), who has
 * arrived with which token, and who has been seen. Arrived patients can be consulted from here as from the queue.
 */
export function WorklistAppointments({
  appointments,
  loading,
  isToday,
  showDoctor,
  canWrite,
  busy,
  now,
  onOpenChart,
  onConsult,
  onOpenVideo,
}: {
  appointments: WorklistAppointment[] | undefined;
  loading: boolean;
  isToday: boolean;
  /** The department's appointments: say whose each one is. */
  showDoctor: boolean;
  canWrite: boolean;
  busy: boolean;
  now: Date;
  onOpenChart: (patientId: string) => void;
  onConsult: (appointment: WorklistAppointment) => void;
  onOpenVideo: () => void;
}) {
  const styles = useStyles();
  return (
    <DataTable
      ariaLabel="Appointments"
      rows={appointments}
      loading={loading}
      getRowId={(a) => a.id}
      onRowClick={(a) => onOpenChart(a.patientId)}
      size="small"
      columns={[
        {
          key: 'time',
          header: 'Time',
          width: 96,
          render: (a) => (
            <span className={styles.time}>
              <Text weight="semibold">{formatTime(a.startsAt)}</Text>
              {a.isTelehealth && (
                <Badge size="small" appearance="tint" color="brand" icon={<VideoRegular />}>
                  Video
                </Badge>
              )}
              {isToday && isLate(a, now) && (
                <Badge size="small" appearance="tint" color="warning">
                  Late
                </Badge>
              )}
            </span>
          ),
        },
        {
          key: 'patient',
          header: 'Patient',
          render: (a) => (
            <WorklistPatient
              name={a.patientName}
              age={a.age}
              gender={a.gender}
              uhid={a.uhid}
              hasActiveAllergies={a.hasActiveAllergies}
              hasCriticalAlerts={a.hasCriticalAlerts}
              isRestricted={a.isRestricted}
            >
              {a.visitType === 'FollowUp' && (
                <Badge size="small" appearance="outline">
                  Follow-up
                </Badge>
              )}
            </WorklistPatient>
          ),
        },
        { key: 'reason', header: 'Reason', priority: 'low', render: (a) => a.reason ?? '—' },
        ...(showDoctor
          ? [
              {
                key: 'doctor',
                header: 'Doctor',
                priority: 'low' as const,
                render: (a: WorklistAppointment) => a.practitionerName ?? '—',
              },
            ]
          : []),
        {
          key: 'status',
          header: 'Status',
          width: 150,
          render: (a) => (
            <span className={styles.status}>
              <StatusBadge
                status={a.status}
                label={
                  a.tokenLabel && (a.status === 'CheckedIn' || a.status === 'InConsultation')
                    ? `${appointmentStatusLabel[a.status]} · ${a.tokenLabel}`
                    : appointmentStatusLabel[a.status]
                }
              />
              {a.encounterStatus && (
                <Caption1 className={styles.muted}>
                  {a.encounterStatus === 'InProgress' ? 'Note open' : 'Note signed'}
                </Caption1>
              )}
            </span>
          ),
        },
      ]}
      rowActions={(a) => {
        switch (appointmentAction(a, { canWrite, isToday })) {
          case 'consult':
            return (
              <Button
                size="small"
                appearance="primary"
                icon={<PlayRegular />}
                disabled={busy}
                onClick={() => onConsult(a)}
                aria-label={`${a.encounterId ? 'Continue' : 'Start'} the consultation of ${a.patientName}`}
              >
                {a.encounterId ? 'Continue' : 'Start'}
              </Button>
            );
          case 'note':
            return (
              <Button
                size="small"
                icon={<DocumentTextRegular />}
                onClick={() => onConsult(a)}
                aria-label={`Open the note of ${a.patientName}`}
              >
                Open note
              </Button>
            );
          case 'video':
            return (
              <Button
                size="small"
                icon={<VideoRegular />}
                onClick={onOpenVideo}
                aria-label={`Open video consultations for ${a.patientName}`}
              >
                Video
              </Button>
            );
          default:
            return null;
        }
      }}
      emptyTitle={isToday ? 'No appointments today' : 'No appointments on this day'}
      emptyDescription="Booked consultations appear here in slot order. Walk-in patients are in the queue."
    />
  );
}
