import { Field, Input, makeStyles, Text } from '@fluentui/react-components';
import type { PaperEntry } from './downtimeApi';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '8px' },
  row: { display: 'flex', gap: '12px', flexWrap: 'wrap' },
});

/**
 * Entering a registration from the paper form filled in while HC360 was down: the downtime UHID the patient was given
 * and the time on the form, which the record keeps instead of a new UHID and the time of entry.
 */
export function PaperRegistrationFields({
  value,
  onChange,
  problems,
}: {
  value: PaperEntry;
  onChange: (next: PaperEntry) => void;
  /** The server's messages about the UHID and the time, by field. */
  problems?: { uhid?: string; at?: string };
}) {
  const styles = useStyles();
  return (
    <div className={styles.root}>
      <Text size={200}>
        For a form filled in on paper while HC360 was down: the patient keeps the downtime UHID written on it, and the
        record shows the time on the form as the registration time.
      </Text>
      <div className={styles.row}>
        <Field
          label="UHID on the form"
          required
          validationMessage={problems?.uhid}
          validationState={problems?.uhid ? 'error' : 'none'}
        >
          <Input value={value.uhid} maxLength={20} onChange={(_, data) => onChange({ ...value, uhid: data.value })} />
        </Field>
        <Field
          label="Registered on the form at"
          required
          validationMessage={problems?.at}
          validationState={problems?.at ? 'error' : 'none'}
        >
          <Input
            type="datetime-local"
            value={value.at}
            onChange={(_, data) => onChange({ ...value, at: data.value })}
          />
        </Field>
      </div>
    </div>
  );
}
