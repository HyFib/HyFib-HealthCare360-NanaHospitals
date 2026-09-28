import { Button, Caption1, makeStyles, mergeClasses, Text, tokens } from '@fluentui/react-components';
import { WarningRegular } from '@fluentui/react-icons';
import { ErrorState, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useFacilityId, useLiveInvalidation, useNow } from '@/modules/ot/shared/hooks';
import { bloodKeys, bloodQueries, KNOWN_GROUPS } from './api';
import { ExpiryText } from './BloodBadges';
import { componentShort, groupLabel } from './labels';
import { stockLevel } from './rules';

const useStyles = makeStyles({
  alert: { display: 'flex', gap: '6px', alignItems: 'center', color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold, marginBottom: '8px' },
  groups: { display: 'grid', gridTemplateColumns: 'repeat(4, minmax(0, 1fr))', gap: '6px', marginBottom: '10px' },
  cell: { display: 'flex', flexDirection: 'column', alignItems: 'center', padding: '6px', borderRadius: tokens.borderRadiusMedium, border: `1px solid ${tokens.colorNeutralStroke2}` },
  low: { border: `1px solid ${tokens.colorPaletteDarkOrangeBorder2}`, backgroundColor: tokens.colorPaletteDarkOrangeBackground1 },
  out: { border: `1px solid ${tokens.colorPaletteRedBorder2}`, backgroundColor: tokens.colorPaletteRedBackground1 },
  count: { fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold },
  muted: { color: tokens.colorNeutralForeground3 },
  expiring: { display: 'flex', flexDirection: 'column', gap: '2px' },
});

/** Home-screen blood stock: packed red cells by group (low and out flagged in words) and units about to expire. */
export function BloodStockWidget() {
  const styles = useStyles();
  const navigate = useNavigate();
  const facilityId = useFacilityId();
  const now = useNow(60_000);
  const inventory = useQuery(bloodQueries.inventory());
  useLiveInvalidation(facilityId ? `facility:${facilityId}` : null, 'bloodbank.changed', [bloodKeys.inventory]);

  if (inventory.error) {
    return <ErrorState error={inventory.error} onRetry={() => void inventory.refetch()} />;
  }
  const d = inventory.data;
  const prbc = (group: string) => d?.stock.find((c) => c.component === 'PRBC' && c.bloodGroup === group)?.available ?? 0;
  const groups = KNOWN_GROUPS.filter((g) => !g.startsWith('Bombay'));

  return (
    <Section
      title="Blood stock"
      description="Packed red cells available now, by group"
      actions={
        <Button size="small" appearance="subtle" onClick={() => void navigate({ to: '/blood-bank' })}>
          Inventory
        </Button>
      }
    >
      {!d ? (
        <Caption1>Loading…</Caption1>
      ) : (
        <>
          {d.emergencyRequests > 0 && (
            <Text className={styles.alert}>
              <WarningRegular aria-hidden /> {d.emergencyRequests} emergency request{d.emergencyRequests === 1 ? '' : 's'} open
            </Text>
          )}
          <div className={styles.groups}>
            {groups.map((group) => {
              const available = prbc(group);
              const level = stockLevel('PRBC', available);
              return (
                <div key={group} className={mergeClasses(styles.cell, level === 'low' && styles.low, level === 'out' && styles.out)}>
                  <Text weight="semibold">{groupLabel(group)}</Text>
                  <span className={styles.count}>{available}</span>
                  <Caption1 className={styles.muted}>{level === 'out' ? 'None' : level === 'low' ? 'Low' : componentShort.PRBC}</Caption1>
                </div>
              );
            })}
          </div>
          {d.expiringSoon.length > 0 ? (
            <div className={styles.expiring}>
              <Text weight="semibold">Expiring within 3 days: {d.expiringSoon.length}</Text>
              {d.expiringSoon.slice(0, 3).map((u) => (
                <Caption1 key={u.id}>
                  {u.unitNumber} · {componentShort[u.component]} {groupLabel(u.bloodGroup)} · <ExpiryText expiresAt={u.expiresAt} now={now} />
                </Caption1>
              ))}
            </div>
          ) : (
            <Caption1 className={styles.muted}>No units expire in the next 3 days.</Caption1>
          )}
        </>
      )}
    </Section>
  );
}
