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
  MessageBarTitle,
  ProgressBar,
  Select,
  Spinner,
  Tab,
  TabList,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { formatDateOnly, formatInr, todayIso } from '@hc360/core';
import { DataTable, useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import {
  loadAllPrices,
  loadServiceCatalogue,
  mastersApi,
  mastersKeys,
  type SaveTariffPriceRequest,
  type TariffPlanListItem,
} from './mastersApi';
import { parsePercent } from './money';
import {
  inBatches,
  parsePriceSheet,
  PRICE_ROUNDING,
  PRICE_ROUNDING_LABEL,
  revisePrice,
  type PriceRounding,
} from './tariffBulk';
import { ActionError } from './ui';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  row: { display: 'flex', gap: '12px', flexWrap: 'wrap', alignItems: 'end' },
  muted: { color: tokens.colorNeutralForeground3 },
  sheet: { fontFamily: tokens.fontFamilyMonospace },
});

interface PreviewRow {
  key: string;
  code: string;
  name: string;
  current: number | null;
  next: number;
}

interface Prepared {
  prices: SaveTariffPriceRequest[];
  preview: PreviewRow[];
  problems: string[];
  truncated: boolean;
}

/**
 * Bulk price update of a tariff plan: revise every price (or those matching a search) by a percentage, or paste a
 * price list from Excel. New prices start on the chosen date; the previous prices are closed the day before, so the
 * history stays intact. Sent in batches of 500.
 */
export function TariffBulkDialog({ plan, onClose }: { plan: TariffPlanListItem; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [mode, setMode] = useState<'percent' | 'paste'>('percent');
  const [search, setSearch] = useState('');
  const [percent, setPercent] = useState('');
  const [rounding, setRounding] = useState<PriceRounding>('rupee');
  const [sheet, setSheet] = useState('');
  const [from, setFrom] = useState(todayIso(1));
  const [prepared, setPrepared] = useState<Prepared | null>(null);
  const [progress, setProgress] = useState<number | null>(null);

  const parsedPercent = parsePercent(percent.replace(/^[+-]/, ''), { allowZero: false });
  const signedPercent = parsedPercent.ok
    ? percent.trim().startsWith('-')
      ? -parsedPercent.percent
      : parsedPercent.percent
    : null;

  const prepare = useMutation({
    mutationFn: async (): Promise<Prepared> => {
      if (mode === 'percent') {
        const { prices, truncated } = await loadAllPrices(plan.id, {
          search: search.trim() || undefined,
          onDate: from,
        });
        const rows = prices.map((p) => ({
          key: p.id,
          serviceItemId: p.serviceItemId,
          code: p.serviceCode,
          name: p.serviceName,
          current: p.price,
          next: revisePrice(p.price, signedPercent ?? 0, rounding),
        }));
        const changed = rows.filter((r) => r.next !== r.current);
        return {
          prices: changed.map((r) => ({
            serviceItemId: r.serviceItemId,
            price: r.next,
            effectiveFrom: from,
            effectiveTo: null,
          })),
          preview: changed,
          problems:
            prices.length === 0
              ? ['No prices on this plan match. Services without a plan price are charged at their standard price.']
              : [],
          truncated,
        };
      }
      const { rows, problems } = parsePriceSheet(sheet);
      const catalogue = await loadServiceCatalogue();
      const byCode = new Map(catalogue.map((item) => [item.code.toUpperCase(), item]));
      const prices: SaveTariffPriceRequest[] = [];
      const preview: PreviewRow[] = [];
      const messages = problems.map((p) => `Line ${p.line}: ${p.message}`);
      for (const row of rows) {
        const item = byCode.get(row.code);
        if (!item) {
          messages.push(`Line ${row.line}: no active service has the code ${row.code}.`);
          continue;
        }
        prices.push({ serviceItemId: item.id, price: row.price, effectiveFrom: from, effectiveTo: null });
        preview.push({ key: item.id, code: item.code, name: item.name, current: null, next: row.price });
      }
      return { prices, preview, problems: messages, truncated: false };
    },
    onSuccess: setPrepared,
  });

  const apply = useMutation({
    mutationFn: async () => {
      if (!prepared) {
        return { created: 0, updated: 0 };
      }
      const batches = inBatches(prepared.prices);
      let created = 0;
      let updated = 0;
      for (let index = 0; index < batches.length; index++) {
        setProgress(index / batches.length);
        const result = await mastersApi.savePrices(plan.id, batches[index]!);
        created += result.created;
        updated += result.updated;
      }
      setProgress(1);
      return { created, updated };
    },
    onSuccess: (result) => {
      notify.success(
        'Prices updated',
        `${result.created} new and ${result.updated} changed prices from ${formatDateOnly(from)}`,
      );
      void queryClient.invalidateQueries({ queryKey: mastersKeys.tariffs });
      onClose();
    },
    onSettled: () => setProgress(null),
  });

  /** Any change to the inputs invalidates the preview. */
  const edit = (change: () => void) => {
    change();
    setPrepared(null);
  };
  const canPrepare = mode === 'percent' ? signedPercent !== null : sheet.trim().length > 0;
  const busy = prepare.isPending || apply.isPending;

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !busy && onClose()}>
      <DialogSurface style={{ maxWidth: 820, width: '100%' }}>
        <DialogBody>
          <DialogTitle>Bulk price update · {plan.name}</DialogTitle>
          <DialogContent className={styles.body}>
            <TabList
              selectedValue={mode}
              onTabSelect={(_, d) => {
                setMode(d.value as 'percent' | 'paste');
                setPrepared(null);
              }}
            >
              <Tab value="percent">Change by a percentage</Tab>
              <Tab value="paste">Paste a price list</Tab>
            </TabList>
            {mode === 'percent' ? (
              <div className={styles.row}>
                <Field
                  label="Services"
                  hint="Name or code; empty for every price on the plan"
                  style={{ flex: '1 1 220px' }}
                >
                  <Input value={search} onChange={(_, d) => edit(() => setSearch(d.value))} />
                </Field>
                <Field
                  label="Change (%)"
                  hint="e.g. 10 or -5"
                  validationMessage={
                    percent && signedPercent === null ? 'Enter a percentage, e.g. 7.5 or -5' : undefined
                  }
                >
                  <Input
                    value={percent}
                    onChange={(_, d) => edit(() => setPercent(d.value))}
                    inputMode="decimal"
                    style={{ width: 120 }}
                    autoFocus
                  />
                </Field>
                <Field label="Round to">
                  <Select value={rounding} onChange={(_, d) => edit(() => setRounding(d.value as PriceRounding))}>
                    {PRICE_ROUNDING.map((r) => (
                      <option key={r} value={r}>
                        {PRICE_ROUNDING_LABEL[r]}
                      </option>
                    ))}
                  </Select>
                </Field>
              </div>
            ) : (
              <Field
                label="Service code and price, one per line"
                hint="Copy two columns from Excel (code, price). A header row is ignored."
              >
                <Textarea
                  className={styles.sheet}
                  value={sheet}
                  onChange={(_, d) => edit(() => setSheet(d.value))}
                  rows={8}
                  placeholder={'CBC\t350\nLFT\t900\nXRAY-CHEST\t450'}
                />
              </Field>
            )}
            <Field label="New prices apply from" hint="The current prices end the day before.">
              <Input
                type="date"
                value={from}
                min={todayIso()}
                onChange={(_, d) => edit(() => setFrom(d.value))}
                style={{ width: 180 }}
              />
            </Field>

            {prepared && (
              <>
                {prepared.problems.length > 0 && (
                  <MessageBar intent="warning" layout="multiline">
                    <MessageBarBody>
                      <MessageBarTitle>{prepared.problems.length} line(s) need attention</MessageBarTitle>
                      {prepared.problems.slice(0, 8).map((p) => (
                        <div key={p}>{p}</div>
                      ))}
                      {prepared.problems.length > 8 && <div>…and {prepared.problems.length - 8} more.</div>}
                    </MessageBarBody>
                  </MessageBar>
                )}
                {prepared.truncated && (
                  <MessageBar intent="warning">
                    <MessageBarBody>
                      Only the first 5,000 prices were loaded. Narrow the search and repeat for the rest.
                    </MessageBarBody>
                  </MessageBar>
                )}
                <Text weight="semibold">
                  {prepared.prices.length} price{prepared.prices.length === 1 ? '' : 's'} will be saved from{' '}
                  {formatDateOnly(from)}
                </Text>
                <DataTable
                  ariaLabel="Price changes"
                  rows={prepared.preview.slice(0, 50)}
                  getRowId={(r) => r.key}
                  size="small"
                  columns={[
                    { key: 'service', header: 'Service', render: (r) => `${r.name} (${r.code})` },
                    {
                      key: 'current',
                      header: 'Now',
                      align: 'end',
                      render: (r) => (r.current === null ? '—' : formatInr(r.current)),
                    },
                    {
                      key: 'next',
                      header: 'New',
                      align: 'end',
                      render: (r) => <Text weight="semibold">{formatInr(r.next)}</Text>,
                    },
                  ]}
                  emptyTitle="Nothing to change"
                />
                {prepared.preview.length > 50 && <Caption1 className={styles.muted}>Showing the first 50.</Caption1>}
              </>
            )}
            {progress !== null && <ProgressBar value={progress} aria-label="Saving prices" />}
            <ActionError error={prepare.error ?? apply.error} />
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={busy}>
              Cancel
            </Button>
            {!prepared ? (
              <Button
                appearance="primary"
                disabled={!canPrepare || busy}
                onClick={() => prepare.mutate()}
                icon={prepare.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Preview changes
              </Button>
            ) : (
              <Button
                appearance="primary"
                disabled={prepared.prices.length === 0 || busy}
                onClick={() => apply.mutate()}
                icon={apply.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Save {prepared.prices.length} price{prepared.prices.length === 1 ? '' : 's'}
              </Button>
            )}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
