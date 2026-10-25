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
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import { MoreHorizontalRegular, OpenRegular } from '@fluentui/react-icons';
import { formatDateTime, isApiError } from '@hc360/core';
import { ErrorState, LoadingState, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import type { ReactNode } from 'react';
import { usePermission } from '@/lib/permissions';
import { PatientBannerWithFlags } from '@/modules/emr/PatientBannerWithFlags';
import { PatientClassBadge, PriorityBadge } from '@/modules/lab/badges';
import { bannerFor } from '@/modules/lab/common/banner';
import { useDiagnosticsLive } from '@/modules/lab/common/live';
import { RADIOLOGY_PERMISSIONS, radiologyQueries, type ImagingStudyDetail } from '@/modules/radiology/api';
import { ModalityBadge, StudyStatusBadge } from '@/modules/radiology/badges';
import { modalityLabel, repeatReasonLabel } from '@/modules/radiology/labels';
import { FormFPanel } from '@/modules/radiology/FormFPanel';
import { RadiologyReportPanel } from '@/modules/radiology/ReportEditor';
import { StudyImages } from '@/modules/radiology/StudyImages';
import { useStudyActions } from '@/modules/radiology/useStudyActions';
import { nextStudyAction, studyActionLabel, studyActions } from '@/modules/radiology/workflow';

export const Route = createFileRoute('/_app/radiology/studies/$studyId')({
  component: ImagingStudyPage,
});

const LIVE = ['radiology'] as const;

const useStyles = makeStyles({
  facts: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(210px, 1fr))', gap: '12px 24px' },
  fact: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  label: { color: tokens.colorNeutralForeground3 },
  mono: { fontFamily: tokens.fontFamilyMonospace, wordBreak: 'break-all' },
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

/** One imaging study: patient, order, scan milestones and technologist actions, then the report and AI findings. */
function ImagingStudyPage() {
  const { studyId } = Route.useParams();
  const study = useQuery(radiologyQueries.study(studyId));
  useDiagnosticsLive(LIVE);

  if (study.isLoading) {
    return <LoadingState label="Opening the study…" />;
  }
  if (study.error || !study.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState
          error={study.error}
          onRetry={() => void study.refetch()}
          title={isApiError(study.error) && study.error.status === 404 ? 'Study not found' : undefined}
        />
      </Page>
    );
  }
  return <StudyView study={study.data} />;
}

function StudyView({ study }: { study: ImagingStudyDetail }) {
  const styles = useStyles();
  // Closing a dialog opened from the study's buttons returns focus to the button that opened it.
  const restoreFocusTarget = useRestoreFocusTarget();
  const navigate = useNavigate();
  const canWork = usePermission(RADIOLOGY_PERMISSIONS.worklist);
  const canSeeImages = usePermission(RADIOLOGY_PERMISSIONS.images);
  const actions = useStudyActions();
  const next = canWork ? nextStudyAction(study.status) : null;
  const others = canWork ? studyActions(study.status).filter((action) => action !== next && action !== 'report') : [];
  const ref = {
    id: study.id,
    studyName: study.studyName,
    patientName: study.patientName,
    status: study.status,
    scheduledAt: study.scheduledAt,
    room: study.room,
    stationAeTitle: study.stationAeTitle,
  };
  const milestones = [
    study.scheduledAt ? `Appointment ${formatDateTime(study.scheduledAt)}` : null,
    study.arrivedAt ? `arrived ${formatDateTime(study.arrivedAt)}` : null,
    study.startedAt ? `started ${formatDateTime(study.startedAt)}` : null,
    study.acquiredAt ? `scanned ${formatDateTime(study.acquiredAt)}` : null,
  ]
    .filter(Boolean)
    .join(' · ');

  return (
    <Page>
      <PatientBannerWithFlags
        patientId={study.patientId}
        patient={bannerFor(study)}
        actions={
          <>
            {next && (
              <Button
                appearance="primary"
                disabled={actions.busy}
                // Stays focusable while the step runs, so focus is not dropped to the page.
                disabledFocusable={actions.busy}
                onClick={() => actions.run(next, ref)}
                {...restoreFocusTarget}
              >
                {studyActionLabel[next]}
              </Button>
            )}
            {canSeeImages && (
              <Button icon={<OpenRegular />} onClick={() => void actions.openImages(study.id)}>
                Open in PACS viewer
              </Button>
            )}
            {others.length > 0 && (
              <Menu>
                <MenuTrigger disableButtonEnhancement>
                  <Button icon={<MoreHorizontalRegular />} aria-label="More study actions" {...restoreFocusTarget} />
                </MenuTrigger>
                <MenuPopover>
                  <MenuList>
                    {others.map((action) => (
                      <MenuItem key={action} onClick={() => actions.run(action, ref)}>
                        {studyActionLabel[action]}
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
        subtitle={`${study.accessionNumber} · ${modalityLabel[study.modality]}${study.bodyPart ? ` · ${study.bodyPart}` : ''}`}
        breadcrumbs={[
          { label: 'Imaging worklist', onClick: () => void navigate({ to: '/radiology' }) },
          { label: study.accessionNumber },
        ]}
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
      {!study.accessionFitsDicom && study.status !== 'Cancelled' && (
        <MessageBar intent="info" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>The accession number is too long for DICOM</MessageBarTitle>
            {study.accessionNumber} has {study.accessionNumber.length} characters; DICOM allows 16. The modality
            worklist sends this study without it, with {study.worklistProcedureId} as its procedure id, and the images
            link by the study instance UID. In PACS, look the study up by the patient&apos;s UHID or name.
          </MessageBarBody>
        </MessageBar>
      )}

      <Section title="Study" description={milestones || undefined}>
        <div className={styles.facts}>
          <Fact label="Status">
            <StudyStatusBadge status={study.status} />
          </Fact>
          <Fact label="Priority">
            <PriorityBadge priority={study.priority} showRoutine />
          </Fact>
          <Fact label="Patient type">
            <PatientClassBadge patientClass={study.patientClass} />
          </Fact>
          <Fact label="Modality">
            <ModalityBadge modality={study.modality} />
          </Fact>
          <Fact label="Ordered">{`${formatDateTime(study.orderedAt)}${study.orderingDoctor ? ` by ${study.orderingDoctor}` : ''}`}</Fact>
          <Fact label="Clinical notes">{study.clinicalNotes}</Fact>
          <Fact label="Preparation">{study.patientPreparation}</Fact>
          <Fact label="Room and station">{[study.room, study.stationAeTitle].filter(Boolean).join(' · ')}</Fact>
          <Fact label="Technologist">{study.technician}</Fact>
          <Fact label="Contrast">
            {study.contrastUsed
              ? `${study.contrastAgent ?? 'Yes'}${study.contrastVolumeMl ? `, ${study.contrastVolumeMl} mL` : ''}`
              : study.acquiredAt
                ? 'None'
                : null}
          </Fact>
          <Fact label="Radiation dose">{study.doseNote}</Fact>
          <Fact label="Technologist notes">{study.technicianNotes}</Fact>
          <Fact label="Images in PACS">
            {study.pacsLinkedAt
              ? `${study.seriesCount ?? '?'} series, ${study.instanceCount ?? '?'} images (linked ${formatDateTime(study.pacsLinkedAt)})`
              : 'Not linked yet'}
          </Fact>
          <Fact label="Repeats">
            {study.repeats.length > 0
              ? study.repeats
                  .map((repeat) => `${repeatReasonLabel[repeat.reason]}${repeat.note ? ` (${repeat.note})` : ''}`)
                  .join('; ')
              : null}
          </Fact>
          <Fact label="Study instance UID">
            <span className={styles.mono}>{study.studyInstanceUid}</span>
          </Fact>
        </div>
      </Section>

      {canSeeImages && (
        <StudyImages
          study={study}
          canLink={canWork}
          linking={actions.busy}
          onLink={() => actions.run('reconcile', ref)}
          onOpenPacsViewer={() => void actions.openImages(study.id)}
        />
      )}

      <FormFPanel study={study} />
      <RadiologyReportPanel study={study} />
      {actions.dialogs}
    </Page>
  );
}
