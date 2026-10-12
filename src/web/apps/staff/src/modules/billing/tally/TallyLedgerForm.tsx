import {
  Button,
  Caption1,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Subtitle2,
  Text,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular, SaveRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import {
  applyServerErrors,
  ErrorState,
  FormGrid,
  LoadingState,
  Section,
  SwitchField,
  TextField,
  useLeaveGuard,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useFieldArray, useForm, useWatch, type Control, type FieldPath } from 'react-hook-form';
import { tallyApi, tallyKeys, tallyQueries, type TallyLedgerMap } from '../tallyApi';
import { ActionError } from '../ui';
import {
  COUNTER_BUILT_IN,
  COUNTER_MODES,
  counterSalesLedger,
  ledgerFormSchema,
  MAX_LEDGER,
  MODE_FIELDS,
  toLedgerForm,
  toLedgerMap,
  type LedgerForm,
} from './ledgerForm';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '16px' },
  rows: { display: 'flex', flexDirection: 'column', gap: '8px' },
  department: {
    display: 'grid',
    gridTemplateColumns: 'minmax(90px, 160px) minmax(0, 1fr) auto',
    gap: '8px',
    alignItems: 'end',
  },
  muted: { color: tokens.colorNeutralForeground3 },
  footer: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
});

/** The ledger names the day book posts to; loads them, and saves them for the signed-in facility. */
export function TallyLedgerSettings({ canEdit }: { canEdit: boolean }) {
  const ledgers = useQuery(tallyQueries.ledgers());
  if (ledgers.isLoading) {
    return <LoadingState label="Loading the ledger names…" />;
  }
  if (ledgers.error || !ledgers.data) {
    return <ErrorState error={ledgers.error} onRetry={() => void ledgers.refetch()} />;
  }
  return <TallyLedgerForm key={ledgers.dataUpdatedAt} map={ledgers.data} canEdit={canEdit} />;
}

function Ledger({
  control,
  name,
  label,
  hint,
  placeholder,
  disabled,
}: {
  control: Control<LedgerForm>;
  name: FieldPath<LedgerForm>;
  label: string;
  hint?: string;
  placeholder?: string;
  disabled: boolean;
}) {
  return (
    <TextField
      control={control}
      name={name}
      label={label}
      hint={hint}
      placeholder={placeholder}
      maxLength={MAX_LEDGER}
      disabled={disabled}
    />
  );
}

/**
 * The ledger names form. Every name must be the ledger's name in Tally exactly (Tally ignores case). Payment modes
 * and the pharmacy's and canteen's own ledgers may stay empty: the day book then uses the name shown in grey.
 */
export function TallyLedgerForm({ map, canEdit }: { map: TallyLedgerMap; canEdit: boolean }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<LedgerForm>({ resolver: zodResolver(ledgerFormSchema), defaultValues: toLedgerForm(map) });
  const { control, handleSubmit, setError, formState } = form;
  const departments = useFieldArray({ control, name: 'departments' });
  const perRate = useWatch({ control, name: 'salesLedgerPerGstRate' });
  const pharmacySales = useWatch({ control, name: 'pharmacy.sales' }) || COUNTER_BUILT_IN.pharmacy.sales;
  const disabled = !canEdit;

  const save = useMutation({
    mutationFn: (values: LedgerForm) => tallyApi.saveLedgers(toLedgerMap(values)),
    onSuccess: (_, values) => {
      // What the form shows is saved now: leaving does not ask (SRS UX-08).
      form.reset(values);
      void queryClient.invalidateQueries({ queryKey: tallyKeys.ledgers });
      void queryClient.invalidateQueries({ queryKey: tallyKeys.all });
      notify.success(
        'Ledger names saved',
        'Day books posted from now on use them. Post a day again to update it in Tally.',
      );
    },
    onError: (error) => applyServerErrors(error, setError),
  });

  // Leaving the screen or closing the window with unsaved changes asks first (SRS UX-08).
  useLeaveGuard({ dirty: canEdit && formState.isDirty, busy: save.isPending });

  return (
    <form className={styles.form} onSubmit={handleSubmit((values) => save.mutate(values))} noValidate>
      <MessageBar intent="info" layout="multiline">
        <MessageBarBody>
          Enter each ledger exactly as it is named in your Tally company; Tally refuses a voucher whose ledger it cannot
          find. These names apply to this facility.
        </MessageBarBody>
      </MessageBar>

      <Section
        title="Income"
        description="Hospital bills post their income by department; others go to the default ledger."
      >
        <div className={styles.rows}>
          <Ledger control={control} name="defaultIncome" label="Default income" disabled={disabled} />
          <Subtitle2 as="h3">Income by department</Subtitle2>
          {departments.fields.length === 0 && (
            <Caption1 className={styles.muted}>No department has a ledger of its own.</Caption1>
          )}
          {departments.fields.map((field, index) => (
            <div key={field.id} className={styles.department}>
              <TextField
                control={control}
                name={`departments.${index}.code`}
                label="Department code"
                maxLength={20}
                disabled={disabled}
              />
              <TextField
                control={control}
                name={`departments.${index}.ledger`}
                label="Income ledger"
                maxLength={MAX_LEDGER}
                disabled={disabled}
              />
              <Button
                appearance="subtle"
                icon={<DeleteRegular />}
                aria-label={`Remove department ${index + 1}`}
                disabled={disabled}
                onClick={() => departments.remove(index)}
              />
            </div>
          ))}
          <div>
            <Button
              icon={<AddRegular />}
              disabled={disabled}
              onClick={() => departments.append({ code: '', ledger: '' })}
            >
              Add a department
            </Button>
          </div>
        </div>
      </Section>

      <Section title="Tax and adjustments">
        <FormGrid columns={2}>
          <Ledger control={control} name="outputCgst" label="Output CGST" disabled={disabled} />
          <Ledger control={control} name="outputSgst" label="Output SGST" disabled={disabled} />
          <Ledger control={control} name="discount" label="Discounts" disabled={disabled} />
          <Ledger control={control} name="roundOff" label="Round off" disabled={disabled} />
        </FormGrid>
      </Section>

      <Section title="Patients, insurers and money received">
        <FormGrid columns={2}>
          <Ledger control={control} name="patientDebtors" label="Patient receivables" disabled={disabled} />
          <Ledger control={control} name="patientAdvances" label="Patient advances and deposits" disabled={disabled} />
          <Ledger
            control={control}
            name="payerReceivables"
            label="Insurers and corporates"
            hint="Used when insurers do not have ledgers of their own."
            disabled={disabled}
          />
          <SwitchField
            control={control}
            name="ledgerPerPayer"
            label="A ledger for each insurer, named as in HC360"
            disabled={disabled}
          />
          {MODE_FIELDS.map((mode) => (
            <Ledger
              key={mode.key}
              control={control}
              name={`modes.${mode.key}`}
              label={mode.label}
              placeholder={mode.builtIn}
              disabled={disabled}
            />
          ))}
        </FormGrid>
      </Section>

      <Section title="Credit notes, write-offs and claims">
        <FormGrid columns={2}>
          <Ledger control={control} name="creditNotes" label="Credit notes" disabled={disabled} />
          <Ledger control={control} name="writeOffs" label="Write-offs" disabled={disabled} />
          <Ledger
            control={control}
            name="insuranceDisallowances"
            label="Insurance claim deductions"
            disabled={disabled}
          />
        </FormGrid>
      </Section>

      <Section title="Doctor fees">
        <FormGrid columns={2}>
          <Ledger control={control} name="doctorFeeExpense" label="Doctor fees (expense)" disabled={disabled} />
          <Ledger
            control={control}
            name="doctorPayablePrefix"
            label="Start of each doctor's payable ledger"
            hint="The doctor's name follows, for example “Doctor Fees Payable - Dr. Arjun Mehta”."
            disabled={disabled}
          />
          <Ledger control={control} name="tdsPayable" label="TDS payable" disabled={disabled} />
          <Ledger control={control} name="doctorRecoveries" label="Recoveries from doctors" disabled={disabled} />
          <Ledger control={control} name="payoutBank" label="Bank for doctor payouts" disabled={disabled} />
        </FormGrid>
      </Section>

      <Section title="Payment gateway settlements">
        <FormGrid columns={2}>
          <Ledger
            control={control}
            name="gatewaySettlementBank"
            label="Bank the gateway pays into"
            hint="Each settlement journal clears the payment gateway ledger above into this bank."
            disabled={disabled}
          />
          <Ledger control={control} name="gatewayCharges" label="Gateway charges" disabled={disabled} />
          <Ledger
            control={control}
            name="gatewayInputGst"
            label="GST on gateway, card and UPI charges"
            hint="Input GST on the payment gateway's and the card acquirer's charges."
            disabled={disabled}
          />
        </FormGrid>
      </Section>

      <Section title="Card and UPI settlements">
        <FormGrid columns={2}>
          <Ledger
            control={control}
            name="posSettlementBank"
            label="Bank the card acquirer pays into"
            hint="Each card and UPI settlement journal clears the Card and UPI ledgers above into this bank."
            disabled={disabled}
          />
          <Ledger control={control} name="posCharges" label="Card and UPI charges (MDR)" disabled={disabled} />
        </FormGrid>
      </Section>

      {(['pharmacy', 'canteen'] as const).map((counter) => (
        <Section
          key={counter}
          title={counter === 'pharmacy' ? 'Pharmacy counter' : 'Canteen'}
          description={
            counter === 'pharmacy'
              ? 'Counter sales, returns and balances paid later, summed for each day.'
              : 'Sales summed for each day; staff credit goes to the recoverable ledger.'
          }
        >
          <FormGrid columns={2}>
            <SwitchField control={control} name={`${counter}.include`} label="Post to Tally" disabled={disabled} />
            <span />
            <Ledger
              control={control}
              name={`${counter}.sales`}
              label="Sales"
              placeholder={COUNTER_BUILT_IN[counter].sales}
              disabled={disabled}
            />
            <Ledger
              control={control}
              name={`${counter}.onAccount`}
              label={counter === 'pharmacy' ? 'Customer balances' : 'Staff credit recoverable'}
              placeholder={COUNTER_BUILT_IN[counter].onAccount}
              disabled={disabled}
            />
            {COUNTER_MODES.map((mode) => (
              <Ledger
                key={mode.key}
                control={control}
                name={`${counter}.${mode.key.toLowerCase() as 'cash' | 'card' | 'upi'}`}
                label={`${mode.label} (own ledger)`}
                placeholder="The hospital's ledger"
                disabled={disabled}
              />
            ))}
          </FormGrid>
        </Section>
      ))}

      <Section title="GST on pharmacy and canteen sales">
        <SwitchField
          control={control}
          name="salesLedgerPerGstRate"
          label="One sales ledger for each GST rate"
          hint="Tally's GST reports need the rate on the sales ledger."
          disabled={disabled}
        />
        <Caption1 className={styles.muted}>
          Pharmacy sales at 12% post to “{counterSalesLedger(pharmacySales, perRate, 12)}”.
        </Caption1>
      </Section>

      {canEdit ? (
        <div className={styles.footer}>
          <Button
            type="submit"
            appearance="primary"
            icon={<SaveRegular />}
            disabled={save.isPending || !formState.isDirty}
          >
            Save ledger names
          </Button>
          <Button disabled={save.isPending || !formState.isDirty} onClick={() => form.reset()}>
            Undo changes
          </Button>
        </div>
      ) : (
        <Text className={styles.muted}>
          You can see the ledger names. Changing them needs the permission to set up Tally.
        </Text>
      )}
      <ActionError error={save.error} title="The ledger names were not saved" />
    </form>
  );
}
