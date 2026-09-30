import {
  Button,
  Caption1,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Tooltip,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { ArrowSyncRegular, CopyRegular, PrintRegular, TabletRegular } from '@fluentui/react-icons';
import { useSession } from '@hc360/core';
import { ConfirmDialog, useNotify } from '@hc360/ui';
import { useQueryClient } from '@tanstack/react-query';
import { QRCodeSVG } from 'qrcode.react';
import { useState } from 'react';
import { feedbackApi, feedbackKeys, type FeedbackFormDto } from './api';
import { printQrPoster } from './print';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '12px' },
  qr: {
    alignSelf: 'center',
    padding: '12px',
    backgroundColor: '#ffffff',
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  row: { display: 'flex', gap: '6px' },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
});

/** The kiosk address: the same form, with answers counted under the Kiosk channel. */
function kioskUrl(url: string): string {
  return `${url}${url.includes('?') ? '&' : '?'}kiosk=true`;
}

/**
 * QR code of a feedback form for posters and kiosks. Answers through it are anonymous. Rotating the code retires every
 * poster already printed (for example after misuse), so it asks first.
 */
export function FormQrPanel({
  form,
  canManage,
  onRotated,
}: {
  form: FeedbackFormDto;
  canManage: boolean;
  onRotated?: (form: FeedbackFormDto) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const facilityName = useSession((s) => s.user?.facilityName) ?? 'Our hospital';
  const [confirming, setConfirming] = useState(false);
  const [rotating, setRotating] = useState(false);
  const [printing, setPrinting] = useState(false);

  const copy = async (text: string, what: string) => {
    try {
      await navigator.clipboard.writeText(text);
      notify.success(`${what} copied`);
    } catch {
      notify.warning('Could not copy', 'Select the address and copy it with Ctrl+C.');
    }
  };

  const print = async (kiosk: boolean) => {
    if (!form.qrUrl) {
      return;
    }
    setPrinting(true);
    try {
      await printQrPoster({
        facilityName,
        formTitle: form.title,
        introText: form.introText,
        url: kiosk ? kioskUrl(form.qrUrl) : form.qrUrl,
        kiosk,
      });
    } catch (error) {
      notify.error(error, 'Could not print the poster');
    } finally {
      setPrinting(false);
    }
  };

  const rotate = async () => {
    setRotating(true);
    try {
      const updated = await feedbackApi.rotateToken(form.id);
      notify.success('New QR code ready', 'Print and put up the new posters; the old ones no longer work.');
      void queryClient.invalidateQueries({ queryKey: feedbackKeys.forms });
      onRotated?.(updated);
      setConfirming(false);
    } catch (error) {
      notify.error(error, 'Could not change the QR code');
    } finally {
      setRotating(false);
    }
  };

  if (!form.qrUrl) {
    return (
      <MessageBar intent="warning">
        <MessageBarBody>
          The QR code needs the hospital's public portal address. Ask an administrator to set{' '}
          <strong>portal.baseUrl</strong> (an https address) in the facility settings.
        </MessageBarBody>
      </MessageBar>
    );
  }

  return (
    <div className={styles.root}>
      {!form.isActive && (
        <MessageBar intent="warning">
          <MessageBarBody>
            This form is inactive. Its QR code shows “link not valid” until the form is made active again.
          </MessageBarBody>
        </MessageBar>
      )}
      <div className={styles.qr}>
        <QRCodeSVG
          value={form.qrUrl}
          size={176}
          level="M"
          marginSize={0}
          aria-label={`QR code for ${form.title}`}
          role="img"
        />
      </div>
      <Caption1>Answers through this code are anonymous. Low scores still raise a complaint for follow-up.</Caption1>
      <Field label="Poster link">
        <div className={styles.row}>
          <Input value={form.qrUrl} readOnly style={{ flex: 1 }} aria-label="Poster link" />
          <Tooltip content="Copy poster link" relationship="label">
            <Button icon={<CopyRegular />} onClick={() => void copy(form.qrUrl ?? '', 'Poster link')} />
          </Tooltip>
        </div>
      </Field>
      <Field
        label="Kiosk link"
        hint="Open this on the feedback tablet at the exit; answers are counted as kiosk feedback."
      >
        <div className={styles.row}>
          <Input value={kioskUrl(form.qrUrl)} readOnly style={{ flex: 1 }} aria-label="Kiosk link" />
          <Tooltip content="Copy kiosk link" relationship="label">
            <Button icon={<CopyRegular />} onClick={() => void copy(kioskUrl(form.qrUrl ?? ''), 'Kiosk link')} />
          </Tooltip>
        </div>
      </Field>
      <div className={styles.actions}>
        <Button appearance="primary" icon={<PrintRegular />} onClick={() => void print(false)} disabled={printing}>
          Print poster
        </Button>
        <Button icon={<TabletRegular />} onClick={() => void print(true)} disabled={printing}>
          Print kiosk card
        </Button>
        {canManage && (
          <Button icon={<ArrowSyncRegular />} onClick={() => setConfirming(true)}>
            Change QR code
          </Button>
        )}
      </div>
      <ConfirmDialog
        open={confirming}
        title="Change this form's QR code?"
        confirmLabel="Change QR code"
        destructive
        busy={rotating}
        onConfirm={() => void rotate()}
        onCancel={() => setConfirming(false)}
      >
        Every poster and kiosk link already in use stops working at once. Use this when a poster has been misused, then
        print and put up the new posters.
      </ConfirmDialog>
    </div>
  );
}
