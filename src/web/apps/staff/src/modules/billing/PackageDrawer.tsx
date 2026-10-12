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
  Select,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular, DismissRegular } from '@fluentui/react-icons';
import { formatInr, useDebouncedValue } from '@hc360/core';
import { useNotify, useUnsavedChanges, useValuesChanged } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { searchServices } from './api';
import {
  PACKAGE_USE_LABEL,
  PACKAGE_USES,
  SERVICE_CATEGORIES,
  SERVICE_CATEGORY_LABEL,
  type PackageUse,
  type ServiceCategory,
} from './labels';
import { mastersApi, mastersKeys, mastersQueries, type PackageDetail, type PackageInclusionInput } from './mastersApi';
import { amountInputText, parseRupees } from './money';
import { ActionError, AmountField } from './ui';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  grid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: '12px',
    '@media (max-width: 600px)': { gridTemplateColumns: '1fr' },
  },
  inclusion: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 2fr) 90px 120px auto',
    gap: '8px',
    alignItems: 'end',
    padding: '8px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  muted: { color: tokens.colorNeutralForeground3 },
});

interface InclusionDraft {
  key: string;
  serviceItemId: string | null;
  label: string;
  category: ServiceCategory | null;
  maxQuantity: string;
  maxAmount: string;
  notes: string;
}

let inclusionSequence = 0;
const nextKey = () => `inc-${++inclusionSequence}`;

/** A small search box for one catalogue service (optionally of one category). */
function ServiceSearch({
  label,
  category,
  onPick,
  required,
}: {
  label: string;
  category?: ServiceCategory;
  onPick: (item: { id: string; name: string; code: string }) => void;
  required?: boolean;
}) {
  const [term, setTerm] = useState('');
  const debounced = useDebouncedValue(term.trim(), 250);
  const results = useQuery({
    queryKey: ['billing', 'service-search', debounced, category ?? ''],
    queryFn: () => searchServices(debounced, category),
    enabled: category !== undefined || debounced.length >= 2,
  });
  return (
    <Field label={label} required={required}>
      <Combobox
        placeholder="Type a service name or code"
        value={term}
        onChange={(event) => setTerm(event.target.value)}
        onOptionSelect={(_, data) => {
          const item = results.data?.find((s) => s.id === data.optionValue);
          if (item) {
            onPick(item);
          }
          setTerm('');
        }}
      >
        {(results.data ?? []).map((s) => (
          <Option key={s.id} value={s.id} text={s.name}>
            {s.name} ({s.code}) · {formatInr(s.basePrice)}
          </Option>
        ))}
      </Combobox>
    </Field>
  );
}

/**
 * Create or edit a treatment package: price, whether it prices admissions, OP bills or both, stay, and what it includes
 * (a service or a whole category, with caps).
 */
export function PackageDrawer({
  packageDetail,
  onClose,
}: {
  packageDetail: PackageDetail | null;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const plans = useQuery(mastersQueries.tariffPlans());
  const p = packageDetail;
  const [code, setCode] = useState(p?.code ?? '');
  const [name, setName] = useState(p?.name ?? '');
  const [billing, setBilling] = useState<{ id: string; label: string } | null>(
    p ? { id: p.serviceItemId, label: p.serviceCode ?? 'Package service' } : null,
  );
  const [price, setPrice] = useState(p ? amountInputText(p.price) : '');
  const [usedFor, setUsedFor] = useState<PackageUse>(p?.usedFor ?? 'InPatient');
  const [stay, setStay] = useState(p?.lengthOfStayDays ? String(p.lengthOfStayDays) : '');
  const [planId, setPlanId] = useState(p?.tariffPlanId ?? '');
  const [description, setDescription] = useState(p?.description ?? '');
  const [active, setActive] = useState(p?.isActive ?? true);
  const [inclusions, setInclusions] = useState<InclusionDraft[]>(() =>
    (p?.inclusions ?? []).map((i) => ({
      key: nextKey(),
      serviceItemId: i.serviceItemId,
      label: i.serviceItemId ? `${i.serviceName ?? ''} (${i.serviceCode ?? ''})` : '',
      category: i.category,
      maxQuantity: i.maxQuantity !== null ? String(i.maxQuantity) : '',
      maxAmount: i.maxAmount !== null ? amountInputText(i.maxAmount) : '',
      notes: i.notes ?? '',
    })),
  );

  const parsedPrice = parseRupees(price, { allowZero: true });
  // An out-patient package has no stay.
  const stayDays = usedFor !== 'OutPatient' && stay.trim() ? Number(stay) : null;
  const stayOk = stayDays === null || (Number.isInteger(stayDays) && stayDays >= 1 && stayDays <= 365);
  const inclusionProblem = inclusions
    .map((i) => {
      if (!i.serviceItemId && !i.category) {
        return 'Every inclusion names a service or a category.';
      }
      if (i.maxQuantity.trim() && !(Number(i.maxQuantity) > 0)) {
        return 'Quantity caps must be more than zero.';
      }
      if (i.maxAmount.trim() && !parseRupees(i.maxAmount, { allowZero: true }).ok) {
        return 'Check the amount caps (rupees, at most 2 decimals).';
      }
      return null;
    })
    .find(Boolean);
  const codeOk = /^[A-Za-z0-9][A-Za-z0-9._-]*$/.test(code.trim()) && code.trim().length <= 32;
  const valid = codeOk && name.trim().length > 0 && billing !== null && parsedPrice.ok && stayOk && !inclusionProblem;

  const update = (key: string, patch: Partial<InclusionDraft>) =>
    setInclusions((list) => list.map((i) => (i.key === key ? { ...i, ...patch } : i)));

  const save = useMutation({
    mutationFn: () => {
      const request = {
        code: code.trim().toUpperCase(),
        name: name.trim(),
        serviceItemId: billing!.id,
        price: parsedPrice.ok ? parsedPrice.rupees : 0,
        lengthOfStayDays: stayDays,
        tariffPlanId: planId || null,
        usedFor,
        description: description.trim() || null,
        isActive: active,
        inclusions: inclusions.map<PackageInclusionInput>((i) => {
          const cap = i.maxAmount.trim() ? parseRupees(i.maxAmount, { allowZero: true }) : null;
          return {
            serviceItemId: i.serviceItemId,
            category: i.serviceItemId ? null : i.category,
            maxQuantity: i.maxQuantity.trim() ? Number(i.maxQuantity) : null,
            maxAmount: cap?.ok ? cap.rupees : null,
            notes: i.notes.trim() || null,
          };
        }),
        version: p?.version ?? null,
      };
      return p ? mastersApi.updatePackage(p.id, request) : mastersApi.createPackage(request);
    },
    onSuccess: (saved) => {
      notify.success(p ? 'Package saved' : 'Package created', saved.name);
      void queryClient.invalidateQueries({ queryKey: mastersKeys.packages });
      onClose();
    },
  });

  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ code, name, billing, price, usedFor, stay, planId, description, active, inclusions }),
    busy: save.isPending,
    onClose,
  });

  return (
    <OverlayDrawer open position="end" size="large" onOpenChange={guard.onOpenChange}>
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
            {p ? p.name : 'New package'}
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          <div className={styles.form}>
            <div className={styles.grid}>
              <Field
                label="Code"
                required
                validationMessage={code && !codeOk ? 'Use letters, digits, dots, dashes or underscores.' : undefined}
                validationState={code && !codeOk ? 'error' : 'none'}
              >
                <Input
                  value={code}
                  onChange={(_, d) => setCode(d.value)}
                  disabled={Boolean(p)}
                  maxLength={32}
                  autoFocus={!p}
                />
              </Field>
              <Field label="Name" required>
                <Input value={name} onChange={(_, d) => setName(d.value)} maxLength={200} />
              </Field>
              <AmountField
                label="Package price"
                required
                value={price}
                onChange={setPrice}
                error={price && !parsedPrice.ok ? parsedPrice.error : null}
              />
              <Field label="Used for" hint="Which bills can be priced at this package">
                <Select value={usedFor} onChange={(_, d) => setUsedFor(d.value as PackageUse)}>
                  {PACKAGE_USES.map((use) => (
                    <option key={use} value={use}>
                      {PACKAGE_USE_LABEL[use]}
                    </option>
                  ))}
                </Select>
              </Field>
              {usedFor !== 'OutPatient' && (
                <Field
                  label="Length of stay (days)"
                  validationMessage={stayOk ? undefined : 'Between 1 and 365 days'}
                  validationState={stayOk ? 'none' : 'error'}
                >
                  <Input value={stay} onChange={(_, d) => setStay(d.value.replace(/\D/g, ''))} inputMode="numeric" />
                </Field>
              )}
              <Field label="Only for tariff" hint="Leave empty for every payer">
                <Select value={planId} onChange={(_, d) => setPlanId(d.value)}>
                  <option value="">Any tariff</option>
                  {(plans.data ?? []).map((plan) => (
                    <option key={plan.id} value={plan.id}>
                      {plan.name}
                    </option>
                  ))}
                </Select>
              </Field>
              <Field label="Status">
                <Select value={active ? 'active' : 'inactive'} onChange={(_, d) => setActive(d.value === 'active')}>
                  <option value="active">Active</option>
                  <option value="inactive">Inactive</option>
                </Select>
              </Field>
            </div>
            {billing ? (
              <Field label="Billed as" hint="The catalogue service (category Package) that carries the package price">
                <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                  <Text>{billing.label}</Text>
                  <Button size="small" onClick={() => setBilling(null)}>
                    Change
                  </Button>
                </div>
              </Field>
            ) : (
              <ServiceSearch
                label="Billed as (catalogue service of category Package)"
                category="Package"
                required
                onPick={(item) => setBilling({ id: item.id, label: `${item.name} (${item.code})` })}
              />
            )}
            <Field label="Description">
              <Input value={description} onChange={(_, d) => setDescription(d.value)} maxLength={1000} />
            </Field>

            <Text weight="semibold">What the package includes</Text>
            <Caption1 className={styles.muted}>
              Name a service or a whole category. Caps limit what the package absorbs; the rest is billed as excess.
            </Caption1>
            {inclusions.map((i) => (
              <div key={i.key} className={styles.inclusion}>
                {i.serviceItemId ? (
                  <Field label="Service">
                    <Text>{i.label}</Text>
                  </Field>
                ) : (
                  <Field label="Category">
                    <Select
                      value={i.category ?? ''}
                      onChange={(_, d) => update(i.key, { category: (d.value || null) as ServiceCategory | null })}
                    >
                      <option value="">Choose…</option>
                      {SERVICE_CATEGORIES.map((c) => (
                        <option key={c} value={c}>
                          {SERVICE_CATEGORY_LABEL[c]}
                        </option>
                      ))}
                    </Select>
                  </Field>
                )}
                <Field label="Max qty">
                  <Input
                    value={i.maxQuantity}
                    onChange={(_, d) => update(i.key, { maxQuantity: d.value })}
                    inputMode="decimal"
                  />
                </Field>
                <Field label="Max ₹">
                  <Input
                    value={i.maxAmount}
                    onChange={(_, d) => update(i.key, { maxAmount: d.value })}
                    inputMode="decimal"
                  />
                </Field>
                <Button
                  appearance="subtle"
                  icon={<DeleteRegular />}
                  aria-label="Remove inclusion"
                  onClick={() => setInclusions((list) => list.filter((x) => x.key !== i.key))}
                />
              </div>
            ))}
            <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap', alignItems: 'end' }}>
              <div style={{ flex: '1 1 260px' }}>
                <ServiceSearch
                  label="Add a service"
                  onPick={(item) =>
                    setInclusions((list) => [
                      ...list,
                      {
                        key: nextKey(),
                        serviceItemId: item.id,
                        label: `${item.name} (${item.code})`,
                        category: null,
                        maxQuantity: '',
                        maxAmount: '',
                        notes: '',
                      },
                    ])
                  }
                />
              </div>
              <Button
                icon={<AddRegular />}
                onClick={() =>
                  setInclusions((list) => [
                    ...list,
                    {
                      key: nextKey(),
                      serviceItemId: null,
                      label: '',
                      category: null,
                      maxQuantity: '',
                      maxAmount: '',
                      notes: '',
                    },
                  ])
                }
              >
                Add a category
              </Button>
            </div>
            {inclusionProblem && (
              <Caption1 style={{ color: tokens.colorPaletteRedForeground1 }}>{inclusionProblem}</Caption1>
            )}
            <ActionError error={save.error} />
          </div>
        </DrawerBody>
        <DrawerFooter>
          <Button
            type="submit"
            appearance="primary"
            disabled={!valid || save.isPending}
            icon={save.isPending ? <Spinner size="tiny" /> : undefined}
          >
            {p ? 'Save package' : 'Create package'}
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
