import { Checkbox, Field, FluentProvider, webLightTheme } from '@fluentui/react-components';
import { CheckboxGroup } from '@hc360/ui';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useEffect, useState } from 'react';
import { useForm } from 'react-hook-form';
import { describe, expect, it } from 'vitest';
import { CheckboxGroupField } from '@/modules/ot/shared/fields';

// The sets of checkboxes the screens share: CheckboxGroup (@hc360/ui, tested here with Testing Library, which that
// package does not have) and the CheckboxGroupField of the OT, physiotherapy, dialysis and blood bank forms. Inside a
// Fluent Field every box had the Field's id, so each box's label named and ticked the first box, and every box was
// required.

const ROLES = ['Accountant', 'Billing', 'FrontOffice', 'Nurse'];

function Roles({
  required,
  message,
  hint,
  inField,
}: {
  required?: boolean;
  message?: string;
  hint?: string;
  inField?: boolean;
}) {
  const [chosen, setChosen] = useState<string[]>([]);
  const group = (
    <CheckboxGroup label="Roles" required={required} hint={hint} validationMessage={message}>
      {ROLES.map((role) => (
        <Checkbox
          key={role}
          label={role}
          checked={chosen.includes(role)}
          onChange={(_, d) =>
            setChosen((current) => (d.checked ? [...current, role] : current.filter((r) => r !== role)))
          }
        />
      ))}
    </CheckboxGroup>
  );
  return (
    <FluentProvider theme={webLightTheme}>{inField ? <Field label="Access">{group}</Field> : group}</FluentProvider>
  );
}

describe('CheckboxGroup', () => {
  it('names each box by its own label, gives each its own id, and ticks the box whose label is clicked', async () => {
    const user = userEvent.setup();
    render(<Roles />);

    const group = screen.getByRole('group', { name: 'Roles' });
    const boxes = ROLES.map((role) => within(group).getByRole('checkbox', { name: role }));
    expect(new Set(boxes.map((box) => box.id)).size).toBe(ROLES.length);

    await user.click(within(group).getByText('FrontOffice'));
    expect(within(group).getByRole('checkbox', { name: 'FrontOffice' })).toBeChecked();
    expect(within(group).getByRole('checkbox', { name: 'Accountant' })).not.toBeChecked();
  });

  it('asks for at least one box on the group: its label has the only asterisk, and its name says so', () => {
    render(<Roles required />);

    // A group cannot carry aria-required (axe aria-allowed-attr): its name says it instead.
    const group = screen.getByRole('group', { name: 'Roles (required)' });
    expect(group).not.toHaveAttribute('aria-required');
    expect(within(group).getAllByText('*')).toHaveLength(1);
    for (const box of within(group).getAllByRole('checkbox')) {
      expect(box).not.toBeRequired();
    }
  });

  it('is described by its validation message, announced as it appears, and by its hint', () => {
    render(<Roles message="Give the user at least one role." hint="Roles decide what the user may open." />);

    const group = screen.getByRole('group', { name: 'Roles' });
    expect(group).toHaveAccessibleDescription('Give the user at least one role. Roles decide what the user may open.');
    expect(within(group).getByRole('alert')).toHaveTextContent('Give the user at least one role.');
    for (const box of within(group).getAllByRole('checkbox')) {
      expect(box).not.toHaveAccessibleDescription();
    }
  });

  it('keeps the context of a Field it is put in away from its boxes', () => {
    render(<Roles required inField />);

    const boxes = ROLES.map((role) => screen.getByRole('checkbox', { name: role }));
    expect(new Set(boxes.map((box) => box.id)).size).toBe(ROLES.length);
    for (const box of boxes) {
      expect(box).not.toBeRequired();
    }
  });
});

describe('CheckboxGroupField', () => {
  function Modalities({ error }: { error?: string }) {
    const form = useForm<{ modalities: string[] }>({ defaultValues: { modalities: ['tens'] } });
    useEffect(() => {
      if (error) {
        form.setError('modalities', { type: 'server', message: error });
      }
    }, [form, error]);
    return (
      <FluentProvider theme={webLightTheme}>
        <CheckboxGroupField
          control={form.control}
          name="modalities"
          label="Modalities"
          required
          layout="columns"
          hint="Tick each one given"
          options={[
            { value: 'tens', label: 'TENS' },
            { value: 'ift', label: 'IFT' },
            { value: 'ultrasound', label: 'Ultrasound' },
          ]}
        />
      </FluentProvider>
    );
  }

  it('ticks each choice by its own label, and asks for one on the group', async () => {
    const user = userEvent.setup();
    render(<Modalities />);

    const group = screen.getByRole('group', { name: 'Modalities (required)' });
    expect(group).toHaveAccessibleDescription('Tick each one given');
    const boxes = within(group).getAllByRole('checkbox');
    expect(new Set(boxes.map((box) => box.id)).size).toBe(3);
    for (const box of boxes) {
      expect(box).not.toBeRequired();
    }

    await user.click(within(group).getByText('Ultrasound'));
    expect(within(group).getByRole('checkbox', { name: 'Ultrasound' })).toBeChecked();
    expect(within(group).getByRole('checkbox', { name: 'TENS' })).toBeChecked();
    expect(within(group).getByRole('checkbox', { name: 'IFT' })).not.toBeChecked();
    await user.click(within(group).getByText('TENS'));
    expect(within(group).getByRole('checkbox', { name: 'TENS' })).not.toBeChecked();
  });

  it("shows the form's message on the group", () => {
    render(<Modalities error="Choose what was given." />);

    expect(screen.getByRole('group', { name: 'Modalities (required)' })).toHaveAccessibleDescription(
      'Choose what was given. Tick each one given',
    );
  });
});
