import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  makeStyles,
  Select,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular } from '@fluentui/react-icons';
import { formatInr, todayIso } from '@hc360/core';
import { useNotify, useUnsavedChanges, useValuesChanged } from '@hc360/ui';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { useDepartments, usePractitioners } from '@/lib/lookups';
import { billingApi, quotePrice, type ServiceItemDto } from './api';
import { useCanManageCharges, useRefreshBilling } from './hooks';
import { PRICE_SOURCE_LABEL } from './labels';
import { amountInputText, formatPercent, fromPaise, parseQuantity, parseRupees, roundDiv, toPaise } from './money';
import { ServiceSearchField } from './ServicePicker';
import { AmountField, ReasonDialog } from './ui';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  row: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** Where a manual charge is posted: the patient and their visit (OP) or admission (IP). */
export interface ChargeTarget {
  patientId: string;
  visitId: string | null;
  admissionId: string | null;
  /** What the charge is posted against, in words ("Visit NHOV260000123", "Admission NHIP260000045"). */
  label: string;
}

/** Charges for the date picked: today is "now" (the server's clock); an earlier day is charged at noon IST. */
function serviceDateOf(date: string): string | null {
  return date && date !== todayIso() ? `${date}T12:00:00+05:30` : null;
}

/**
 * Manual charge from the billing desk (MB-01, billing.charges.manage): a service given but not ordered electronically,
 * posted against the patient's visit or admission. It waits unbilled like any other service until the bill takes it.
 */
export function AddChargeDialog({
  target,
  open,
  onClose,
}: {
  target: ChargeTarget;
  open: boolean;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const refresh = useRefreshBilling();
  const [item, setItem] = useState<ServiceItemDto | null>(null);
  const [quantity, setQuantity] = useState('1');
  const [price, setPrice] = useState('');
  const [description, setDescription] = useState('');
  const [departmentId, setDepartmentId] = useState('');
  const [doctorId, setDoctorId] = useState('');
  const [date, setDate] = useState(todayIso());
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setItem(null);
      setQuantity('1');
      setPrice('');
      setDescription('');
      setDepartmentId('');
      setDoctorId('');
      setDate(todayIso());
    }
  }

  const quote = useQuery({
    queryKey: ['billing', 'quote', item?.id ?? '', target.visitId ?? '', target.admissionId ?? '', date],
    queryFn: () =>
      quotePrice({
        serviceItemId: item!.id,
        visitId: target.visitId,
        admissionId: target.admissionId,
        date: date || undefined,
      }),
    enabled: open && item !== null,
    staleTime: 60_000,
  });
  const departments = useDepartments();
  const doctors = usePractitioners((departmentId || item?.departmentId) ?? null);

  const qty = parseQuantity(quantity);
  const editable = quote.data?.isPriceEditable ?? item?.isPriceEditable ?? false;
  const override = editable && price.trim() ? parseRupees(price, { allowZero: true }) : null;
  const unitPrice = override?.ok ? override.rupees : (quote.data?.unitPrice ?? null);
  const lineAmount =
    qty !== null && unitPrice !== null
      ? fromPaise(roundDiv(toPaise(unitPrice) * BigInt(Math.round(qty * 1000)), 1000n))
      : null;
  const futureDate = date !== '' && date > todayIso();
  const valid = item !== null && qty !== null && (override === null || override.ok) && !futureDate;

  const post = useMutation({
    mutationFn: () =>
      billingApi.postCharge({
        patientId: target.patientId,
        visitId: target.visitId,
        admissionId: target.admissionId,
        serviceItemId: item!.id,
        quantity: qty ?? 1,
        unitPrice: override?.ok ? override.rupees : null,
        description: description.trim() || null,
        practitionerId: doctorId || null,
        departmentId: departmentId || null,
        serviceDate: serviceDateOf(date),
      }),
    onSuccess: (charge) => {
      notify.success('Charge added', `${charge.description} · ${formatInr(charge.netAmount)}`);
      void refresh();
      onClose();
    },
    onError: (error) => notify.error(error, 'The charge was not added'),
  });

  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ item: item?.id, quantity, price, description, departmentId, doctorId, date }, open),
    busy: post.isPending,
    open,
    onClose,
  });

  return (
    <Dialog open={open} onOpenChange={guard.onOpenChange} modalType="alert">
      <DialogSurface style={{ maxWidth: 620 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !post.isPending) {
              post.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Add a charge</DialogTitle>
            <DialogContent className={styles.body}>
              <Text>{target.label}. The service waits unbilled until the bill takes it; nothing is collected now.</Text>
              <ServiceSearchField
                label="Service"
                required
                value={item}
                enabled={open}
                onChange={(picked) => {
                  setItem(picked);
                  setPrice(picked?.isPriceEditable ? amountInputText(picked.basePrice) : '');
                  setDoctorId('');
                }}
              />
              <div className={styles.row}>
                <Field
                  label="Quantity"
                  required
                  validationMessage={quantity && qty === null ? 'More than 0, at most 3 decimals' : undefined}
                  validationState={quantity && qty === null ? 'error' : 'none'}
                >
                  <Input value={quantity} onChange={(_, data) => setQuantity(data.value)} inputMode="decimal" />
                </Field>
                {editable && (
                  <AmountField
                    label="Price each"
                    value={price}
                    onChange={setPrice}
                    error={override && !override.ok ? override.error : null}
                    hint="This service's price can be changed"
                  />
                )}
                <Field
                  label="Service date"
                  validationMessage={futureDate ? 'Services cannot be charged for a future date' : undefined}
                  validationState={futureDate ? 'error' : 'none'}
                >
                  <Input type="date" value={date} max={todayIso()} onChange={(_, data) => setDate(data.value)} />
                </Field>
              </div>
              <div className={styles.row}>
                <Field label="Department">
                  <Select
                    value={departmentId}
                    onChange={(_, data) => {
                      setDepartmentId(data.value);
                      setDoctorId('');
                    }}
                  >
                    <option value="">The service's own</option>
                    {departments.data?.map((department) => (
                      <option key={department.id} value={department.id}>
                        {department.name}
                      </option>
                    ))}
                  </Select>
                </Field>
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
              </div>
              <Field label="Description on the bill" hint="Leave empty to print the service name">
                <Input
                  value={description}
                  onChange={(_, data) => setDescription(data.value)}
                  maxLength={300}
                  placeholder={item?.name}
                />
              </Field>
              {item && (
                <Caption1 className={styles.muted} aria-live="polite">
                  {quote.isLoading
                    ? 'Checking the price for this patient…'
                    : quote.data
                      ? `${PRICE_SOURCE_LABEL[quote.data.source]}${quote.data.tariffPlanName ? ` (${quote.data.tariffPlanName})` : ''}: ${formatInr(quote.data.unitPrice)} each${
                          quote.data.taxRate > 0 ? ` + GST ${formatPercent(quote.data.taxRate)}` : ''
                        }${lineAmount !== null ? ` · ${formatInr(lineAmount)} in all` : ''}`
                      : 'The price could not be checked. The server prices the charge from the tariff.'}
                </Caption1>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={post.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || post.isPending}
                // Keeps focus while busy (a disabled button drops it); Enter can still submit, which is ignored.
                disabledFocusable={post.isPending}
                icon={post.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Add charge
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}

/** "Add charge" with its dialog; nothing for a user without billing.charges.manage. */
export function AddChargeButton({
  target,
  appearance = 'secondary',
}: {
  target: ChargeTarget;
  appearance?: 'secondary' | 'subtle' | 'transparent';
}) {
  const allowed = useCanManageCharges();
  const [open, setOpen] = useState(false);
  if (!allowed) {
    return null;
  }
  return (
    <>
      <Button appearance={appearance} icon={<AddRegular />} onClick={() => setOpen(true)}>
        Add charge
      </Button>
      {open && <AddChargeDialog target={target} open onClose={() => setOpen(false)} />}
    </>
  );
}

/** An unbilled charge to cancel, as the screen shows it. */
export interface CancellableCharge {
  id: string;
  description: string;
  netAmount: number;
  /** The module that posted it (`billing.manual` for the billing desk); unknown on the IP running bill. */
  sourceModule?: string | null;
  /** Reserved by a draft bill: cancelling takes it off that draft. */
  onDraft?: boolean;
}

/**
 * Cancels an unbilled charge (billing.charges.manage) after asking why. The server takes it off a draft bill first and
 * refuses one already on a finalized bill. The order, dispense or admission it came from is not changed.
 */
export function CancelChargeDialog({ charge, onClose }: { charge: CancellableCharge | null; onClose: () => void }) {
  const notify = useNotify();
  const refresh = useRefreshBilling();
  const cancel = useMutation({
    mutationFn: ({ id, reason }: { id: string; reason: string }) => billingApi.cancelCharge(id, reason),
    onSuccess: () => {
      notify.success('Charge cancelled', charge ? `${charge.description} will not be billed.` : undefined);
      void refresh();
      onClose();
    },
    onError: (error) => notify.error(error, 'The charge was not cancelled'),
  });
  const manual = charge?.sourceModule === 'billing.manual';

  return (
    <ReasonDialog
      open={charge !== null}
      title={`Cancel ${charge?.description ?? 'this charge'}?`}
      description={
        charge && (
          <>
            {formatInr(charge.netAmount)} is no longer billed to the patient.
            {charge.onDraft ? ' It is also taken off the draft bill, which is worked out again.' : ''}
            {manual
              ? ''
              : ' If it came from an order or a dispense, that is not cancelled here; do it in its own module if the service was not given.'}
          </>
        )
      }
      label="Why is it cancelled?"
      placeholder="e.g. Charged twice, service not given"
      confirmLabel="Cancel charge"
      destructive
      busy={cancel.isPending}
      onConfirm={(reason) => charge && cancel.mutate({ id: charge.id, reason })}
      onCancel={() => {
        cancel.reset();
        onClose();
      }}
    />
  );
}
