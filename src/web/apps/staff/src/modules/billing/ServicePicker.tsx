import {
  Button,
  Caption1,
  Combobox,
  Field,
  Input,
  makeStyles,
  Option,
  Select,
  Spinner,
  Text,
  tokens,
  type FieldProps,
} from '@fluentui/react-components';
import { AddRegular } from '@fluentui/react-icons';
import { formatInr, useDebouncedValue, useHotkey } from '@hc360/core';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useRef, useState, type Ref } from 'react';
import { usePractitioners } from '@/lib/lookups';
import { billingApi, quotePrice, searchServices, type BillDetail, type ServiceItemDto } from './api';
import { PRICE_SOURCE_LABEL, SERVICE_CATEGORY_LABEL } from './labels';
import { amountInputText, formatPercent, fromPaise, parseQuantity, parseRupees, roundDiv, toPaise } from './money';
import { ActionError, KeyHint } from './ui';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '8px' },
  row: { display: 'flex', gap: '10px', alignItems: 'end', flexWrap: 'wrap' },
  search: { flex: '1 1 320px', minWidth: '260px' },
  small: { width: '100px' },
  price: { width: '130px' },
  muted: { color: tokens.colorNeutralForeground3 },
  option: { display: 'flex', justifyContent: 'space-between', gap: '12px', width: '100%' },
});

/**
 * Catalogue search (active services, 2+ letters): type a name or code and pick one. Shared by the "add a service" row
 * of a draft bill and the manual charge dialog.
 */
export function ServiceSearchField({
  label,
  value,
  onChange,
  enabled = true,
  required,
  className,
  inputRef,
}: {
  label: FieldProps['label'];
  value: ServiceItemDto | null;
  onChange: (item: ServiceItemDto | null) => void;
  enabled?: boolean;
  required?: boolean;
  className?: string;
  inputRef?: Ref<HTMLInputElement>;
}) {
  const styles = useStyles();
  const [term, setTerm] = useState('');
  const debounced = useDebouncedValue(term.trim(), 250);
  const results = useQuery({
    queryKey: ['billing', 'service-search', debounced],
    queryFn: () => searchServices(debounced),
    enabled: enabled && debounced.length >= 2,
    staleTime: 60_000,
  });
  // The text clears once a service is picked; picking nothing (cleared) clears the service too.
  const [shown, setShown] = useState(value);
  if (shown !== value) {
    setShown(value);
    if (value === null) {
      setTerm('');
    }
  }

  const items = results.data ?? [];
  return (
    <Field label={label} className={className} required={required}>
      <Combobox
        ref={inputRef}
        placeholder="Type a service name or code (2+ letters)"
        value={term || (value ? `${value.name} (${value.code})` : '')}
        selectedOptions={value ? [value.id] : []}
        onChange={(event) => {
          setTerm(event.target.value);
          if (!event.target.value) {
            onChange(null);
          }
        }}
        onOptionSelect={(_, data) => {
          setTerm('');
          onChange(items.find((s) => s.id === data.optionValue) ?? null);
        }}
        expandIcon={results.isFetching ? <Spinner size="extra-tiny" aria-hidden /> : undefined}
        freeform
        clearable
        disabled={!enabled}
      >
        {debounced.length >= 2 && items.length === 0 && !results.isFetching && (
          <Option value="" disabled text="No match">
            No service matches “{debounced}”
          </Option>
        )}
        {items.map((service) => (
          <Option key={service.id} value={service.id} text={`${service.name} (${service.code})`}>
            <span className={styles.option}>
              <span>
                {service.name}
                <Caption1 block className={styles.muted}>
                  {service.code} · {SERVICE_CATEGORY_LABEL[service.category]}
                </Caption1>
              </span>
              <Text>{formatInr(service.basePrice)}</Text>
            </span>
          </Option>
        ))}
      </Combobox>
    </Field>
  );
}

/**
 * Adds a service to a draft OP bill (posted as an ad-hoc charge): search the catalogue, see the tariff price that
 * applies to this patient's visit, set the quantity and press Enter. F2 jumps here from anywhere on the screen.
 */
export function ServicePicker({
  bill,
  onAdded,
  enabled = true,
}: {
  bill: BillDetail;
  onAdded: (bill: BillDetail) => void;
  enabled?: boolean;
}) {
  const styles = useStyles();
  const searchRef = useRef<HTMLInputElement>(null);
  const quantityRef = useRef<HTMLInputElement>(null);
  const [item, setItem] = useState<ServiceItemDto | null>(null);
  const [quantity, setQuantity] = useState('1');
  const [price, setPrice] = useState('');
  const [doctorId, setDoctorId] = useState('');

  useHotkey('f2', () => searchRef.current?.focus(), { enabled, allowInInputs: true });

  const quote = useQuery({
    queryKey: ['billing', 'quote', item?.id ?? '', bill.visitId ?? '', bill.tariffPlanId ?? ''],
    queryFn: () =>
      quotePrice({
        serviceItemId: item!.id,
        visitId: bill.visitId,
        tariffPlanId: bill.visitId ? null : bill.tariffPlanId,
      }),
    enabled: item !== null,
    staleTime: 60_000,
  });
  const doctors = usePractitioners(item?.departmentId ?? null);

  const qty = parseQuantity(quantity);
  const editable = quote.data?.isPriceEditable ?? false;
  const override = editable && price.trim() ? parseRupees(price, { allowZero: true }) : null;
  const unitPrice = override?.ok ? override.rupees : (quote.data?.unitPrice ?? null);
  const lineAmount =
    qty !== null && unitPrice !== null
      ? fromPaise(roundDiv(toPaise(unitPrice) * BigInt(Math.round(qty * 1000)), 1000n))
      : null;

  const clearSelection = () => {
    setItem(null);
    setQuantity('1');
    setPrice('');
    setDoctorId('');
    searchRef.current?.focus();
  };

  const add = useMutation({
    mutationFn: () =>
      billingApi.addLine(bill.id, {
        serviceItemId: item!.id,
        quantity: qty ?? 1,
        unitPrice: override?.ok ? override.rupees : null,
        description: null,
        practitionerId: doctorId || null,
        departmentId: item?.departmentId ?? null,
        version: bill.version,
      }),
    onSuccess: (updated) => {
      onAdded(updated);
      clearSelection();
    },
  });

  const canAdd =
    item !== null && qty !== null && quote.data !== undefined && (override === null || override.ok) && !add.isPending;
  const submit = () => {
    if (canAdd) {
      add.mutate();
    }
  };

  return (
    <div className={styles.root}>
      <div className={styles.row}>
        <ServiceSearchField
          label={
            <>
              Add a service <KeyHint>F2</KeyHint>
            </>
          }
          className={styles.search}
          inputRef={searchRef}
          value={item}
          enabled={enabled}
          onChange={(picked) => {
            setItem(picked);
            setPrice(picked?.isPriceEditable ? amountInputText(picked.basePrice) : '');
            if (picked) {
              window.setTimeout(() => quantityRef.current?.select(), 0);
            }
          }}
        />
        <Field
          label="Qty"
          validationMessage={quantity && qty === null ? 'More than 0, at most 3 decimals' : undefined}
          validationState={quantity && qty === null ? 'error' : 'none'}
        >
          <Input
            ref={quantityRef}
            className={styles.small}
            value={quantity}
            onChange={(_, data) => setQuantity(data.value)}
            inputMode="decimal"
            disabled={!item}
            onKeyDown={(event) => {
              if (event.key === 'Enter') {
                event.preventDefault();
                submit();
              }
            }}
          />
        </Field>
        {editable && (
          <Field
            label="Price (₹)"
            validationMessage={override && !override.ok ? override.error : undefined}
            validationState={override && !override.ok ? 'error' : 'none'}
          >
            <Input
              className={styles.price}
              value={price}
              onChange={(_, data) => setPrice(data.value)}
              inputMode="decimal"
              contentBefore={<Text>₹</Text>}
            />
          </Field>
        )}
        {item && (doctors.data?.length ?? 0) > 0 && (
          <Field label="Doctor">
            <Select value={doctorId} onChange={(_, data) => setDoctorId(data.value)}>
              <option value="">Not specific</option>
              {doctors.data?.map((doctor) => (
                <option key={doctor.id} value={doctor.id}>
                  {doctor.name}
                </option>
              ))}
            </Select>
          </Field>
        )}
        <Button
          appearance="primary"
          icon={add.isPending ? <Spinner size="tiny" aria-hidden /> : <AddRegular />}
          disabled={!canAdd}
          onClick={submit}
        >
          Add
        </Button>
      </div>
      {item && (
        <Caption1 className={styles.muted} aria-live="polite">
          {quote.isLoading
            ? 'Checking the price for this patient…'
            : quote.data
              ? `${PRICE_SOURCE_LABEL[quote.data.source]}${quote.data.tariffPlanName ? ` (${quote.data.tariffPlanName})` : ''}: ${formatInr(quote.data.unitPrice)} each${
                  quote.data.taxRate > 0 ? ` + GST ${formatPercent(quote.data.taxRate)}` : ''
                }${lineAmount !== null ? ` · line ${formatInr(lineAmount)}` : ''}${quote.data.isDiscountAllowed ? '' : ' · no discount on this service'}`
              : 'The price could not be checked. It is priced by the server when added.'}
        </Caption1>
      )}
      <ActionError error={add.error} />
    </div>
  );
}
