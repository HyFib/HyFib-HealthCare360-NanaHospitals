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
  MessageBar,
  MessageBarBody,
} from '@fluentui/react-components';
import { isApiError } from '@hc360/core';
import { useNotify, useUnsavedChanges, useValuesChanged } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { pharmacyApi, pharmacyKeys, type QueueEntryDetail } from './api';
import { dispensedOnPaperOf } from './paperSale';

/**
 * A prescription that was handed over on paper during a downtime reaches the queue when HC360 is back: this closes it as
 * dispensed, so it is not dispensed a second time. Name the sale entered from the paper, or say where the paper is.
 */
export function DispensedOnPaperDialog({ entry, onClose }: { entry: QueueEntryDetail; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [at, setAt] = useState('');
  const [invoiceNumber, setInvoiceNumber] = useState('');
  const [note, setNote] = useState('');
  const { body, problem } = dispensedOnPaperOf({ at, invoiceNumber, note });
  const save = useMutation({
    mutationFn: () => pharmacyApi.dispensedOnPaper(entry.id, body!),
    onSuccess: () => {
      notify.success(`Prescription ${entry.prescriptionNumber} closed as dispensed on paper`);
      void queryClient.invalidateQueries({ queryKey: pharmacyKeys.queue });
      void queryClient.invalidateQueries({ queryKey: ['pharmacy', 'queue', 'detail', entry.id] });
      void queryClient.invalidateQueries({ queryKey: pharmacyKeys.dashboard });
      onClose();
    },
  });
  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ at, invoiceNumber, note }),
    busy: save.isPending,
    onClose,
  });
  const refused = isApiError(save.error) ? save.error.fieldErrors : {};

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 480 }}>
        <DialogBody>
          <DialogTitle>Dispensed on paper during a downtime</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Caption1>
              Use this for a prescription the pharmacy already handed over on paper. It leaves the queue as dispensed.
              Enter the paper sale itself under its number first, from the counter sale screen, so the stock and the
              invoice are right.
            </Caption1>
            <Field label="Dispensed on the paper at" required validationMessage={refused['at']?.[0]}>
              <Input type="datetime-local" value={at} onChange={(_, d) => setAt(d.value)} autoFocus />
            </Field>
            <Field
              label="Sale number on the paper"
              hint="The invoice you entered the paper sale under"
              validationMessage={refused['invoiceNumber']?.[0]}
            >
              <Input value={invoiceNumber} maxLength={24} onChange={(_, d) => setInvoiceNumber(d.value)} />
            </Field>
            <Field
              label="Note"
              hint="Where the paper sale is kept, if it is not entered yet"
              validationMessage={refused['note']?.[0]}
            >
              <Input value={note} maxLength={200} onChange={(_, d) => setNote(d.value)} />
            </Field>
            {save.error && !Object.keys(refused).length && (
              <MessageBar intent="error">
                <MessageBarBody>{isApiError(save.error) ? save.error.userMessage : 'Not saved.'}</MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={guard.requestClose} disabled={save.isPending}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={!body || save.isPending}
              disabledFocusable={save.isPending}
              title={problem ?? undefined}
              onClick={() => save.mutate()}
            >
              Close as dispensed
            </Button>
          </DialogActions>
        </DialogBody>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
