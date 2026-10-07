import {
  Button,
  Combobox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  makeStyles,
  Option,
  Spinner,
  Text,
  Textarea,
} from '@fluentui/react-components';
import { formatInr, useDebouncedValue, type PagedResult } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '@/lib/api';
import { usePermission } from '@/lib/permissions';
import { billingApi, billingKeys, type DiscountApprovalDto, type DiscountLimitDto } from './api';
import { useRefreshBilling } from './hooks';
import { formatPercent, parsePercent, parseRupees } from './money';
import { ActionError, AmountField, ReasonDialog } from './ui';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

/** Approve or reject a discount above the requester's limit; either way the decision carries a reason (kept for audit). */
export function DecideDiscountDialog({
  approval,
  decision,
  onClose,
  onDecided,
}: {
  approval: DiscountApprovalDto;
  decision: 'approve' | 'reject';
  onClose: () => void;
  onDecided?: () => void;
}) {
  const notify = useNotify();
  const refresh = useRefreshBilling();
  const queryClient = useQueryClient();
  const share =
    approval.discountBase > 0
      ? ` (${formatPercent((approval.requestedAmount / approval.discountBase) * 100)} of ${formatInr(approval.discountBase)})`
      : '';
  const decide = useMutation({
    mutationFn: (remarks: string) =>
      decision === 'approve'
        ? billingApi.approveDiscount(approval.id, remarks || null)
        : billingApi.rejectDiscount(approval.id, remarks),
    onSuccess: () => {
      notify.success(
        decision === 'approve' ? 'Discount approved' : 'Discount rejected',
        `${approval.patientName ?? 'Patient'} · ${formatInr(approval.requestedAmount)}`,
      );
      void queryClient.invalidateQueries({ queryKey: billingKeys.discounts() });
      void refresh();
      onDecided?.();
      onClose();
    },
  });
  return (
    <ReasonDialog
      open
      title={
        decision === 'approve'
          ? `Approve a discount of ${formatInr(approval.requestedAmount)}?`
          : `Reject the discount of ${formatInr(approval.requestedAmount)}?`
      }
      description={`${approval.patientName ?? 'Patient'} (${approval.uhid ?? '—'})${share}. Requested by ${approval.requestedByName ?? 'a cashier'}: “${approval.reason}”.`}
      label={decision === 'approve' ? 'Why is it approved?' : 'Why is it rejected?'}
      hint="The requester sees this and it is kept with the bill. At least 5 characters."
      placeholder={
        decision === 'approve'
          ? 'e.g. Senior citizen, as per the management circular'
          : 'e.g. Concession already given on the package'
      }
      confirmLabel={decision === 'approve' ? 'Approve discount' : 'Reject discount'}
      destructive={decision === 'reject'}
      busy={decide.isPending}
      error={decide.error}
      onConfirm={(text) => decide.mutate(text)}
      onCancel={onClose}
    />
  );
}

interface UserOption {
  id: string;
  displayName: string;
  userName: string;
}

/** Sets a user's discount limit (percentage of the discountable value, with an optional rupee cap). */
export function DiscountLimitDialog({ limit, onClose }: { limit: DiscountLimitDto | null; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canPickUsers = usePermission('admin.users.read');
  const [user, setUser] = useState<UserOption | null>(
    limit ? { id: limit.userId, displayName: limit.userName ?? 'User', userName: '' } : null,
  );
  const [term, setTerm] = useState('');
  const debounced = useDebouncedValue(term.trim(), 300);
  const [percent, setPercent] = useState(limit ? String(limit.maxPercent) : '');
  const [cap, setCap] = useState(
    limit?.maxAmount !== null && limit?.maxAmount !== undefined ? String(limit.maxAmount) : '',
  );
  const [notes, setNotes] = useState(limit?.notes ?? '');

  const users = useQuery({
    queryKey: ['billing', 'discount-limit-users', debounced],
    queryFn: ({ signal }) =>
      api.get<PagedResult<UserOption>>('/api/v1/admin/users', {
        query: { search: debounced, active: true, pageSize: 10 },
        signal,
      }),
    enabled: canPickUsers && !limit && debounced.length >= 2,
  });

  const parsedPercent = parsePercent(percent, { allowZero: true });
  const parsedCap = cap.trim() ? parseRupees(cap, { allowZero: true }) : null;
  const valid = user !== null && parsedPercent.ok && (parsedCap === null || parsedCap.ok);

  const save = useMutation({
    mutationFn: () =>
      billingApi.saveDiscountLimit({
        userId: user!.id,
        maxPercent: parsedPercent.ok ? parsedPercent.percent : 0,
        maxAmount: parsedCap?.ok ? parsedCap.rupees : null,
        notes: notes.trim() || null,
      }),
    onSuccess: () => {
      notify.success('Discount limit saved', user?.displayName);
      void queryClient.invalidateQueries({ queryKey: billingKeys.discounts() });
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !save.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !save.isPending) {
              save.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>
              {limit ? `Discount limit of ${limit.userName ?? 'user'}` : 'Add a discount limit'}
            </DialogTitle>
            <DialogContent className={styles.body}>
              {!limit &&
                (canPickUsers ? (
                  <Field label="User" required>
                    <Combobox
                      placeholder="Type a name or user name"
                      value={term || (user ? user.displayName : '')}
                      selectedOptions={user ? [user.id] : []}
                      onChange={(event) => setTerm(event.target.value)}
                      onOptionSelect={(_, data) => {
                        setUser(users.data?.items.find((u) => u.id === data.optionValue) ?? null);
                        setTerm('');
                      }}
                      autoFocus
                    >
                      {(users.data?.items ?? []).map((u) => (
                        <Option key={u.id} value={u.id} text={u.displayName}>
                          {u.displayName} ({u.userName})
                        </Option>
                      ))}
                    </Combobox>
                  </Field>
                ) : (
                  <Text>Ask an administrator to add limits for new users; you can change existing limits.</Text>
                ))}
              <Field
                label="Maximum discount without approval (%)"
                required
                hint="Of the discountable value of a bill"
                validationMessage={percent && !parsedPercent.ok ? parsedPercent.error : undefined}
                validationState={percent && !parsedPercent.ok ? 'error' : 'none'}
              >
                <Input
                  value={percent}
                  onChange={(_, d) => setPercent(d.value)}
                  inputMode="decimal"
                  contentAfter={<Text>%</Text>}
                  autoFocus={Boolean(limit)}
                />
              </Field>
              <AmountField
                label="Rupee cap per bill"
                hint="Optional. The lower of the two limits applies."
                value={cap}
                onChange={setCap}
                error={parsedCap && !parsedCap.ok ? parsedCap.error : null}
              />
              <Field label="Notes">
                <Textarea value={notes} onChange={(_, d) => setNotes(d.value)} maxLength={300} rows={2} />
              </Field>
              <ActionError error={save.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || save.isPending}
                // Keeps focus while busy (a disabled button drops it); Enter can still submit, which is ignored.
                disabledFocusable={save.isPending}
                icon={save.isPending ? <Spinner size="tiny" /> : undefined}
              >
                Save limit
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
