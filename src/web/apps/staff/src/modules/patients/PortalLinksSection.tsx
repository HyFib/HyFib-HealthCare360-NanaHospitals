import {
  Button,
  Caption1,
  Checkbox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Subtitle2,
  Text,
  tokens,
} from '@fluentui/react-components';
import { SendRegular } from '@fluentui/react-icons';
import { formatDateTime, isApiError } from '@hc360/core';
import { DataTable, StatusBadge } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import {
  patientKeys,
  patientQueries,
  portalLinkApi,
  type PatientPortalLinksDto,
  type PortalLinkChannel,
  type PortalLinkSent,
} from './api';
import { channelsText, linkSentText, linkStateDetail, linkStateLabel, linkStateTone } from './portalAccess';

const useStyles = makeStyles({
  section: { display: 'flex', flexDirection: 'column', gap: '8px', marginBottom: '20px', maxWidth: '960px' },
  heading: { margin: 0 },
  intro: { color: tokens.colorNeutralForeground3, maxWidth: '72ch' },
  toolbar: { display: 'flex', gap: '12px', alignItems: 'center', flexWrap: 'wrap' },
  cell: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: '2px',
    paddingBlock: '6px',
    whiteSpace: 'normal',
    overflowWrap: 'anywhere',
  },
  muted: { color: tokens.colorNeutralForeground3 },
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  channels: { display: 'flex', flexDirection: 'column', gap: '2px' },
});

/**
 * Links to the patient portal sent to the patient's registered number (PP-01): staff who register patients send one by
 * SMS, WhatsApp or both, and everyone who may see the record sees the latest ones. The link is not a way in by itself:
 * opening it sends a sign-in code to the same number, so only the holder of that phone signs in. Sending is refused,
 * with the reason shown before anyone tries, for a record in a protected category (portal access is given at the desk),
 * without a mobile number, or while the portal has no public address.
 */
export function PortalLinksSection({ patientId }: { patientId: string }) {
  const styles = useStyles();
  const headingId = useId();
  const reasonId = useId();
  const canSend = usePermission('patients.register');
  const links = useQuery(patientQueries.portalLinks(patientId));
  const [sending, setSending] = useState(false);
  const [sent, setSent] = useState<PortalLinkSent | null>(null);
  const refusal = links.data?.cannotSend ?? null;

  return (
    <section className={styles.section} aria-labelledby={headingId}>
      <Subtitle2 as="h2" id={headingId} className={styles.heading}>
        Link to the portal
      </Subtitle2>
      <Caption1 className={styles.intro}>
        A link sent to the patient’s registered mobile number opens the patient portal. Opening it sends a sign-in code
        to the same number, so only the person with that phone can sign in. A link works for 7 days, and a new one
        replaces the one sent before.
      </Caption1>
      {canSend && links.data && (
        <div className={styles.toolbar}>
          <Button
            icon={<SendRegular />}
            disabled={refusal !== null}
            aria-describedby={refusal ? reasonId : undefined}
            onClick={() => setSending(true)}
          >
            Send portal link
          </Button>
          {refusal && (
            <Caption1 id={reasonId} className={styles.muted}>
              {refusal.message}
            </Caption1>
          )}
        </div>
      )}
      {sent && (
        <MessageBar intent={sent.notice ? 'warning' : 'success'} layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Portal link sent</MessageBarTitle>
            {linkSentText(sent)}
          </MessageBarBody>
        </MessageBar>
      )}
      <DataTable
        ariaLabel="Links to the portal sent to the patient"
        rows={links.data?.links}
        loading={links.isLoading}
        error={links.error}
        onRetry={() => void links.refetch()}
        getRowId={(l) => l.id}
        size="small"
        columns={[
          {
            key: 'sent',
            header: 'Sent',
            render: (l) => (
              <span className={styles.cell}>
                <Text>{formatDateTime(l.sentAt)}</Text>
                {l.sentByName && <Caption1 className={styles.muted}>by {l.sentByName}</Caption1>}
              </span>
            ),
          },
          { key: 'channels', header: 'By', render: (l) => channelsText(l.channels) },
          { key: 'number', header: 'To', render: (l) => l.maskedPhone },
          {
            key: 'state',
            header: 'Status',
            render: (l) => (
              <span className={styles.cell}>
                <StatusBadge status={l.state} tone={linkStateTone[l.state]} label={linkStateLabel[l.state]} />
                <Caption1 className={styles.muted}>{linkStateDetail(l)}</Caption1>
              </span>
            ),
          },
        ]}
        emptyTitle="No links sent"
        emptyDescription={
          canSend
            ? 'Send the patient a link when they want to use the portal and do not want to type their number.'
            : 'Links to the portal sent to the patient appear here.'
        }
      />
      {links.data && (
        <SendDialog
          patientId={patientId}
          links={links.data}
          open={sending}
          onClose={() => setSending(false)}
          onSent={(result) => {
            setSent(result);
            setSending(false);
          }}
        />
      )}
    </section>
  );
}

function SendDialog({
  patientId,
  links,
  open,
  onClose,
  onSent,
}: {
  patientId: string;
  links: PatientPortalLinksDto;
  open: boolean;
  onClose: () => void;
  onSent: (sent: PortalLinkSent) => void;
}) {
  return (
    <Dialog open={open} onOpenChange={(_, data) => !data.open && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        {open && <SendForm patientId={patientId} links={links} onClose={onClose} onSent={onSent} />}
      </DialogSurface>
    </Dialog>
  );
}

/** The channels to send on. The dialog mounts it each time it opens, so it starts from SMS alone. */
export function SendForm({
  patientId,
  links,
  onClose,
  onSent,
}: {
  patientId: string;
  links: PatientPortalLinksDto;
  onClose: () => void;
  onSent: (sent: PortalLinkSent) => void;
}) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const [channels, setChannels] = useState<PortalLinkChannel[]>(['Sms']);
  const toggle = (channel: PortalLinkChannel, on: boolean) =>
    setChannels((current) =>
      on ? [...current.filter((c) => c !== channel), channel] : current.filter((c) => c !== channel),
    );

  const send = useMutation({
    mutationFn: () => portalLinkApi.send(patientId, channels),
    onSuccess: (result) => {
      void queryClient.invalidateQueries({ queryKey: patientKeys.portalLinks(patientId) });
      onSent(result);
    },
    onError: (error) => {
      // Refused for the record as it is now (a category, a new number): the panel's reason follows it.
      if (isApiError(error) && error.status === 422) {
        void queryClient.invalidateQueries({ queryKey: patientKeys.portalLinks(patientId) });
      }
    },
  });

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        send.mutate();
      }}
      noValidate
    >
      <DialogBody>
        <DialogTitle>Send portal link</DialogTitle>
        <DialogContent className={styles.form}>
          <Text block>
            The link goes to the patient’s registered number, {links.maskedPhone ?? 'which is not on record'}. Opening
            it sends a sign-in code to the same number. It works for 7 days and replaces any link sent before.
          </Text>
          <div role="group" aria-label="Send by" className={styles.channels}>
            <Checkbox
              label="SMS"
              checked={channels.includes('Sms')}
              onChange={(_, data) => toggle('Sms', data.checked === true)}
            />
            <Checkbox
              label="WhatsApp"
              checked={channels.includes('WhatsApp')}
              disabled={links.whatsAppStopped}
              onChange={(_, data) => toggle('WhatsApp', data.checked === true)}
            />
            {links.whatsAppStopped && (
              <Caption1 className={styles.muted}>
                This number replied STOP on WhatsApp, so the link goes by SMS.
              </Caption1>
            )}
          </div>
          {send.error && (
            <MessageBar intent="error" layout="multiline" politeness="assertive">
              <MessageBarBody>
                <MessageBarTitle>The link was not sent</MessageBarTitle>
                {isApiError(send.error) ? send.error.userMessage : 'Please try again.'}
              </MessageBarBody>
            </MessageBar>
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={send.isPending}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={channels.length === 0 || send.isPending}
            icon={send.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            Send
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
