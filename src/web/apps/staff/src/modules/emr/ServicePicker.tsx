import { Badge, Caption1, Combobox, Field, makeStyles, Option, Spinner, Text, tokens, type FieldProps } from '@fluentui/react-components';
import { formatInr, useDebouncedValue } from '@hc360/core';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { serviceCategoryLabel, type ServiceCategory } from './orderCart';
import { orderQueries, type ServiceItemDto } from './ordersApi';

const useStyles = makeStyles({
  option: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  row: { display: 'flex', gap: '8px', alignItems: 'baseline', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/**
 * Searches the service catalogue (tests, imaging, procedures…) by name or code. With one category the server filters;
 * with several (e.g. procedures + surgery) the results are narrowed here.
 */
export function ServicePicker({
  label,
  categories,
  onPick,
  hint,
  autoFocus,
  disabled,
  placeholder,
  showPrice = true,
}: {
  label: string;
  /** Allowed categories; null = any orderable category supplied by `fallbackCategories`. */
  categories: readonly ServiceCategory[] | null;
  onPick: (service: ServiceItemDto) => void;
  hint?: FieldProps['hint'];
  autoFocus?: boolean;
  disabled?: boolean;
  placeholder?: string;
  showPrice?: boolean;
}) {
  const styles = useStyles();
  const [term, setTerm] = useState('');
  const debounced = useDebouncedValue(term.trim(), 250);
  const single = categories && categories.length === 1 ? categories[0]! : null;
  const results = useQuery({ ...orderQueries.services(single, debounced), enabled: debounced.length >= 2 });
  const items = debounced.length >= 2 ? (results.data ?? []).filter((s) => !categories || categories.includes(s.category)) : [];

  return (
    <Field label={label} hint={hint}>
      <Combobox
        freeform
        autoFocus={autoFocus}
        disabled={disabled}
        placeholder={placeholder ?? 'Test or service name or code, e.g. CBC, LFT, chest X-ray'}
        value={term}
        selectedOptions={[]}
        onChange={(event) => setTerm(event.target.value)}
        onOptionSelect={(_, data) => {
          const picked = items.find((s) => s.id === data.optionValue);
          if (picked) {
            onPick(picked);
          }
          setTerm('');
        }}
        expandIcon={results.isFetching ? <Spinner size="extra-tiny" /> : undefined}
        positioning={{ autoSize: 'width' }}
      >
        {items.map((service) => (
          <Option key={service.id} value={service.id} text={service.name}>
            <span className={styles.option}>
              <span className={styles.row}>
                <Text weight="semibold">{service.name}</Text>
                <Badge size="small" appearance="tint">
                  {serviceCategoryLabel[service.category]}
                </Badge>
              </span>
              <Caption1 className={styles.muted}>
                {service.code}
                {showPrice && service.basePrice > 0 ? ` · ${formatInr(service.basePrice)}` : ''}
              </Caption1>
            </span>
          </Option>
        ))}
        {debounced.length >= 2 && !results.isFetching && items.length === 0 && (
          <Option value="" disabled text="No match">
            No service matches “{debounced}”.
          </Option>
        )}
      </Combobox>
    </Field>
  );
}
