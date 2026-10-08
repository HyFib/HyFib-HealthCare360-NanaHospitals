import { Field, Input } from '@fluentui/react-components';
import { BarcodeScannerRegular } from '@fluentui/react-icons';
import { useNotify } from '@hc360/ui';
import { useState } from 'react';
import { api } from '@/lib/api';
import { normalizeScan } from '@/modules/lab/scan';
import type { PharmacyStockRow } from './api';
import { isExactScan } from './tags';

/**
 * Scan to check a medicine (INT-BAR): the barcode on the strip, bottle or pharmacy tag in hand is looked up in the store,
 * and the medicine (with the batch, for a pharmacy tag) is handed to `onScanned`, which checks it against what is being
 * dispensed. A scanner types the code and presses Enter, as does a person.
 */
export function ScanCheck({
  storeId,
  onScanned,
  disabled,
  label = 'Scan to check a medicine',
  hint = 'Scan the strip or its pharmacy tag: the medicine is checked against the prescription, and a tag picks its batch.',
}: {
  storeId: string | null;
  onScanned: (row: PharmacyStockRow) => void;
  disabled?: boolean;
  label?: string;
  hint?: string;
}) {
  const notify = useNotify();
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);

  const check = async () => {
    const code = normalizeScan(text);
    if (!code || !storeId || busy) {
      return;
    }
    setBusy(true);
    try {
      // The search also matches names, so only the item the code names exactly is taken.
      const rows = await api.get<PharmacyStockRow[]>('/api/v1/pharmacy/stock', {
        query: { storeId, search: code, take: 25 },
      });
      const row = rows.find((r) => isExactScan(r, code));
      if (row) {
        onScanned(row);
      } else {
        notify.warning(
          `Nothing in this store has the code ${code}`,
          'Scan the barcode on the strip or the pharmacy tag, or check the item master’s barcode.',
        );
      }
      setText('');
    } catch (error) {
      notify.error(error, 'Could not look up the scanned code');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Field label={label} hint={hint}>
      <Input
        contentBefore={<BarcodeScannerRegular aria-hidden />}
        value={text}
        disabled={disabled || !storeId}
        placeholder="Scan here"
        autoComplete="off"
        onChange={(_, d) => setText(d.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter') {
            e.preventDefault();
            void check();
          }
        }}
      />
    </Field>
  );
}
