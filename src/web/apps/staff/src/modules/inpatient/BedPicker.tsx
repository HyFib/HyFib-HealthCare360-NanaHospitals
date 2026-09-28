import {
  Caption1,
  Field,
  makeStyles,
  mergeClasses,
  SearchBox,
  Skeleton,
  SkeletonItem,
  Subtitle2,
  Text,
  ToggleButton,
  tokens,
  Tooltip,
  useArrowNavigationGroup,
} from '@fluentui/react-components';
import { BedRegular, CheckmarkCircleRegular, InfoRegular } from '@fluentui/react-icons';
import { EmptyState, ErrorState } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { inpatientQueries, WARD_TYPE_LABEL, type AvailableBedDto } from './api';
import { BedFeatures } from './BedTile';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '12px', minWidth: 0 },
  filters: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'flex-end' },
  wards: { display: 'flex', gap: '6px', flexWrap: 'wrap' },
  group: { display: 'flex', flexDirection: 'column', gap: '8px' },
  groupHeader: { display: 'flex', alignItems: 'baseline', gap: '8px', flexWrap: 'wrap' },
  grid: { display: 'grid', gap: '8px', gridTemplateColumns: 'repeat(auto-fill, minmax(128px, 1fr))' },
  bed: {
    appearance: 'none',
    margin: 0,
    textAlign: 'start',
    fontFamily: 'inherit',
    fontSize: 'inherit',
    color: tokens.colorNeutralForeground1,
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    padding: '10px 12px',
    minHeight: '72px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    borderLeft: `5px solid ${tokens.colorPaletteGreenBorderActive}`,
    backgroundColor: tokens.colorNeutralBackground1,
    cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-visible': { outline: `3px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '2px' },
  },
  selected: {
    border: `2px solid ${tokens.colorBrandStroke1}`,
    borderLeft: `5px solid ${tokens.colorBrandStroke1}`,
    backgroundColor: tokens.colorBrandBackground2,
  },
  disabled: {
    cursor: 'not-allowed',
    opacity: 0.6,
    borderLeft: `5px solid ${tokens.colorNeutralStroke1}`,
    ':hover': { backgroundColor: tokens.colorNeutralBackground1 },
  },
  bedHeader: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: '4px' },
  label: { fontWeight: tokens.fontWeightBold, fontFamily: tokens.fontFamilyNumeric },
  muted: { color: tokens.colorNeutralForeground3 },
  reason: { color: tokens.colorPaletteRedForeground1 },
  check: { color: tokens.colorBrandForeground1, fontSize: '18px', display: 'flex' },
});

export interface BedPickerProps {
  /** Checks ward rules (sex, NICU/PICU age, maternity) for this patient. */
  patientId: string | null;
  value: string | null;
  onChange: (bed: AvailableBedDto | null) => void;
  /** The patient's current bed (transfers). */
  excludeBedId?: string | null;
  label?: string;
  validationMessage?: string;
}

type Feature = 'oxygen' | 'ventilator' | 'monitor';

/**
 * Pick a vacant bed: grouped by ward, filterable by ward, bed label and equipment. Beds the patient may not use are
 * shown disabled with the reason, so the front desk understands why a vacant bed cannot be chosen.
 */
export function BedPicker({ patientId, value, onChange, excludeBedId, label = 'Bed', validationMessage }: BedPickerProps) {
  const styles = useStyles();
  const navigation = useArrowNavigationGroup({ axis: 'grid-linear', memorizeCurrent: true });
  const [wardId, setWardId] = useState<string | null>(null);
  const [term, setTerm] = useState('');
  const [features, setFeatures] = useState<Feature[]>([]);
  const available = useQuery(inpatientQueries.available(patientId));

  const beds = useMemo(() => (available.data ?? []).filter((bed) => bed.bedId !== excludeBedId), [available.data, excludeBedId]);

  const wards = useMemo(() => {
    const map = new Map<string, { wardId: string; wardName: string; wardType: AvailableBedDto['wardType']; beds: AvailableBedDto[] }>();
    for (const bed of beds) {
      const entry = map.get(bed.wardId) ?? { wardId: bed.wardId, wardName: bed.wardName, wardType: bed.wardType, beds: [] };
      entry.beds.push(bed);
      map.set(bed.wardId, entry);
    }
    return [...map.values()];
  }, [beds]);

  const needle = term.trim().toLowerCase();
  const visible = wards
    .filter((ward) => !wardId || ward.wardId === wardId)
    .map((ward) => ({
      ...ward,
      beds: ward.beds.filter(
        (bed) =>
          (!needle || bed.label.toLowerCase().includes(needle) || bed.roomNumber?.toLowerCase().includes(needle)) &&
          (!features.includes('oxygen') || bed.hasOxygen) &&
          (!features.includes('ventilator') || bed.hasVentilator) &&
          (!features.includes('monitor') || bed.hasMonitor),
      ),
    }))
    .filter((ward) => ward.beds.length > 0);

  const toggleFeature = (feature: Feature) =>
    setFeatures((current) => (current.includes(feature) ? current.filter((f) => f !== feature) : [...current, feature]));

  if (available.error) {
    return <ErrorState error={available.error} onRetry={() => void available.refetch()} title="Could not load vacant beds" />;
  }

  return (
    <Field label={label} required validationMessage={validationMessage} validationState={validationMessage ? 'error' : 'none'}>
      <div className={styles.root}>
        <div className={styles.filters}>
          <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Bed or room number" aria-label="Find a bed" style={{ minWidth: 200 }} />
          <ToggleButton size="small" checked={features.includes('oxygen')} onClick={() => toggleFeature('oxygen')}>
            Oxygen
          </ToggleButton>
          <ToggleButton size="small" checked={features.includes('ventilator')} onClick={() => toggleFeature('ventilator')}>
            Ventilator
          </ToggleButton>
          <ToggleButton size="small" checked={features.includes('monitor')} onClick={() => toggleFeature('monitor')}>
            Monitor
          </ToggleButton>
        </div>

        {wards.length > 1 && (
          <div className={styles.wards} role="toolbar" aria-label="Wards">
            <ToggleButton size="small" shape="circular" checked={wardId === null} onClick={() => setWardId(null)}>
              All wards ({beds.filter((b) => b.isEligible).length})
            </ToggleButton>
            {wards.map((ward) => (
              <ToggleButton
                key={ward.wardId}
                size="small"
                shape="circular"
                checked={wardId === ward.wardId}
                onClick={() => setWardId(wardId === ward.wardId ? null : ward.wardId)}
              >
                {ward.wardName} ({ward.beds.filter((b) => b.isEligible).length})
              </ToggleButton>
            ))}
          </div>
        )}

        {!patientId && (
          <Caption1 className={styles.muted}>
            <InfoRegular aria-hidden /> Choose the patient first so ward rules (male/female wards, NICU, PICU, maternity) are checked.
          </Caption1>
        )}

        {available.isLoading ? (
          <div className={styles.grid} aria-busy="true">
            {Array.from({ length: 8 }, (_, i) => (
              <Skeleton key={i} aria-label="Loading beds">
                <SkeletonItem style={{ height: 72 }} />
              </Skeleton>
            ))}
          </div>
        ) : visible.length === 0 ? (
          <EmptyState
            icon={<BedRegular />}
            title={beds.length === 0 ? 'No vacant beds right now' : 'No bed matches these filters'}
            description={
              beds.length === 0
                ? 'Beds being cleaned become available as soon as housekeeping marks them clean. Check the bed board or the housekeeping queue.'
                : 'Clear the search or the equipment filters to see more beds.'
            }
          />
        ) : (
          <div role="radiogroup" aria-label={label} className={styles.root} {...navigation}>
            {visible.map((ward) => (
              <section key={ward.wardId} className={styles.group} aria-label={ward.wardName}>
                <div className={styles.groupHeader}>
                  <Subtitle2 as="h3">{ward.wardName}</Subtitle2>
                  <Caption1 className={styles.muted}>
                    {WARD_TYPE_LABEL[ward.wardType] ?? ward.wardType} · {ward.beds.filter((b) => b.isEligible).length} available
                  </Caption1>
                </div>
                <div className={styles.grid}>
                  {ward.beds.map((bed) => {
                    const selected = bed.bedId === value;
                    const button = (
                      <button
                        key={bed.bedId}
                        type="button"
                        role="radio"
                        aria-checked={selected}
                        aria-disabled={!bed.isEligible}
                        aria-label={`Bed ${bed.label}${bed.roomNumber ? `, room ${bed.roomNumber}` : ''}, ${ward.wardName}${bed.isEligible ? '' : `. Not allowed: ${bed.ineligibleReason ?? ''}`}`}
                        className={mergeClasses(styles.bed, selected && styles.selected, !bed.isEligible && styles.disabled)}
                        onClick={() => bed.isEligible && onChange(selected ? null : bed)}
                      >
                        <span className={styles.bedHeader}>
                          <Text className={styles.label}>{bed.label}</Text>
                          {selected && (
                            <span className={styles.check}>
                              <CheckmarkCircleRegular aria-hidden />
                            </span>
                          )}
                        </span>
                        <Caption1 className={styles.muted}>
                          {bed.roomNumber ? `Room ${bed.roomNumber}` : 'Open ward'}
                          {bed.status === 'Reserved' ? ' · reserved' : ''}
                        </Caption1>
                        <BedFeatures oxygen={bed.hasOxygen} ventilator={bed.hasVentilator} monitor={bed.hasMonitor} />
                        {!bed.isEligible && <Caption1 className={styles.reason}>Not allowed</Caption1>}
                      </button>
                    );
                    return bed.isEligible ? (
                      button
                    ) : (
                      <Tooltip key={bed.bedId} content={bed.ineligibleReason ?? 'This patient cannot use this bed.'} relationship="description">
                        {button}
                      </Tooltip>
                    );
                  })}
                </div>
              </section>
            ))}
          </div>
        )}
      </div>
    </Field>
  );
}
