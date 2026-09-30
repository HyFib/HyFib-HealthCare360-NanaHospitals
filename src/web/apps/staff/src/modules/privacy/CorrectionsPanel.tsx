import {
  Button,
  Caption1,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Text,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkRegular, DismissRegular, EditRegular, OpenRegular } from '@fluentui/react-icons';
import { Section } from '@hc360/ui';
import { useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { ReasonDialog } from '@/modules/hr/shared/ReasonDialog';
import { privacyApi, type CorrectionItemDto, type DataRequestDetail } from './api';
import { DecisionBadge } from './badges';
import { PlainTable } from './PlainTable';
import { correctionKind, correctionProgress, isOpenStatus } from './rules';
import type { RequestCommand } from './useRequestCommand';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
  value: { whiteSpace: 'pre-wrap' },
  actions: { display: 'flex', gap: '6px', flexWrap: 'wrap' },
  links: { display: 'flex', gap: '8px', flexWrap: 'wrap', marginTop: '8px' },
});

const kindHint: Record<ReturnType<typeof correctionKind>, string> = {
  registration: 'Changed in the patient record',
  health: 'The treating doctor adds a correction',
  other: 'Handled as the request describes',
};

/**
 * The changes a correction request asks for, with a decision on each. Deciding records the decision only: registration
 * details are changed in the patient record (with its duplicate checks and audit), and the treating doctor answers a
 * change to the health record by adding a correction rather than rewriting it.
 */
export function CorrectionsPanel({
  request,
  canManage,
  canEditPatient,
  command,
}: {
  request: DataRequestDetail;
  canManage: boolean;
  /** May edit registration details (patients.register). */
  canEditPatient: boolean;
  command: RequestCommand;
}) {
  const styles = useStyles();
  const navigate = useNavigate();
  const [declining, setDeclining] = useState<CorrectionItemDto | null>(null);
  const open = isOpenStatus(request.status);
  const progress = correctionProgress(request.corrections);
  const deciding = canManage && open;
  const hasRegistration = request.corrections.some((c) => correctionKind(c.field) === 'registration');
  const hasHealth = request.corrections.some((c) => correctionKind(c.field) === 'health');

  const decide = async (item: CorrectionItemDto, decision: 'Accepted' | 'Declined', note: string | null) => {
    const result = await command.run(
      () => privacyApi.decideCorrections(request.id, [{ index: item.index, decision, note }]),
      {
        success:
          decision === 'Accepted' ? `${item.fieldLabel}: change accepted` : `${item.fieldLabel}: change declined`,
        failure: 'Could not record the decision',
      },
    );
    if (result.ok) {
      setDeclining(null);
    }
  };

  if (request.corrections.length === 0) {
    return (
      <Section title="Changes asked for">
        <Text>No changes were listed. The request is described above.</Text>
      </Section>
    );
  }

  return (
    <Section
      title="Changes asked for"
      description={
        open
          ? `${progress.total - progress.pending} of ${progress.total} decided. Every change needs a decision before the request can be completed.`
          : `${progress.accepted} accepted, ${progress.declined} declined.`
      }
    >
      <div className={styles.stack}>
        {open && (hasRegistration || hasHealth) && (
          <MessageBar intent="info" layout="multiline">
            <MessageBarBody>
              <MessageBarTitle>Accepting records the decision; it does not change the record</MessageBarTitle>
              {hasRegistration &&
                (canEditPatient
                  ? 'Change registration details in the patient record, where duplicate checks and the audit trail apply. '
                  : 'Registration details are changed in the patient record by staff who can edit it, such as the front desk. ')}
              {hasHealth &&
                'A change to the health record goes to the treating doctor, who adds a correction (an addendum) rather than rewriting it.'}
              {hasRegistration && request.patientId && (
                <div className={styles.links}>
                  {canEditPatient && (
                    <Button
                      size="small"
                      icon={<EditRegular />}
                      onClick={() =>
                        void navigate({
                          to: '/patients/$patientId/edit',
                          params: { patientId: request.patientId ?? '' },
                        })
                      }
                    >
                      Edit registration details
                    </Button>
                  )}
                  <Button
                    size="small"
                    appearance="subtle"
                    icon={<OpenRegular />}
                    onClick={() =>
                      void navigate({ to: '/patients/$patientId', params: { patientId: request.patientId ?? '' } })
                    }
                  >
                    Open the patient record
                  </Button>
                </div>
              )}
            </MessageBarBody>
          </MessageBar>
        )}
        <PlainTable
          ariaLabel="Changes asked for"
          rows={request.corrections}
          getRowId={(c) => String(c.index)}
          columns={[
            {
              key: 'detail',
              header: 'Detail',
              width: '20%',
              render: (c) => (
                <span>
                  <Text weight="semibold">{c.fieldLabel}</Text>
                  <Caption1 block className={styles.muted}>
                    {kindHint[correctionKind(c.field)]}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'current',
              header: 'On record when asked',
              priority: 'low',
              render: (c) =>
                c.currentValue ? (
                  <Text className={styles.value}>{c.currentValue}</Text>
                ) : (
                  <Caption1 className={styles.muted}>Not recorded</Caption1>
                ),
            },
            {
              key: 'requested',
              header: 'Asked for',
              render: (c) => <Text className={styles.value}>{c.requestedValue}</Text>,
            },
            {
              key: 'decision',
              header: 'Decision',
              render: (c) => (
                <span>
                  <DecisionBadge decision={c.decision} />
                  {c.note && (
                    <Caption1 block className={styles.value}>
                      {c.note}
                    </Caption1>
                  )}
                </span>
              ),
            },
            ...(deciding
              ? [
                  {
                    key: 'actions',
                    header: <span className={styles.muted}>Decide</span>,
                    width: 190,
                    render: (c: CorrectionItemDto) => (
                      <span className={styles.actions}>
                        {c.decision !== 'Accepted' && (
                          <Button
                            size="small"
                            icon={<CheckmarkRegular />}
                            disabled={command.busy}
                            onClick={() => void decide(c, 'Accepted', null)}
                            aria-label={`Accept the change to ${c.fieldLabel}`}
                          >
                            Accept
                          </Button>
                        )}
                        {c.decision !== 'Declined' && (
                          <Button
                            size="small"
                            icon={<DismissRegular />}
                            disabled={command.busy}
                            onClick={() => setDeclining(c)}
                            aria-label={`Decline the change to ${c.fieldLabel}`}
                          >
                            Decline
                          </Button>
                        )}
                      </span>
                    ),
                  },
                ]
              : []),
          ]}
        />
      </div>
      <ReasonDialog
        open={declining !== null}
        title={`Decline the change to ${declining?.fieldLabel.toLowerCase() ?? 'this detail'}`}
        description="Say why. The reason is shown to the patient with the decision, in the reply and the patient portal."
        label="Reason"
        maxLength={500}
        confirmLabel="Decline change"
        busy={command.busy}
        onConfirm={(note) => declining && void decide(declining, 'Declined', note)}
        onCancel={() => setDeclining(null)}
      />
    </Section>
  );
}
