import type { LookupItem } from '@hc360/core';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakeServer } from '@/test/appScreen';
import { renderPicker, typeAndChooseAsTheSearchRuns } from '@/test/searchPickers';
import type { ReportInputs, ReportParameter } from './api';
import { ReportParameterForm } from './ReportInputs';

// A list of 200 or more (doctors in a big hospital) is filtered here for one letter and searched on the server from
// two. The search moves to the server 250 ms after the second letter. Its options stay while it runs only because the
// lookup query keeps the last results (analyticsQueries.lookup), and the server search starts on the whole list's;
// without that, a doctor chosen then would not be chosen. This test fails if the query stops keeping them.

const DOCTOR: ReportParameter = {
  key: 'practitionerId',
  label: 'Doctor',
  kind: 'Practitioner',
  required: false,
  defaultValue: null,
  choices: null,
  min: null,
  max: null,
  hint: null,
};

const ANIL: LookupItem = { id: 'd-anil', code: 'D000', name: 'Dr. Anil Rao' };
/** 200 doctors: enough that two letters are searched on the server. Only Dr. Anil Rao has an "a" in his name. */
const DOCTORS: LookupItem[] = [
  ANIL,
  ...Array.from({ length: 199 }, (_, i) => ({ id: `d-${i + 1}`, code: `D${i + 1}`, name: `Dr. Other ${i + 1}` })),
];

function Form({ onChange }: { onChange: (next: ReportInputs) => void }) {
  const [values, setValues] = useState<ReportInputs>({});
  return (
    <ReportParameterForm
      parameters={[DOCTOR]}
      values={values}
      onChange={(next) => {
        setValues(next);
        onChange(next);
      }}
    />
  );
}

describe('a report input chosen from a long list', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('a doctor chosen as the search moves to the server is still the one chosen', async () => {
    fakeServer({ 'GET /api/v1/analytics/lookups/practitioners': DOCTORS });
    const onChange = vi.fn();
    const user = userEvent.setup();
    renderPicker(<Form onChange={onChange} />);
    const combobox = screen.getByRole('combobox', { name: 'Doctor' });

    await user.type(combobox, 'A');
    await screen.findByRole('option', { name: 'Dr. Anil Rao' });
    await typeAndChooseAsTheSearchRuns(user, combobox, 'n', 'Dr. Anil Rao');

    await waitFor(() => expect(onChange).toHaveBeenLastCalledWith({ practitionerId: 'd-anil' }));
    expect(combobox).toHaveValue('Dr. Anil Rao');
  });
});
