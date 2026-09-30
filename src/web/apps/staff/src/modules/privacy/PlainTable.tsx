import {
  makeStyles,
  mergeClasses,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
} from '@fluentui/react-components';
import type { ReactNode } from 'react';

export interface PlainColumn<T> {
  key: string;
  header: ReactNode;
  render: (row: T) => ReactNode;
  width?: number | string;
  /** Hidden on tablets. */
  priority?: 'low';
  align?: 'end';
}

const useStyles = makeStyles({
  wrapper: { overflowX: 'auto', width: '100%' },
  cell: { overflowWrap: 'anywhere', verticalAlign: 'top', paddingTop: '8px', paddingBottom: '8px' },
  low: { '@media (max-width: 900px)': { display: 'none' } },
  end: { textAlign: 'end' },
});

/**
 * A small read-only table whose cells wrap (the shared DataTable truncates them): corrections, holds, summary items,
 * where the whole text matters.
 */
export function PlainTable<T>({
  ariaLabel,
  columns,
  rows,
  getRowId,
}: {
  ariaLabel: string;
  columns: PlainColumn<T>[];
  rows: readonly T[];
  getRowId: (row: T, index: number) => string;
}) {
  const styles = useStyles();
  const cellClass = (column: PlainColumn<T>) =>
    mergeClasses(styles.cell, column.priority === 'low' && styles.low, column.align === 'end' && styles.end);
  return (
    <div className={styles.wrapper}>
      <Table aria-label={ariaLabel} size="small">
        <TableHeader>
          <TableRow>
            {columns.map((column) => (
              <TableHeaderCell
                key={column.key}
                className={mergeClasses(column.priority === 'low' && styles.low, column.align === 'end' && styles.end)}
                style={column.width ? { width: column.width } : undefined}
              >
                {column.header}
              </TableHeaderCell>
            ))}
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map((row, index) => (
            <TableRow key={getRowId(row, index)}>
              {columns.map((column) => (
                <TableCell key={column.key} className={cellClass(column)}>
                  {column.render(row)}
                </TableCell>
              ))}
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}
