import {
  Caption1,
  makeStyles,
  mergeClasses,
  Select,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  tokens,
  Tooltip,
  useArrowNavigationGroup,
} from '@fluentui/react-components';
import { ChevronRightRegular, LockClosedRegular } from '@fluentui/react-icons';
import { EmptyState, Pagination } from '@hc360/ui';
import type { KeyboardEvent } from 'react';
import type { ReportResult } from './api';
import { formatValue, isNumericType } from './format';
import { PAGE_SIZES } from './labels';

const useStyles = makeStyles({
  wrapper: { overflow: 'auto', width: '100%', maxHeight: '70vh' },
  compactWrapper: { overflow: 'auto', width: '100%' },
  headerCell: {
    position: 'sticky',
    top: 0,
    zIndex: 1,
    backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: `inset 0 -1px 0 ${tokens.colorNeutralStroke2}`,
    whiteSpace: 'nowrap',
  },
  numeric: { textAlign: 'end', justifyContent: 'flex-end', fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' },
  text: { minWidth: '120px' },
  headerLabel: { display: 'inline-flex', alignItems: 'center', gap: '4px' },
  lock: { color: tokens.colorPaletteMarigoldForeground1, display: 'inline-flex' },
  row: { cursor: 'pointer', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
  drillCell: { width: '28px', color: tokens.colorNeutralForeground3 },
  totals: {
    fontWeight: tokens.fontWeightSemibold,
    backgroundColor: tokens.colorNeutralBackground3,
    '& td': { borderTop: `2px solid ${tokens.colorNeutralStroke1}` },
  },
  footer: { display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: '8px', flexWrap: 'wrap' },
  pageSize: { display: 'flex', alignItems: 'center', gap: '8px', padding: '8px 16px' },
  muted: { color: tokens.colorNeutralForeground3 },
  more: { padding: '6px 12px', color: tokens.colorNeutralForeground3 },
});

export interface ReportTableProps {
  result: ReportResult;
  /** Sort by a column (server-side, over the whole report); omit for a read-only table. */
  onSort?: (key: string, desc: boolean) => void;
  onPage?: (page: number) => void;
  onPageSize?: (size: number) => void;
  /** Opens the drill-down for a row key; rows without a key are not clickable. */
  onDrill?: (key: string) => void;
  /** Dashboard tiles: small rows, at most `maxRows`, no paging controls. */
  compact?: boolean;
  maxRows?: number;
}

/** The report grid: typed formatting, sticky header, totals row, sortable columns and row drill-down. */
export function ReportTable({ result, onSort, onPage, onPageSize, onDrill, compact, maxRows }: ReportTableProps) {
  const styles = useStyles();
  const navigation = useArrowNavigationGroup({ axis: 'grid' });
  const rows = maxRows !== undefined ? result.rows.slice(0, maxRows) : result.rows;
  const drill = result.drillDown && result.rowKeys && onDrill ? result.drillDown : null;

  if (result.rows.length === 0) {
    return <EmptyState title="No data for these inputs" description="Try a longer period or remove a filter." />;
  }

  const keyOf = (index: number) => result.rowKeys?.[index] ?? null;
  const onRowKey = (event: KeyboardEvent, key: string | null) => {
    if (key && drill && (event.key === 'Enter' || event.key === ' ')) {
      event.preventDefault();
      onDrill?.(key);
    }
  };

  return (
    <div>
      <div className={compact ? styles.compactWrapper : styles.wrapper}>
        <Table aria-label={result.title} size={compact ? 'extra-small' : 'small'} sortable={Boolean(onSort)} {...navigation}>
          <TableHeader>
            <TableRow>
              {result.columns.map((column) => {
                const active = result.sort === column.key;
                return (
                  <TableHeaderCell
                    key={column.key}
                    className={mergeClasses(styles.headerCell, isNumericType(column.type) ? styles.numeric : styles.text)}
                    sortDirection={onSort && active ? (result.desc ? 'descending' : 'ascending') : undefined}
                    onClick={onSort ? () => onSort(column.key, active ? !result.desc : isNumericType(column.type)) : undefined}
                  >
                    <span className={styles.headerLabel}>
                      {column.label}
                      {column.isPatientIdentifier && (
                        <Tooltip content="Patient identifier: views and exports are audited" relationship="description">
                          <span className={styles.lock} aria-label="Patient identifier">
                            <LockClosedRegular />
                          </span>
                        </Tooltip>
                      )}
                    </span>
                  </TableHeaderCell>
                );
              })}
              {drill && <TableHeaderCell className={mergeClasses(styles.headerCell, styles.drillCell)} aria-label={drill.label} />}
            </TableRow>
          </TableHeader>
          <TableBody>
            {rows.map((row, index) => {
              const key = keyOf(index);
              const clickable = Boolean(drill && key);
              return (
                <TableRow
                  key={`${result.page}:${index}:${key ?? ''}`}
                  className={clickable ? styles.row : undefined}
                  onClick={clickable && key ? () => onDrill?.(key) : undefined}
                  onKeyDown={(event) => onRowKey(event, key)}
                  tabIndex={clickable ? 0 : undefined}
                  aria-label={clickable ? `${String(row[0] ?? '')}: ${drill?.label ?? ''}` : undefined}
                >
                  {result.columns.map((column, columnIndex) => (
                    <TableCell key={column.key} className={isNumericType(column.type) ? styles.numeric : undefined}>
                      {formatValue(row[columnIndex], column.type)}
                    </TableCell>
                  ))}
                  {drill && <TableCell className={styles.drillCell}>{clickable && <ChevronRightRegular aria-hidden />}</TableCell>}
                </TableRow>
              );
            })}
            {result.totals && (!compact || rows.length === result.rows.length) && (
              <TableRow className={styles.totals}>
                {result.columns.map((column, columnIndex) => {
                  const value = result.totals?.[columnIndex] ?? null;
                  return (
                    <TableCell key={column.key} className={isNumericType(column.type) ? styles.numeric : undefined}>
                      {value === null ? '' : formatValue(value, column.type)}
                    </TableCell>
                  );
                })}
                {drill && <TableCell />}
              </TableRow>
            )}
          </TableBody>
        </Table>
      </div>

      {compact && rows.length < result.totalRows && (
        <Caption1 block className={styles.more}>
          Showing {rows.length} of {result.totalRows.toLocaleString('en-IN')} rows
        </Caption1>
      )}

      {!compact && onPage && result.totalRows > result.pageSize && (
        <div className={styles.footer}>
          <Pagination page={result.page} pageSize={result.pageSize} totalCount={result.totalRows} onPageChange={onPage} />
          {onPageSize && (
            <label className={styles.pageSize}>
              <Text size={200} className={styles.muted}>
                Rows per page
              </Text>
              <Select size="small" value={String(result.pageSize)} onChange={(_, data) => onPageSize(Number(data.value))} aria-label="Rows per page">
                {PAGE_SIZES.map((size) => (
                  <option key={size} value={size}>
                    {size}
                  </option>
                ))}
              </Select>
            </label>
          )}
        </div>
      )}
    </div>
  );
}
