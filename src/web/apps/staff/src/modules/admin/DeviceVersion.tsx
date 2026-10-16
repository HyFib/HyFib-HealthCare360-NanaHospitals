import { Badge, Caption1, makeStyles, tokens } from '@fluentui/react-components';
import type { DeviceDto } from './api';
import { desktopVersionStatus } from './desktopVersions';

const useStyles = makeStyles({
  cell: { display: 'inline-flex', flexDirection: 'column', gap: '2px', alignItems: 'flex-start' },
  line: { display: 'inline-flex', gap: '6px', alignItems: 'center' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/**
 * Administration › Devices: the HC360 Desktop version a device reported, against the one the server offers
 * (SRS DED-18). A device behind is marked "Out of date"; it takes the update at its next check, within 4 hours, and
 * installs it the next time HC360 Desktop is closed, unless IT turned self-update off on it.
 */
export function DeviceVersion({
  device,
  offeredVersion,
}: {
  device: Pick<DeviceDto, 'kind' | 'appVersion' | 'isRevoked'>;
  offeredVersion: string | null | undefined;
}) {
  const styles = useStyles();
  const status = desktopVersionStatus(device, offeredVersion);
  return (
    <span className={styles.cell}>
      <span className={styles.line}>
        {device.appVersion ?? '—'}
        {status === 'outdated' && (
          <Badge appearance="tint" color="warning" shape="rounded">
            Out of date
          </Badge>
        )}
      </span>
      {status === 'outdated' && <Caption1 className={styles.muted}>{offeredVersion} offered</Caption1>}
      {status === 'current' && <Caption1 className={styles.muted}>Up to date</Caption1>}
      {status === 'unknown' && <Caption1 className={styles.muted}>Version not reported</Caption1>}
    </span>
  );
}
