import {
  Button,
  Caption1,
  Field,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Select,
  Text,
  tokens,
} from '@fluentui/react-components';
import { ArrowSyncRegular } from '@fluentui/react-icons';
import { formatDateTime, isApiError } from '@hc360/core';
import { DataTable, ErrorState, LoadingState, Section, StatusBadge } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { qualityKeys, type IndicatorDto } from './api';
import {
  computedApi,
  computedQueries,
  monthLabel,
  monthStart,
  OUTCOME_LABELS,
  ROLE_LABELS,
  type MeasureRole,
} from './computedApi';
import { formatIndicatorValue } from './rules';

const useStyles = makeStyles({
  head: { display: 'flex', gap: '12px', alignItems: 'flex-end', flexWrap: 'wrap' },
  figures: { display: 'flex', gap: '24px', flexWrap: 'wrap', paddingBlock: '8px' },
  muted: { color: tokens.colorNeutralForeground3 },
  cell: { display: 'flex', flexDirection: 'column', gap: '2px', paddingBlock: '4px', whiteSpace: 'normal' },
});

const ROLE_TONE: Record<MeasureRole, 'success' | 'informative' | 'warning'> = {
  Numerator: 'success',
  Denominator: 'informative',
  Excluded: 'warning',
};

/**
 * The records behind a computed indicator's value for a month, computed now, next to the value stored by the monthly
 * computation; and recomputing the month, which stores what is shown.
 */
export function IndicatorDrilldownPanel({ indicator }: { indicator: IndicatorDto }) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const months = Array.from({ length: 13 }, (_, back) => monthStart(back));
  const [period, setPeriod] = useState(months[1]!);
  const drill = useQuery(computedQueries.drilldown(indicator.id, period));
  const recompute = useMutation({
    mutationFn: () => computedApi.compute(indicator.id, period),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: qualityKeys.indicators }),
  });
  const d = drill.data;
  const outcome = recompute.data?.[0]?.outcome;

  return (
    <Section
      title="Records behind the value"
      description="Computed now from HC360's records. The monthly computation stores the month just ended during the first ten days of the next."
      flush
      actions={
        <div className={styles.head}>
          <Field label="Month" orientation="horizontal">
            <Select value={period} onChange={(_, data) => setPeriod(data.value)}>
              {months.map((m, i) => (
                <option key={m} value={m}>
                  {monthLabel(m)}
                  {i === 0 ? ' (to date)' : ''}
                </option>
              ))}
            </Select>
          </Field>
          <Button icon={<ArrowSyncRegular />} disabled={recompute.isPending} onClick={() => recompute.mutate()}>
            Recompute {monthLabel(period)}
          </Button>
        </div>
      }
    >
      <div style={{ padding: '0 16px' }}>
        {outcome && (
          <MessageBar intent={outcome === 'Stored' ? 'success' : 'warning'}>
            <MessageBarBody>
              <MessageBarTitle>Checklist</MessageBarTitle>
              {monthLabel(period)}: {OUTCOME_LABELS[outcome]}.
            </MessageBarBody>
          </MessageBar>
        )}
        {recompute.error && (
          <MessageBar intent={'error'}>
            <MessageBarBody>
              <MessageBarTitle>Not saved</MessageBarTitle>
              {isApiError(recompute.error) ? recompute.error.message : 'The month could not be recomputed.'}
            </MessageBarBody>
          </MessageBar>
        )}
        {drill.isLoading && <LoadingState />}
        {drill.error && <ErrorState error={drill.error} onRetry={() => void drill.refetch()} />}
        {d?.unavailable && (
          <MessageBar intent={'warning'}>
            <MessageBarBody>
              <MessageBarTitle>Please note</MessageBarTitle>
              {d.note}
            </MessageBarBody>
          </MessageBar>
        )}
        {d && !d.unavailable && (
          <div className={styles.figures}>
            <Text>
              <Text weight="semibold">Numerator</Text> {d.numerator.toLocaleString('en-IN')}
            </Text>
            <Text>
              <Text weight="semibold">Denominator</Text> {d.denominator.toLocaleString('en-IN')}
            </Text>
            <Text>
              <Text weight="semibold">Value now</Text> {formatIndicatorValue(d.value, indicator.unit)}
            </Text>
            <Text>
              <Text weight="semibold">Stored</Text>{' '}
              {d.stored
                ? `${formatIndicatorValue(d.stored.value, indicator.unit)}${d.stored.isComputed ? '' : ' (entered by hand)'}`
                : 'Not yet'}
            </Text>
            {d.note && <Caption1 className={styles.muted}>{d.note}</Caption1>}
          </div>
        )}
      </div>
      {d && !d.unavailable && (
        <DataTable
          ariaLabel={`Records behind ${indicator.code} for ${d.periodLabel}`}
          rows={d.records}
          getRowId={(r) => `${r.kind}-${r.reference}-${r.at}`}
          columns={[
            {
              key: 'record',
              header: 'Record',
              render: (r) => (
                <div className={styles.cell}>
                  <Text weight="semibold">{r.reference}</Text>
                  <Caption1 className={styles.muted}>
                    {r.kind}
                    {r.uhid ? ` · ${r.uhid}` : ''}
                  </Caption1>
                </div>
              ),
            },
            { key: 'at', header: 'When', width: 180, render: (r) => formatDateTime(r.at) },
            {
              key: 'role',
              header: 'Counts as',
              width: 170,
              render: (r) => <StatusBadge status={r.role} tone={ROLE_TONE[r.role]} label={ROLE_LABELS[r.role]} />,
            },
            {
              key: 'detail',
              header: 'Detail',
              priority: 'low',
              render: (r) =>
                r.minutes !== null ? (
                  `${r.minutes.toLocaleString('en-IN')} min`
                ) : r.detail ? (
                  <Caption1>{r.detail}</Caption1>
                ) : (
                  '—'
                ),
            },
          ]}
          emptyTitle="No records in this month"
          size="small"
        />
      )}
    </Section>
  );
}
