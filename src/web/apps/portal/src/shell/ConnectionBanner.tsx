import { Button, makeStyles, MessageBar, MessageBarActions, MessageBarBody, MessageBarTitle } from '@fluentui/react-components';
import { ArrowClockwiseRegular, PlugDisconnectedRegular, WifiWarningRegular } from '@fluentui/react-icons';
import { useConnection } from '@hc360/core';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { useT } from '@/i18n';

const useStyles = makeStyles({
  banner: { borderRadius: 0 },
});

/**
 * Shown while the phone is offline or the hospital server cannot be reached (mobile data drops, weak signal). What
 * the patient typed stays on screen; saves are retried safely (idempotency keys), and everything visible is
 * refreshed as soon as the connection is back.
 */
export function ConnectionBanner() {
  const styles = useStyles();
  const t = useT();
  const browserOnline = useConnection((s) => s.browserOnline);
  const serverReachable = useConnection((s) => s.serverReachable);
  const setReachable = useConnection((s) => s.setReachable);
  const queryClient = useQueryClient();
  const offline = !browserOnline || !serverReachable;
  const wasOffline = useRef(false);
  const [checking, setChecking] = useState(false);

  useEffect(() => {
    if (wasOffline.current && !offline) {
      void queryClient.invalidateQueries();
    }
    wasOffline.current = offline;
  }, [offline, queryClient]);

  if (!offline) {
    return null;
  }

  const checkAgain = async () => {
    setChecking(true);
    try {
      const response = await fetch('/health/live', { cache: 'no-store', signal: AbortSignal.timeout(8000) });
      setReachable(response.ok);
    } catch {
      setReachable(false);
    } finally {
      setChecking(false);
    }
  };

  return (
    <MessageBar
      intent="warning"
      layout="multiline"
      className={styles.banner}
      icon={browserOnline ? <PlugDisconnectedRegular /> : <WifiWarningRegular />}
      politeness="assertive"
    >
      <MessageBarBody>
        <MessageBarTitle>{browserOnline ? t('shell.server.title') : t('shell.offline.title')}</MessageBarTitle>
        {browserOnline ? t('shell.server.body') : t('shell.offline.body')}
      </MessageBarBody>
      {browserOnline && (
        <MessageBarActions>
          <Button icon={<ArrowClockwiseRegular />} onClick={() => void checkAgain()} disabled={checking}>
            {t('shell.checkAgain')}
          </Button>
        </MessageBarActions>
      )}
    </MessageBar>
  );
}
