import {
  Button,
  makeStyles,
  Skeleton,
  SkeletonItem,
  Table,
  TableBody,
  TableCell,
  TableCellLayout,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  tokens,
  useArrowNavigationGroup,
} from '@fluentui/react-components';
import { ChevronLeftRegular, ChevronRightRegular } from '@fluentui/react-icons';
import type { KeyboardEvent, ReactNode } from 'react';
import { EmptyState, ErrorState } from '../feedback/feedback';

export interface Column<T> {
  key: string;
  header: ReactNode;
  render: (row: T) => ReactNode;
  width?: number | string;
  sortable?: boolean;
  align?: 'start' | 'end' | 'center';
  /** Hide on narrow screens (tablets on ward rounds). */
  priority?: 'high' | 'low';
}

export interface SortState {
  key: string;
  desc: boolean;
}

export interface DataTableProps<T> {
  ariaLabel: string;
  columns: Column<T>[];
  rows: T[] | undefined;
  getRowId: (row: T) => string;
  loading?: boolean;
  error?: unknown;
  onRetry?: () => void;
  onRowClick?: (row: T) => void;
  rowActions?: (row: T) => ReactNode;
  sort?: SortState;
  onSortChange?: (sort: SortState) => void;
  emptyTitle?: string;
  emptyDescription?: string;
  emptyAction?: ReactNode;
  page?: number;
  pageSize?: number;
  totalCount?: number;
  onPageChange?: (page: number) => void;
  size?: 'small' | 'medium';
}

const useStyles = makeStyles({
  wrapper: { overflowX: 'auto', width: '100%' },
  row: { cursor: 'pointer' },
  lowPriority: { '@media (max-width: 900px)': { display: 'none' } },
  alignEnd: { textAlign: 'end', justifyContent: 'flex-end' },
  footer: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: '8px 16px',
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    gap: '8px',
    flexWrap: 'wrap',
  },
  muted: { color: tokens.colorNeutralForeground3 },
  actionsCell: { width: '1%', whiteSpace: 'nowrap' },
});

/**
 * Server-paged table with sorting, keyboard navigation (arrow keys between cells, Enter opens the row),
 * skeleton loading, empty and error states (SRS UX-02/07/12).
 */
export function DataTable<T>(props: DataTableProps<T>) {
  const styles = useStyles();
  const navigation = useArrowNavigationGroup({ axis: 'grid' });
  const { columns, rows, loading, error } = props;

  if (error && !rows) {
    return <ErrorState error={error} onRetry={props.onRetry} />;
  }

  const showSkeleton = loading && !rows;
  const colClass = (column: Column<T>) =>
    [column.priority === 'low' ? styles.lowPriority : '', column.align === 'end' ? styles.alignEnd : ''].filter(Boolean).join(' ');

  const onRowKey = (event: KeyboardEvent, row: T) => {
    if (event.key === 'Enter' && props.onRowClick) {
      event.preventDefault();
      props.onRowClick(row);
    }
  };

  return (
    <div>
      <div className={styles.wrapper}>
        <Table aria-label={props.ariaLabel} size={props.size ?? 'medium'} sortable={Boolean(props.onSortChange)} {...navigation}>
          <TableHeader>
            <TableRow>
              {columns.map((column) => {
                const active = props.sort?.key === column.key;
                return (
                  <TableHeaderCell
                    key={column.key}
                    className={colClass(column)}
                    style={column.width ? { width: column.width } : undefined}
                    sortDirection={active ? (props.sort?.desc ? 'descending' : 'ascending') : undefined}
                    onClick={
                      column.sortable && props.onSortChange
                        ? () => props.onSortChange?.({ key: column.key, desc: active ? !props.sort?.desc : false })
                        : undefined
                    }
                  >
                    {column.header}
                  </TableHeaderCell>
                );
              })}
              {props.rowActions && <TableHeaderCell className={styles.actionsCell} aria-label="Actions" />}
            </TableRow>
          </TableHeader>
          <TableBody>
            {showSkeleton &&
              Array.from({ length: 6 }, (_, index) => (
                <TableRow key={`skeleton-${index}`}>
                  {columns.map((column) => (
                    <TableCell key={column.key} className={colClass(column)}>
                      <Skeleton aria-label="Loading">
                        <SkeletonItem />
                      </Skeleton>
                    </TableCell>
                  ))}
                  {props.rowActions && <TableCell />}
                </TableRow>
              ))}
            {!showSkeleton &&
              rows?.map((row) => (
                <TableRow
                  key={props.getRowId(row)}
                  className={props.onRowClick ? styles.row : undefined}
                  onClick={props.onRowClick ? () => props.onRowClick?.(row) : undefined}
                  onKeyDown={(event) => onRowKey(event, row)}
                  tabIndex={props.onRowClick ? 0 : undefined}
                >
                  {columns.map((column) => (
                    <TableCell key={column.key} className={colClass(column)}>
                      <TableCellLayout truncate>{column.render(row)}</TableCellLayout>
                    </TableCell>
                  ))}
                  {props.rowActions && (
                    <TableCell className={styles.actionsCell} onClick={(event) => event.stopPropagation()}>
                      {props.rowActions(row)}
                    </TableCell>
                  )}
                </TableRow>
              ))}
          </TableBody>
        </Table>
      </div>

      {!showSkeleton && rows && rows.length === 0 && (
        <EmptyState title={props.emptyTitle ?? 'Nothing here yet'} description={props.emptyDescription} action={props.emptyAction} />
      )}

      {props.onPageChange && props.totalCount !== undefined && props.totalCount > 0 && (
        <Pagination page={props.page ?? 1} pageSize={props.pageSize ?? 25} totalCount={props.totalCount} onPageChange={props.onPageChange} />
      )}
    </div>
  );
}

export function Pagination({ page, pageSize, totalCount, onPageChange }: { page: number; pageSize: number; totalCount: number; onPageChange: (page: number) => void }) {
  const styles = useStyles();
  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize));
  const from = (page - 1) * pageSize + 1;
  const to = Math.min(page * pageSize, totalCount);
  return (
    <div className={styles.footer}>
      <Text size={200} className={styles.muted}>
        Showing {from.toLocaleString('en-IN')}–{to.toLocaleString('en-IN')} of {totalCount.toLocaleString('en-IN')}
      </Text>
      <div>
        <Button appearance="subtle" icon={<ChevronLeftRegular />} disabled={page <= 1} onClick={() => onPageChange(page - 1)} aria-label="Previous page" />
        <Text size={200} style={{ margin: '0 8px' }}>
          Page {page} of {totalPages}
        </Text>
        <Button appearance="subtle" icon={<ChevronRightRegular />} disabled={page >= totalPages} onClick={() => onPageChange(page + 1)} aria-label="Next page" />
      </div>
    </div>
  );
}
