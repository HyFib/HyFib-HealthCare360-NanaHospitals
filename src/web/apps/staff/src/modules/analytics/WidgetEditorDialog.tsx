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
  Radio,
  RadioGroup,
  SearchBox,
  Subtitle2,
  Text,
  tokens,
} from '@fluentui/react-components';
import { ArrowLeftRegular, LockClosedRegular } from '@fluentui/react-icons';
import { isApiError } from '@hc360/core';
import { EmptyState } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import {
  analyticsQueries,
  type ReportCatalogue,
  type ReportCatalogueItem,
  type ReportChartType,
  type ReportInputs,
  type WidgetWidth,
} from './api';
import { availableViews, defaultView } from './charts';
import { CATEGORY_ICONS } from './icons';
import { CATEGORY_ORDER, WIDGET_WIDTHS } from './labels';
import { withDefaults } from './params';
import { ReportParameterForm } from './ReportInputs';
import { ViewSwitcher } from './ReportView';
import { WidgetBody } from './WidgetCard';

const useStyles = makeStyles({
  surface: { width: 'min(1000px, 96vw)', maxWidth: 'min(1000px, 96vw)' },
  picker: { display: 'flex', flexDirection: 'column', gap: '12px', minHeight: '360px' },
  list: {
    display: 'flex',
    flexDirection: 'column',
    gap: '14px',
    maxHeight: '52vh',
    overflowY: 'auto',
    paddingRight: '4px',
  },
  group: { display: 'flex', flexDirection: 'column', gap: '2px' },
  groupTitle: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    color: tokens.colorNeutralForeground3,
    padding: '4px 0',
  },
  option: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: '2px',
    padding: '8px 10px',
    border: 'none',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: 'transparent',
    color: tokens.colorNeutralForeground1,
    textAlign: 'start',
    cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorSubtleBackgroundHover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
  muted: { color: tokens.colorNeutralForeground3 },
  layout: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1.1fr)',
    gap: '20px',
    '@media (max-width: 860px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  column: { display: 'flex', flexDirection: 'column', gap: '12px', minWidth: 0 },
  chosen: { display: 'flex', alignItems: 'center', gap: '8px', justifyContent: 'space-between' },
  preview: {
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
    padding: '12px',
    backgroundColor: tokens.colorNeutralBackground2,
    minHeight: '260px',
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
});

export interface WidgetDraft {
  id: string;
  reportId: string;
  title: string;
  view: ReportChartType;
  width: WidgetWidth;
  parameters: ReportInputs;
}

/**
 * Adds or changes a dashboard tile: pick a report (patient lists are excluded), a view, a width and the inputs, with a
 * live preview of exactly what the tile will show.
 */
export function WidgetEditorDialog({
  catalogue,
  initial,
  newId,
  onSave,
  onClose,
}: {
  catalogue: ReportCatalogue;
  initial: WidgetDraft | null;
  newId: string;
  onSave: (widget: WidgetDraft) => void;
  onClose: () => void;
}) {
  const styles = useStyles();
  const [reportId, setReportId] = useState<string | null>(initial?.reportId ?? null);
  const [title, setTitle] = useState(initial?.title ?? '');
  const [view, setView] = useState<ReportChartType>(initial?.view ?? 'Bar');
  const [width, setWidth] = useState<WidgetWidth>(initial?.width ?? 2);
  const [parameters, setParameters] = useState<ReportInputs>(initial?.parameters ?? {});
  const report = catalogue.reports.find((r) => r.id === reportId);

  const choose = (next: ReportCatalogueItem) => {
    setReportId(next.id);
    setView(defaultView(next.chart));
    setParameters(withDefaults(next.parameters, {}));
    if (!title || title === report?.title) {
      setTitle(next.title);
    }
  };

  const values = report ? withDefaults(report.parameters, parameters) : {};
  const preview = useQuery({
    ...analyticsQueries.run(reportId ?? '', values, { page: 1, pageSize: view === 'Table' ? 8 : 1, chart: view }),
    enabled: Boolean(report),
  });
  const fieldErrors =
    isApiError(preview.error) && preview.error.isValidation ? firstMessages(preview.error.fieldErrors) : {};
  const invalid = Object.keys(fieldErrors).length > 0;

  const save = () => {
    if (!report || invalid) {
      return;
    }
    onSave({
      id: initial?.id ?? newId,
      reportId: report.id,
      title: title.trim() || report.title,
      view,
      width,
      parameters: values,
    });
  };

  return (
    <Dialog open onOpenChange={(_, data) => !data.open && onClose()}>
      <DialogSurface className={styles.surface}>
        <DialogBody>
          <DialogTitle>{initial ? 'Change tile' : 'Add a tile'}</DialogTitle>
          <DialogContent>
            {!report ? (
              <ReportPicker catalogue={catalogue} onChoose={choose} />
            ) : (
              <div className={styles.layout}>
                <div className={styles.column}>
                  <div className={styles.chosen}>
                    <div>
                      <Caption1 className={styles.muted}>Report</Caption1>
                      <Text block weight="semibold">
                        {report.title}
                      </Text>
                    </div>
                    <Button appearance="subtle" icon={<ArrowLeftRegular />} onClick={() => setReportId(null)}>
                      Choose another
                    </Button>
                  </div>
                  <Field label="Tile title">
                    <Input
                      value={title}
                      maxLength={80}
                      placeholder={report.title}
                      onChange={(_, data) => setTitle(data.value)}
                    />
                  </Field>
                  <Field label="Show as">
                    <ViewSwitcher views={availableViews(report.chart, 'dashboard')} value={view} onChange={setView} />
                  </Field>
                  <Field label="Width">
                    <RadioGroup
                      layout="horizontal"
                      value={String(width)}
                      onChange={(_, data) => setWidth(Number(data.value) as WidgetWidth)}
                    >
                      {WIDGET_WIDTHS.map((option) => (
                        <Radio key={option.value} value={String(option.value)} label={option.label} />
                      ))}
                    </RadioGroup>
                  </Field>
                  {report.parameters.length > 0 && (
                    <Field label="Inputs" hint="Use a period such as “This month” so the tile stays current.">
                      <ReportParameterForm
                        parameters={report.parameters}
                        values={values}
                        onChange={setParameters}
                        periods={catalogue.periods}
                        errors={fieldErrors}
                        mode="widget"
                        canConsolidate={catalogue.canConsolidate}
                        range={preview.data ? { from: preview.data.from, to: preview.data.to } : null}
                        maxDays={report.maxDays}
                      />
                    </Field>
                  )}
                </div>
                <div className={styles.preview} aria-label="Preview">
                  <Subtitle2>{title.trim() || report.title}</Subtitle2>
                  <WidgetBody
                    widget={{ title: title || report.title, view, width }}
                    result={preview.data}
                    error={invalid ? null : preview.error}
                    loading={preview.isPending}
                    onRetry={() => void preview.refetch()}
                  />
                </div>
              </div>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>Cancel</Button>
            <Button appearance="primary" disabled={!report || invalid} onClick={save}>
              {initial ? 'Update tile' : 'Add tile'}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

function firstMessages(errors: Record<string, string[]>): Record<string, string> {
  return Object.fromEntries(Object.entries(errors).map(([key, messages]) => [key, messages[0] ?? 'Check this input.']));
}

/** Searchable, grouped list of the reports that can go on a dashboard. */
function ReportPicker({
  catalogue,
  onChoose,
}: {
  catalogue: ReportCatalogue;
  onChoose: (report: ReportCatalogueItem) => void;
}) {
  const styles = useStyles();
  const [term, setTerm] = useState('');
  const words = term.trim().toLowerCase().split(/\s+/).filter(Boolean);
  const eligible = catalogue.reports.filter((r) => !r.containsPatientIdentifiers);
  const matches = eligible.filter((r) =>
    words.every((w) => `${r.title} ${r.description} ${r.categoryLabel}`.toLowerCase().includes(w)),
  );
  const hidden = catalogue.reports.length - eligible.length;

  return (
    <div className={styles.picker}>
      <SearchBox
        value={term}
        onChange={(_, data) => setTerm(data.value)}
        placeholder="Search reports, e.g. occupancy, revenue, TAT"
        autoFocus
        aria-label="Search reports"
      />
      <div className={styles.list}>
        {matches.length === 0 && (
          <EmptyState
            title="No report matches"
            description="Try another word, such as census, revenue or turnaround."
          />
        )}
        {CATEGORY_ORDER.map((category) => {
          const reports = matches.filter((r) => r.category === category);
          if (reports.length === 0) {
            return null;
          }
          const Icon = CATEGORY_ICONS[category];
          return (
            <section key={category} className={styles.group} aria-label={reports[0]?.categoryLabel}>
              <Caption1 className={styles.groupTitle}>
                <Icon aria-hidden />
                {reports[0]?.categoryLabel}
              </Caption1>
              {reports.map((r) => (
                <button key={r.id} type="button" className={styles.option} onClick={() => onChoose(r)}>
                  <Text weight="semibold">{r.title}</Text>
                  <Caption1 className={styles.muted}>{r.description}</Caption1>
                </button>
              ))}
            </section>
          );
        })}
      </div>
      {hidden > 0 && (
        <Caption1 className={styles.muted}>
          <LockClosedRegular aria-hidden /> Reports that list patients cannot be put on dashboards; open them from MIS
          reports.
        </Caption1>
      )}
    </div>
  );
}
