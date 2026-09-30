import {
  Badge,
  Button,
  Caption1,
  makeStyles,
  Menu,
  MenuDivider,
  MenuGroupHeader,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Spinner,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import { CheckmarkRegular, ChevronDownRegular } from '@fluentui/react-icons';
import { formatInr } from '@hc360/core';
import type { BatchStock } from '@/modules/inventory/api';
import { batchProblem, batchProblemText, type BatchAllocation } from '@/modules/inventory/fefo';
import { expiryLabel, formatExpiry, formatQty } from '@/modules/inventory/format';

const useStyles = makeStyles({
  row: { display: 'flex', alignItems: 'center', gap: '4px', flexWrap: 'wrap' },
  chip: { fontFamily: tokens.fontFamilyMonospace },
  muted: { color: tokens.colorNeutralForeground3 },
});

/**
 * The batches a line will be taken from: suggested first-expiry-first-out, or one batch the pharmacist picked
 * (the pack in hand). Batches that cannot be used are listed with the reason.
 */
export function BatchPick({
  batches,
  allocations,
  pickedBatchId,
  onPick,
  minimumShelfLifeDays = 0,
  unit,
  disabled,
  showMrp = true,
}: {
  batches: readonly BatchStock[] | undefined;
  allocations: readonly BatchAllocation[];
  pickedBatchId: string | null;
  onPick: (batchId: string | null) => void;
  minimumShelfLifeDays?: number;
  unit?: string | null;
  disabled?: boolean;
  showMrp?: boolean;
}) {
  const styles = useStyles();
  if (!batches) {
    return <Spinner size="extra-tiny" label="Checking batches…" labelPosition="after" />;
  }
  return (
    <div className={styles.row}>
      {allocations.length === 0 && <Caption1 className={styles.muted}>No batch</Caption1>}
      {allocations.map((a) => {
        const batch = batches.find((b) => b.batchId === a.batchId);
        return (
          <Tooltip
            key={a.batchId}
            relationship="description"
            content={`Batch ${a.batchNumber} · ${batch ? expiryLabel(batch.daysToExpiry) : formatExpiry(a.expiryDate)}${batch && showMrp ? ` · MRP ${formatInr(batch.mrp)}` : ''}`}
          >
            <Badge appearance="outline" color="informative" className={styles.chip}>
              {a.batchNumber} · {formatExpiry(a.expiryDate)} · {formatQty(a.quantity, unit)}
            </Badge>
          </Tooltip>
        );
      })}
      <Menu>
        <MenuTrigger disableButtonEnhancement>
          <Button
            size="small"
            appearance="subtle"
            icon={<ChevronDownRegular />}
            iconPosition="after"
            disabled={disabled}
            aria-label="Choose the batch"
          >
            {pickedBatchId ? 'Picked' : 'FEFO'}
          </Button>
        </MenuTrigger>
        <MenuPopover>
          <MenuList>
            <MenuItem
              icon={pickedBatchId ? undefined : <CheckmarkRegular />}
              onClick={() => onPick(null)}
              secondaryContent="Suggested"
            >
              Earliest expiry first
            </MenuItem>
            <MenuDivider />
            <MenuGroupHeader>Or take one batch (the pack in hand)</MenuGroupHeader>
            {batches.length === 0 && <MenuItem disabled>No batches in this store</MenuItem>}
            {batches.map((b) => {
              const problem = batchProblem(b, minimumShelfLifeDays);
              return (
                <MenuItem
                  key={b.batchId}
                  icon={b.batchId === pickedBatchId ? <CheckmarkRegular /> : undefined}
                  disabled={problem !== null}
                  onClick={() => onPick(b.batchId)}
                  secondaryContent={
                    problem
                      ? batchProblemText(problem, b, minimumShelfLifeDays)
                      : `${formatQty(b.available, unit)} free`
                  }
                >
                  {b.batchNumber} · exp {formatExpiry(b.expiryDate)}
                  {showMrp ? ` · MRP ${formatInr(b.mrp)}` : ''}
                </MenuItem>
              );
            })}
          </MenuList>
        </MenuPopover>
      </Menu>
    </div>
  );
}
