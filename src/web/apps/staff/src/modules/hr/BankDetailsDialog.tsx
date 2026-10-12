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
import { isApiError } from '@hc360/core';
import {
  applyServerErrors,
  CloseRequestScope,
  FormGrid,
  TextField,
  useCloseRequests,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { ignoreEscapeWhile } from '@/lib/dialogFocus';
import { hrApi, hrKeys, type EmployeeDetail } from './api';
import { isValidAccount, isValidIfsc, normalizeIdentifier } from './employeeModel';

const schema = z
  .object({
    bankName: z.string().trim().max(100),
    accountNumber: z.string().trim().refine(isValidAccount, 'Account numbers have 9 to 18 digits.'),
    confirmAccountNumber: z.string().trim(),
    ifsc: z.string().trim().refine(isValidIfsc, 'Enter a valid IFSC, for example SBIN0001234.'),
  })
  .refine((v) => v.accountNumber.replace(/[\s-]/g, '') === v.confirmAccountNumber.replace(/[\s-]/g, ''), {
    path: ['confirmAccountNumber'],
    message: 'The account numbers do not match.',
  });

type Values = z.infer<typeof schema>;

/**
 * Salary account change. The numbers are typed twice, never shown again after saving, stored encrypted, audited, and
 * the employee is always notified (a fraud alert if the change was not theirs).
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
  const [confirmShared, setConfirmShared] = useState(false);
  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: { bankName: employee.bankName ?? '', accountNumber: '', confirmAccountNumber: '', ifsc: '' },
  });

  const onValid = async (values: Values) => {
    setBusy(true);
    try {
      await hrApi.updateBankDetails(employee.id, {
        bankName: values.bankName.trim() || null,
        accountNumber: values.accountNumber.replace(/[\s-]/g, ''),
        ifsc: normalizeIdentifier(values.ifsc),
        confirmSharedAccount: confirmShared,
        version: employee.version,
      });
      notify.success('Bank details changed', `${employee.fullName} has been notified of the change.`);
      void queryClient.invalidateQueries({ queryKey: hrKeys.employees });
      onClose();
    } catch (error) {
      if (isApiError(error) && error.code === 'employee.bank_account_shared') {
        setShared(error.userMessage);
      } else if (isApiError(error) && error.isConflict) {
        notify.error(error, 'The employee record was changed by someone else');
        void queryClient.invalidateQueries({ queryKey: hrKeys.employees });
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not change the bank details');
      }
    } finally {
      setBusy(false);
    }
  };

  // A shared account is changed only once someone ticks that it is meant to be shared.
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
        <DialogTitle>Change salary bank account</DialogTitle>
        <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          <MessageBar intent="info" layout="multiline">
            <MessageBarBody>
              Current account: {employee.bankAccountMasked ?? 'none'}{' '}
              {employee.bankIfscMasked ? `(${employee.bankIfscMasked})` : ''}. The change is recorded in the audit trail
              and {employee.fullName} is notified.
            </MessageBarBody>
          </MessageBar>
          <FormGrid columns={2} minColumnWidth={220}>
            <TextField control={form.control} name="bankName" label="Bank name" autoComplete="off" />
            <TextField
              control={form.control}
              name="ifsc"
              label="IFSC"
              required
              autoComplete="off"
              maxLength={11}
              hint="11 characters, e.g. SBIN0001234"
            />
            <TextField
              control={form.control}
              name="accountNumber"
              label="Account number"
              required
              autoComplete="off"
              type="password"
              autoFocus
            />
            <TextField
              control={form.control}
              name="confirmAccountNumber"
              label="Type the account number again"
              required
              autoComplete="off"
              type="password"
            />
          </FormGrid>
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
            Change account
          </Button>
        </DialogActions>
      </DialogBody>
      {guard.dialogs}
    </form>
  );
}
