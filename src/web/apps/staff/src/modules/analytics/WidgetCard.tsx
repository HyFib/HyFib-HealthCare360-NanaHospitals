import {
  Button,
  Caption1,
  makeStyles,
  mergeClasses,
  Menu,
  MenuItem,
  MenuItemRadio,
  MenuList,
  MenuPopover,
  MenuTrigger,
  ProgressBar,
  Skeleton,
  SkeletonItem,
  Subtitle2,
  tokens,
  Tooltip,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  ArrowLeftRegular,
  ArrowRightRegular,
  DeleteRegular,
  EditRegular,
  LockClosedRegular,
  MoreHorizontalRegular,
  OpenRegular,
} from '@fluentui/react-icons';
import { ErrorState } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import {
  analyticsQueries,
  type ReportCatalogueItem,
  type ReportChartType,
  type ReportInputs,
  type ReportResult,
  type WidgetWidth,
} from './api';
import { describeRange } from './format';
import { ReportTable } from './ReportTable';
import { ReportChart, SummaryFigures } from './ReportView';
import { WIDGET_WIDTHS } from './labels';
import { toSearchInputs } from './params';

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0,
    height: '100%',
    boxSizing: 'border-box',
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusXLarge,
    boxShadow: tokens.shadow4,
    overflow: 'hidden',
  },
  editing: { outline: `1px dashed ${tokens.colorBrandStroke1}`, outlineOffset: '-1px' },
  header: { display: 'flex', alignItems: 'flex-start', gap: '8px', padding: '12px 12px 4px 16px' },
  titles: { flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' },
  title: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  subtitle: {
    color: tokens.colorNeutralForeground3,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  body: { flex: 1, padding: '4px 16px 14px', minHeight: 0, display: 'flex', flexDirection: 'column', gap: '8px' },
  progress: { height: '2px' },
  locked: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    color: tokens.colorNeutralForeground3,
    padding: '24px 0',
  },
  actions: { display: 'flex', alignItems: 'center', gap: '2px', flexShrink: 0 },
  note: { color: tokens.colorNeutralForeground3 },
  iconLink: {
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: '28px',
    height: '28px',
    fontSize: '18px',
    borderRadius: tokens.borderRadiusMedium,
    color: tokens.colorNeutralForeground2,
    ':hover': { backgroundColor: tokens.colorSubtleBackgroundHover, color: tokens.colorBrandForeground1 },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
});

export interface WidgetConfig {
  id: string;
  reportId: string;
  title: string;
  view: ReportChartType;
  width: WidgetWidth;
  parameters: ReportInputs;
  canView: boolean;
}

export interface WidgetEditActions {
  onEdit: () => void;
  onRemove: () => void;
  onMove: (delta: -1 | 1) => void;
  onResize: (width: WidgetWidth) => void;
  isFirst: boolean;
  isLast: boolean;
}

/** Chart heights by tile width, so tiles in a row line up. */
function widgetHeight(width: WidgetWidth): number {
  return width === 1 ? 190 : 250;
}

/**
 * A dashboard tile: runs its report with its saved inputs (or the dashboard-wide period) and shows it as a chart, a short
 * table or headline figures, with a link to the full report.
 */
export function WidgetCard({
  widget,
  report,
  inputs,
  editing,
}: {
  widget: WidgetConfig;
  report: ReportCatalogueItem | undefined;
  inputs: ReportInputs;
  editing?: WidgetEditActions;
}) {
  const styles = useStyles();
  const runnable = widget.canView && report !== undefined;
  const run = useQuery({
    ...analyticsQueries.run(widget.reportId, inputs, {
      page: 1,
      pageSize: widget.view === 'Table' ? 8 : 1,
      chart: widget.view,
    }),
    enabled: runnable,
  });
  const result = run.data;

  return (
    <article className={mergeClasses(styles.card, editing && styles.editing)} aria-label={widget.title}>
      <div className={styles.progress}>
        {run.isFetching && result && <ProgressBar thickness="medium" aria-label="Refreshing" />}
      </div>
      <header className={styles.header}>
        <div className={styles.titles}>
          <Subtitle2 as="h3" className={styles.title} title={widget.title}>
            {widget.title}
          </Subtitle2>
          <Caption1 className={styles.subtitle}>
            {result ? coverage(result) : report ? report.categoryLabel : ' '}
          </Caption1>
        </div>
        <div className={styles.actions}>
          {runnable && (
            <Tooltip content="Open the full report" relationship="label">
              <Link
                to="/analytics/reports/$reportId"
                params={{ reportId: widget.reportId }}
                search={{ ...toSearchInputs(inputs), view: widget.view === 'Summary' ? undefined : widget.view }}
                className={styles.iconLink}
              >
                <OpenRegular aria-hidden />
              </Link>
            </Tooltip>
          )}
          {editing && <EditMenu widget={widget} editing={editing} />}
        </div>
      </header>
      <div className={styles.body}>
        {!widget.canView ? (
          <div className={styles.locked}>
            <LockClosedRegular aria-hidden />
            <Caption1>You do not have access to this report. Ask the dashboard owner or your administrator.</Caption1>
          </div>
        ) : !report ? (
          <Caption1 className={styles.note}>
            This report is no longer available. Remove the tile or choose another report.
          </Caption1>
        ) : (
          <WidgetBody
            widget={widget}
            result={result}
            error={run.error}
            loading={run.isPending}
            onRetry={() => void run.refetch()}
          />
        )}
      </div>
    </article>
  );
}

function EditMenu({ widget, editing }: { widget: WidgetConfig; editing: WidgetEditActions }) {
  // Closing the tile editor opened from this menu returns focus to the menu button.
  const restoreFocusTarget = useRestoreFocusTarget();
  return (
    <Menu
      checkedValues={{ width: [String(widget.width)] }}
      onCheckedValueChange={(_, data) => editing.onResize(Number(data.checkedItems[0]) as WidgetWidth)}
    >
      <MenuTrigger disableButtonEnhancement>
        <Button
          appearance="subtle"
          size="small"
          icon={<MoreHorizontalRegular />}
          aria-label={`Change ${widget.title}`}
          {...restoreFocusTarget}
        />
      </MenuTrigger>
      <MenuPopover>
        <MenuList>
          <MenuItem icon={<EditRegular />} onClick={editing.onEdit}>
            Change report or inputs
          </MenuItem>
          <MenuItem icon={<ArrowLeftRegular />} disabled={editing.isFirst} onClick={() => editing.onMove(-1)}>
            Move earlier
          </MenuItem>
          <MenuItem icon={<ArrowRightRegular />} disabled={editing.isLast} onClick={() => editing.onMove(1)}>
            Move later
          </MenuItem>
          {WIDGET_WIDTHS.map((width) => (
            <MenuItemRadio key={width.value} name="width" value={String(width.value)}>
              {width.label}
            </MenuItemRadio>
          ))}
          <MenuItem icon={<DeleteRegular />} onClick={editing.onRemove}>
            Remove from dashboard
          </MenuItem>
        </MenuList>
      </MenuPopover>
    </Menu>
  );
}

/** "01 Sept 2026 – 28 Sept 2026 · Main hospital", or "Current position" for snapshot reports. */
function coverage(result: ReportResult): string {
  return `${result.from && result.to ? describeRange(result.from, result.to) : 'Current position'} · ${result.facilityName}`;
}

/** The tile's content for its view; falls back to the table when the report has no chart or no headline figures. */
export function WidgetBody({
  widget,
  result,
  error,
  loading,
  onRetry,
}: {
  widget: Pick<WidgetConfig, 'title' | 'view' | 'width'>;
  result: ReportResult | undefined;
  error: unknown;
  loading: boolean;
  onRetry: () => void;
}) {
  const styles = useStyles();
  if (error && !result) {
    return <ErrorState error={error} onRetry={onRetry} title="Could not load this tile" />;
  }
  if (loading || !result) {
    return (
      <Skeleton aria-label="Loading">
        <SkeletonItem shape="rectangle" style={{ height: widgetHeight(widget.width) - 20 }} />
      </Skeleton>
    );
  }
  if (widget.view === 'Summary' && result.summary.length > 0) {
    return <SummaryFigures figures={result.summary} compact />;
  }
  if (widget.view !== 'Table' && widget.view !== 'Summary' && result.chart) {
    return <ReportChart chart={result.chart} title={widget.title} height={widgetHeight(widget.width)} compact />;
  }
  return (
    <>
      {widget.view === 'Summary' && (
        <Caption1 className={styles.note}>This report has no headline figures, so the table is shown.</Caption1>
      )}
      <ReportTable result={result} compact maxRows={8} />
    </>
  );
}
