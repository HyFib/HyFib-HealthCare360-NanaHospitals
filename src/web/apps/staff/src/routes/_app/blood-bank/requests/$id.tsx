import {
  Button,
  Caption1,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  ProgressBar,
  Text,
} from '@fluentui/react-components';
import { DismissCircleRegular } from '@fluentui/react-icons';
import { formatDateTime, useRealtimeEvent } from '@hc360/core';
import { LoadingState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import {
  bloodApi,
  bloodKeys,
  bloodQueries,
  type BloodIssueDto,
  type BloodRequestDetail,
} from '@/modules/blood-bank/api';
import { RequestStatusBadge, UrgencyBadge } from '@/modules/blood-bank/BloodBadges';
import { componentLabel } from '@/modules/blood-bank/labels';
import { ReportReactionDialog } from '@/modules/blood-bank/ReactionDialogs';
import {
  CompatibilityHint,
  CompatibleUnitsPanel,
  CrossMatchesTable,
  EmergencyReleasePanel,
  GroupingCard,
  IssuePanel,
  IssuesTable,
} from '@/modules/blood-bank/RequestPanels';
import { Detail, DetailGrid, NoAccess, ReasonDialog, RecordLoadError } from '@/modules/ot/shared/feedback';
import { useFacilityId, useNow } from '@/modules/ot/shared/hooks';
import { SpecialtyPatientBanner } from '@/modules/ot/shared/SpecialtyPatientBanner';

export const Route = createFileRoute('/_app/blood-bank/requests/$id')({
  component: BloodRequestPage,
});

const useStyles = makeStyles({
  progress: { display: 'flex', flexDirection: 'column', gap: '4px', maxWidth: '480px' },
  badges: { display: 'inline-flex', gap: '6px', alignItems: 'center' },
});

function BloodRequestPage() {
  const { id } = Route.useParams();
  const queryClient = useQueryClient();
  const facilityId = useFacilityId();
  const canRead = usePermission('bloodbank.read');
  const request = useQuery({ ...bloodQueries.request(id), enabled: canRead });

  useRealtimeEvent(facilityId ? `facility:${facilityId}` : null, 'bloodbank.changed', () => {
    void queryClient.invalidateQueries({ queryKey: bloodKeys.request(id) });
  });

  if (!canRead) {
    return (
      <Page>
        <NoAccess what="blood requests" />
      </Page>
    );
  }
  if (request.isLoading) {
    return <LoadingState label="Loading the request…" />;
  }
  if (request.error || !request.data) {
    return (
      <Page maxWidth={900}>
        <RecordLoadError error={request.error} onRetry={() => void request.refetch()} what="blood request" />
      </Page>
    );
  }
  return <RequestView request={request.data} />;
}

function RequestView({ request: r }: { request: BloodRequestDetail }) {
  const styles = useStyles();
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const now = useNow(30_000);
  const canCreate = usePermission('bloodbank.requests.create');
  const canCrossmatch = usePermission('bloodbank.crossmatch.perform');
  const canIssue = usePermission('bloodbank.units.issue');
  const canReport = usePermission('bloodbank.reactions.report');
  const [cancelling, setCancelling] = useState(false);
  const [reporting, setReporting] = useState<BloodIssueDto | null>(null);
  const open = r.status !== 'Issued' && r.status !== 'Cancelled';

  const cancel = useMutation({
    mutationFn: (reason: string) => bloodApi.cancelRequest(r.id, reason),
    onSuccess: () => {
      setCancelling(false);
      notify.success('Request cancelled', 'Reserved units were released to stock.');
      void queryClient.invalidateQueries({ queryKey: bloodKeys.all });
    },
  });

  return (
    <Page>
      <SpecialtyPatientBanner
        patientId={r.patientId}
        fallback={{ uhid: r.uhid, fullName: r.patientName }}
        hideBloodGroup
        alerts={r.urgency === 'Emergency' && open ? ['Emergency blood request'] : undefined}
      />
      <PageHeader
        title={`${componentLabel[r.componentType]} · ${r.unitsRequested} unit${r.unitsRequested === 1 ? '' : 's'}`}
        subtitle={`Request ${r.requestNumber}${r.requestedByName ? ` by ${r.requestedByName}` : ''}${r.requiredBy ? ` · needed by ${formatDateTime(r.requiredBy)}` : ''}`}
        breadcrumbs={[
          { label: 'Blood stock', onClick: () => void navigate({ to: '/blood-bank' }) },
          { label: 'Requests', onClick: () => void navigate({ to: '/blood-bank/requests' }) },
          { label: r.requestNumber },
        ]}
        actions={
          <>
            <span className={styles.badges}>
              <UrgencyBadge urgency={r.urgency} />
              <RequestStatusBadge status={r.status} />
            </span>
            {canCreate && open && r.unitsIssued === 0 && (
              <Button icon={<DismissCircleRegular />} onClick={() => setCancelling(true)}>
                Cancel request
              </Button>
            )}
          </>
        }
      />

      {r.status === 'Cancelled' && (
        <MessageBar intent="warning">
          <MessageBarBody>
            <MessageBarTitle>Cancelled</MessageBarTitle>
            {r.cancelReason}
          </MessageBarBody>
        </MessageBar>
      )}

      <div className={styles.progress}>
        <Text>
          {r.unitsIssued} of {r.unitsRequested} issued · {r.unitsReserved} reserved (cross-matched)
        </Text>
        <ProgressBar
          value={r.unitsRequested ? r.unitsIssued / r.unitsRequested : 0}
          thickness="large"
          aria-label={`${r.unitsIssued} of ${r.unitsRequested} units issued`}
        />
      </div>

      <EmergencyReleasePanel request={r} canIssue={canIssue} />

      <Section title="Patient’s blood group">
        <GroupingCard request={r} canGroup={canCrossmatch} />
        <div style={{ marginTop: 12 }}>
          <CompatibilityHint request={r} />
        </div>
      </Section>

      <Section title="Request">
        <DetailGrid>
          <Detail label="Indication">{r.indication}</Detail>
          <Detail label="Haemoglobin before">{r.haemoglobinGdl !== null ? `${r.haemoglobinGdl} g/dL` : null}</Detail>
          <Detail label="Platelets before">
            {r.plateletCount !== null ? `${r.plateletCount.toLocaleString('en-IN')} /µL` : null}
          </Detail>
          <Detail label="Encounter">
            {r.admissionId ? 'In-patient (charges go to the IP bill)' : r.visitId ? 'Out-patient visit' : null}
          </Detail>
          <Detail label="Origin">{r.orderItemId ? 'From a doctor’s order' : 'Requested directly'}</Detail>
        </DetailGrid>
      </Section>

      {canCrossmatch && open && (
        <Section
          title="Compatible units"
          description="In date, ABO/Rh compatible, first expiry first. A compatible cross-match reserves the unit for 72 hours."
          flush
        >
          <CompatibleUnitsPanel request={r} now={now} />
        </Section>
      )}

      <Section title={`Cross-matches (${r.crossMatches.length})`} flush>
        <CrossMatchesTable crossMatches={r.crossMatches} now={now} />
      </Section>

      {canIssue && open && (
        <Section
          title="Issue a unit"
          description="Every issue is checked again by the server; the issue slip prints automatically."
        >
          <IssuePanel request={r} now={now} />
        </Section>
      )}

      <Section
        title={`Issued units (${r.issues.length})`}
        description="Return unused units within 30 minutes with the cold chain kept; report any reaction at once."
        flush
      >
        <IssuesTable request={r} canIssue={canIssue} canReport={canReport} onReportReaction={setReporting} />
      </Section>

      {!canCrossmatch && !canIssue && open && (
        <Caption1>
          The blood bank groups the sample, cross-matches and issues the units. This page updates as they work.
        </Caption1>
      )}

      {reporting && (
        <ReportReactionDialog issue={reporting} patientName={r.patientName} onClose={() => setReporting(null)} />
      )}
      <ReasonDialog
        open={cancelling}
        title="Cancel this blood request?"
        description="Reserved units go back to stock for other patients."
        confirmLabel="Cancel request"
        destructive
        busy={cancel.isPending}
        error={cancel.error}
        presets={['No longer needed', 'Patient transferred', 'Requested by mistake', 'Duplicate request']}
        onConfirm={(reason) => cancel.mutate(reason)}
        onCancel={() => {
          setCancelling(false);
          cancel.reset();
        }}
      />
    </Page>
  );
}
