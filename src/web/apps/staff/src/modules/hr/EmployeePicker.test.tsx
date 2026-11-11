import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, paged } from '@/test/appScreen';
import { renderPicker, typeAndChooseAsTheSearchRuns } from '@/test/searchPickers';
import type { EmployeeListItem } from './api';
import { EmployeePicker, type PickedEmployee } from './EmployeePicker';

const MEERA = {
  id: 'e1',
  employeeCode: 'EMP-0042',
  fullName: 'Meera Joshi',
  designationName: 'Staff Nurse',
  departmentName: 'Ward 3',
} as EmployeeListItem;

function Picker({ onChange }: { onChange: (employee: PickedEmployee | null) => void }) {
  const [value, setValue] = useState<PickedEmployee | null>(null);
  return (
    <EmployeePicker
      value={value}
      onChange={(employee) => {
        setValue(employee);
        onChange(employee);
      }}
    />
  );
}

describe('EmployeePicker', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('an employee chosen as the search for what was typed runs is still the one chosen', async () => {
    fakeServer({ 'GET /api/v1/hr/employees': paged([MEERA]) });
    const onChange = vi.fn();
    const user = userEvent.setup();
    renderPicker(<Picker onChange={onChange} />);
    const combobox = screen.getByRole('combobox', { name: 'Employee' });

    await user.type(combobox, 'Me');
    await screen.findByRole('option', { name: /Meera Joshi/ });
    await typeAndChooseAsTheSearchRuns(user, combobox, 'e', /Meera Joshi/);

    await waitFor(() => expect(onChange).toHaveBeenLastCalledWith({ id: 'e1', code: 'EMP-0042', name: 'Meera Joshi' }));
  });
});
