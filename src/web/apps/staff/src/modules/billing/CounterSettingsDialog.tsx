import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  makeStyles,
  Radio,
  RadioGroup,
  Switch,
  Text,
} from '@fluentui/react-components';
import { useNotify } from '@hc360/ui';
import { useState } from 'react';
import { saveCounterPrefs, useCounterPrefs, type CounterPrefs } from './counterPrefs';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

/** Settings of this billing counter (kept on the computer): counter name, card terminal and receipt printing. */
export function CounterSettingsDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const prefs = useCounterPrefs();
  const [draft, setDraft] = useState<CounterPrefs>(prefs);
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setDraft(prefs);
    }
  }

  const save = () => {
    saveCounterPrefs({ ...draft, counter: draft.counter.trim(), terminalId: draft.terminalId.trim() });
    notify.success('Counter settings saved', 'They apply to everyone who uses this computer.');
    onClose();
  };

  return (
    <Dialog open={open} onOpenChange={(_, data) => !data.open && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            save();
          }}
        >
          <DialogBody>
            <DialogTitle>Counter settings</DialogTitle>
            <DialogContent className={styles.body}>
              <Text>These settings belong to this computer, not to your user.</Text>
              <Field label="Counter name" hint="Used when you open a cash shift, e.g. OPD Counter 1">
                <Input value={draft.counter} onChange={(_, data) => setDraft({ ...draft, counter: data.value })} maxLength={64} autoFocus />
              </Field>
              <Field label="Card / UPI terminal id" hint="Printed on the terminal. Leave blank if no terminal is connected.">
                <Input value={draft.terminalId} onChange={(_, data) => setDraft({ ...draft, terminalId: data.value })} maxLength={64} />
              </Field>
              <Field label="Receipts print on">
                <RadioGroup value={draft.receiptFormat} onChange={(_, data) => setDraft({ ...draft, receiptFormat: data.value === 'A4' ? 'A4' : 'Receipt80' })}>
                  <Radio value="Receipt80" label="Receipt printer (80 mm), without asking" />
                  <Radio value="A4" label="A4 paper, choosing the printer each time" />
                </RadioGroup>
              </Field>
              <Switch checked={draft.autoPrint} onChange={(_, data) => setDraft({ ...draft, autoPrint: data.checked })} label="Print the receipt as soon as a payment is recorded" />
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose}>Cancel</Button>
              <Button type="submit" appearance="primary">
                Save
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
