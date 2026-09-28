import { Badge, Button, Caption1, Field, Input, makeStyles, mergeClasses, Select, Text, ToggleButton, tokens } from '@fluentui/react-components';
import {
  BeakerRegular,
  ClipboardPulseRegular,
  DismissRegular,
  DocumentRegular,
  DocumentTextRegular,
  HeartPulseRegular,
  NoteRegular,
  PillRegular,
  StethoscopeRegular,
  SyringeRegular,
  TaskListLtrRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, formatTime, todayIso } from '@hc360/core';
import { EmptyState, ErrorState, LoadingState, Pagination, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import type { ReactElement } from 'react';
import { toOptions, useDepartments } from '@/lib/lookups';
import { emrQueries, type TimelineFilters } from './api';
import { groupByDay, prettyTitle, TIMELINE_TYPES, timelineTarget, timelineTypeLabel, toggleType, type TimelineEntry, type TimelineEntryType, type TimelineTarget } from './timeline';

const useStyles = makeStyles({
  filters: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'flex-end', marginBottom: '12px' },
  types: { display: 'flex', gap: '4px', flexWrap: 'wrap', marginBottom: '12px' },
  day: { display: 'flex', flexDirection: 'column', gap: '4px', marginBottom: '12px' },
  dayLabel: { padding: '4px 0', fontWeight: tokens.fontWeightSemibold, borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  entry: {
    display: 'grid',
    gridTemplateColumns: '64px 28px minmax(0, 1fr) auto',
    gap: '8px',
    alignItems: 'start',
    padding: '8px',
    borderRadius: tokens.borderRadiusMedium,
    cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
  critical: { borderLeft: `3px solid ${tokens.colorPaletteRedBorder2}` },
  icon: { fontSize: '20px', color: tokens.colorBrandForeground1, display: 'flex', paddingTop: '2px' },
  text: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  summary: { color: tokens.colorNeutralForeground2, overflow: 'hidden', textOverflow: 'ellipsis', display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical' },
  muted: { color: tokens.colorNeutralForeground3 },
});

const TYPE_ICON: Record<TimelineEntryType, ReactElement> = {
  Encounter: <StethoscopeRegular />,
  Vitals: <HeartPulseRegular />,
  Diagnosis: <TaskListLtrRegular />,
  Prescription: <PillRegular />,
  Order: <ClipboardPulseRegular />,
  Result: <BeakerRegular />,
  Note: <NoteRegular />,
  Immunization: <SyringeRegular />,
  Document: <DocumentRegular />,
  DischargeSummary: <DocumentTextRegular />,
};

/** The longitudinal record across OP, IP, emergency and health check-ups, filterable by type, date and department. */
export function TimelinePanel({
  patientId,
  filters,
  onFiltersChange,
  onOpen,
}: {
  patientId: string;
  filters: TimelineFilters;
  onFiltersChange: (filters: TimelineFilters) => void;
  onOpen: (target: TimelineTarget) => void;
}) {
  const styles = useStyles();
  const departments = useDepartments('Clinical');
  const timeline = useQuery(emrQueries.timeline(patientId, filters));
  const today = todayIso();
  const set = (patch: Partial<TimelineFilters>) => onFiltersChange({ ...filters, ...patch, page: undefined });
  const filtered = Boolean(filters.types || filters.from || filters.to || filters.departmentId);
  const days = groupByDay(timeline.data?.items ?? []);

  return (
    <div>
      <div className={styles.types} role="group" aria-label="Show entry types">
        {TIMELINE_TYPES.map((type) => (
          <ToggleButton
            key={type}
            size="small"
            shape="circular"
            icon={TYPE_ICON[type]}
            checked={filters.types?.includes(type) ?? false}
            onClick={() => set({ types: toggleType(filters.types, type) })}
          >
            {timelineTypeLabel[type]}
          </ToggleButton>
        ))}
      </div>
      <div className={styles.filters}>
        <Field label="From">
          <Input type="date" value={filters.from ?? ''} max={filters.to ?? today} onChange={(_, d) => set({ from: d.value || undefined })} />
        </Field>
        <Field label="To">
          <Input type="date" value={filters.to ?? ''} min={filters.from} max={today} onChange={(_, d) => set({ to: d.value || undefined })} />
        </Field>
        <Field label="Department">
          <Select value={filters.departmentId ?? ''} onChange={(_, d) => set({ departmentId: d.value || undefined })}>
            <option value="">All departments</option>
            {toOptions(departments.data).map((o) => (
              <option key={o.value} value={o.value}>
                {o.label}
              </option>
            ))}
          </Select>
        </Field>
        {filtered && (
          <Button appearance="subtle" icon={<DismissRegular />} onClick={() => onFiltersChange({})}>
            Clear filters
          </Button>
        )}
      </div>

      {timeline.isLoading ? (
        <LoadingState />
      ) : timeline.error ? (
        <ErrorState error={timeline.error} onRetry={() => void timeline.refetch()} />
      ) : days.length === 0 ? (
        <EmptyState
          title={filtered ? 'Nothing matches these filters' : 'No clinical history yet'}
          description={filtered ? 'Clear the filters to see everything.' : 'Encounters, vitals, orders, prescriptions and results appear here as they happen.'}
        />
      ) : (
        <>
          {days.map((day) => (
            <section key={day.day} className={styles.day} aria-label={formatDateOnly(day.day)}>
              <Text className={styles.dayLabel}>{formatDateOnly(day.day)}</Text>
              {day.entries.map((entry: TimelineEntry) => (
                <div
                  key={`${entry.type}-${entry.id}`}
                  className={mergeClasses(styles.entry, entry.isCritical ? styles.critical : undefined)}
                  role="button"
                  tabIndex={0}
                  onClick={() => onOpen(timelineTarget(entry))}
                  onKeyDown={(event) => {
                    if (event.key === 'Enter' || event.key === ' ') {
                      event.preventDefault();
                      onOpen(timelineTarget(entry));
                    }
                  }}
                >
                  <Caption1 className={styles.muted}>{formatTime(entry.occurredAt)}</Caption1>
                  <span className={styles.icon} aria-hidden>
                    {TYPE_ICON[entry.type]}
                  </span>
                  <span className={styles.text}>
                    <Text weight="semibold">{prettyTitle(entry)}</Text>
                    {entry.summary && <Caption1 className={styles.summary}>{entry.summary}</Caption1>}
                  </span>
                  <span>
                    {entry.isCritical && (
                      <Badge color="danger" size="small" style={{ marginRight: 4 }}>
                        Critical
                      </Badge>
                    )}
                    {entry.status && <StatusBadge status={entry.status === 'InProgress' ? 'Draft' : entry.status} label={entry.status === 'InProgress' ? 'Unsigned' : undefined} />}
                  </span>
                </div>
              ))}
            </section>
          ))}
          {(timeline.data?.totalCount ?? 0) > 30 && (
            <Pagination page={filters.page ?? 1} pageSize={30} totalCount={timeline.data?.totalCount ?? 0} onPageChange={(page) => onFiltersChange({ ...filters, page: page > 1 ? page : undefined })} />
          )}
        </>
      )}
    </div>
  );
}
