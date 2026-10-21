import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { TextField } from '@hc360/ui';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useForm } from 'react-hook-form';
import { describe, expect, it } from 'vitest';
import { useLoincNameFollowsCode } from './loincName';

const HAEMOGLOBIN = { loinc: '718-7', loincName: 'Hemoglobin [Mass/volume] in Blood' };
const CRP = { loinc: '1988-5', loincName: 'C reactive protein [Mass/volume] in Serum or Plasma' };

// The two fields as the test master's form has them.
function Harness() {
  const form = useForm({ defaultValues: HAEMOGLOBIN });
  useLoincNameFollowsCode(form);
  return (
    <FluentProvider theme={webLightTheme}>
      <TextField control={form.control} name="loinc" label="LOINC code" />
      <TextField control={form.control} name="loincName" label="LOINC name" />
      <button type="button" onClick={() => form.reset(CRP)}>
        Load CRP
      </button>
    </FluentProvider>
  );
}

describe('useLoincNameFollowsCode', () => {
  it('clears the name when the code is changed, so the old name never goes with the new code', async () => {
    render(<Harness />);
    const code = screen.getByRole('textbox', { name: 'LOINC code' });

    await userEvent.clear(code);
    await userEvent.type(code, '1988-5');

    expect(screen.getByRole('textbox', { name: 'LOINC name' })).toHaveValue('');
  });

  it("keeps a name typed after the code, the new code's own", async () => {
    render(<Harness />);
    await userEvent.clear(screen.getByRole('textbox', { name: 'LOINC code' }));
    await userEvent.type(screen.getByRole('textbox', { name: 'LOINC code' }), CRP.loinc);
    // Pasted, as it is copied from loinc.org (typing would read its brackets as keys).
    await userEvent.click(screen.getByRole('textbox', { name: 'LOINC name' }));
    await userEvent.paste(CRP.loincName);

    expect(screen.getByRole('textbox', { name: 'LOINC name' })).toHaveValue(CRP.loincName);
  });

  it('keeps the name when the same code is typed again', async () => {
    render(<Harness />);
    const code = screen.getByRole('textbox', { name: 'LOINC code' });

    await userEvent.clear(code);
    await userEvent.type(code, HAEMOGLOBIN.loinc);

    expect(screen.getByRole('textbox', { name: 'LOINC name' })).toHaveValue(HAEMOGLOBIN.loincName);
  });

  it('gives the name back when the code is changed back', async () => {
    render(<Harness />);
    const code = screen.getByRole('textbox', { name: 'LOINC code' });

    await userEvent.type(code, '1');
    expect(screen.getByRole('textbox', { name: 'LOINC name' })).toHaveValue('');
    await userEvent.type(code, '{Backspace}');

    expect(screen.getByRole('textbox', { name: 'LOINC name' })).toHaveValue(HAEMOGLOBIN.loincName);
  });

  it('keeps the name of a test loaded into the form', async () => {
    render(<Harness />);
    expect(screen.getByRole('textbox', { name: 'LOINC name' })).toHaveValue(HAEMOGLOBIN.loincName);

    await userEvent.click(screen.getByRole('button', { name: 'Load CRP' }));

    expect(screen.getByRole('textbox', { name: 'LOINC name' })).toHaveValue(CRP.loincName);
  });
});
