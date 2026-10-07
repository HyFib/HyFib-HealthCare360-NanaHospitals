import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { zodResolver } from '@hookform/resolvers/zod';
import { DEFAULT_PRINT_TEMPLATE } from '@hc360/core';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useForm } from 'react-hook-form';
import { describe, expect, it, vi } from 'vitest';
import type { PrintTemplateItem, PrintTemplateList } from './api';
import { printTemplateFormSchema, toForm, type PrintTemplateForm, type TemplateScope } from './form';
import { PrintTemplateFields } from './PrintTemplateEditor';

const bills: PrintTemplateItem = {
  key: 'bill',
  name: 'Bills',
  area: 'Billing',
  covers: 'Tax invoices and bills of supply, the 80 mm counter bill and the IP running bill.',
  papers: [],
  allowsPreprinted: true,
  hospitalWide: DEFAULT_PRINT_TEMPLATE,
  facility: null,
};

const twoFacilities: PrintTemplateList = {
  facilityId: 'f1',
  facilityName: 'Nana Hospitals, Baner',
  facilityCount: 2,
  templates: [bills],
};

// The drawer around the fields is Fluent's; the fields themselves are what is tested (drawers flake in jsdom).
function Harness({
  item,
  list = twoFacilities,
  onScopeChange = () => {},
  onSubmit = () => {},
}: {
  item: PrintTemplateItem;
  list?: PrintTemplateList;
  onScopeChange?: (scope: TemplateScope) => void;
  onSubmit?: (values: PrintTemplateForm) => void;
}) {
  const form = useForm<PrintTemplateForm>({
    resolver: zodResolver(printTemplateFormSchema),
    defaultValues: toForm(item, 'hospital'),
  });
  return (
    <FluentProvider theme={webLightTheme}>
      <form onSubmit={form.handleSubmit(onSubmit)} noValidate>
        <PrintTemplateFields control={form.control} item={item} list={list} onScopeChange={onScopeChange} />
        <button type="submit">Save</button>
      </form>
    </FluentProvider>
  );
}

describe('PrintTemplateFields', () => {
  it('asks for the blank space only for pre-printed stationery, within what a page can give', async () => {
    const onSubmit = vi.fn();
    render(<Harness item={bills} onSubmit={onSubmit} />);

    expect(screen.queryByRole('spinbutton', { name: /Space at the top/ })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('radio', { name: 'Pre-printed stationery' }));
    const top = screen.getByRole('spinbutton', { name: /Space at the top/ });
    await userEvent.clear(top);
    await userEvent.type(top, '90');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('Enter between 0 and 80 mm.')).toBeInTheDocument();
    expect(onSubmit).not.toHaveBeenCalled();

    await userEvent.clear(top);
    await userEvent.type(top, '38');
    await userEvent.type(screen.getByRole('textbox', { name: /Closing note/ }), 'Subject to Pune jurisdiction.');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalled());
    expect(onSubmit.mock.calls[0]?.[0]).toMatchObject({
      letterhead: 'Preprinted',
      topSpaceMm: 38,
      closingNote: 'Subject to Pune jurisdiction.',
    });
  });

  it('never offers pre-printed paper for 80 mm rolls, and the paper only where there is a choice', () => {
    render(
      <Harness
        item={{ ...bills, key: 'sales-invoice', name: 'Pharmacy and canteen bills', allowsPreprinted: false }}
      />,
    );

    expect(screen.queryByRole('radio', { name: 'Pre-printed stationery' })).not.toBeInTheDocument();
    expect(screen.getByText(/never pre-printed, so the letterhead is always printed/)).toBeInTheDocument();
    expect(screen.queryByRole('combobox', { name: 'Paper' })).not.toBeInTheDocument();
  });

  it('offers the paper of prescriptions, A5 first', () => {
    render(<Harness item={{ ...bills, key: 'prescription', name: 'Prescriptions', papers: ['A5', 'A4'] }} />);

    const paper = screen.getByRole('combobox', { name: 'Paper' }) as HTMLSelectElement;
    expect(paper.value).toBe('A5');
    expect([...paper.options].map((o) => o.textContent)).toEqual(['A5 (half of A4)', 'A4']);
  });

  it('lets an administrator of several facilities set this facility apart', async () => {
    const onScopeChange = vi.fn();
    const { unmount } = render(<Harness item={bills} onScopeChange={onScopeChange} />);

    await userEvent.click(screen.getByRole('radio', { name: 'Only Nana Hospitals, Baner' }));
    expect(onScopeChange).toHaveBeenCalledWith('facility');
    unmount();

    render(<Harness item={bills} list={{ ...twoFacilities, facilityCount: 1 }} />);
    expect(screen.queryByRole('radio', { name: /Every facility/ })).not.toBeInTheDocument();
  });
});
