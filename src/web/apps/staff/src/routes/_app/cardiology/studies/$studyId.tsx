import {
  Button,
  Caption1,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AttachRegular, DocumentRegular, MoreHorizontalRegular } from '@fluentui/react-icons';
import { formatDateTime, isApiError } from '@hc360/core';
import { ErrorState, LoadingState, Page, PageHeader, PatientBanner, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import type { ReactNode } from 'react';
import { usePermission } from '@/lib/permissions';
import { PatientClassBadge, PriorityBadge } from '@/modules/lab/badges';
import { bannerFor } from '@/modules/lab/common/banner';
import { useFileViewer } from '@/modules/lab/common/FileViewerDialog';
import { useDiagnosticsLive } from '@/modules/lab/common/live';
import { CARDIOLOGY_PERMISSIONS, cardiologyQueries, type CardiologyStudyDetail } from '@/modules/cardiology/api';
import { CardiologyStatusBadge, StudyTypeBadge } from '@/modules/cardiology/badges';
import { CardiologyReportPanel } from '@/modules/cardiology/CardiologyReportEditor';
import { studyTypeLongLabel } from '@/modules/cardiology/labels';
import { useCardiologyActions } from '@/modules/cardiology/useCardiologyActions';
import { cardiologyActionLabel, cardiologyActions, nextCardiologyAction } from '@/modules/cardiology/workflow';

export const Route = createFileRoute('/_app/cardiology/studies/$studyId')({
  component: CardiologyStudyPage,
});

const LIVE = ['cardiology'] as const;

const useStyles = makeStyles({
  facts: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(210px, 1fr))', gap: '12px 24px' },
  fact: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  label: { color: tokens.colorNeutralForeground3 },
  files: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
});

function Fact({ label, children }: { label: string; children: ReactNode }) {
  const styles = useStyles();
  if (children === null || children === undefined || children === '') {
    return null;
  }
  return (
    <div className={styles.fact}>
      <Caption1 className={styles.label}>{label}</Caption1>
      <Text>{children}</Text>
    </div>
  );
}

/** One cardiology study: order, recording, device output and the structured report. */
function CardiologyStudyPage() {
  const { studyId } = Route.useParams();
  const study = useQuery(cardiologyQueries.study(studyId));
  useDiagnosticsLive(LIVE);

  if (study.isLoading) {
    return <LoadingState label="Opening the study…" />;
  }
  if (study.error || !study.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={study.error} onRetry={() => void study.refetch()} title={isApiError(study.error) && study.error.status === 404 ? 'Study not found' : undefined} />
      </Page>
    );
  }
  return <StudyView study={study.data} />;
}

function StudyView({ study }: { study: CardiologyStudyDetail }) {
  const styles = useStyles();
  const navigate = useNavigate();
  const canManage = usePermission(CARDIOLOGY_PERMISSIONS.manage);
  const canReadFiles = usePermission('files.read');
  const actions = useCardiologyActions();
  const viewer = useFileViewer();
  const next = canManage ? nextCardiologyAction(study.status, study.files.length) : null;
  const others = canManage ? cardiologyActions(study.status).filter((action) => action !== next) : [];
  const ref = {
    id: study.id,
    patientId: study.patientId,
    studyName: study.studyName,
    studyType: study.studyType,
    patientName: study.patientName,
    status: study.status,
    scheduledAt: study.scheduledAt,
    room: study.room,
    deviceName: study.deviceName,
    fileCount: study.files.length,
  };

  return (
    <Page>
      <PatientBanner
        patient={bannerFor(study)}
        actions={
          <>
            {next && (
              <Button appearance="primary" icon={next === 'attach' ? <AttachRegular /> : undefined} onClick={() => actions.run(next, ref)}>
                {cardiologyActionLabel[next]}
              </Button>
            )}
            {others.length > 0 && (
              <Menu>
                <MenuTrigger disableButtonEnhancement>
                  <Button icon={<MoreHorizontalRegular />} aria-label="More study actions" />
                </MenuTrigger>
                <MenuPopover>
                  <MenuList>
                    {others.map((action) => (
                      <MenuItem key={action} onClick={() => actions.run(action, ref)}>
                        {cardiologyActionLabel[action]}
                      </MenuItem>
                    ))}
                  </MenuList>
                </MenuPopover>
              </Menu>
            )}
          </>
        }
      />
      <PageHeader
        title={study.studyName}
        subtitle={`${study.accessionNumber} · ${studyTypeLongLabel[study.studyType]}`}
        breadcrumbs={[{ label: 'Cardiology studies', onClick: () => void navigate({ to: '/cardiology' }) }, { label: study.accessionNumber }]}
      />

      {study.status === 'Cancelled' && (
        <MessageBar intent="warning">
          <MessageBarBody>
            <MessageBarTitle>Cancelled</MessageBarTitle>
            {study.cancelReason}
          </MessageBarBody>
        </MessageBar>
      )}
      {study.chargeError && (
        <MessageBar intent="warning">
          <MessageBarBody>
            <MessageBarTitle>Charge not posted to the bill</MessageBarTitle>
            {study.chargeError}
          </MessageBarBody>
        </MessageBar>
      )}

      <Section title="Study">
        <div className={styles.facts}>
          <Fact label="Status">
            <CardiologyStatusBadge status={study.status} />
          </Fact>
          <Fact label="Test">
            <StudyTypeBadge type={study.studyType} />
          </Fact>
          <Fact label="Priority">
            <PriorityBadge priority={study.priority} showRoutine />
          </Fact>
          <Fact label="Patient type">
            <PatientClassBadge patientClass={study.patientClass} />
          </Fact>
          <Fact label="Ordered">{`${formatDateTime(study.orderedAt)}${study.orderingDoctor ? ` by ${study.orderingDoctor}` : ''}`}</Fact>
          <Fact label="Clinical notes">{study.clinicalNotes}</Fact>
          <Fact label="Appointment">{study.scheduledAt ? `${formatDateTime(study.scheduledAt)}${study.room ? ` · ${study.room}` : ''}` : null}</Fact>
          <Fact label="Recorded">
            {study.startedAt || study.acquiredAt
              ? [study.startedAt ? `started ${formatDateTime(study.startedAt)}` : null, study.acquiredAt ? `completed ${formatDateTime(study.acquiredAt)}` : null].filter(Boolean).join(' · ')
              : null}
          </Fact>
          <Fact label="Technician">{study.technician}</Fact>
          <Fact label="Machine">{study.deviceName}</Fact>
          <Fact label="Technician notes">{study.technicianNotes}</Fact>
        </div>
      </Section>

      <Section
        title="Device output"
        description={study.files.length ? 'Tracings and images from the machine' : 'Nothing attached yet. The report is written from the attached tracing.'}
        actions={
          canManage &&
          !['Verified', 'Released', 'Cancelled'].includes(study.status) && (
            <Button icon={<AttachRegular />} onClick={() => actions.run('attach', ref)}>
              Attach files
            </Button>
          )
        }
      >
        {study.files.length > 0 && (
          <div className={styles.files}>
            {study.files.map((file, index) => (
              <Button
                key={file.id}
                icon={<DocumentRegular />}
                disabled={!canReadFiles || viewer.opening}
                onClick={() => void viewer.open(file.id, file.fileName ?? `${study.studyName} ${index + 1}`)}
              >
                {file.fileName ?? `File ${index + 1}`}
                {file.sizeBytes ? ` (${Math.ceil(file.sizeBytes / 1024)} KB)` : ''}
              </Button>
            ))}
          </div>
        )}
      </Section>

      <CardiologyReportPanel study={study} />
      {actions.dialogs}
      {viewer.dialog}
    </Page>
  );
}
