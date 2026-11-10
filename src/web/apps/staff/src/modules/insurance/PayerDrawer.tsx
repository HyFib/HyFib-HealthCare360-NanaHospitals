import {
  Button,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  makeStyles,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  Spinner,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { normalizePhone } from '@hc360/core';
import {
  applyServerErrors,
  FormGrid,
  NumberField,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { askedForApproval } from '@/modules/admin/api';
import { PendingGrantNotice } from '@/modules/admin/PendingGrantNotice';
import { mastersQueries } from '@/modules/billing/mastersApi';
import { hasAtMostTwoDecimals } from '@/modules/billing/money';
import { ActionError } from '@/modules/billing/ui';
import { insuranceApi, insuranceKeys, type PayerDetail, type PayerTerms } from './api';
import { PAYER_CATEGORIES, PAYER_CATEGORY_LABEL } from './labels';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

const GSTIN = /^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$/;

const schema = z.object({
  code: z
    .string()
    .trim()
    .min(1, 'Enter a code.')
    .max(32)
    .regex(/^[A-Za-z0-9][A-Za-z0-9._-]*$/, 'Use letters, digits, dots, dashes or underscores.'),
  name: z.string().trim().min(1, 'Enter the name.').max(200),
  category: z.enum(PAYER_CATEGORIES),
  contactPerson: z.string().trim().max(200),
  phone: z
    .string()
    .trim()
    .refine((v) => !v || normalizePhone(v) !== null, 'Enter a valid phone number.'),
  email: z
    .string()
    .trim()
    .max(256)
    .refine((v) => !v || /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(v), 'Enter a valid email address.'),
  address: z.string().trim().max(500),
  gstin: z
    .string()
    .trim()
    .refine((v) => !v || GSTIN.test(v.toUpperCase()), 'Enter a valid 15-character GSTIN.'),
  tariffPlanId: z.string().nullable(),
  creditDays: z
    .number({ error: 'Enter the credit days.' })
    .int('Whole days only.')
    .min(0)
    .max(365, 'At most 365 days.'),
  defaultCoPayPercent: z
    .number({ error: 'Enter a percentage.' })
    .min(0)
    .max(100, 'At most 100 %.')
    .refine(hasAtMostTwoDecimals, 'Use at most 2 decimal places.'),
  portalUrl: z
    .string()
    .trim()
    .max(300)
    .refine((v) => !v || /^https:\/\/\S+$/i.test(v), "Enter the portal's https:// address."),
  isActive: z.boolean(),
  notes: z.string().max(1000),
  reason: z.string().max(500),
});

type PayerForm = z.infer<typeof schema>;

const blank = (value: string) => (value.trim() ? value.trim() : null);

/**
 * Insurer, TPA, corporate or government scheme: contact, tariff, credit period and default co-pay (INS-01/02). What its
 * patients pay (its kind, tariff plan and co-pay, and putting it in use) changes only when a second person who may approve
 * prices agrees; the form holds the terms that wait, so saving again asks for nothing twice.
 */
export function PayerDrawer({ payer, onClose }: { payer: PayerDetail | null; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const plans = useQuery(mastersQueries.tariffPlans());
  const terms = payer?.pendingTerms?.asked ?? payer;
  const form = useForm<PayerForm>({
    resolver: zodResolver(schema),
    defaultValues: {
      code: payer?.code ?? '',
      name: payer?.name ?? '',
      category: terms?.category ?? 'Insurer',
      contactPerson: payer?.contactPerson ?? '',
      phone: payer?.phone ?? '',
      email: payer?.email ?? '',
      address: payer?.address ?? '',
      gstin: payer?.gstin ?? '',
      tariffPlanId: terms?.tariffPlanId ?? null,
      creditDays: payer?.creditDays ?? 30,
      defaultCoPayPercent: terms?.defaultCoPayPercent ?? 0,
      portalUrl: payer?.portalUrl ?? '',
      isActive: terms?.isActive ?? true,
      notes: payer?.notes ?? '',
      reason: '',
    },
  });

  const save = useMutation({
    mutationFn: (values: PayerForm) => {
      const request = {
        code: values.code.trim().toUpperCase(),
        name: values.name.trim(),
        category: values.category,
        contactPerson: blank(values.contactPerson),
        phone: blank(values.phone),
        email: blank(values.email),
        address: blank(values.address),
        gstin: blank(values.gstin)?.toUpperCase() ?? null,
        tariffPlanId: values.tariffPlanId,
        creditDays: values.creditDays,
        defaultCoPayPercent: values.defaultCoPayPercent,
        portalUrl: blank(values.portalUrl),
        isActive: values.isActive,
        notes: blank(values.notes),
        version: payer?.version ?? null,
        reason: blank(values.reason),
      };
      return payer ? insuranceApi.updatePayer(payer.id, request) : insuranceApi.createPayer(request);
    },
    onSuccess: (result, values) => {
      // What a payer's patients pay changes only when two people agree: a save that changes it says it was asked for.
      const asked = askedForApproval(result);
      const created = payer ? null : (result as PayerDetail);
      if (asked) {
        notify.success(
          'Asked for approval',
          `${asked.summary}. Someone who may approve prices decides it; until then the payer's terms in use apply.`,
        );
      } else if (created?.pendingTerms) {
        notify.success(
          'Asked for approval',
          `${values.name.trim()} is added out of use (listed under Show inactive); its terms wait for someone who may approve prices.`,
        );
      } else {
        notify.success(payer ? 'Payer saved' : 'Payer added', values.name.trim());
      }
      void queryClient.invalidateQueries({ queryKey: insuranceKeys.payers });
      onClose();
    },
    onError: (error) => {
      applyServerErrors(error, form.setError);
    },
  });

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <OverlayDrawer open position="end" size="medium" onOpenChange={guard.onOpenChange}>
      <form onSubmit={form.handleSubmit((values) => save.mutate(values))} noValidate style={{ display: 'contents' }}>
        <DrawerHeader>
          <DrawerHeaderTitle
            action={
              <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={guard.requestClose} />
            }
          >
            {payer ? payer.name : 'Add a payer'}
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          <div className={styles.form}>
            {payer?.pendingTerms && (
              <PendingGrantNotice
                approvalId={payer.pendingTerms.approvalId}
                what={pendingTermsWhat(
                  payer,
                  payer.pendingTerms.asked,
                  (id) => plans.data?.find((p) => p.id === id)?.name,
                )}
                requestedBy={payer.pendingTerms.requestedBy}
                requestedAt={payer.pendingTerms.requestedAt}
                decides="Someone who may approve prices decides it; until then the payer's terms in use apply. The form shows them as asked."
              />
            )}
            <FormGrid columns={2} minColumnWidth={180}>
              <TextField
                control={form.control}
                name="code"
                label="Code"
                required
                disabled={Boolean(payer)}
                hint={payer ? 'Codes cannot be changed.' : 'e.g. STAR, MEDIASSIST'}
                autoFocus={!payer}
              />
              <SelectField
                control={form.control}
                name="category"
                label="Kind"
                required
                options={PAYER_CATEGORIES.map((c) => ({ value: c, label: PAYER_CATEGORY_LABEL[c] }))}
              />
            </FormGrid>
            <TextField control={form.control} name="name" label="Name" required autoFocus={Boolean(payer)} />
            <FormGrid columns={2} minColumnWidth={180}>
              <TextField control={form.control} name="contactPerson" label="Contact person" />
              <TextField control={form.control} name="phone" label="Phone" type="tel" />
              <TextField control={form.control} name="email" label="Email" type="email" />
              <TextField control={form.control} name="gstin" label="GSTIN" maxLength={15} />
            </FormGrid>
            <TextAreaField control={form.control} name="address" label="Address" rows={2} maxLength={500} />
            <FormGrid columns={2} minColumnWidth={180}>
              <SelectField
                control={form.control}
                name="tariffPlanId"
                label="Tariff"
                placeholder="Hospital default"
                options={(plans.data ?? []).map((p) => ({ value: p.id, label: p.name }))}
              />
              <NumberField
                control={form.control}
                name="creditDays"
                label="Credit period (days)"
                min={0}
                max={365}
                step={1}
                hint="Used for receivable ageing"
              />
              <NumberField
                control={form.control}
                name="defaultCoPayPercent"
                label="Default co-pay (%)"
                min={0}
                max={100}
                step={0.5}
              />
            </FormGrid>
            <TextField
              control={form.control}
              name="portalUrl"
              label="Claims portal address"
              type="url"
              hint="https:// address of the insurer or TPA portal"
            />
            <SwitchField control={form.control} name="isActive" label="Active" />
            <TextAreaField control={form.control} name="notes" label="Notes" rows={2} maxLength={1000} />
            <TextAreaField
              control={form.control}
              name="reason"
              label="Reason for a change to the terms, for the approver"
              rows={2}
              maxLength={500}
              hint={
                payer
                  ? 'The kind, tariff, co-pay and putting it in use wait for someone who may approve prices.'
                  : 'A new payer is added out of use; its terms wait for someone who may approve prices.'
              }
            />
            {form.formState.errors.root && (
              <MessageBar intent="error">
                <MessageBarBody>{form.formState.errors.root.message}</MessageBarBody>
              </MessageBar>
            )}
            <ActionError error={save.error} />
          </div>
        </DrawerBody>
        <DrawerFooter>
          <Button
            type="submit"
            appearance="primary"
            disabled={save.isPending}
            icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            {payer ? 'Save' : 'Add payer'}
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

/** What waits for approval in a payer's terms, against what applies now. */
function pendingTermsWhat(payer: PayerDetail, asked: PayerTerms, planName: (id: string) => string | undefined): string {
  const plan = (id: string | null) => (id ? (planName(id) ?? 'a tariff plan') : 'none');
  const parts: string[] = [];
  if (asked.isActive !== payer.isActive) {
    parts.push(asked.isActive ? 'put in use' : 'taken out of use');
  }
  if (asked.category !== payer.category) {
    parts.push(`${PAYER_CATEGORY_LABEL[asked.category]} (now ${PAYER_CATEGORY_LABEL[payer.category]})`);
  }
  if (asked.tariffPlanId !== payer.tariffPlanId) {
    parts.push(`tariff ${plan(asked.tariffPlanId)} (now ${plan(payer.tariffPlanId)})`);
  }
  if (asked.defaultCoPayPercent !== payer.defaultCoPayPercent) {
    parts.push(`co-pay ${asked.defaultCoPayPercent}% (now ${payer.defaultCoPayPercent}%)`);
  }
  return parts.length > 0 ? `Terms: ${parts.join('; ')}` : 'Terms';
}
