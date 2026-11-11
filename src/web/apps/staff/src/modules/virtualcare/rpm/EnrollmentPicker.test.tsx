import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, paged } from '@/test/appScreen';
import { renderPicker, typeAndChooseAsTheSearchRuns } from '@/test/searchPickers';
import type { RpmEnrollmentListItem } from './api';
import { EnrollmentPicker, type PickedEnrollment } from './EnrollmentPicker';

const ASHA = {
  id: 'n1',
  uhid: 'NH000123',
  patientName: 'Asha Raman',
  programName: 'Hypertension at home',
} as RpmEnrollmentListItem;

function Picker({ onChange }: { onChange: (enrollment: PickedEnrollment | null) => void }) {
  const [value, setValue] = useState<PickedEnrollment | null>(null);
  return (
    <EnrollmentPicker
      value={value}
      onChange={(enrollment) => {
        setValue(enrollment);
        onChange(enrollment);
      }}
    />
  );
}

describe('EnrollmentPicker', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('an enrolment chosen as the search for what was typed runs is still the one chosen', async () => {
    fakeServer({ 'GET /api/v1/rpm/enrollments': paged([ASHA]) });
    const onChange = vi.fn();
    const user = userEvent.setup();
    renderPicker(<Picker onChange={onChange} />);
    const combobox = screen.getByRole('combobox', { name: 'Monitored patient' });

    await user.type(combobox, 'As');
    await screen.findByRole('option', { name: /Asha Raman/ });
    await typeAndChooseAsTheSearchRuns(user, combobox, 'h', /Asha Raman/);

    await waitFor(() =>
      expect(onChange).toHaveBeenLastCalledWith({
        id: 'n1',
        patientName: 'Asha Raman',
        uhid: 'NH000123',
        programName: 'Hypertension at home',
      }),
    );
  });
});
