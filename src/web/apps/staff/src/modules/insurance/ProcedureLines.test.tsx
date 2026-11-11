import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakeServer } from '@/test/appScreen';
import { renderPicker, typeAndChooseAsTheSearchRuns } from '@/test/searchPickers';
import type { PreAuthProcedureOption } from './api';
import { ProcedureLines, type ProcedureDraft } from './ProcedureLines';

const CHOLECYSTECTOMY: PreAuthProcedureOption = {
  system: 'Hbp',
  code: 'SG039A',
  name: 'Laparoscopic cholecystectomy',
  rate: 20000,
};

function Lines({ onChange }: { onChange: (lines: ProcedureDraft[]) => void }) {
  const [lines, setLines] = useState<ProcedureDraft[]>([]);
  return (
    <ProcedureLines
      lines={lines}
      onChange={(next) => {
        setLines(next);
        onChange(next);
      }}
    />
  );
}

const ANSWERS = { 'GET /api/v1/insurance/preauths/procedure-options': [CHOLECYSTECTOMY] };

describe('ProcedureLines', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('a procedure chosen as the search for what was typed runs is still the one added', async () => {
    fakeServer(ANSWERS);
    const onChange = vi.fn();
    const user = userEvent.setup();
    renderPicker(<Lines onChange={onChange} />);
    const combobox = screen.getByRole('combobox', { name: 'Add a planned procedure' });

    await user.type(combobox, 'chol');
    await screen.findByRole('option', { name: /SG039A/ });
    await typeAndChooseAsTheSearchRuns(user, combobox, 'e', /SG039A/);

    await waitFor(() =>
      expect(onChange).toHaveBeenLastCalledWith([
        {
          system: 'Hbp',
          code: 'SG039A',
          name: 'Laparoscopic cholecystectomy',
          quantity: '1',
          amount: expect.stringMatching(/^20,?000/),
        },
      ]),
    );
  });

  it('offers none of the last search once the box is cleared', async () => {
    fakeServer(ANSWERS);
    const user = userEvent.setup();
    renderPicker(<Lines onChange={vi.fn()} />);
    const combobox = screen.getByRole('combobox', { name: 'Add a planned procedure' });

    await user.type(combobox, 'c');
    await screen.findByRole('option', { name: /SG039A/ });
    await user.type(combobox, '{Backspace}', { skipClick: true });

    await waitFor(() => expect(screen.queryByRole('option', { name: /SG039A/ })).not.toBeInTheDocument());
    expect(combobox).toHaveAttribute('aria-expanded', 'true');
  });
});
