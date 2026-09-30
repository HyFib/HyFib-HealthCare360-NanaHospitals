import {
  Badge,
  Body1,
  Button,
  Caption1,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Text,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import {
  ContactCardRegular,
  LockClosedRegular,
  NoteAddRegular,
  OpenRegular,
  PersonArrowRightRegular,
  PlayRegular,
  SendRegular,
  ShieldPersonRegular,
} from '@fluentui/react-icons';
import { formatDateTime, formatPhone, isApiError } from '@hc360/core';
import { EmptyState, ErrorState, LoadingState, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { Detail, DetailGrid } from '@/modules/hr/shared/DetailGrid';
import { MainAside, Stack } from '@/modules/hr/shared/layout';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { ReasonDialog } from '@/modules/hr/shared/ReasonDialog';
import { patientQueries } from '@/modules/patients/api';
import { PatientBannerWithPhoto } from '@/modules/patients/PatientBannerWithPhoto';
import { privacyApi, privacyQueries, type DataRequestDetail, type ErasureOutcome } from '@/modules/privacy/api';
import { RequestStatusBadge } from '@/modules/privacy/badges';
import { CorrectionsPanel } from '@/modules/privacy/CorrectionsPanel';
import { DataSummaryPanel } from '@/modules/privacy/DataSummaryPanel';
import { ErasurePanel } from '@/modules/privacy/ErasurePanel';
import {
  identityCheckLabel,
  PRIVACY_PERMISSIONS as P,
  requestChannelLabel,
  requesterRelationLabel,
  requestStatusLabel,
  requestTypeLabel,
} from '@/modules/privacy/labels';
import { AnswerDialog, AssignDialog, VerifyIdentityDialog, type AnswerPrefill } from '@/modules/privacy/RequestDialogs';
import { RequestTimeline } from '@/modules/privacy/RequestTimeline';
import {
  answerHint,
  identityRequired,
  isOpenStatus,
  latestErasureSummary,
  replyByText,
  requestActions,
  requestFacts,
} from '@/modules/privacy/rules';
import { useRequestCommand } from '@/modules/privacy/useRequestCommand';

export const Route = createFileRoute('/_app/privacy/requests/$requestId')({
  component: () => (
    <RequirePermission permission={P.requestsRead} what="privacy requests">
      <RequestPage />
    </RequirePermission>
  ),
});

const useStyles = makeStyles({
  text: { whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', margin: 0 },
  subtitle: { display: 'inline-flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  patient: { display: 'flex', alignItems: 'center', gap: '12px', flexWrap: 'wrap' },
  uhid: { fontFamily: tokens.fontFamilyMonospace },
  grow: { flex: 1, minWidth: '200px', display: 'flex', flexDirection: 'column', gap: '2px' },
});

type DialogKind = 'verify' | 'assign' | 'note' | 'answer';

function RequestPage() {
  const { requestId } = Route.useParams();
  const navigate = useNavigate();
  const request = useQuery(privacyQueries.request(requestId));
  const crumbs = [{ label: 'Privacy requests', onClick: () => void navigate({ to: '/privacy/requests' }) }];

  if (!request.data) {
    const error = request.error;
    return (
      <Page maxWidth={900}>
        <PageHeader title="Privacy request" icon={<ShieldPersonRegular />} breadcrumbs={crumbs} />
        {!error ? (
          <LoadingState />
        ) : isApiError(error) && error.code === 'patient.restricted' ? (
          <Section>
            <EmptyState
              icon={<LockClosedRegular />}
              title="This request is about a restricted record"
              description="Only staff allowed to open restricted (VIP) records can open it. Ask the hospital administrator if you need to handle it."
              action={<Button onClick={() => void navigate({ to: '/privacy/requests' })}>Back to the requests</Button>}
            />
          </Section>
        ) : isApiError(error) && error.status === 404 ? (
          <Section>
            <EmptyState
              title="This request was not found"
              description="It may be for another facility, or the link is out of date."
              action={<Button onClick={() => void navigate({ to: '/privacy/requests' })}>Back to the requests</Button>}
            />
          </Section>
        ) : (
          <ErrorState error={error} onRetry={() => void request.refetch()} />
        )}
      </Page>
    );
  }
  return <RequestView request={request.data} fetchedAt={request.dataUpdatedAt} crumbs={crumbs} />;
}

function RequestView({
  request: r,
  fetchedAt,
  crumbs,
}: {
  request: DataRequestDetail;
  /** When the details were loaded; the deadline words are worked out against it. */
  fetchedAt: number;
  crumbs: { label: string; onClick?: () => void }[];
}) {
  const styles = useStyles();
  const navigate = useNavigate();
  const canManage = usePermission(P.requestsManage);
  const canReadPatients = usePermission('patients.read');
  const canEditPatients = usePermission('patients.register');
  const command = useRequestCommand(r.id);
  const [dialog, setDialog] = useState<DialogKind | null>(null);
  const [prefill, setPrefill] = useState<AnswerPrefill | null>(null);
  const [erased, setErased] = useState<ErasureOutcome | null>(null);
  const patient = useQuery({
    ...patientQueries.detail(r.patientId ?? ''),
    enabled: Boolean(r.patientId) && canReadPatients,
    retry: false,
    staleTime: 5 * 60_000,
  });

  const facts = requestFacts(r);
  const actions = requestActions(facts, canManage);
  const open = isOpenStatus(r.status);
  const hint = answerHint(facts);
  const erasureSummary = erased?.summary ?? latestErasureSummary(r.events);

  const close = () => {
    setDialog(null);
    setPrefill(null);
  };
  const start = () =>
    void command.run(() => privacyApi.start(r.id), {
      success: 'Request started',
      successBody: r.assignedToUserId ? undefined : 'You are its owner now.',
      failure: 'Could not start the request',
    });
  const addNote = async (text: string) => {
    const result = await command.run(() => privacyApi.addNote(r.id, text), {
      success: 'Note added',
      failure: 'Could not add the note',
    });
    if (result.ok) {
      close();
    }
  };
  const answerAfterErasure = () => {
    if (erased) {
      setPrefill({
        outcome: erased.keepPermanently || erased.keepUntil ? 'PartlyCompleted' : 'Completed',
        reply: erased.summary,
      });
    }
    setDialog('answer');
  };

  const answerButton = (
    <Button
      appearance={actions.start ? 'secondary' : 'primary'}
      icon={<SendRegular />}
      disabled={command.busy}
      onClick={() => setDialog('answer')}
    >
      Answer
    </Button>
  );

  return (
    <Page>
      <PageHeader
        title={r.requestNumber}
        subtitle={
          <span className={styles.subtitle}>
            <RequestStatusBadge status={r.status} isOverdue={r.isOverdue} />
            <span>
              {requestTypeLabel[r.type]} request · {requestChannelLabel[r.channel].toLowerCase()} ·{' '}
              {formatDateTime(r.receivedAt)}
            </span>
          </span>
        }
        icon={<ShieldPersonRegular />}
        breadcrumbs={[...crumbs, { label: r.requestNumber }]}
        actions={
          <>
            {actions.start && (
              <Button appearance="primary" icon={<PlayRegular />} disabled={command.busy} onClick={start}>
                Start
              </Button>
            )}
            {actions.verify && (
              <Button icon={<ContactCardRegular />} disabled={command.busy} onClick={() => setDialog('verify')}>
                Confirm identity
              </Button>
            )}
            {actions.assign && (
              <Button icon={<PersonArrowRightRegular />} disabled={command.busy} onClick={() => setDialog('assign')}>
                {r.assignedToUserId ? 'Reassign' : 'Assign'}
              </Button>
            )}
            {actions.note && (
              <Button icon={<NoteAddRegular />} disabled={command.busy} onClick={() => setDialog('note')}>
                Add note
              </Button>
            )}
            {actions.answer &&
              (hint ? (
                <Tooltip content={hint} relationship="description">
                  {answerButton}
                </Tooltip>
              ) : (
                answerButton
              ))}
          </>
        }
      />

      <ReplyByBanner request={r} fetchedAt={fetchedAt} />

      {r.patientId ? (
        patient.data ? (
          <PatientBannerWithPhoto
            photoFileId={patient.data.photoFileId}
            patient={{
              id: patient.data.id,
              uhid: patient.data.uhid,
              fullName: patient.data.fullName,
              gender: patient.data.gender,
              age: patient.data.age,
              phone: patient.data.phone,
              isVip: patient.data.isVip,
              isDeceased: patient.data.isDeceased,
            }}
            actions={
              <Button
                icon={<OpenRegular />}
                onClick={() => void navigate({ to: '/patients/$patientId', params: { patientId: r.patientId ?? '' } })}
              >
                Open patient record
              </Button>
            }
          />
        ) : (
          <Section>
            <div className={styles.patient}>
              <div className={styles.grow}>
                <Text weight="semibold" size={400}>
                  {r.principalName}
                </Text>
                <Caption1 className={styles.muted}>
                  Registered patient{r.uhid ? ' · UHID ' : ''}
                  {r.uhid && <span className={styles.uhid}>{r.uhid}</span>}
                </Caption1>
              </div>
              {canReadPatients && (
                <Button
                  icon={<OpenRegular />}
                  onClick={() =>
                    void navigate({ to: '/patients/$patientId', params: { patientId: r.patientId ?? '' } })
                  }
                >
                  Open patient record
                </Button>
              )}
            </div>
          </Section>
        )
      ) : (
        <Section>
          <div className={styles.patient}>
            <div className={styles.grow}>
              <Text weight="semibold" size={400}>
                {r.principalName}
              </Text>
              <Caption1 className={styles.muted}>Not a registered patient</Caption1>
            </div>
          </div>
        </Section>
      )}

      <MainAside>
        <Stack>
          <Section title={r.type === 'Grievance' ? 'The grievance' : 'What they asked'}>
            {r.details ? (
              <Body1 as="p" className={styles.text}>
                {r.details}
              </Body1>
            ) : (
              <Text className={styles.muted}>
                {r.type === 'Correction' ? 'The changes are listed below.' : 'No description was given.'}
              </Text>
            )}
          </Section>

          {r.type === 'Correction' && (
            <CorrectionsPanel request={r} canManage={canManage} canEditPatient={canEditPatients} command={command} />
          )}
          {r.type === 'Access' && <DataSummaryPanel request={r} />}
          {r.type === 'Erasure' && (
            <ErasurePanel
              request={r}
              canManage={canManage}
              command={command}
              outcome={erased}
              onErased={setErased}
              onAnswer={answerAfterErasure}
            />
          )}

          {r.response && (
            <Section title="The answer">
              <DetailGrid minColumnWidth={180}>
                <Detail label="Outcome">{requestStatusLabel[r.status]}</Detail>
                <Detail label="Answered">{r.closedAt ? formatDateTime(r.closedAt) : null}</Detail>
                <Detail label="By">{r.closedBy}</Detail>
                <Detail label="Reply sent to the requester" wide>
                  <span className={styles.text}>{r.response}</span>
                </Detail>
              </DetailGrid>
            </Section>
          )}

          <RequestTimeline events={r.events} />
        </Stack>

        <Stack>
          <Section title="Who is asking">
            <DetailGrid minColumnWidth={160}>
              <Detail label="Asked by" wide>
                {requesterRelationLabel[r.requestedBy]}
                {r.requesterName ? `: ${r.requesterName}` : ''}
              </Detail>
              <Detail label="How it reached us">{requestChannelLabel[r.channel]}</Detail>
              <Detail label="Received">{formatDateTime(r.receivedAt)}</Detail>
              <Detail label="Reply to" wide>
                {r.contactPhone || r.contactEmail
                  ? [r.contactPhone ? formatPhone(r.contactPhone) : null, r.contactEmail].filter(Boolean).join(' · ')
                  : 'No phone number or e-mail address: the reply cannot be sent by SMS or e-mail.'}
              </Detail>
            </DetailGrid>
            {r.requestedBy === 'Nominee' && r.patientId && <NomineeOnRecord patientId={r.patientId} />}
          </Section>

          <Section title="Identity">
            {r.identityVerifiedAt ? (
              <DetailGrid minColumnWidth={160}>
                <Detail label="Checked" wide>
                  {r.identityCheck ? identityCheckLabel[r.identityCheck] : 'Checked'}
                </Detail>
                {r.identityNote && (
                  <Detail label="Note" wide>
                    {r.identityNote}
                  </Detail>
                )}
                <Detail label="When">{formatDateTime(r.identityVerifiedAt)}</Detail>
                <Detail label="By">{r.identityVerifiedBy}</Detail>
              </DetailGrid>
            ) : identityRequired(r.type) ? (
              open ? (
                <MessageBar intent="warning" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>Not checked yet</MessageBarTitle>
                    Check who is asking, and that they may act for the patient, before giving out, changing or erasing
                    any data.
                  </MessageBarBody>
                </MessageBar>
              ) : (
                <Text className={styles.muted}>The identity was not checked.</Text>
              )
            ) : (
              <Text className={styles.muted}>Not needed to answer a grievance.</Text>
            )}
          </Section>

          <Section title="Handling">
            <DetailGrid minColumnWidth={160}>
              <Detail label="Owner">{r.assignedToName ?? 'Not assigned'}</Detail>
              <Detail label="Status">{requestStatusLabel[r.status]}</Detail>
            </DetailGrid>
            {!canManage && (
              <Caption1 block className={styles.muted} style={{ marginTop: 8 }}>
                You can view this request. Handling it needs the grievance officer’s access.
              </Caption1>
            )}
          </Section>
        </Stack>
      </MainAside>

      <VerifyIdentityDialog open={dialog === 'verify'} request={r} command={command} onClose={close} />
      <AssignDialog open={dialog === 'assign'} request={r} command={command} onClose={close} />
      <AnswerDialog
        open={dialog === 'answer'}
        request={r}
        prefill={prefill}
        erasureSummary={erasureSummary}
        command={command}
        onClose={close}
      />
      <ReasonDialog
        open={dialog === 'note'}
        title="Add a staff note"
        description="Staff notes are kept with the request and never shown to the patient."
        label="Note"
        maxLength={2000}
        confirmLabel="Add note"
        busy={command.busy}
        onConfirm={(text) => void addNote(text)}
        onCancel={close}
      />
    </Page>
  );
}

function ReplyByBanner({ request: r, fetchedAt }: { request: DataRequestDetail; fetchedAt: number }) {
  if (r.status === 'Withdrawn') {
    return (
      <MessageBar intent="info">
        <MessageBarBody>
          <MessageBarTitle>Withdrawn</MessageBarTitle>
          The requester withdrew the request{r.closedAt ? ` on ${formatDateTime(r.closedAt)}` : ''}.
        </MessageBarBody>
      </MessageBar>
    );
  }
  if (!isOpenStatus(r.status)) {
    return (
      <MessageBar intent={r.status === 'Declined' ? 'info' : 'success'}>
        <MessageBarBody>
          <MessageBarTitle>{requestStatusLabel[r.status]}</MessageBarTitle>
          Answered{r.closedAt ? ` on ${formatDateTime(r.closedAt)}` : ''}
          {r.closedBy ? ` by ${r.closedBy}` : ''}. The reply was due by {formatDateTime(r.dueAt)}.
        </MessageBarBody>
      </MessageBar>
    );
  }
  const due = replyByText(r.dueAt, fetchedAt);
  return (
    <MessageBar intent={due.overdue ? 'error' : due.soon ? 'warning' : 'info'} layout="multiline">
      <MessageBarBody>
        <MessageBarTitle>{due.text}</MessageBarTitle>
        {due.overdue
          ? `The reply was due on ${formatDateTime(r.dueAt)}. Answer it as soon as possible.`
          : `The reply is due by ${formatDateTime(r.dueAt)}.`}
      </MessageBarBody>
    </MessageBar>
  );
}

/** For a request made by the nominee: who the patient named, to check against. */
function NomineeOnRecord({ patientId }: { patientId: string }) {
  const styles = useStyles();
  const nominee = useQuery(privacyQueries.nominee(patientId));
  if (nominee.isLoading) {
    return null;
  }
  if (nominee.error) {
    return (
      <Caption1 block className={styles.muted} style={{ marginTop: 8 }}>
        The nominee on record could not be loaded.
      </Caption1>
    );
  }
  const n = nominee.data?.nominee;
  return n ? (
    <div style={{ marginTop: 12 }}>
      <Caption1 className={styles.muted}>Nominee on record</Caption1>
      <Text block>
        {n.name} ({n.relationship}){n.phone ? ` · ${formatPhone(n.phone)}` : ''}
        {n.email ? ` · ${n.email}` : ''}
      </Text>
      <Badge appearance="tint" color="informative">
        Check the requester is this person
      </Badge>
    </div>
  ) : (
    <MessageBar intent="warning" style={{ marginTop: 12 }}>
      <MessageBarBody>No nominee is recorded for this patient. Check their authority another way.</MessageBarBody>
    </MessageBar>
  );
}
