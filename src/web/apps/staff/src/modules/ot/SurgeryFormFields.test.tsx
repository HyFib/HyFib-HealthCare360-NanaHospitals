import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useEffect } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { useForm, type UseFormReturn } from 'react-hook-form';
import { fakeServer } from '@/test/appScreen';
import { renderPicker, typeAndChooseAsTheSearchRuns } from '@/test/searchPickers';
import { AssistantsPicker, ProcedurePicker } from './SurgeryFormFields';
import { emptySurgeryForm, type SurgeryFormValues } from './surgeryForm';

let form: UseFormReturn<SurgeryFormValues>;

/** Owns the form the picker writes to, and hands it to the test to read the values back. */
function useHostForm() {
  const hosted = useForm<SurgeryFormValues>({ defaultValues: emptySurgeryForm });
  useEffect(() => {
    form = hosted;
  }, [hosted]);
  return hosted;
}

function ProcedureHost() {
  return <ProcedurePicker form={useHostForm()} />;
}

function AssistantsHost() {
  return <AssistantsPicker form={useHostForm()} />;
}

const CHOLECYSTECTOMY = { id: 's1', code: 'SRG-001', name: 'Laparoscopic cholecystectomy', basePrice: 45000 };
const DR_RAO = { id: 'd1', code: 'DR1', name: 'Dr. Kavita Rao' };

describe('ProcedurePicker', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('a procedure chosen as the search for what was typed runs is still the one chosen', async () => {
    fakeServer({ 'GET /api/v1/masters/lookups/services': [CHOLECYSTECTOMY] });
    const user = userEvent.setup();
    renderPicker(<ProcedureHost />);
    const combobox = screen.getByRole('combobox', { name: /Planned procedure/ });

    await user.type(combobox, 'Lap');
    await screen.findByRole('option', { name: /Laparoscopic cholecystectomy/ });
    await typeAndChooseAsTheSearchRuns(user, combobox, 'a', /Laparoscopic cholecystectomy/);

    await waitFor(() => expect(form.getValues('procedureServiceItemId')).toBe('s1'));
    expect(form.getValues('procedureName')).toBe('Laparoscopic cholecystectomy');
  });
});

describe('AssistantsPicker', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('an assistant chosen as the search for what was typed runs is still the one chosen, and is named', async () => {
    fakeServer({ 'GET /api/v1/masters/lookups/practitioners': [DR_RAO] });
    const user = userEvent.setup();
    renderPicker(<AssistantsHost />);
    const combobox = screen.getByRole('combobox', { name: /Assistant surgeons/ });

    await user.type(combobox, 'Rao');
    await screen.findByRole('menuitemcheckbox', { name: /Dr. Kavita Rao/ });
    // A multiselect's options have the role menuitemcheckbox, which the shared helper does not look for.
    await user.type(combobox, 'x', { skipClick: true });
    const shown = screen.getByRole('menuitemcheckbox', { name: /Dr. Kavita Rao/ });
    await new Promise((resolve) => setTimeout(resolve, 400));
    await user.click(shown);

    await waitFor(() => expect(form.getValues('assistantIds')).toEqual(['d1']));
    const tags = await screen.findByRole('toolbar', { name: 'Chosen assistant surgeons' });
    expect(tags).toHaveTextContent('Dr. Kavita Rao');
    expect(tags).not.toHaveTextContent('Assistant from the original booking');
  });
});
