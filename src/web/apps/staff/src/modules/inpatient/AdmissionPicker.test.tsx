import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, paged } from '@/test/appScreen';
import { renderPicker, typeAndChooseAsTheSearchRuns } from '@/test/searchPickers';
import type { AdmissionListItem } from './api';
import { AdmissionPicker, type PickedAdmission } from './AdmissionPicker';

const ASHA = {
  id: 'a1',
  admissionNumber: 'IP-2026-0007',
  patientId: 'p1',
  uhid: 'NH000123',
  patientName: 'Asha Raman',
  wardName: 'Ward 3',
  bedLabel: 'W3-12',
} as AdmissionListItem;

function Picker({ onChange }: { onChange: (admission: PickedAdmission | null) => void }) {
  const [value, setValue] = useState<PickedAdmission | null>(null);
  return (
    <AdmissionPicker
      value={value}
      onChange={(admission) => {
        setValue(admission);
        onChange(admission);
      }}
    />
  );
}

describe('AdmissionPicker', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('an admission chosen as the search for what was typed runs is still the one chosen', async () => {
    fakeServer({ 'GET /api/v1/inpatient/admissions': paged([ASHA]) });
    const onChange = vi.fn();
    const user = userEvent.setup();
    renderPicker(<Picker onChange={onChange} />);
    const combobox = screen.getByRole('combobox', { name: 'Admitted patient' });

    await user.type(combobox, 'Ash');
    await screen.findByRole('option', { name: /Asha Raman/ });
    await typeAndChooseAsTheSearchRuns(user, combobox, 'a', /Asha Raman/);

    await waitFor(() =>
      expect(onChange).toHaveBeenLastCalledWith({
        admissionId: 'a1',
        patientId: 'p1',
        admissionNumber: 'IP-2026-0007',
        patientName: 'Asha Raman',
        uhid: 'NH000123',
        wardName: 'Ward 3',
        bedLabel: 'W3-12',
      }),
    );
  });
});
