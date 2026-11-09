import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { formatDateTime, type PayerType } from '@hc360/core';
import { useNotify, useUnsavedChanges, useValuesChanged } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { insuranceQueries } from '@/modules/insurance/api';
import { useRefreshBilling } from './hooks';
import { PAYER_TYPE_LABEL, PAYER_TYPES } from './labels';
import { ActionError } from './ui';
import { visitPayerApi, visitPayerKeys, visitPayerQueries } from './visitPayerApi';

const THIRD_PARTY: readonly PayerType[] = ['Insurance', 'Corporate', 'GovernmentScheme'];

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/**
 * Who pays for an out-patient visit, changed after registration while its bill is not final: its unbilled services are
 * priced again on the new payer's plan. Another kind of payer, or a plan that prices any service lower, waits for someone
 * who approves discounts.
 */
export function VisitPayerDialog({ visitId, onClose }: { visitId: string; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const refresh = useRefreshBilling();
  const view = useQuery(visitPayerQueries.view(visitId));
  const [payerType, setPayerType] = useState<PayerType | null>(null);
  const [payerId, setPayerId] = useState<string | null>(null);
  const [reason, setReason] = useState('');
  const currentType = payerType ?? view.data?.terms.payerType ?? 'Self';
  const currentPayer = payerId ?? view.data?.terms.payerId ?? '';
  const needsPayer = THIRD_PARTY.includes(currentType);
  const payers = useQuery({ ...insuranceQueries.payers(), enabled: needsPayer });
  const payerOptions = (payers.data ?? []).filter((p) =>
    currentType === 'Insurance'
      ? p.category === 'Insurer' || p.category === 'Tpa'
      : currentType === 'Corporate'
        ? p.category === 'Corporate'
        : p.category === 'GovernmentScheme',
  );
  const unchanged =
    view.data !== undefined &&
    currentType === view.data.terms.payerType &&
    (needsPayer ? currentPayer || null : null) === view.data.terms.payerId;
  const valid = view.data !== undefined && !view.data.pending && !unchanged && (!needsPayer || currentPayer !== '');

  const save = useMutation({
    mutationFn: () =>
      visitPayerApi.change(visitId, {
        payerType: currentType,
        payerId: needsPayer ? currentPayer : null,
        reason: reason.trim() || null,
        version: view.data!.version,
      }),
    onSuccess: (outcome) => {
      if (outcome.kind === 'asked') {
        notify.info('Asked for approval', 'The payer changes once someone who approves discounts approves it.');
      } else {
        notify.success('Payer changed', outcome.changed);
      }
      void queryClient.invalidateQueries({ queryKey: visitPayerKeys.all });
      refresh();
      onClose();
    },
  });

  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ payerType, payerId, reason }),
    busy: save.isPending,
    onClose,
  });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 560, width: '100%' }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !save.isPending) {
              save.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Change the visit's payer</DialogTitle>
            <DialogContent className={styles.body}>
              {view.isLoading && <Spinner label="Loading the visit's payer" />}
              {view.data && (
                <>
                  <Text>
                    Visit {view.data.visitNumber} is paid by {PAYER_TYPE_LABEL[view.data.terms.payerType]}
                    {view.data.payerName ? ` · ${view.data.payerName}` : ''}.
                  </Text>
                  {view.data.pending && (
                    <MessageBar intent="warning">
                      <MessageBarBody>
                        A change to {PAYER_TYPE_LABEL[view.data.pending.asked.payerType]}
                        {view.data.pending.payerName ? ` · ${view.data.pending.payerName}` : ''}, asked for by{' '}
                        {view.data.pending.requestedBy} on {formatDateTime(view.data.pending.requestedAt)}, is waiting
                        for approval.
                      </MessageBarBody>
                    </MessageBar>
                  )}
                  <Field label="Paid by" required>
                    <Select
                      value={currentType}
                      onChange={(_, d) => {
                        setPayerType(d.value as PayerType);
                        setPayerId('');
                      }}
                    >
                      {PAYER_TYPES.map((t) => (
                        <option key={t} value={t}>
                          {PAYER_TYPE_LABEL[t]}
                        </option>
                      ))}
                    </Select>
                  </Field>
                  {needsPayer && (
                    <Field
                      label={
                        currentType === 'Insurance' ? 'Insurer' : currentType === 'Corporate' ? 'Company' : 'Scheme'
                      }
                      required
                      hint="The patient's policy or membership must be verified by a second person, as at registration."
                    >
                      <Select value={currentPayer} onChange={(_, d) => setPayerId(d.value)}>
                        <option value="">Choose…</option>
                        {payerOptions.map((p) => (
                          <option key={p.id} value={p.id}>
                            {p.name}
                          </option>
                        ))}
                      </Select>
                    </Field>
                  )}
                  <Field label="Reason" hint="For the audit trail, and for the approver when the change waits">
                    <Textarea value={reason} onChange={(_, d) => setReason(d.value)} maxLength={500} rows={2} />
                  </Field>
                  <Text size={200} className={styles.muted}>
                    The visit's unbilled services are priced again on the new payer's plan. A change to another kind of
                    payer, or one that makes any service cheaper, waits for someone who approves discounts. A visit with
                    a final bill cannot change payer; discard a draft bill first.
                  </Text>
                </>
              )}
              <ActionError error={save.error ?? view.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || save.isPending}
                icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Change payer
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
