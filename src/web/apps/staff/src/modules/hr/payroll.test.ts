import { describe, expect, it } from 'vitest';
import { inputAmountLabel, previewInputsCsv, previewTotal, runActions, runStepIndex, splitCsvLine, structurePreview } from './payroll';

describe('payroll run actions', () => {
  const both = { process: true, approve: true };

  it('follows the maker-checker states', () => {
    expect(runActions('Draft', both)).toEqual(['process', 'editInputs', 'delete']);
    expect(runActions('Processed', both)).toEqual(['process', 'editInputs', 'delete', 'approve']);
    expect(runActions('Approved', both)).toEqual(['reopen', 'markPaid', 'bankFile']);
    expect(runActions('Paid', both)).toEqual(['lock', 'bankFile']);
    expect(runActions('Locked', both)).toEqual(['bankFile']);
  });

  it('offers only what the permission allows', () => {
    expect(runActions('Processed', { process: true, approve: false })).toEqual(['process', 'editInputs', 'delete']);
    expect(runActions('Processed', { process: false, approve: true })).toEqual(['approve']);
    expect(runActions('Approved', { process: true, approve: false })).toEqual([]);
  });

  it('knows the step of each state', () => {
    expect(runStepIndex('Draft')).toBe(0);
    expect(runStepIndex('Locked')).toBe(4);
  });

  it('labels input amounts', () => {
    expect(inputAmountLabel('LopDaysOverride', 2)).toBe('2 days');
    expect(inputAmountLabel('LopDaysOverride', 1)).toBe('1 day');
    expect(inputAmountLabel('Deduction', 1250)).toContain('1,250.00');
  });
});

describe('payroll inputs CSV preview', () => {
  it('splits quoted fields', () => {
    expect(splitCsvLine('NHEMP00042,CANTEEN,450.50,"Canteen, September"')).toEqual(['NHEMP00042', 'CANTEEN', '450.50', 'Canteen, September']);
    expect(splitCsvLine('a,"say ""hi""",3')).toEqual(['a', 'say "hi"', '3']);
  });

  it('skips the header and blank lines and reports bad lines by number', () => {
    const csv = ['employeeCode,componentCode,amount,description', 'nhemp00042,canteen,450.5,Canteen', '', 'NHEMP00043,ADVANCE,-5', 'NHEMP00044,OT,12.345', 'BAD CODE,OT,100'].join('\n');
    const preview = previewInputsCsv(csv);
    expect(preview.lines).toEqual([{ line: 2, employeeCode: 'NHEMP00042', componentCode: 'CANTEEN', amount: 450.5, description: 'Canteen' }]);
    expect(preview.errors.map((e) => e.line)).toEqual([4, 5, 6]);
    expect(previewTotal(preview)).toBe(450.5);
  });

  it('accepts files without a header', () => {
    const preview = previewInputsCsv('NHEMP00042,INCENTIVE,1000\r\nNHEMP00043,INCENTIVE,1500.29\r\n');
    expect(preview.errors).toEqual([]);
    expect(preview.lines).toHaveLength(2);
    expect(previewTotal(preview)).toBe(2500.29);
  });
});

describe('salary structure preview', () => {
  it('derives %-of-basic heads from BASIC', () => {
    const preview = structurePreview([
      { code: 'BASIC', kind: 'Earning', calculationType: 'Fixed', value: 20000 },
      { code: 'HRA', kind: 'Earning', calculationType: 'PercentOfBasic', value: 40 },
      { code: 'CONV', kind: 'Earning', calculationType: 'Fixed', value: 1600 },
      { code: 'LOAN', kind: 'Deduction', calculationType: 'Fixed', value: 2000 },
    ]);
    expect(preview.basic).toBe(20000);
    expect(preview.amounts.HRA).toBe(8000);
    expect(preview.gross).toBe(29600);
    expect(preview.deductions).toBe(2000);
  });

  it('is zero without a basic', () => {
    expect(structurePreview([{ code: 'HRA', kind: 'Earning', calculationType: 'PercentOfBasic', value: 40 }]).gross).toBe(0);
  });
});
