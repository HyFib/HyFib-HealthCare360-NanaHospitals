import { Badge, Button, Caption1, Text, Tooltip } from '@fluentui/react-components';
import { EditRegular, LockClosedRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatTime } from '@hc360/core';
import { DataTable, StatusBadge, type Column } from '@hc360/ui';
import type { ReactNode } from 'react';
import type { AttendanceDayDto } from './api';
import { attendanceFlagTone, attendanceTone, canRegularise, parseAttendanceFlags } from './attendance';
import { attendanceFlagLabel, attendanceStatusLabel } from './labels';
import { dayHeader, formatMinutes } from './shared/dates';

export function FlagBadges({ flags }: { flags: string }) {
  const parsed = parseAttendanceFlags(flags);
  if (parsed.length === 0) {
    return null;
  }
  return (
    <span style={{ display: 'inline-flex', gap: 4, flexWrap: 'wrap' }}>
      {parsed.map((flag) => (
        <Badge key={flag} size="small" appearance="tint" color={attendanceFlagTone(flag)}>
          {attendanceFlagLabel[flag]}
        </Badge>
      ))}
    </span>
  );
}

/**
 * Computed attendance days: in/out, worked time, late/early/overtime, status and flags. `showEmployee` for the daily
 * sheet; `onCorrect` adds a "Correct" action on days a regularisation can fix.
 */
export function AttendanceTable({
  rows,
  loading,
  error,
  onRetry,
  showEmployee,
  showDate = true,
  onCorrect,
  onRowClick,
  footer,
  page,
  pageSize,
  totalCount,
  onPageChange,
  emptyTitle = 'No attendance computed',
  emptyDescription = 'Attendance is computed every night for the previous two days. Use "Compute" for today or older days.',
}: {
  rows: AttendanceDayDto[] | undefined;
  loading: boolean;
  error: unknown;
  onRetry: () => void;
  showEmployee?: boolean;
  showDate?: boolean;
  onCorrect?: (day: AttendanceDayDto) => void;
  onRowClick?: (day: AttendanceDayDto) => void;
  footer?: ReactNode;
  page?: number;
  pageSize?: number;
  totalCount?: number;
  onPageChange?: (page: number) => void;
  emptyTitle?: string;
  emptyDescription?: string;
}) {
  const columns: Column<AttendanceDayDto>[] = [];
  if (showDate) {
    columns.push({
      key: 'date',
      header: 'Date',
      width: 130,
      render: (d) => {
        const head = dayHeader(d.date);
        return (
          <span>
            {formatDateOnly(d.date)} <Caption1>{head.weekday}</Caption1>
          </span>
        );
      },
    });
  }
  if (showEmployee) {
    columns.push({
      key: 'employee',
      header: 'Employee',
      render: (d) => (
        <span>
          <Text weight="semibold">{d.employeeName}</Text> <Caption1>{d.employeeCode}</Caption1>
        </span>
      ),
    });
  }
  columns.push(
    { key: 'shift', header: 'Shift', width: 70, render: (d) => d.shiftCode ?? '—' },
    { key: 'in', header: 'In', width: 90, render: (d) => formatTime(d.firstIn) },
    { key: 'out', header: 'Out', width: 90, render: (d) => formatTime(d.lastOut) },
    { key: 'worked', header: 'Worked', width: 100, render: (d) => formatMinutes(d.workedMinutes) },
    {
      key: 'late',
      header: 'Late / early',
      priority: 'low',
      render: (d) => [d.lateMinutes ? `${formatMinutes(d.lateMinutes)} late` : '', d.earlyExitMinutes ? `${formatMinutes(d.earlyExitMinutes)} early` : ''].filter(Boolean).join(' · ') || '—',
    },
    { key: 'ot', header: 'Overtime', priority: 'low', width: 100, render: (d) => formatMinutes(d.overtimeMinutes) },
    {
      key: 'status',
      header: 'Status',
      render: (d) => (
        <span style={{ display: 'inline-flex', gap: 4, alignItems: 'center' }}>
          <StatusBadge status={d.status} tone={attendanceTone(d.status)} label={attendanceStatusLabel[d.status]} />
          {d.isLocked && (
            <Tooltip content="Locked: payroll for this month is closed" relationship="label">
              <LockClosedRegular />
            </Tooltip>
          )}
        </span>
      ),
    },
    { key: 'flags', header: 'Notes', priority: 'low', render: (d) => <FlagBadges flags={d.flags} /> },
  );

  return (
    <>
      <DataTable
        ariaLabel="Attendance"
        rows={rows}
        loading={loading}
        error={error}
        onRetry={onRetry}
        getRowId={(d) => d.id}
        onRowClick={onRowClick}
        columns={columns}
        rowActions={
          onCorrect
            ? (d) =>
                canRegularise(d) ? (
                  <Tooltip content="Request a correction for this day" relationship="label">
                    <Button size="small" appearance="subtle" icon={<EditRegular />} onClick={() => onCorrect(d)}>
                      Correct
                    </Button>
                  </Tooltip>
                ) : null
            : undefined
        }
        emptyTitle={emptyTitle}
        emptyDescription={emptyDescription}
        page={page}
        pageSize={pageSize}
        totalCount={totalCount}
        onPageChange={onPageChange}
        size="small"
      />
      {footer}
    </>
  );
}
