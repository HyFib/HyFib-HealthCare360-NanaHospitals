import {
  Button,
  Caption1,
  Combobox,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  Input,
  makeStyles,
  Option,
  OverlayDrawer,
  Radio,
  RadioGroup,
  Select,
  Spinner,
  Switch,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { todayIso, useDebouncedValue, type PayerType } from '@hc360/core';
import { useNotify, useUnsavedChanges, useValuesChanged } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { searchServices } from '@/modules/billing/api';
import {
  PAYER_TYPE_LABEL,
  PAYER_TYPES,
  SERVICE_CATEGORIES,
  SERVICE_CATEGORY_LABEL,
  type ServiceCategory,
} from '@/modules/billing/labels';
import { amountInputText, parsePercent, parseRupees } from '@/modules/billing/money';
import { ActionError } from '@/modules/billing/ui';
import { feeApi, feeKeys, SHARE_METHOD_LABEL, SHARE_METHODS, type ShareMethod, type ShareRuleDto } from './api';
import { PractitionerPicker, type PickedDoctor } from './PractitionerPicker';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  grid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: '12px',
    '@media (max-width: 600px)': { gridTemplateColumns: '1fr' },
  },
  muted: { color: tokens.colorNeutralForeground3 },
});

type Scope = 'all' | 'category' | 'service';

/**
 * Doctor revenue-share rule (DFA-01): whose, for what (a service, a category or everything), for which payer type, and
 * how much. The most specific rule in force on the service date wins; leave the doctor empty for the hospital default.
 */
export function RuleDrawer({ rule, onClose }: { rule: ShareRuleDto | null; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [doctor, setDoctor] = useState<PickedDoctor | null>(
    rule?.practitionerId ? { id: rule.practitionerId, name: rule.practitionerName ?? 'Doctor' } : null,
  );
  const [scope, setScope] = useState<Scope>(rule?.serviceItemId ? 'service' : rule?.category ? 'category' : 'all');
  const [service, setService] = useState<{ id: string; label: string } | null>(
    rule?.serviceItemId ? { id: rule.serviceItemId, label: rule.serviceCode ?? 'Service' } : null,
  );
  const [serviceTerm, setServiceTerm] = useState('');
  const [category, setCategory] = useState<ServiceCategory | ''>(rule?.category ?? '');
  const [payerType, setPayerType] = useState<PayerType | ''>(rule?.payerType ?? '');
  const [method, setMethod] = useState<ShareMethod>(rule?.method ?? 'Percentage');
  const [value, setValue] = useState(
    rule ? (rule.method === 'Percentage' ? String(rule.value) : amountInputText(rule.value)) : '',
  );
  const [from, setFrom] = useState(rule?.effectiveFrom ?? todayIso());
  const [to, setTo] = useState(rule?.effectiveTo ?? '');
  const [active, setActive] = useState(rule?.isActive ?? true);
  const [notes, setNotes] = useState(rule?.notes ?? '');
  const debounced = useDebouncedValue(serviceTerm.trim(), 250);
  const services = useQuery({
    queryKey: ['billing', 'service-search', debounced],
    queryFn: () => searchServices(debounced),
    enabled: scope === 'service' && debounced.length >= 2,
  });

  const parsedValue =
    method === 'Percentage' ? parsePercent(value, { allowZero: true }) : parseRupees(value, { allowZero: true });
  const numericValue = parsedValue.ok ? ('percent' in parsedValue ? parsedValue.percent : parsedValue.rupees) : null;
  const datesOk = Boolean(from) && (!to || to >= from);
  const scopeOk = scope === 'all' || (scope === 'service' ? service !== null : category !== '');
  const valid = numericValue !== null && datesOk && scopeOk;

  const save = useMutation<void, unknown, void>({
    mutationFn: async () => {
      const request = {
        practitionerId: doctor?.id ?? null,
        serviceItemId: scope === 'service' ? (service?.id ?? null) : null,
        category: scope === 'category' && category ? category : null,
        payerType: payerType || null,
        method,
        value: numericValue ?? 0,
        effectiveFrom: from,
        effectiveTo: to || null,
        isActive: active,
        notes: notes.trim() || null,
        version: rule?.version ?? null,
      };
      if (rule) {
        await feeApi.updateRule(rule.id, request);
      } else {
        await feeApi.createRule(request);
      }
    },
    onSuccess: () => {
      notify.success(rule ? 'Rule saved' : 'Rule added', 'It applies to bills finalised from now on.');
      void queryClient.invalidateQueries({ queryKey: feeKeys.rules });
      onClose();
    },
  });

  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ doctor, scope, service, category, payerType, method, value, from, to, active, notes }),
    busy: save.isPending,
    onClose,
  });

  return (
    <OverlayDrawer open position="end" size="medium" onOpenChange={guard.onOpenChange}>
      <form
        onSubmit={(event) => {
          event.preventDefault();
          if (valid && !save.isPending) {
            save.mutate();
          }
        }}
        style={{ display: 'contents' }}
      >
        <DrawerHeader>
          <DrawerHeaderTitle
            action={
              <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={guard.requestClose} />
            }
          >
            {rule ? 'Sharing rule' : 'Add a sharing rule'}
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          <div className={styles.form}>
            <PractitionerPicker
              value={doctor}
              onChange={setDoctor}
              hint="Leave empty for the hospital-wide default"
              autoFocus={!rule}
            />
            <Field label="Applies to">
              <RadioGroup layout="horizontal" value={scope} onChange={(_, d) => setScope(d.value as Scope)}>
                <Radio value="all" label="Every service" />
                <Radio value="category" label="A category" />
                <Radio value="service" label="One service" />
              </RadioGroup>
            </Field>
            {scope === 'category' && (
              <Field label="Category" required>
                <Select value={category} onChange={(_, d) => setCategory(d.value as ServiceCategory | '')}>
                  <option value="">Choose…</option>
                  {SERVICE_CATEGORIES.map((c) => (
                    <option key={c} value={c}>
                      {SERVICE_CATEGORY_LABEL[c]}
                    </option>
                  ))}
                </Select>
              </Field>
            )}
            {scope === 'service' && (
              <Field label="Service" required>
                <Combobox
                  placeholder="Type a service name or code"
                  value={serviceTerm || service?.label || ''}
                  selectedOptions={service ? [service.id] : []}
                  onChange={(event) => setServiceTerm(event.target.value)}
                  onOptionSelect={(_, data) => {
                    const item = services.data?.find((s) => s.id === data.optionValue);
                    setService(item ? { id: item.id, label: `${item.name} (${item.code})` } : null);
                    setServiceTerm('');
                  }}
                >
                  {(services.data ?? []).map((s) => (
                    <Option key={s.id} value={s.id} text={`${s.name} (${s.code})`}>
                      {s.name} ({s.code})
                    </Option>
                  ))}
                </Combobox>
              </Field>
            )}
            <Field
              label="For patients paying by"
              hint="Leave as any unless the share differs, e.g. for government schemes"
            >
              <Select value={payerType} onChange={(_, d) => setPayerType(d.value as PayerType | '')}>
                <option value="">Any payer</option>
                {PAYER_TYPES.map((t) => (
                  <option key={t} value={t}>
                    {PAYER_TYPE_LABEL[t]}
                  </option>
                ))}
              </Select>
            </Field>
            <div className={styles.grid}>
              <Field label="Share" required>
                <Select
                  value={method}
                  onChange={(_, d) => {
                    setMethod(d.value as ShareMethod);
                    setValue('');
                  }}
                >
                  {SHARE_METHODS.map((m) => (
                    <option key={m} value={m}>
                      {SHARE_METHOD_LABEL[m]}
                    </option>
                  ))}
                </Select>
              </Field>
              <Field
                label={method === 'Percentage' ? 'Percent' : 'Rupees per unit'}
                required
                validationMessage={value && !parsedValue.ok ? parsedValue.error : undefined}
                validationState={value && !parsedValue.ok ? 'error' : 'none'}
              >
                <Input
                  value={value}
                  onChange={(_, d) => setValue(d.value)}
                  inputMode="decimal"
                  contentBefore={method === 'FixedPerUnit' ? <Text>₹</Text> : undefined}
                  contentAfter={method === 'Percentage' ? <Text>%</Text> : undefined}
                />
              </Field>
              <Field label="From" required>
                <Input type="date" value={from} onChange={(_, d) => setFrom(d.value)} />
              </Field>
              <Field
                label="Until"
                validationMessage={datesOk ? undefined : 'The end date must be on or after the start date.'}
                validationState={datesOk ? 'none' : 'error'}
              >
                <Input type="date" value={to} min={from} onChange={(_, d) => setTo(d.value)} />
              </Field>
            </div>
            <Caption1 className={styles.muted}>
              A percentage is of the line&apos;s billed value after discounts and before GST; a fixed share is capped at
              the line&apos;s value.
            </Caption1>
            <Switch checked={active} onChange={(_, d) => setActive(d.checked)} label="Active" />
            <Field label="Notes">
              <Textarea value={notes} onChange={(_, d) => setNotes(d.value)} maxLength={300} rows={2} />
            </Field>
            <ActionError error={save.error} />
          </div>
        </DrawerBody>
        <DrawerFooter>
          <Button
            type="submit"
            appearance="primary"
            disabled={!valid || save.isPending}
            // Keeps focus while busy (a disabled button drops it); Enter can still submit, which is ignored.
            disabledFocusable={save.isPending}
            icon={save.isPending ? <Spinner size="tiny" /> : undefined}
          >
            {rule ? 'Save rule' : 'Add rule'}
          </Button>
          <Button onClick={guard.requestClose} disabled={save.isPending}>
            Cancel
          </Button>
        </DrawerFooter>
      </form>
      {guard.dialogs}
    </OverlayDrawer>
  );
}
