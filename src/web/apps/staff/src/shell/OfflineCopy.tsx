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
  Text,
  Title3,
  tokens,
} from '@fluentui/react-components';
import { DocumentTableRegular } from '@fluentui/react-icons';
import { formatDateTime, serverOutage, useConnection, useIdle, useSession } from '@hc360/core';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { PUBLIC_SETTINGS_KEY } from '@/lib/lookups';
import { fetchOfflineJson, offlineBackend } from '@/lib/offline/keeper';
import {
  ageText,
  columnsOf,
  columnTitle,
  copyOf,
  readKeptCopy,
  REFRESH_MS,
  takeOfflineCopy,
  useOfflineCopy,
  type OfflineWorklist,
} from '@/lib/offline/offlineCopy';
import { DowntimePackView, PackTable } from '@/modules/downtime/DowntimePackView';
import { idleLockMinutes } from './lockSync';

/** A pack older than this is called out: a drug stopped or started since is not in its medicine lists. */
const PACK_STALE_MS = 60 * 60 * 1000;

const useStyles = makeStyles({
  banner: { margin: '0 16px 8px' },
  surface: { maxWidth: '1100px', width: '95vw' },
  content: { display: 'flex', flexDirection: 'column', gap: '16px', maxHeight: '75vh', overflowY: 'auto' },
  muted: { color: tokens.colorNeutralForeground3 },
  reader: {
    width: 'min(1100px, calc(100vw - 32px))',
    maxHeight: 'calc(100vh - 32px)',
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    padding: '20px',
    boxSizing: 'border-box',
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: tokens.shadow28,
  },
  readerContent: { display: 'flex', flexDirection: 'column', gap: '16px', overflowY: 'auto', minHeight: 0 },
  readerActions: { display: 'flex', justifyContent: 'flex-end' },
});

type HeldCopy = NonNullable<ReturnType<typeof copyOf>>;

/**
 * Keeps the offline copy fresh (read-only offline mode): it takes the latest pack and worklists again every 15 minutes
 * while HC360 can work; when it cannot (unreachable, or its database down), it opens the copy kept on this computer.
 * The copy is first taken, and wiped, from app start (`startOfflineCopy`).
 */
export function OfflineCopyKeeper() {
  const userId = useSession((s) => s.user?.id ?? null);
  const status = useSession((s) => s.status);
  const offline = useConnection(serverOutage);

  useEffect(() => {
    if (offline || status !== 'authenticated') {
      return undefined;
    }
    const timer = setInterval(
      () => void takeOfflineCopy({ backend: offlineBackend, fetchJson: fetchOfflineJson }),
      REFRESH_MS,
    );
    return () => clearInterval(timer);
  }, [offline, status]);

  useEffect(() => {
    if (offline && userId) {
      void readKeptCopy(userId, offlineBackend);
    }
  }, [offline, userId]);

  return null;
}

/** How old each part of the copy is: the ward sheets and medicines by the pack's time, the worklists by the fetch. */
function CopyAge({ copy, now }: { copy: HeldCopy; now: () => number }) {
  const takenAt = copy.takenAt ?? now();
  const fetchedAt = formatDateTime(new Date(takenAt).toISOString());
  const packAge = copy.pack ? now() - Date.parse(copy.pack.generatedAt) : null;
  const packStale = packAge !== null && packAge > PACK_STALE_MS;
  return (
    <>
      {copy.pack ? (
        <>
          The ward sheets and medicine lists are {ageText(Date.parse(copy.pack.generatedAt), now())} old. The worklists
          were fetched {fetchedAt}, {ageText(takenAt, now())} ago.{' '}
        </>
      ) : (
        <>Use the printed pack for ward sheets and medicines. The worklists were fetched {fetchedAt}. </>
      )}
      Anything since then is not in it.
      {packStale && ' The pack is more than an hour old: check medicines against the paper MAR.'} Nothing can be saved:
      record on paper.
    </>
  );
}

function copyTitle(copy: HeldCopy): string {
  return copy.pack
    ? `Read-only copy: ward sheets and medicines as of ${formatDateTime(copy.pack.generatedAt)}`
    : 'Read-only copy: no pre-downtime pack in it';
}

function packStale(copy: HeldCopy, now: () => number): boolean {
  return copy.pack !== null && now() - Date.parse(copy.pack.generatedAt) > PACK_STALE_MS;
}

/** The copy itself: the pack, then the worklists. Nothing in it writes. */
function CopyContents({ copy }: { copy: HeldCopy }) {
  return (
    <>
      {copy.pack ? (
        <DowntimePackView pack={copy.pack} />
      ) : (
        <p>The copy holds no pre-downtime pack: use the printed one.</p>
      )}
      {copy.worklists.map((list) => (
        <OfflineWorklistTable key={list.slot} list={list} />
      ))}
    </>
  );
}

/**
 * While HC360 cannot be reached, or answers but its database is down: how old the offline copy is, and the copy itself,
 * read only. Nothing can be written from it; the ward records on paper and enters it when HC360 is back.
 */
export function OfflineCopyBanner({ now = Date.now }: { now?: () => number }) {
  const styles = useStyles();
  const offline = useConnection(serverOutage);
  const userId = useSession((s) => s.user?.id);
  const copy = copyOf(useOfflineCopy(), userId);
  const [open, setOpen] = useState(false);

  if (!offline) {
    return null;
  }

  if (!copy) {
    return (
      <MessageBar intent="warning" className={styles.banner}>
        <MessageBarBody>
          <MessageBarTitle>No offline copy on this computer</MessageBarTitle>
          Work from the printed pre-downtime pack and the paper forms.
        </MessageBarBody>
      </MessageBar>
    );
  }

  return (
    <>
      <MessageBar
        intent={packStale(copy, now) || !copy.pack ? 'warning' : 'info'}
        layout="multiline"
        className={styles.banner}
      >
        <MessageBarBody>
          <MessageBarTitle>{copyTitle(copy)}</MessageBarTitle>
          <CopyAge copy={copy} now={now} />
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
              <CopyContents copy={copy} />
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

/**
 * The offline copy opened on the locked screen, once the PIN or password was checked on this computer because the server
 * could not be asked. Read only, as the banner's: nothing on it writes, and the session stays locked behind it, its
 * screens hidden. It closes when its user closes it, and on the idle lock's own timer (`ui.idleLockMinutes`), as the
 * screen would lock; the PIN is then asked again.
 */
export function LockedOfflineCopy({
  userId,
  onClose,
  now = Date.now,
}: {
  userId: string;
  onClose: () => void;
  now?: () => number;
}) {
  const styles = useStyles();
  const copy = copyOf(useOfflineCopy(), userId);
  const outage = useConnection(serverOutage);
  // The lock time the app last had from the server: the server cannot be asked now.
  const settings = useQueryClient().getQueryData<Record<string, unknown>>(PUBLIC_SETTINGS_KEY);
  const lockMinutes = idleLockMinutes(settings?.['ui.idleLockMinutes']);
  useIdle(lockMinutes * 60_000, onClose, lockMinutes > 0);

  return (
    <section className={styles.reader} aria-labelledby="locked-offline-copy">
      <Title3 as="h2" id="locked-offline-copy">
        Offline copy (read only)
      </Title3>
      {!outage && (
        <MessageBar intent="success" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>HealthCare360 is back</MessageBarTitle>
            Close the copy and unlock the screen to carry on.
          </MessageBarBody>
        </MessageBar>
      )}
      {copy ? (
        <>
          <Text weight="semibold">{copyTitle(copy)}</Text>
          <Text className={styles.muted}>
            <CopyAge copy={copy} now={now} />
          </Text>
          <div className={styles.readerContent}>
            <CopyContents copy={copy} />
          </div>
        </>
      ) : (
        <Text>There is no offline copy on this computer: work from the printed pre-downtime pack.</Text>
      )}
      <div className={styles.readerActions}>
        <Button appearance="primary" onClick={onClose}>
          Close
        </Button>
      </div>
    </section>
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
