import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { zodResolver } from '@hookform/resolvers/zod';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useForm } from 'react-hook-form';
import { describe, expect, it, vi } from 'vitest';
import type { NumberSeriesItem } from './api';
import { numberSeriesFormSchema, toForm, type EditorContext, type NumberSeriesForm } from './form';
import { NumberSeriesFields } from './NumberSeriesEditor';

const october2 = { year: 2026, month: 10, day: 2 };

const context: EditorContext = {
  facilityCount: 2,
  longestFacilityCode: 2,
  otherFormats: [{ name: 'IP bill', format: '{FAC}IB{YY}{SEQ:7}', isDefault: true }],
};

function series(overrides: Partial<NumberSeriesItem> = {}): NumberSeriesItem {
  return {
    key: 'billing.bill.op',
    name: 'OP bill',
    area: 'Billing',
    scope: 'facility',
    format: '{FAC}OB{YY}{SEQ:7}',
    resetPolicy: 'Yearly',
    lastNumber: 123,
    nextExample: 'NHOB260000124',
    facilityCode: 'NH',
    isCustomised: false,
    isLocked: false,
    lockedReason: null,
    maxLength: 24,
    defaultFormat: '{FAC}OB{YY}{SEQ:7}',
    defaultResetPolicy: 'Yearly',
    ...overrides,
  };
}

// The drawer around the fields is Fluent's; the fields themselves are what is tested (drawers flake in jsdom).
function Harness({
  item,
  onSubmit = () => {},
}: {
  item: NumberSeriesItem;
  onSubmit?: (values: NumberSeriesForm) => void;
}) {
  const form = useForm<NumberSeriesForm>({
    resolver: zodResolver(numberSeriesFormSchema(item, context)),
    defaultValues: toForm(item),
  });
  return (
    <FluentProvider theme={webLightTheme}>
      <form onSubmit={form.handleSubmit(onSubmit)}>
        <NumberSeriesFields
          control={form.control}
          setValue={form.setValue}
          series={item}
          context={context}
          today={october2}
        />
        <button type="submit">Save</button>
      </form>
    </FluentProvider>
  );
}

const formatBox = () => screen.getByRole('textbox', { name: /Format/ }) as HTMLInputElement;

describe('NumberSeriesFields', () => {
  it('shows the next number and the checks while the format is typed, in capitals', async () => {
    render(<Harness item={series()} />);

    expect(screen.getByText('NHOB260000124')).toBeInTheDocument();
    expect(screen.getByText('Different from the other number formats.')).toBeInTheDocument();
    expect(screen.getByText(/From 01 Jan 2027 numbers start again/)).toBeInTheDocument();

    await userEvent.clear(formatBox());
    await userEvent.type(formatBox(), 'nh/op/{{yy}/{{seq:5}');

    expect(formatBox()).toHaveValue('NH/OP/{YY}/{SEQ:5}');
    expect(screen.getByText('NH/OP/26/00124')).toBeInTheDocument();
    // Two facilities count OP bills separately: without the facility code they would issue the same numbers.
    expect(
      screen.getByText(/Add \{FAC\}\. Each facility counts its own numbers, so without the facility code/),
    ).toBeInTheDocument();
  });

  it('inserts a part where the cursor is', async () => {
    render(<Harness item={series({ format: 'OB{YY}{SEQ:7}' })} />);
    const input = formatBox();
    input.focus();
    input.setSelectionRange(0, 0);

    await userEvent.click(screen.getByRole('button', { name: '{FAC}' }));

    expect(input).toHaveValue('{FAC}OB{YY}{SEQ:7}');
    await waitFor(() => expect(input.selectionStart).toBe(5));
  });

  it('explains what is wrong as soon as it is typed', async () => {
    render(<Harness item={series()} />);

    await userEvent.clear(formatBox());
    // user-event types "{{" as one "{".
    await userEvent.type(formatBox(), '{{FAC}IB{{YY}{{SEQ:7}');

    expect(
      screen.getByText(
        'IP bill already uses this format. Change the fixed text so the two kinds of number can be told apart.',
      ),
    ).toBeInTheDocument();
  });

  it('restores the default format and reset', async () => {
    render(<Harness item={series({ format: '{FAC}/OP/{YY}/{SEQ:6}', resetPolicy: 'Monthly', isCustomised: true })} />);

    expect(screen.getByText(/starts again every year/)).toHaveTextContent(
      'Default: {FAC}OB{YY}{SEQ:7}, starts again every year',
    );
    await userEvent.click(screen.getByRole('button', { name: 'Restore default' }));

    expect(formatBox()).toHaveValue('{FAC}OB{YY}{SEQ:7}');
    expect(screen.getByRole('combobox', { name: /Start again from 1/ })).toHaveValue('Yearly');
    expect(screen.queryByRole('button', { name: 'Restore default' })).not.toBeInTheDocument();
  });

  it('refuses a next number that has already been issued', async () => {
    const onSubmit = vi.fn();
    render(<Harness item={series()} onSubmit={onSubmit} />);

    await userEvent.type(screen.getByRole('spinbutton', { name: /Next number/ }), '120');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect(
      await screen.findByText('Number 123 has already been issued this year. Enter a number above 123.'),
    ).toBeInTheDocument();
    expect(onSubmit).not.toHaveBeenCalled();
  });

  it('offers only the next number of a locked series, and says why', async () => {
    const onSubmit = vi.fn();
    const uhid = series({
      key: 'uhid',
      name: 'UHID (patient number)',
      area: 'Patients',
      scope: 'global',
      format: '{FAC}{YY}{SEQ:6}',
      resetPolicy: 'Never',
      defaultFormat: '{FAC}{YY}{SEQ:6}',
      defaultResetPolicy: 'Never',
      lastNumber: 4000,
      isLocked: true,
      lockedReason: 'The AI assistant recognises UHIDs by their shape, so the format cannot change.',
      maxLength: 20,
    });
    render(<Harness item={uhid} onSubmit={onSubmit} />);

    expect(screen.getByText('The format is fixed')).toBeInTheDocument();
    expect(screen.getByText(/recognises UHIDs by their shape/)).toBeInTheDocument();
    expect(screen.queryByRole('textbox', { name: /Format/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Restore default' })).not.toBeInTheDocument();
    expect(screen.getByText('Shared by all facilities', { exact: false })).toBeInTheDocument();

    await userEvent.type(screen.getByRole('spinbutton', { name: /Next number/ }), '250001');

    expect(screen.getByText('NH26250001')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(onSubmit).toHaveBeenCalled());
    expect(onSubmit.mock.calls[0]?.[0]).toMatchObject({ startAt: 250001 });
  });
});
