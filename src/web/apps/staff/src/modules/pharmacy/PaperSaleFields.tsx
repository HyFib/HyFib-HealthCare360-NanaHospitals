import { Checkbox, Field, Input, makeStyles, Text } from '@fluentui/react-components';
import type { PaperSaleEntry, PaperSaleProblems } from './paperSale';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '8px' },
  row: { display: 'flex', gap: '12px', flexWrap: 'wrap' },
});

/**
 * Entering a sale written on paper while HC360 was down: the pharmacy sale number printed on the paper and the time
 * written on it, which the invoice keeps instead of the next number and the time of entry.
 */
export function PaperSaleFields({
  value,
  onChange,
  problems,
  disabled,
}: {
  value: PaperSaleEntry;
  onChange: (next: PaperSaleEntry) => void;
  /** The server's messages about the number and the time. */
  problems?: PaperSaleProblems;
  disabled?: boolean;
}) {
  const styles = useStyles();
  return (
    <div className={styles.root}>
      <Checkbox
        checked={value.onPaper}
        disabled={disabled}
        onChange={(_, data) => onChange({ ...value, onPaper: data.checked === true })}
        label="Sale written on paper during a downtime"
      />
      {value.onPaper && (
        <>
          <Text size={200}>
            The invoice takes the sale number written on the paper and shows the time on it as the time of sale. Stock
            is taken off now, from the batches handed over.
          </Text>
          <div className={styles.row}>
            <Field
              label="Sale number on the paper"
              required
              validationMessage={problems?.number}
              validationState={problems?.number ? 'error' : 'none'}
            >
              <Input
                value={value.number}
                maxLength={24}
                disabled={disabled}
                onChange={(_, data) => onChange({ ...value, number: data.value })}
              />
            </Field>
            <Field
              label="Sold on the paper at"
              required
              validationMessage={problems?.at}
              validationState={problems?.at ? 'error' : 'none'}
            >
              <Input
                type="datetime-local"
                value={value.at}
                disabled={disabled}
                onChange={(_, data) => onChange({ ...value, at: data.value })}
              />
            </Field>
          </div>
        </>
      )}
    </div>
  );
}
