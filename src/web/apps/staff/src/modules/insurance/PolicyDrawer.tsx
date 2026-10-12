import {
  Button,
  Caption1,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  makeStyles,
  OverlayDrawer,
  Spinner,
  tokens,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateOnly, todayIso } from '@hc360/core';
import {
  applyServerErrors,
  DateField,
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
import { hasAtMostTwoDecimals } from '@/modules/billing/money';
import { ActionError, ReasonDialog } from '@/modules/billing/ui';
import { insuranceApi, insuranceKeys, insuranceQueries, type PolicyDto } from './api';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

function schemaFor(isNew: boolean) {
  return z
    .object({
      payerId: z.string({ error: 'Choose the insurer or scheme.' }).min(1, 'Choose the insurer or scheme.'),
      tpaId: z.string().nullable(),
      policyNumber: z
        .string()
        .trim()
        .max(64)
        .refine((v) => /^[A-Za-z0-9 /-]*$/.test(v), 'Use letters, digits, spaces, dashes or slashes.')
        .refine((v) => !isNew || v.length > 0, 'Enter the policy or card number.'),
      memberId: z.string().trim().max(64),
      policyHolderName: z.string().trim().max(200),
      relationToHolder: z.string().trim().max(64),
      validFrom: z.string({ error: 'Enter the start date.' }).min(1, 'Enter the start date.'),
      validTo: z.string({ error: 'Enter the end date.' }).min(1, 'Enter the end date.'),
      sumInsured: z
        .number({ error: 'Enter the sum insured.' })
        .min(0)
        .max(100_000_000, 'The amount is too large.')
        .refine(hasAtMostTwoDecimals, 'Use at most 2 decimal places.'),
      coPayPercent: z
        .number({ error: 'Enter a percentage.' })
        .min(0)
        .max(100, 'At most 100 %.')
        .refine(hasAtMostTwoDecimals, 'Use at most 2 decimal places.'),
      isActive: z.boolean(),
      notes: z.string().max(1000),
    })
    .refine((v) => !v.validFrom || !v.validTo || v.validTo >= v.validFrom, {
      path: ['validTo'],
      message: 'The policy must end after it starts.',
    });
}

type PolicyForm = z.infer<ReturnType<typeof schemaFor>>;

const blank = (value: string) => (value.trim() ? value.trim() : null);

/** Record or update a patient's insurance policy (INS-03). The number is stored encrypted and shown masked. */
export function PolicyDrawer({
  patientId,
  policy,
  onClose,
}: {
  patientId: string;
  policy: PolicyDto | null;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const payers = useQuery(insuranceQueries.payers());
  const form = useForm<PolicyForm>({
    resolver: zodResolver(schemaFor(policy === null)),
    defaultValues: {
      payerId: policy?.payerId ?? '',
      tpaId: policy?.tpaId ?? null,
      policyNumber: '',
      memberId: policy?.memberId ?? '',
      policyHolderName: policy?.policyHolderName ?? '',
      relationToHolder: policy?.relationToHolder ?? '',
      validFrom: policy?.validFrom ?? todayIso(),
      validTo: policy?.validTo ?? todayIso(364),
      sumInsured: policy?.sumInsured ?? 0,
      coPayPercent: policy?.coPayPercent ?? 0,
      isActive: policy?.isActive ?? true,
      notes: policy?.notes ?? '',
    },
  });

  const save = useMutation<void, unknown, PolicyForm>({
    mutationFn: async (values) => {
      const request = {
        patientId,
        payerId: values.payerId,
        tpaId: values.tpaId,
        policyNumber: blank(values.policyNumber),
        memberId: blank(values.memberId),
        policyHolderName: blank(values.policyHolderName),
        relationToHolder: blank(values.relationToHolder),
        validFrom: values.validFrom,
        validTo: values.validTo,
        sumInsured: values.sumInsured,
        coPayPercent: values.coPayPercent,
        isActive: values.isActive,
        cardFileId: policy?.cardFileId ?? null,
        notes: blank(values.notes),
        version: policy?.version ?? null,
      };
      if (policy) {
        await insuranceApi.updatePolicy(policy.id, request);
      } else {
        await insuranceApi.createPolicy(request);
      }
    },
    onSuccess: () => {
      notify.success(policy ? 'Policy saved' : 'Policy recorded');
      void queryClient.invalidateQueries({ queryKey: insuranceKeys.policies });
      onClose();
    },
    onError: (error) => {
      applyServerErrors(error, form.setError);
    },
  });

  const insurers = (payers.data ?? []).filter((p) => p.category !== 'Tpa').map((p) => ({ value: p.id, label: p.name }));
  const tpas = (payers.data ?? []).filter((p) => p.category === 'Tpa').map((p) => ({ value: p.id, label: p.name }));

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <OverlayDrawer open position="end" size="medium" onOpenChange={guard.onOpenChange}>
      <form
        onSubmit={form.handleSubmit((values) => !save.isPending && save.mutate(values))}
        noValidate
        style={{ display: 'contents' }}
      >
        <DrawerHeader>
          <DrawerHeaderTitle
            action={
              <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={guard.requestClose} />
            }
          >
            {policy ? `Policy ${policy.policyNumberMasked}` : 'Record a policy'}
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          <div className={styles.form}>
            <SelectField
              control={form.control}
              name="payerId"
              label="Insurer or scheme"
              required
              placeholder="Choose…"
              options={insurers}
            />
            <SelectField
              control={form.control}
              name="tpaId"
              label="TPA"
              placeholder="No TPA (insurer settles directly)"
              options={tpas}
            />
            <TextField
              control={form.control}
              name="policyNumber"
              label="Policy or card number"
              required={!policy}
              hint={
                policy
                  ? `Stored as ${policy.policyNumberMasked}. Leave empty to keep it; type a new number only to correct it.`
                  : 'Stored encrypted; lists show it masked.'
              }
              autoComplete="off"
              autoFocus
            />
            <FormGrid columns={2} minColumnWidth={180}>
              <TextField control={form.control} name="memberId" label="Member id" />
              <TextField control={form.control} name="policyHolderName" label="Policy holder" />
              <TextField
                control={form.control}
                name="relationToHolder"
                label="Relation to holder"
                hint="e.g. Self, Spouse, Son"
              />
              <DateField control={form.control} name="validFrom" label="Valid from" required />
              <DateField control={form.control} name="validTo" label="Valid to" required />
              <NumberField control={form.control} name="sumInsured" label="Sum insured (₹)" min={0} step={1000} />
              <NumberField control={form.control} name="coPayPercent" label="Co-pay (%)" min={0} max={100} step={0.5} />
            </FormGrid>
            <SwitchField control={form.control} name="isActive" label="Active" />
            <TextAreaField control={form.control} name="notes" label="Notes" rows={2} maxLength={1000} />
            <Caption1 className={styles.muted}>
              Scan the insurance card into the patient&apos;s documents on the patient profile.
            </Caption1>
            <ActionError error={save.error} />
          </div>
        </DrawerBody>
        <DrawerFooter>
          <Button
            type="submit"
            appearance="primary"
            disabled={save.isPending}
            // Keeps focus while saving (a disabled button drops it); Enter can still submit, which is ignored.
            disabledFocusable={save.isPending}
            icon={save.isPending ? <Spinner size="tiny" /> : undefined}
          >
            {policy ? 'Save policy' : 'Record policy'}
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

/** Records that the policy was confirmed with the insurer/TPA (portal check or call), with optional notes. */
export function VerifyPolicyDialog({ policy, onClose }: { policy: PolicyDto; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const verify = useMutation({
    mutationFn: (notes: string) => insuranceApi.verifyPolicy(policy.id, notes || null),
    onSuccess: () => {
      notify.success('Policy marked as verified');
      void queryClient.invalidateQueries({ queryKey: insuranceKeys.policies });
      onClose();
    },
  });
  return (
    <ReasonDialog
      open
      title={`Verified policy ${policy.policyNumberMasked}?`}
      description={`${policy.payerName ?? 'Insurer'} · valid ${formatDateOnly(policy.validFrom)} to ${formatDateOnly(policy.validTo)}. Confirm only after checking it on the insurer's portal or by phone.`}
      label="How was it verified?"
      optional
      placeholder="e.g. Checked on the TPA portal, active, room rent capped at ₹5,000"
      confirmLabel="Mark as verified"
      busy={verify.isPending}
      error={verify.error}
      onConfirm={(notes) => verify.mutate(notes)}
      onCancel={onClose}
    />
  );
}
