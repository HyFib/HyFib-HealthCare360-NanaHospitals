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
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
  tokens,
} from '@fluentui/react-components';
import { VehicleTruckProfileRegular } from '@fluentui/react-icons';
import { useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { errorMessage } from './common/errors';
import { labApi, labKeys, labQueries } from './api';

const useStyles = makeStyles({
  content: { display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

export interface SendOutTarget {
  itemId: string;
  testName: string;
  patientName: string;
  barcode?: string | null;
  referralLabId?: string | null;
}

/** Dispatch of an outsourced test to a reference laboratory, with the courier docket for tracing (SRS LIS-08). */
export function SendOutDialog({ target, onClose }: { target: SendOutTarget; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const labs = useQuery(labQueries.referralLabs());
  const [labId, setLabId] = useState(target.referralLabId ?? '');
  const [reference, setReference] = useState('');
  const chosen = labs.data?.find((lab) => lab.id === labId);

  const send = useMutation({
    mutationFn: () => labApi.sendOut(target.itemId, labId, reference.trim() || null),
    onSuccess: () => {
      notify.success('Sent to the reference laboratory', chosen ? `${target.testName} → ${chosen.name}` : undefined);
      void queryClient.invalidateQueries({ queryKey: labKeys.outsourcing });
      void queryClient.invalidateQueries({ queryKey: labKeys.orders });
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, data) => !data.open && !send.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (labId && !send.isPending) {
              send.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>
              Send to a reference laboratory
              <Caption1 block className={styles.muted}>
                {target.testName} · {target.patientName}
                {target.barcode ? ` · sample ${target.barcode}` : ''}
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.content}>
              {send.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(send.error, 'The test could not be sent out.')}</MessageBarBody>
                </MessageBar>
              )}
              <Field
                label="Reference laboratory"
                required
                hint={
                  chosen ? `Agreed turnaround: ${chosen.tatDays} ${chosen.tatDays === 1 ? 'day' : 'days'}` : undefined
                }
                validationMessage={
                  labs.data && labs.data.length === 0
                    ? 'No reference laboratory is set up. Add one in Lab setup.'
                    : undefined
                }
              >
                <Select value={labId} onChange={(_, data) => setLabId(data.value)} autoFocus>
                  <option value="">{labs.isLoading ? 'Loading…' : 'Choose laboratory'}</option>
                  {labs.data?.map((lab) => (
                    <option key={lab.id} value={lab.id}>
                      {lab.name}
                      {lab.accreditationNumber ? ` (${lab.accreditationNumber})` : ''}
                    </option>
                  ))}
                </Select>
              </Field>
              <Field label="Courier docket / manifest number" hint="Optional. Helps trace the sample if it is delayed.">
                <Input value={reference} onChange={(_, data) => setReference(data.value.slice(0, 64))} />
              </Field>
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={send.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!labId || send.isPending}
                // Keeps focus while busy (a disabled button drops it); Enter can still submit, which is ignored.
                disabledFocusable={send.isPending}
                icon={send.isPending ? <Spinner size="tiny" /> : <VehicleTruckProfileRegular />}
              >
                Mark as sent
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
