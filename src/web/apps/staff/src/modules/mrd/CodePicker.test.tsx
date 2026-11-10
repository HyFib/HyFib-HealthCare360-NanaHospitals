import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakeServer } from '@/test/appScreen';
import { renderPicker, typeAndChooseAsTheSearchRuns } from '@/test/searchPickers';
import type { IcdCodeDto, ProcedureCodeDto } from './api';
import { IcdCodePicker, ProcedureCodePicker } from './CodePicker';

const APPENDECTOMY: ProcedureCodeDto = {
  id: 'pc1',
  code: '0DTJ0ZZ',
  title: 'Resection of appendix, open approach',
  system: 'ICD-10-PCS',
  category: 'Gastrointestinal',
  keywords: null,
  source: 'Seed',
  isActive: true,
  version: 1,
};

const DIABETES: IcdCodeDto = {
  id: 'icd1',
  code: 'E11.9',
  title: 'Type 2 diabetes mellitus without complications',
  release: 'ICD-10',
  chapter: null,
  block: null,
  blockTitle: null,
  isBillable: true,
  usage: null,
  keywords: null,
  includedTerms: null,
  source: 'Seed',
  isActive: true,
  version: 1,
};

describe('ProcedureCodePicker', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('a procedure code chosen as the search for what was typed runs is still the one chosen', async () => {
    fakeServer({ 'GET /api/v1/mrd/procedure-codes': [APPENDECTOMY] });
    const onPick = vi.fn();
    const user = userEvent.setup();
    renderPicker(<ProcedureCodePicker label="Procedure" onPick={onPick} />);
    const combobox = screen.getByRole('combobox', { name: 'Procedure' });

    await user.type(combobox, 'Ap');
    await screen.findByRole('option', { name: /0DTJ0ZZ/ });
    await typeAndChooseAsTheSearchRuns(user, combobox, 'p', /0DTJ0ZZ/);

    await waitFor(() => expect(onPick).toHaveBeenCalledWith(APPENDECTOMY));
  });

  it('offers none of the last search once the text is shorter than a search', async () => {
    fakeServer({ 'GET /api/v1/mrd/procedure-codes': [APPENDECTOMY] });
    const user = userEvent.setup();
    renderPicker(<ProcedureCodePicker label="Procedure" onPick={vi.fn()} />);
    const combobox = screen.getByRole('combobox', { name: 'Procedure' });

    await user.type(combobox, 'Ap');
    await screen.findByRole('option', { name: /0DTJ0ZZ/ });
    await user.type(combobox, '{Backspace}', { skipClick: true });

    await waitFor(() => expect(screen.queryByRole('option', { name: /0DTJ0ZZ/ })).not.toBeInTheDocument());
    expect(combobox).toHaveAttribute('aria-expanded', 'true');
  });
});

describe('IcdCodePicker', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  // Protected by `mrdQueries.icd` itself, which keeps the previous results while the next search runs.
  it('an ICD code chosen as the search for what was typed runs is still the one chosen', async () => {
    fakeServer({ 'GET /api/v1/mrd/icd-codes': [DIABETES] });
    const onPick = vi.fn();
    const user = userEvent.setup();
    renderPicker(<IcdCodePicker label="Diagnosis" onPick={onPick} />);
    const combobox = screen.getByRole('combobox', { name: 'Diagnosis' });

    await user.type(combobox, 'E1');
    await screen.findByRole('option', { name: /E11\.9/ });
    await typeAndChooseAsTheSearchRuns(user, combobox, '1', /E11\.9/);

    await waitFor(() => expect(onPick).toHaveBeenCalledWith(DIABETES));
  });
});
