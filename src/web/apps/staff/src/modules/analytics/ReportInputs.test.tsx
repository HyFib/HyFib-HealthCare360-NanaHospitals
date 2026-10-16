import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Core from '@hc360/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
  createMemoryHistory,
  createRootRoute,
  createRoute,
  createRouter,
  RouterProvider,
  useNavigate,
} from '@tanstack/react-router';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState, type FormEvent, type ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ReportCatalogue, ReportInputs, ReportParameter } from './api';
import { PERIOD_PRESETS } from './labels';
import { inputsOf, parseReportSearch, toSearchInputs, withDefaults } from './params';
import { ReportParameterForm } from './ReportInputs';
import { WidgetEditorDialog, type WidgetDraft } from './WidgetEditorDialog';

// "Today" decides which dates are in the future. The tile editor's preview never answers: only its inputs are tested.
vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  todayIso: () => '2026-10-02',
}));
vi.mock('@/lib/api', () => ({ api: { get: () => new Promise(() => {}) } }));

const PARAMETERS: ReportParameter[] = [
  {
    key: 'period',
    label: 'Period',
    kind: 'Period',
    required: false,
    defaultValue: null,
    choices: null,
    min: null,
    max: null,
    hint: null,
  },
  {
    key: 'days',
    label: 'Days overdue',
    kind: 'Number',
    required: false,
    defaultValue: '30',
    choices: null,
    min: 1,
    max: 365,
    hint: null,
  },
];

const SEPTEMBER: ReportInputs = { period: 'custom', from: '2026-09-01', to: '2026-09-30', days: '30' };

function renderWithProviders(ui: ReactNode) {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <FluentProvider theme={webLightTheme}>{ui}</FluentProvider>
    </QueryClientProvider>,
  );
}

/** The form as the tile editor and the schedule drawer hold it: the inputs in state, with the defaults filled in. */
function InputsInState({ onChange }: { onChange: (next: ReportInputs) => void }) {
  const [inputs, setInputs] = useState(SEPTEMBER);
  return (
    <>
      <ReportParameterForm
        parameters={PARAMETERS}
        values={withDefaults(PARAMETERS, inputs)}
        onChange={(next) => {
          onChange(next);
          setInputs(next);
        }}
      />
      {/* Changes from outside the form: the report page's "Reset inputs" and the browser's Back button. */}
      <button type="button" onClick={() => setInputs({})}>
        Reset inputs
      </button>
      <button type="button" onClick={() => setInputs(SEPTEMBER)}>
        Back
      </button>
    </>
  );
}

// The report page keeps the inputs in the address: a change navigates, and the new inputs come back a moment later.
const rootRoute = createRootRoute();
const reportRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/analytics/reports/$reportId',
  validateSearch: parseReportSearch,
  component: InputsInAddress,
});
const routeTree = rootRoute.addChildren([reportRoute]);

function InputsInAddress() {
  const search = reportRoute.useSearch();
  const navigate = useNavigate({ from: reportRoute.fullPath });
  return (
    <ReportParameterForm
      parameters={PARAMETERS}
      values={withDefaults(PARAMETERS, inputsOf(search))}
      onChange={(next) => void navigate({ search: toSearchInputs(next) })}
    />
  );
}

const CATALOGUE: ReportCatalogue = {
  categories: [{ id: 'Revenue', label: 'Revenue', count: 1 }],
  reports: [
    {
      id: 'overdue-bills',
      title: 'Overdue bills',
      category: 'Revenue',
      categoryLabel: 'Revenue',
      description: 'Bills not settled by their due date.',
      definition: 'Bills with a balance after their due date.',
      parameters: PARAMETERS,
      columns: [],
      chart: null,
      containsPatientIdentifiers: false,
      canSchedule: true,
      maxDays: 366,
    },
  ],
  periods: [...PERIOD_PRESETS],
  canManageDashboards: true,
  canManageSchedules: true,
  canSeePatientLevel: false,
  canConsolidate: false,
  dataSource: 'Primary',
};

const TILE: WidgetDraft = {
  id: 'w1',
  reportId: 'overdue-bills',
  title: 'Overdue bills',
  view: 'Table',
  width: 2,
  parameters: SEPTEMBER,
};

describe('ReportParameterForm', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('keeps focus in a number input when Enter applies it', async () => {
    const onChange = vi.fn();
    renderWithProviders(<InputsInState onChange={onChange} />);
    const days = screen.getByRole('spinbutton', { name: 'Days overdue' });

    await userEvent.clear(days);
    await userEvent.type(days, '90{Enter}');

    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange).toHaveBeenCalledWith({ ...SEPTEMBER, days: '90' });
    expect(days).toHaveFocus();
    expect(days).toHaveValue(90);
  });

  it('does not submit the form around the inputs when Enter applies a number', async () => {
    const onChange = vi.fn();
    const onSubmit = vi.fn((event: FormEvent) => event.preventDefault());
    // The schedule drawer's inputs sit in its form, whose submit button is outside it (form="…"). user-event presses
    // only a submit button inside the form on Enter, so this form has its own.
    renderWithProviders(
      <form onSubmit={onSubmit}>
        <InputsInState onChange={onChange} />
        <button type="submit">Save</button>
      </form>,
    );
    const days = screen.getByRole('spinbutton', { name: 'Days overdue' });

    await userEvent.clear(days);
    await userEvent.type(days, '90{Enter}');

    expect(onChange).toHaveBeenCalledWith({ ...SEPTEMBER, days: '90' });
    expect(onSubmit).not.toHaveBeenCalled();
    expect(days).toHaveFocus();
  });

  it('keeps focus in a date input when its date changes', async () => {
    const onChange = vi.fn();
    renderWithProviders(<InputsInState onChange={onChange} />);
    const from = screen.getByLabelText('From');

    await userEvent.clear(from);
    await userEvent.type(from, '2026-09-15');

    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange).toHaveBeenCalledWith({ ...SEPTEMBER, from: '2026-09-15' });
    expect(from).toHaveFocus();
    expect(from).toHaveValue('2026-09-15');
  });

  it('shows inputs changed from outside the form', async () => {
    renderWithProviders(<InputsInState onChange={() => {}} />);
    const days = screen.getByRole('spinbutton', { name: 'Days overdue' });
    const from = screen.getByLabelText('From');
    await userEvent.clear(days);
    await userEvent.type(days, '90{Enter}');
    await userEvent.clear(from);
    await userEvent.type(from, '2026-09-15');

    await userEvent.click(screen.getByRole('button', { name: 'Back' }));
    expect(days).toHaveValue(30);
    expect(from).toHaveValue('2026-09-01');
    expect(screen.getByLabelText('To')).toHaveValue('2026-09-30');

    await userEvent.clear(days);
    await userEvent.type(days, '120{Enter}');
    await userEvent.click(screen.getByRole('button', { name: 'Reset inputs' }));
    expect(days).toHaveValue(30);
    expect(screen.getByRole('combobox', { name: 'Period' })).toHaveValue('thisMonth');
    expect(screen.queryByLabelText('From')).not.toBeInTheDocument();

    // Custom dates start from the dates of the chosen preset.
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Period' }), 'Last 7 days');
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Period' }), 'Custom dates');
    expect(screen.getByLabelText('From')).toHaveValue('2026-09-26');
    expect(screen.getByLabelText('To')).toHaveValue('2026-10-02');
  });

  it('keeps focus while the inputs go through the address, as on the report page', async () => {
    // jsdom cannot scroll, which the router does after each navigation.
    vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
    const router = createRouter({
      routeTree,
      history: createMemoryHistory({
        initialEntries: ['/analytics/reports/overdue-bills?period=custom&from=2026-09-01&to=2026-09-30'],
      }),
    });
    renderWithProviders(<RouterProvider router={router} />);
    const days = await screen.findByRole('spinbutton', { name: 'Days overdue' });
    const from = screen.getByLabelText('From');

    await userEvent.clear(days);
    await userEvent.type(days, '90{Enter}');
    await waitFor(() => expect(router.history.location.search).toContain('days=90'));
    expect(days).toHaveFocus();
    expect(days).toHaveValue(90);

    await userEvent.clear(from);
    await userEvent.type(from, '2026-09-15');
    await waitFor(() => expect(router.history.location.search).toContain('from=2026-09-15'));
    expect(from).toHaveFocus();
    expect(from).toHaveValue('2026-09-15');
  });

  it('keeps focus in the tile editor dialog and saves what was entered', async () => {
    const onSave = vi.fn();
    renderWithProviders(
      <WidgetEditorDialog catalogue={CATALOGUE} initial={TILE} newId="w2" onSave={onSave} onClose={() => {}} />,
    );
    const dialog = await screen.findByRole('dialog', { name: 'Change tile' });
    const days = within(dialog).getByRole('spinbutton', { name: 'Days overdue' });
    const from = within(dialog).getByLabelText('From');

    await userEvent.clear(days);
    await userEvent.type(days, '90{Enter}');
    expect(days).toHaveFocus();
    // Enter in a number input applies the number; it does not save the tile as well.
    expect(onSave).not.toHaveBeenCalled();

    await userEvent.clear(from);
    await userEvent.type(from, '2026-09-15');
    expect(from).toHaveFocus();

    await userEvent.click(within(dialog).getByRole('button', { name: 'Update tile' }));
    expect(onSave).toHaveBeenCalledWith({
      ...TILE,
      parameters: { period: 'custom', from: '2026-09-15', to: '2026-09-30', days: '90' },
    });
  });

  it('saves the tile with Enter in its title', async () => {
    const onSave = vi.fn();
    renderWithProviders(
      <WidgetEditorDialog catalogue={CATALOGUE} initial={TILE} newId="w2" onSave={onSave} onClose={() => {}} />,
    );
    const dialog = await screen.findByRole('dialog', { name: 'Change tile' });
    const title = within(dialog).getByRole('textbox', { name: 'Tile title' });

    await userEvent.clear(title);
    await userEvent.type(title, 'Overdue bills{Enter}');
    expect(onSave).toHaveBeenCalledWith({ ...TILE, title: 'Overdue bills' });
  });
});
