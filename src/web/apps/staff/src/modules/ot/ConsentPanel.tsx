import {
  Button,
  Caption1,
  Field,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowClockwiseRegular,
  CheckmarkCircleRegular,
  DismissCircleRegular,
  ShieldErrorRegular,
  SubtractCircleRegular,
} from '@fluentui/react-icons';
import { formatDateTime, useSession } from '@hc360/core';
import { useLeaveGuard, useNotify } from '@hc360/ui';
import { useIsFetching, useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import type { WitnessConfirmed } from '@/modules/witness/api';
import { WitnessPanel } from '@/modules/witness/WitnessPanel';
import {
  givenBySurgeon,
  otApi,
  otKeys,
  type SurgeryDetail,
  type SurgeryStatus,
  type SurgicalConsentCheck,
} from './api';
import { anaesthesiaShort, consentKindLabel } from './labels';
import { consentGate, consentRecordedBy, saidNoConsent, saidNoNotice, waiverGate } from './logic';
import { ActionError } from './shared/feedback';

/** The shortest clinical reason for operating without consent (server `SurgicalConsent.MinimumReasonLength`). */
const WAIVER_REASON_MIN = 10;

/** Before the incision: where recording the consent, or waiving it in an emergency, still matters. */
const BEFORE_INCISION: readonly SurgeryStatus[] = ['Requested', 'Scheduled', 'InPreOp', 'Postponed'];

const useStyles = makeStyles({
  panel: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
    padding: '12px 14px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  list: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: '6px' },
  line: { display: 'grid', gridTemplateColumns: '24px minmax(0, 1fr)', gap: '8px', alignItems: 'start' },
  lineText: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  icon: { fontSize: '20px', display: 'flex', paddingTop: '1px' },
  ok: { color: tokens.colorPaletteGreenForeground1 },
  missing: { color: tokens.colorPaletteRedForeground1 },
  muted: { color: tokens.colorNeutralForeground3 },
  form: { display: 'flex', flexDirection: 'column', gap: '10px' },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'center' },
});

/**
 * "recorded 21 Nov 2026, 10:15 am", "withdrawn …", "not recorded": where one consent stands, with its time. A consent
 * recorded before the booking (`since`) and withdrawn since is said as such: the withdrawal stops this operation too.
 */
function consentLineText(
  check: SurgicalConsentCheck,
  booking: Pick<SurgeryDetail, 'anaesthesiaType'>,
  since: string,
): string {
  const at = (iso: string | null | undefined) => (iso ? ` ${formatDateTime(iso)}` : '');
  switch (check.state) {
    case 'Recorded':
      return `recorded${at(check.consent?.capturedAt)}`;
    case 'BeforeBooking':
      return `recorded${at(check.consent?.capturedAt)}, before this operation was booked: take it again for this operation`;
    case 'Withdrawn':
      return check.consent && new Date(check.consent.capturedAt) < new Date(since)
        ? `withdrawn${at(check.consent.revokedAt)} (recorded${at(check.consent.capturedAt)}, before this operation was booked)`
        : `withdrawn${at(check.consent?.revokedAt)}`;
    case 'Refused':
      return `refused by the patient${at(check.consent?.capturedAt)}`;
    case 'NotNeeded':
      return `not needed: ${anaesthesiaShort[booking.anaesthesiaType].toLowerCase()} anaesthesia is given by the surgeon`;
    case 'OtherOperation':
      return `recorded${at(check.consent?.capturedAt)}, but relied on by another operation${
        check.consent?.reliedOnBy ? ` (${check.consent.reliedOnBy})` : ''
      }: take it again for this operation`;
    default:
      return 'not recorded';
  }
}

/**
 * The patient's consent to the operation as the theatre's gates read it (server `SurgicalConsent`): each consent needed
 * and where it stands, read from the consents the front office records, never from a tick. While one is missing, "consent
 * signed", the WHO Sign-in and Time-out and the incision wait: the consent is recorded at the front office, or, for an
 * emergency operation that cannot wait, the operating surgeon goes ahead without it, with a second doctor who agrees at
 * this screen with their own user name and password (audited as critical).
 */
export function SurgicalConsentPanel({ booking }: { booking: SurgeryDetail }) {
  const styles = useStyles();
  const practitionerId = useSession((s) => s.user?.practitionerId ?? null);
  const canPerform = usePermission('ot.surgery.perform');
  const [waiving, setWaiving] = useState(false);
  const queryClient = useQueryClient();
  // The front office records the consent on its own screen: the booking is read again on asking, not only on the next visit.
  const checking = useIsFetching({ queryKey: otKeys.booking(booking.id), exact: true }) > 0;
  const consent = booking.consent;
  // Once the incision is made the gates are past: the booking's details say what it relied on (`consentSummary`).
  if (!consent || booking.startedAt !== null) {
    return null;
  }

  const beforeIncision = BEFORE_INCISION.includes(booking.status);
  const blocked = !consentGate(booking).allowed;
  const mayWaive = canPerform && waiverGate(booking, practitionerId).allowed;
  const waiver = consent.waiver;
  // The patient's latest word on a consent needed is "no", a refusal or a withdrawal: no emergency waiver overrides it
  // (server `surgery.consent_refused`, `surgery.consent_withdrawn`).
  const no = saidNoConsent(consent);
  // A waiver form already open stays, to say why it cannot be sent, when the front office records a "no" meanwhile.
  const formOpen = waiving && canPerform && (mayWaive || no !== null);

  return (
    <section className={styles.panel} aria-label="Consent to the operation">
      <Text weight="semibold">Consent to the operation</Text>
      <ul className={styles.list}>
        {consent.checks.map((check) => {
          const ok = check.state === 'Recorded' || check.state === 'NotNeeded';
          return (
            <li key={check.kind} className={styles.line}>
              <span
                className={mergeClasses(
                  styles.icon,
                  check.state === 'Recorded' ? styles.ok : ok ? styles.muted : styles.missing,
                )}
              >
                {check.state === 'Recorded' ? (
                  <CheckmarkCircleRegular aria-hidden />
                ) : ok ? (
                  <SubtractCircleRegular aria-hidden />
                ) : (
                  <DismissCircleRegular aria-hidden />
                )}
              </span>
              <span className={styles.lineText}>
                <Text>
                  {consentKindLabel[check.kind]}: {consentLineText(check, booking, consent.since)}
                </Text>
                {check.consent && <Caption1 className={styles.muted}>{consentRecordedBy(check.consent)}</Caption1>}
                {check.kind === 'Anaesthesia' &&
                  check.state !== 'NotNeeded' &&
                  consent.anaesthesiaConsentRequiredAtSignIn &&
                  givenBySurgeon(booking.anaesthesiaType) && (
                    <Caption1 className={styles.muted}>
                      Still needed: the WHO Sign-in was done under anaesthesia given by the anaesthetist.
                    </Caption1>
                  )}
              </span>
            </li>
          );
        })}
      </ul>

      {waiver && (
        <MessageBar intent="warning" layout="multiline" icon={<ShieldErrorRegular />}>
          <MessageBarBody>
            <MessageBarTitle>Going ahead without consent (emergency)</MessageBarTitle>
            Waived by the operating surgeon {waiver.surgeonName} on {formatDateTime(waiver.at)}, {waiver.concurringName}{' '}
            agreeing at the surgeon&apos;s screen. Reason: {waiver.reason}
            {!consent.satisfied && !consent.recorded
              ? no
                ? no.state === 'Refused'
                  ? ' The patient has refused since: an emergency does not override a refusal, so the waiver no longer stands.'
                  : ' The patient has withdrawn consent since: an emergency does not override a withdrawal, so the waiver no longer stands.'
                : ' The booking is no longer an emergency: record the patient’s consent before going on.'
              : ''}
          </MessageBarBody>
        </MessageBar>
      )}

      {blocked && beforeIncision && (
        <MessageBar intent="error" layout="multiline">
          <MessageBarBody>
            {no ? (
              <>
                <MessageBarTitle>{saidNoTitle(no)}</MessageBarTitle>
                {saidNoNotice(no)} &quot;Consent signed&quot;, the WHO Sign-in and Time-out and the incision wait for
                it.
              </>
            ) : (
              <>
                <MessageBarTitle>Consent not recorded</MessageBarTitle>
                Record the patient&apos;s consent under the patient&apos;s consents at the front office. &quot;Consent
                signed&quot;, the WHO Sign-in and Time-out and the incision wait for it.
              </>
            )}
          </MessageBarBody>
          <MessageBarActions>
            <Button
              size="small"
              icon={<ArrowClockwiseRegular />}
              onClick={() => void queryClient.invalidateQueries({ queryKey: otKeys.booking(booking.id), exact: true })}
              disabled={checking}
              // Keeps focus while checking: a disabled button drops it.
              disabledFocusable={checking}
            >
              Check again
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}

      {blocked && beforeIncision && !waiver && (
        <>
          {formOpen ? (
            <ConsentWaiverForm booking={booking} onDone={() => setWaiving(false)} />
          ) : mayWaive ? (
            <div className={styles.actions}>
              <Button icon={<ShieldErrorRegular />} onClick={() => setWaiving(true)}>
                Go ahead without consent (emergency)
              </Button>
              <Caption1 className={styles.muted}>
                Only when the operation cannot wait and no one can consent for the patient.
              </Caption1>
            </div>
          ) : booking.priority === 'Emergency' && !no ? (
            <Caption1 className={styles.muted}>
              In an emergency that cannot wait, the operating surgeon
              {booking.surgeonName ? ` (${booking.surgeonName})` : ''} may go ahead without consent, from their own
              sign-in, with a second doctor who agrees at their screen.
            </Caption1>
          ) : null}
        </>
      )}
    </section>
  );
}

/** "Consent refused by the patient", or withdrawn: the title of the patient's "no". */
function saidNoTitle(check: SurgicalConsentCheck): string {
  return check.state === 'Refused' ? 'Consent refused by the patient' : 'Consent withdrawn by the patient';
}

/**
 * The operating surgeon's emergency waiver: the clinical reason the operation cannot wait, and a second doctor who agrees,
 * typing their own user name and password here (purpose `ot.consent_waiver`). Sent once both are given, and never while
 * the patient's latest word on a consent needed is "no", a refusal or a withdrawal: the form then says so.
 */
function ConsentWaiverForm({ booking, onDone }: { booking: SurgeryDetail; onDone: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [reason, setReason] = useState('');
  const [witness, setWitness] = useState<WitnessConfirmed | null>(null);
  const no = booking.consent ? saidNoConsent(booking.consent) : null;
  const waive = useMutation({
    mutationFn: () =>
      otApi.waiveConsent(booking.id, {
        reason: reason.trim(),
        concurringDoctorConfirmationId: witness?.confirmationId ?? '',
      }),
    onSuccess: () => {
      notify.warning('Going ahead without consent', 'Recorded with your name, the second doctor’s and the reason.');
      void queryClient.invalidateQueries({ queryKey: otKeys.booking(booking.id) });
      onDone();
    },
  });

  const short = reason.trim().length > 0 && reason.trim().length < WAIVER_REASON_MIN;
  const ready = reason.trim().length >= WAIVER_REASON_MIN && witness !== null && no === null;
  // A typed reason or a doctor's confirmation is not thrown away without asking (SRS UX-08).
  useLeaveGuard({ dirty: reason.trim().length > 0 || witness !== null, busy: waive.isPending });

  return (
    <div className={styles.form} role="group" aria-label="Go ahead without consent">
      <MessageBar intent="warning" layout="multiline">
        <MessageBarBody>
          <MessageBarTitle>The emergency doctrine</MessageBarTitle>
          Only when the operation cannot wait and neither the patient nor anyone on their behalf can consent, and never
          against a refusal or a withdrawal of consent the patient has recorded. It is recorded with your name, the
          second doctor&apos;s and the reason, and audited as critical. Take the consent as soon as it can be taken.
        </MessageBarBody>
      </MessageBar>
      {no && (
        <MessageBar intent="error" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>{saidNoTitle(no)}</MessageBarTitle>
            {saidNoNotice(no)}
          </MessageBarBody>
        </MessageBar>
      )}
      <Field
        label="Why the operation cannot wait for consent"
        required
        hint={`At least ${WAIVER_REASON_MIN} characters.`}
        validationState={short ? 'error' : 'none'}
        validationMessage={short ? `Give the clinical reason (at least ${WAIVER_REASON_MIN} characters).` : undefined}
      >
        <Textarea value={reason} onChange={(_, d) => setReason(d.value)} maxLength={500} rows={2} resize="vertical" />
      </Field>
      <WitnessPanel
        purpose="ot.consent_waiver"
        patientId={booking.patientId}
        witness={witness}
        onChange={setWitness}
        legend="The second doctor who agrees, at this screen"
        description="Another doctor, not you, types their own user name and password here to agree that the operation cannot wait. Nothing is signed in, and the password is not kept."
        confirmedLabel="Agreed by"
        who="Doctor"
      />
      <ActionError error={waive.error} />
      <div className={styles.actions}>
        <Button onClick={onDone} disabled={waive.isPending}>
          Go back
        </Button>
        <Button
          appearance="primary"
          icon={waive.isPending ? <Spinner size="tiny" aria-hidden /> : <ShieldErrorRegular />}
          disabled={!ready || waive.isPending}
          onClick={() => waive.mutate()}
        >
          {witness ? 'Go ahead without consent' : 'Go ahead without consent (second doctor first)'}
        </Button>
      </div>
    </div>
  );
}
