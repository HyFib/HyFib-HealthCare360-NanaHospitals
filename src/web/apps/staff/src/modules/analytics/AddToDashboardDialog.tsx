import {
  Button,
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
  Radio,
  RadioGroup,
  Select,
  Spinner,
} from '@fluentui/react-components';
import { useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { analyticsApi, analyticsKeys, analyticsQueries, type ReportCatalogueItem, type ReportChartType, type ReportResult, type SaveDashboardWidget, type WidgetWidth } from './api';
import { availableViews, defaultView } from './charts';
import { CUSTOM_PERIOD, WIDGET_WIDTHS } from './labels';
import { nextWidgetId } from './params';
import { ViewSwitcher } from './ReportView';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

const NEW = '__new';

/** Puts the report, with the inputs on screen, on one of the user's dashboards (or on a new one). */
export function AddToDashboardDialog({ report, result, view, onClose }: { report: ReportCatalogueItem; result: ReportResult; view: ReportChartType; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const dashboards = useQuery(analyticsQueries.dashboards());
  const editable = (dashboards.data ?? []).filter((d) => d.canEdit && !d.isTemplate);
  const views = availableViews(report.chart, 'dashboard');
  const [target, setTarget] = useState<string | null>(null);
  const [name, setName] = useState('');
  const [title, setTitle] = useState(report.title);
  const [chosenView, setView] = useState<ReportChartType>(views.includes(view) ? view : defaultView(report.chart));
  const [width, setWidth] = useState<WidgetWidth>(2);
  const selected = target ?? editable[0]?.id ?? NEW;
  const customDates = result.parameters.period === CUSTOM_PERIOD;

  const save = useMutation({
    mutationFn: async () => {
      const widget: SaveDashboardWidget = { id: null, reportId: report.id, title: title.trim() || null, view: chosenView, width, parameters: result.parameters };
      if (selected === NEW) {
        return analyticsApi.createDashboard({ name: name.trim(), description: null, isShared: false, widgets: [{ ...widget, id: 'w1' }], version: null });
      }
      const current = await queryClient.fetchQuery({ ...analyticsQueries.dashboard(selected), staleTime: 0 });
      const widgets: SaveDashboardWidget[] = current.widgets.map((w) => ({ id: w.id, reportId: w.reportId, title: w.title, view: w.view, width: w.width, parameters: w.parameters }));
      widgets.push({ ...widget, id: nextWidgetId(widgets.map((w) => w.id ?? '')) });
      return analyticsApi.updateDashboard(selected, {
        name: current.name,
        description: current.description,
        isShared: current.isShared,
        widgets,
        version: current.version,
      });
    },
    onSuccess: (dashboard) => {
      void queryClient.invalidateQueries({ queryKey: analyticsKeys.dashboards });
      notify.success(`Added to “${dashboard.name}”`);
      onClose();
      void navigate({ to: '/analytics/dashboards/$dashboardId', params: { dashboardId: dashboard.id } });
    },
    onError: (error) => notify.error(error, 'Could not add the report to the dashboard'),
  });

  const canSave = selected !== NEW || name.trim().length > 0;

  return (
    <Dialog open onOpenChange={(_, data) => !data.open && onClose()}>
      <DialogSurface>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (canSave && !save.isPending) {
              save.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Add to a dashboard</DialogTitle>
            <DialogContent className={styles.form}>
              {dashboards.isLoading ? (
                <Spinner size="small" label="Loading your dashboards…" />
              ) : (
                <Field label="Dashboard">
                  <Select value={selected} onChange={(_, data) => setTarget(data.value)}>
                    {editable.map((d) => (
                      <option key={d.id} value={d.id}>
                        {d.name}
                      </option>
                    ))}
                    <option value={NEW}>New dashboard…</option>
                  </Select>
                </Field>
              )}
              {selected === NEW && (
                <Field label="Name of the new dashboard" required>
                  <Input value={name} maxLength={100} autoFocus onChange={(_, data) => setName(data.value)} placeholder="For example: Morning review" />
                </Field>
              )}
              <Field label="Tile title">
                <Input value={title} maxLength={80} onChange={(_, data) => setTitle(data.value)} />
              </Field>
              <Field label="Show as">
                <ViewSwitcher views={views} value={chosenView} onChange={setView} />
              </Field>
              <Field label="Width">
                <RadioGroup layout="horizontal" value={String(width)} onChange={(_, data) => setWidth(Number(data.value) as WidgetWidth)}>
                  {WIDGET_WIDTHS.map((option) => (
                    <Radio key={option.value} value={String(option.value)} label={option.label} />
                  ))}
                </RadioGroup>
              </Field>
              {customDates && (
                <MessageBar intent="info">
                  <MessageBarBody>The tile keeps these fixed dates. To keep it current, choose a period such as “This month” before adding it.</MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose}>Cancel</Button>
              <Button appearance="primary" type="submit" disabled={!canSave || save.isPending} icon={save.isPending ? <Spinner size="tiny" /> : undefined}>
                Add tile
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
