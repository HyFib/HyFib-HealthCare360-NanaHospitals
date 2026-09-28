import { Badge, Caption1, Combobox, makeStyles, Option, OptionGroup, Spinner, Text, tokens } from '@fluentui/react-components';
import { useDebouncedValue } from '@hc360/core';
import { useQuery } from '@tanstack/react-query';
import { usePermission } from '@/lib/permissions';
import { emrQueries } from './api';
import { frequencyMeaning } from './dosing';
import { favouriteDefaults, type MedicineDefaults } from './prescriptionForm';
import { DRUG_LOOKUP_PERMISSIONS, prescriptionQueries, type DrugLookupItem } from './prescriptionsApi';

const useStyles = makeStyles({
  option: { display: 'flex', flexDirection: 'column', gap: '1px', minWidth: 0 },
  row: { display: 'flex', gap: '6px', alignItems: 'baseline', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
});

function drugDefaults(item: DrugLookupItem): MedicineDefaults {
  const controlled = item.isControlled || item.schedule === 'H1' || item.schedule === 'X';
  return {
    drugItemId: item.id,
    drugName: item.name,
    genericName: item.genericName,
    strength: item.strength,
    isControlledDrug: controlled,
    controlledSchedule: controlled ? (item.schedule === 'X' ? 'X' : 'H1') : null,
  };
}

function matches(text: string, ...values: (string | null | undefined)[]): boolean {
  const needle = text.trim().toLowerCase();
  return !needle || values.some((value) => value?.toLowerCase().includes(needle));
}

/**
 * Medicine name with suggestions: the doctor's favourites (with their usual dose), the speciality template's medicines
 * and — where the user may search it — the pharmacy drug master. Anything typed is accepted as free text.
 */
export function DrugPicker({
  value,
  onChange,
  onPick,
  suggestions = [],
  invalid,
  autoFocus,
  ariaLabel,
}: {
  value: string;
  onChange: (text: string) => void;
  onPick: (defaults: MedicineDefaults) => void;
  suggestions?: readonly MedicineDefaults[];
  invalid?: boolean;
  autoFocus?: boolean;
  ariaLabel: string;
}) {
  const styles = useStyles();
  const canSearchMaster = usePermission(DRUG_LOOKUP_PERMISSIONS);
  const debounced = useDebouncedValue(value.trim(), 250);
  const favourites = useQuery(emrQueries.favourites('Drug'));
  const master = useQuery({ ...prescriptionQueries.drugs(debounced), enabled: canSearchMaster && debounced.length >= 2 });

  const favouriteMatches = (favourites.data ?? []).filter((f) => matches(value, f.label, f.genericName)).slice(0, 8);
  const suggestionMatches = suggestions.filter((s) => matches(value, s.drugName, s.genericName)).slice(0, 8);
  const masterMatches = debounced.length >= 2 ? (master.data ?? []) : [];

  const pick = (optionValue: string | undefined) => {
    if (!optionValue) {
      return;
    }
    const [source, id] = optionValue.split(':', 2);
    if (source === 'fav') {
      const favourite = favourites.data?.find((f) => f.id === id);
      if (favourite) {
        onPick(favouriteDefaults(favourite));
      }
    } else if (source === 'tpl') {
      const suggestion = suggestionMatches[Number(id)];
      if (suggestion) {
        onPick(suggestion);
      }
    } else if (source === 'drug') {
      const item = masterMatches.find((d) => d.id === id);
      if (item) {
        onPick(drugDefaults(item));
      }
    }
  };

  return (
    <Combobox
      freeform
      autoFocus={autoFocus}
      aria-label={ariaLabel}
      aria-invalid={invalid || undefined}
      placeholder="Medicine (brand or generic)"
      value={value}
      selectedOptions={[]}
      onChange={(event) => onChange(event.target.value)}
      onOptionSelect={(_, data) => pick(data.optionValue)}
      expandIcon={master.isFetching ? <Spinner size="extra-tiny" /> : undefined}
      positioning={{ autoSize: 'width' }}
      style={{ minWidth: 0, width: '100%' }}
    >
      {favouriteMatches.length > 0 && (
        <OptionGroup label="Your favourites">
          {favouriteMatches.map((f) => (
            <Option key={f.id} value={`fav:${f.id}`} text={f.label}>
              <span className={styles.option}>
                <span className={styles.row}>
                  <Text weight="semibold">{f.label}</Text>
                  {f.isControlledDrug && (
                    <Badge size="small" color="danger" appearance="outline">
                      H1/X
                    </Badge>
                  )}
                </span>
                <Caption1 className={styles.muted}>
                  {[f.genericName, f.dose, f.frequency ? frequencyMeaning[f.frequency].toLowerCase() : null, f.durationDays ? `${f.durationDays} days` : null].filter(Boolean).join(' · ')}
                </Caption1>
              </span>
            </Option>
          ))}
        </OptionGroup>
      )}
      {suggestionMatches.length > 0 && (
        <OptionGroup label="From the template">
          {suggestionMatches.map((s, index) => (
            <Option key={`${s.drugName}-${index}`} value={`tpl:${index}`} text={s.drugName}>
              <span className={styles.option}>
                <Text weight="semibold">{s.drugName}</Text>
                <Caption1 className={styles.muted}>{[s.genericName, s.dose, s.frequency].filter(Boolean).join(' · ')}</Caption1>
              </span>
            </Option>
          ))}
        </OptionGroup>
      )}
      {masterMatches.length > 0 && (
        <OptionGroup label="Pharmacy drug master">
          {masterMatches.map((d) => (
            <Option key={d.id} value={`drug:${d.id}`} text={d.name}>
              <span className={styles.option}>
                <span className={styles.row}>
                  <Text weight="semibold">{d.name}</Text>
                  {(d.schedule === 'H1' || d.schedule === 'X') && (
                    <Badge size="small" color="danger" appearance="outline">
                      Schedule {d.schedule}
                    </Badge>
                  )}
                </span>
                <Caption1 className={styles.muted}>
                  {[d.genericName, d.strength, d.available !== null ? `${d.available} ${d.baseUnit} in stock` : null].filter(Boolean).join(' · ')}
                </Caption1>
              </span>
            </Option>
          ))}
        </OptionGroup>
      )}
    </Combobox>
  );
}
