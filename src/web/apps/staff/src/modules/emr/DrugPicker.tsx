import { Badge, Caption1, Combobox, makeStyles, Option, OptionGroup, Spinner, Text, tokens } from '@fluentui/react-components';
import { useDebouncedValue } from '@hc360/core';
import { useQuery } from '@tanstack/react-query';
import { usePermission } from '@/lib/permissions';
import { emrQueries } from './api';
import { frequencyMeaning } from './dosing';
import { drugMasterDefaults, drugOptionView, favouriteDefaults, type MedicineDefaults } from './prescriptionForm';
import { prescriptionQueries } from './prescriptionsApi';

const useStyles = makeStyles({
  option: { display: 'flex', flexDirection: 'column', gap: '1px', minWidth: 0 },
  row: { display: 'flex', gap: '6px', alignItems: 'baseline', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  outOfStock: { color: tokens.colorPaletteDarkOrangeForeground1, fontWeight: tokens.fontWeightSemibold },
});

function matches(text: string, ...values: (string | null | undefined)[]): boolean {
  const needle = text.trim().toLowerCase();
  return !needle || values.some((value) => value?.toLowerCase().includes(needle));
}

/**
 * Medicine name with suggestions: the doctor's favourites (with their usual dose), the speciality template's medicines
 * and — for prescribers — the drug master with the pharmacy's stock. Anything typed is accepted as free text.
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
  const canSearchMaster = usePermission('clinical.prescriptions.write');
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
        onPick(drugMasterDefaults(item));
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
          {masterMatches.map((d) => {
            const view = drugOptionView(d);
            return (
              <Option key={d.id} value={`drug:${d.id}`} text={d.name}>
                <span className={styles.option}>
                  <span className={styles.row}>
                    <Text weight="semibold">{d.name}</Text>
                    {view.flags.map((flag) => (
                      <Badge key={flag.kind} size="small" color={flag.kind === 'controlled' ? 'danger' : 'informative'} appearance="outline">
                        {flag.label}
                      </Badge>
                    ))}
                  </span>
                  <Caption1 className={styles.muted}>
                    {view.detail}
                    {view.detail && view.stock ? ' · ' : ''}
                    {view.stock && <span className={view.outOfStock ? styles.outOfStock : undefined}>{view.stock}</span>}
                  </Caption1>
                </span>
              </Option>
            );
          })}
        </OptionGroup>
      )}
    </Combobox>
  );
}
