import {
  Button,
  Checkbox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
} from '@fluentui/react-components';
import { zodResolver } from '@hookform/resolvers/zod';
import { humanize, isApiError } from '@hc360/core';
import {
  applyServerErrors,
  CloseRequestScope,
  FormGrid,
  SelectField,
  TextAreaField,
  TextField,
  useCloseRequests,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { ignoreEscapeWhile } from '@/lib/dialogFocus';
import { hrApi, hrKeys, type EmployeeDetail } from './api';
import { isValidAccount, isValidIfsc, normalizeIdentifier } from './employeeModel';
import { PAYMENT_MODES } from './labels';
import { payRefusal } from './makerChecker';

const schema = z
  .object({
    paymentMode: z.enum(PAYMENT_MODES),
    bankName: z.string().trim().max(100),
    accountNumber: z.string().trim(),
    confirmAccountNumber: z.string().trim(),
    ifsc: z.string().trim(),
    reason: z.string().trim().min(1, 'Say why: the person who approves it reads it.').max(500),
    hasAccount: z.boolean(),
  })
  .superRefine((v, ctx) => {
    // A new account is typed twice with its IFSC; none keeps the account on record (switching to cash or back to it).
    const typed = v.accountNumber !== '' || v.ifsc !== '';
    if (typed || (v.paymentMode === 'BankTransfer' && !v.hasAccount)) {
      if (!isValidAccount(v.accountNumber)) {
        ctx.addIssue({ code: 'custom', path: ['accountNumber'], message: 'Account numbers have 9 to 18 digits.' });
      }
      if (!isValidIfsc(v.ifsc)) {
        ctx.addIssue({ code: 'custom', path: ['ifsc'], message: 'Enter a valid IFSC, for example SBIN0001234.' });
      }
      if (v.accountNumber.replace(/[\s-]/g, '') !== v.confirmAccountNumber.replace(/[\s-]/g, '')) {
        ctx.addIssue({ code: 'custom', path: ['confirmAccountNumber'], message: 'The account numbers do not match.' });
      }
    }
  });

type Values = z.infer<typeof schema>;

/**
 * Salary payment details: the payment mode, and for a bank transfer the account (typed twice, never shown again, stored
 * encrypted). The change is asked for with a reason and waits for someone else who approves payroll, never the one who
 * asked and never the employee (maker-checker slice 11); the details in force stay in force until then, and the employee
 * is told once it changes.
 */
export function BankDetailsDialog({
  open,
  onClose,
  employee,
}: {
  open: boolean;
  onClose: () => void;
  employee: EmployeeDetail;
}) {
  const closing = useCloseRequests({ onClose });
  return (
    <Dialog open={open} onOpenChange={closing.onOpenChange}>
      <DialogSurface style={{ maxWidth: 620 }}>
        {open && (
          <CloseRequestScope value={closing}>
            <Body onClose={onClose} employee={employee} />
          </CloseRequestScope>
        )}
      </DialogSurface>
    </Dialog>
  );
}

function Body({ onClose, employee }: { onClose: () => void; employee: EmployeeDetail }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [busy, setBusy] = useState(false);
  const [shared, setShared] = useState<string | null>(null);
  const [refusal, setRefusal] = useState<string | null>(null);
  const [confirmShared, setConfirmShared] = useState(false);
  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: {
      paymentMode: employee.paymentMode,
      bankName: employee.bankName ?? '',
      accountNumber: '',
      confirmAccountNumber: '',
      ifsc: '',
      reason: '',
      hasAccount: employee.bankAccountMasked !== null,
    },
  });
  const paymentMode = useWatch({ control: form.control, name: 'paymentMode' });
  const pending = employee.pendingPaymentDetails;

  const onValid = async (values: Values) => {
    setBusy(true);
    setRefusal(null);
    try {
      const account = values.accountNumber.replace(/[\s-]/g, '');
      await hrApi.updateBankDetails(employee.id, {
        paymentMode: values.paymentMode,
        bankName: values.bankName.trim() || null,
        accountNumber: account || null,
        ifsc: account ? normalizeIdentifier(values.ifsc) : null,
        confirmSharedAccount: confirmShared,
        reason: values.reason.trim(),
        version: employee.version,
      });
      notify.success(
        'Asked for approval',
        `Someone else who approves payroll decides it. ${employee.fullName} is paid as before until then.`,
      );
      void queryClient.invalidateQueries({ queryKey: hrKeys.employees });
      onClose();
    } catch (error) {
      const words = isApiError(error) ? payRefusal(error.code) : null;
      if (isApiError(error) && error.code === 'employee.bank_account_shared') {
        setShared(error.userMessage);
      } else if (words) {
        setRefusal(words);
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not ask for the change');
      }
    } finally {
      setBusy(false);
    }
  };

  // A shared account is asked for only once someone ticks that it is meant to be shared.
  const blocked = busy || (shared !== null && !confirmShared);

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy, onClose });

  return (
    <form
      onSubmit={form.handleSubmit((values) => !blocked && onValid(values))}
      onKeyDown={ignoreEscapeWhile(busy)}
      noValidate
      autoComplete="off"
    >
      <DialogBody>
        <DialogTitle>Change salary payment details</DialogTitle>
        <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          <MessageBar intent="info" layout="multiline">
            <MessageBarBody>
              <MessageBarTitle>In force</MessageBarTitle>
              {humanize(employee.paymentMode)}
              {employee.bankAccountMasked ? `, account ${employee.bankAccountMasked}` : ''}{' '}
              {employee.bankIfscMasked ? `(${employee.bankIfscMasked})` : ''}. The change waits for someone else who
              approves payroll, never you and never {employee.fullName}; until then the salary is paid as it is now.
            </MessageBarBody>
          </MessageBar>
          {pending && (
            <MessageBar intent="warning" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>Already waiting for approval</MessageBarTitle>
                {pending.summary}, asked for by {pending.requestedBy}. Withdraw it from Approvals before asking for
                another change.
              </MessageBarBody>
            </MessageBar>
          )}
          <FormGrid columns={2} minColumnWidth={220}>
            <SelectField
              control={form.control}
              name="paymentMode"
              label="Salary paid by"
              required
              options={PAYMENT_MODES.map((m) => ({ value: m, label: humanize(m) }))}
            />
            <TextField control={form.control} name="bankName" label="Bank name" autoComplete="off" />
            <TextField
              control={form.control}
              name="accountNumber"
              label={
                paymentMode === 'BankTransfer' && !employee.bankAccountMasked ? 'Account number' : 'New account number'
              }
              required={paymentMode === 'BankTransfer' && !employee.bankAccountMasked}
              hint={employee.bankAccountMasked ? 'Leave blank to keep the account on record.' : undefined}
              autoComplete="off"
              type="password"
              autoFocus
            />
            <TextField
              control={form.control}
              name="confirmAccountNumber"
              label="Type the account number again"
              autoComplete="off"
              type="password"
            />
            <TextField
              control={form.control}
              name="ifsc"
              label="IFSC"
              autoComplete="off"
              maxLength={11}
              hint="11 characters, e.g. SBIN0001234"
            />
          </FormGrid>
          <TextAreaField control={form.control} name="reason" label="Reason for the change" required maxLength={500} />
          {shared && (
            <MessageBar intent="warning" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>Another employee is paid into this account</MessageBarTitle>
                {shared} Check with the employee before continuing: shared salary accounts are a common sign of payroll
                fraud.
                <div style={{ marginTop: 8 }}>
                  <Checkbox
                    checked={confirmShared}
                    onChange={(_, data) => setConfirmShared(Boolean(data.checked))}
                    label="I have checked; this is a genuine joint account"
                  />
                </div>
              </MessageBarBody>
            </MessageBar>
          )}
          {refusal && (
            <MessageBar intent="error" layout="multiline" politeness="assertive">
              <MessageBarBody>
                <MessageBarTitle>Not asked for</MessageBarTitle>
                {refusal}
              </MessageBarBody>
            </MessageBar>
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={guard.requestClose} disabled={busy}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={blocked}
            // Keeps focus while saving and when a shared account needs confirming: a disabled button drops it. Enter
            // can still submit, which is ignored while blocked.
            disabledFocusable={blocked}
            icon={busy ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            Ask for approval
          </Button>
        </DialogActions>
      </DialogBody>
      {guard.dialogs}
    </form>
  );
}
