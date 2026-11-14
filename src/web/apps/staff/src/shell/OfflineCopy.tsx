import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Subtitle2,
  tokens,
} from '@fluentui/react-components';
import { DocumentTableRegular } from '@fluentui/react-icons';
import { formatDateTime, onSessionKey, serverOutage, useConnection, useSession } from '@hc360/core';
import { useEffect, useState } from 'react';
import { api } from '@/lib/api';
import {
  ageText,
  clearOfflineCopy,
  columnsOf,
  columnTitle,
  openOfflineCopy,
  readKeptCopy,
  REFRESH_MS,
  takeOfflineCopy,
  useOfflineCopy,
  type OfflineWorklist,
} from '@/lib/offline/offlineCopy';
import { indexedDbBackend } from '@/lib/offline/vault';
import { DowntimePackView, PackTable } from '@/modules/downtime/DowntimePackView';

/** A pack older than this is called out: a drug stopped or started since is not in its medicine lists. */
const PACK_STALE_MS = 60 * 60 * 1000;

const useStyles = makeStyles({
  banner: { margin: '0 16px 8px' },
  surface: { maxWidth: '1100px', width: '95vw' },
  content: { display: 'flex', flexDirection: 'column', gap: '16px', maxHeight: '75vh', overflowY: 'auto' },
  muted: { color: tokens.colorNeutralForeground3 },
});

const backend = indexedDbBackend();
const fetchJson = (path: string) => api.get<unknown>(path);

/**
 * Keeps the offline copy (read-only offline mode): once the user's key comes it takes the latest pack and worklists,
 * again every 15 minutes while HC360 can work; when it cannot (unreachable, or its database down), it opens the copy
 * kept on this computer. A session that ends while HC360 can be reached wipes it (a sign-out wipes it in any case:
 * `signOut`).
 */
export function OfflineCopyKeeper() {
  const userId = useSession((s) => s.user?.id ?? null);
  const status = useSession((s) => s.status);
  const offline = useConnection(serverOutage);

  useEffect(
    () =>
      onSessionKey((user, key) => {
        void openOfflineCopy(user, key).then(() =>
          serverOutage(useConnection.getState()) ? false : takeOfflineCopy({ backend, fetchJson }),
        );
      }),
    [],
  );

  useEffect(() => {
    if (offline || status !== 'authenticated') {
      return undefined;
    }
    const timer = setInterval(() => void takeOfflineCopy({ backend, fetchJson }), REFRESH_MS);
    return () => clearInterval(timer);
  }, [offline, status]);

  useEffect(() => {
    if (offline && userId) {
      void readKeptCopy(userId, backend);
    }
  }, [offline, userId]);

  useEffect(() => {
    if (status === 'anonymous' && useConnection.getState().serverReachable) {
      void clearOfflineCopy(backend);
    }
  }, [status]);

  return null;
}

/**
 * While HC360 cannot be reached, or answers but its database is down: how old the offline copy is, and the copy itself,
 * read only. Nothing can be written from it; the ward records on paper and enters it when HC360 is back.
 */
export function OfflineCopyBanner({ now = Date.now }: { now?: () => number }) {
  const styles = useStyles();
  const offline = useConnection(serverOutage);
  const copy = useOfflineCopy();
  const [open, setOpen] = useState(false);

  if (!offline) {
    return null;
  }

  if (copy.takenAt === null) {
    return (
      <MessageBar intent="warning" className={styles.banner}>
        <MessageBarBody>
          <MessageBarTitle>No offline copy on this computer</MessageBarTitle>
          Work from the printed pre-downtime pack and the paper forms.
        </MessageBarBody>
      </MessageBar>
    );
  }

  const fetchedAt = formatDateTime(new Date(copy.takenAt).toISOString());
  const packAge = copy.pack ? now() - Date.parse(copy.pack.generatedAt) : null;
  const packStale = packAge !== null && packAge > PACK_STALE_MS;
  return (
    <>
      <MessageBar intent={packStale || !copy.pack ? 'warning' : 'info'} layout="multiline" className={styles.banner}>
        <MessageBarBody>
          <MessageBarTitle>
            {copy.pack
              ? `Read-only copy: ward sheets and medicines as of ${formatDateTime(copy.pack.generatedAt)}`
              : 'Read-only copy: no pre-downtime pack in it'}
          </MessageBarTitle>
          {copy.pack ? (
            <>
              The ward sheets and medicine lists are {ageText(Date.parse(copy.pack.generatedAt), now())} old. The
              worklists were fetched {fetchedAt}, {ageText(copy.takenAt, now())} ago.{' '}
            </>
          ) : (
            <>Use the printed pack for ward sheets and medicines. The worklists were fetched {fetchedAt}. </>
          )}
          Anything since then is not in it.
          {packStale && ' The pack is more than an hour old: check medicines against the paper MAR.'} Nothing can be
          saved: record on paper.
        </MessageBarBody>
        <MessageBarActions>
          <Button icon={<DocumentTableRegular />} onClick={() => setOpen(true)}>
            Open the offline copy
          </Button>
        </MessageBarActions>
      </MessageBar>
      <Dialog open={open} onOpenChange={(_, d) => setOpen(d.open)}>
        <DialogSurface className={styles.surface}>
          <DialogBody>
            <DialogTitle>Offline copy (read only)</DialogTitle>
            <DialogContent className={styles.content}>
              {copy.pack ? (
                <DowntimePackView pack={copy.pack} />
              ) : (
                <p>The copy holds no pre-downtime pack: use the printed one.</p>
              )}
              {copy.worklists.map((list) => (
                <OfflineWorklistTable key={list.slot} list={list} />
              ))}
            </DialogContent>
            <DialogActions>
              <Button appearance="primary" onClick={() => setOpen(false)}>
                Close
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </>
  );
}

function OfflineWorklistTable({ list }: { list: OfflineWorklist }) {
  const columns = columnsOf(list.rows);
  return (
    <section aria-label={list.title}>
      <Subtitle2 as="h2">{list.title}</Subtitle2>
      <PackTable
        table={{
          heading: list.title,
          note: `${list.rows.length} rows`,
          alert: null,
          columns: columns.map(columnTitle),
          rows: list.rows.map((r) => columns.map((c) => (r[c] === null || r[c] === undefined ? '' : String(r[c])))),
        }}
      />
    </section>
  );
}
