import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Input,
  makeStyles,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  tokens,
} from '@fluentui/react-components';
import { PrintRegular } from '@fluentui/react-icons';
import { formatInr } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useState } from 'react';
import { formatExpiry } from '@/modules/inventory/format';
import { batchTagValue, MAX_TAGS, printPharmacyTags, tagCount, type PharmacyTag } from './tags';

const useStyles = makeStyles({
  copies: { width: '72px' },
  code: { fontFamily: tokens.fontFamilyMonospace },
  table: { overflowX: 'auto' },
  muted: { color: tokens.colorNeutralForeground3 },
  over: { color: tokens.colorPaletteRedForeground1 },
});

/**
 * Pharmacy tags (INT-BAR) for one or more batches, each as many times as there are strips, bottles or boxes to tag.
 * One print takes at most {@link MAX_TAGS} labels.
 */
export function PrintTagsDialog({
  title,
  tags,
  onClose,
}: {
  title: string;
  tags: readonly PharmacyTag[];
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const [copies, setCopies] = useState(() => tags.map((t) => String(t.copies)));
  const [busy, setBusy] = useState(false);
  const chosen = tags.map((tag, i) => ({ ...tag, copies: Math.max(0, Math.floor(Number(copies[i]) || 0)) }));
  const total = tagCount(chosen);

  const print = async () => {
    setBusy(true);
    try {
      await printPharmacyTags(chosen);
      notify.success(total === 1 ? 'Printed 1 tag' : `Printed ${total} tags`);
      onClose();
    } catch (error) {
      notify.error(error, 'Could not print the tags');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !busy && onClose()}>
      <DialogSurface style={{ maxWidth: 760 }}>
        <DialogBody>
          <DialogTitle>{title}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Text>
              One tag per strip, bottle or box. Scanning a tag at the counter or in the dispensing queue picks its
              batch.
            </Text>
            <div className={styles.table}>
              <Table size="small" aria-label="Tags to print">
                <TableHeader>
                  <TableRow>
                    <TableHeaderCell>Medicine</TableHeaderCell>
                    <TableHeaderCell>Batch</TableHeaderCell>
                    <TableHeaderCell>Expiry</TableHeaderCell>
                    <TableHeaderCell>MRP</TableHeaderCell>
                    <TableHeaderCell>Tags</TableHeaderCell>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {tags.map((tag, i) => (
                    <TableRow key={`${tag.itemCode}#${tag.batchNumber ?? ''}#${i}`}>
                      <TableCell>
                        {tag.medicine}
                        <Caption1 block className={styles.code}>
                          {batchTagValue(tag.itemCode, tag.batchNumber)}
                        </Caption1>
                      </TableCell>
                      <TableCell>{tag.batchNumber || '—'}</TableCell>
                      <TableCell>{tag.expiryDate ? formatExpiry(tag.expiryDate) : '—'}</TableCell>
                      <TableCell>
                        {tag.mrp != null ? `${formatInr(tag.mrp)}${tag.unit ? `/${tag.unit}` : ''}` : '—'}
                      </TableCell>
                      <TableCell>
                        <Input
                          className={styles.copies}
                          type="number"
                          min={0}
                          max={MAX_TAGS}
                          aria-label={`Tags for ${tag.medicine}${tag.batchNumber ? `, batch ${tag.batchNumber}` : ''}`}
                          value={copies[i]}
                          onChange={(_, d) => setCopies((prev) => prev.map((c, j) => (j === i ? d.value : c)))}
                        />
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
            <Caption1 className={total > MAX_TAGS ? styles.over : styles.muted}>
              {total > MAX_TAGS
                ? `${total} tags: print at most ${MAX_TAGS} at a time.`
                : `${total} ${total === 1 ? 'tag' : 'tags'} on this computer's label printer.`}
            </Caption1>
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={busy}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              icon={<PrintRegular />}
              disabled={busy || total === 0 || total > MAX_TAGS}
              onClick={() => void print()}
            >
              Print tags
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
