import {
  makeStyles,
  mergeClasses,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  tokens,
} from '@fluentui/react-components';
import type { ReactNode } from 'react';

const useStyles = makeStyles({
  wrap: { overflowX: 'auto', width: '100%' },
  end: { textAlign: 'end' },
  center: { textAlign: 'center' },
  low: { '@media (max-width: 900px)': { display: 'none' } },
  cell: { verticalAlign: 'top', paddingTop: '6px', paddingBottom: '6px' },
  footer: { backgroundColor: tokens.colorNeutralBackground2 },
  highlight: { backgroundColor: tokens.colorPaletteRedBackground1 },
});

export interface LineColumn<T> {
  key: string;
  header: ReactNode;
  render: (row: T, index: number) => ReactNode;
  width?: number | string;
  align?: 'start' | 'end' | 'center';
  /** Hidden on tablets. */
  priority?: 'high' | 'low';
}

/**
 * Plain table for document lines that hold inputs (quantities, batches, prices). Unlike DataTable it does not
 * take the arrow keys, so typing and Tab between inputs behave normally.
 */
export function LineTable<T>({
  ariaLabel,
  columns,
  rows,
  getRowId,
  empty,
  footer,
  isFlagged,
}: {
  ariaLabel: string;
  columns: LineColumn<T>[];
  rows: readonly T[];
  getRowId: (row: T, index: number) => string;
  empty?: ReactNode;
  /** Totals row cells, keyed by column key. */
  footer?: Partial<Record<string, ReactNode>>;
  /** Highlight rows that need attention. */
  isFlagged?: (row: T) => boolean;
}) {
  const styles = useStyles();
  const cellClass = (column: LineColumn<T>) =>
    mergeClasses(
      styles.cell,
      column.align === 'end' && styles.end,
      column.align === 'center' && styles.center,
      column.priority === 'low' && styles.low,
    );
  return (
    <div className={styles.wrap}>
      <Table size="small" aria-label={ariaLabel}>
        <TableHeader>
          <TableRow>
            {columns.map((column) => (
              <TableHeaderCell
                key={column.key}
                className={cellClass(column)}
                style={column.width ? { width: column.width } : undefined}
              >
                {column.header}
              </TableHeaderCell>
            ))}
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map((row, index) => (
            <TableRow key={getRowId(row, index)} className={isFlagged?.(row) ? styles.highlight : undefined}>
              {columns.map((column) => (
                <TableCell key={column.key} className={cellClass(column)}>
                  {column.render(row, index)}
                </TableCell>
              ))}
            </TableRow>
          ))}
          {footer && rows.length > 0 && (
            <TableRow className={styles.footer}>
              {columns.map((column) => (
                <TableCell key={column.key} className={cellClass(column)}>
                  {footer[column.key] ?? null}
                </TableCell>
              ))}
            </TableRow>
          )}
        </TableBody>
      </Table>
      {rows.length === 0 && empty}
    </div>
  );
}
