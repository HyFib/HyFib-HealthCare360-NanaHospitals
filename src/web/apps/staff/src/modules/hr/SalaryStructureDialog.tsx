import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  makeStyles,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  tokens,
} from '@fluentui/react-components';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatInr } from '@hc360/core';
import {
  applyServerErrors,
  DateField,
  FormGrid,
  LoadingState,
  NumberField,
  SelectField,
  SwitchField,
  TextAreaField,
  useNotify,
} from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { hrApi, hrKeys, hrQueries, type SalaryComponentDto, type SalaryStructureDto } from './api';
import { structurePreview } from './payroll';
import { addMonths, currentMonth, monthStart } from './shared/dates';

const schema = z.object({
  effectiveFrom: z
    .string({ error: 'Enter the date the salary applies from.' })
    .min(1, 'Enter the date the salary applies from.'),
  annualCtc: z.number().min(0).max(1_000_000_000).nullable(),
  monthlyTds: z.number({ error: 'Enter the monthly TDS (0 if none).' }).min(0).max(10_000_000),
  pfApplicable: z.boolean(),
  pfOnActualBasic: z.boolean(),
  esiApplicable: z.boolean(),
  professionalTaxState: z.string().nullable(),
  notes: z.string().trim().max(500),
});

type Values = z.infer<typeof schema>;

const useStyles = makeStyles({
  summary: {
    display: 'flex',
    gap: '24px',
    flexWrap: 'wrap',
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
  },
  input: { maxWidth: '140px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** Salary revision: a new structure closes the previous one the day before it starts. */
export function SalaryStructureDialog({
  open,
  onClose,
  employeeId,
  employeeName,
  current,
}: {
  open: boolean;
  onClose: () => void;
  employeeId: string;
  employeeName: string;
  current: SalaryStructureDto | null;
}) {
  return (
    <Dialog open={open} onOpenChange={(_, data) => !data.open && onClose()}>
      <DialogSurface style={{ maxWidth: 860, width: '100%' }}>
        {open && <Body onClose={onClose} employeeId={employeeId} employeeName={employeeName} current={current} />}
      </DialogSurface>
    </Dialog>
  );
}

function Body({
  onClose,
  employeeId,
  employeeName,
  current,
}: {
  onClose: () => void;
  employeeId: string;
  employeeName: string;
  current: SalaryStructureDto | null;
}) {
  const components = useQuery(hrQueries.components());
  const taxes = useQuery(hrQueries.professionalTax());
  if (!components.data) {
    return (
      <DialogBody>
        <DialogTitle>Revise salary</DialogTitle>
        <DialogContent>
          {components.error ? <Text>Could not load the pay heads.</Text> : <LoadingState />}
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose}>Close</Button>
        </DialogActions>
      </DialogBody>
    );
  }
  const heads = components.data.filter((c) => c.isActive && !c.isStatutory);
  return (
    <StructureForm
      onClose={onClose}
      employeeId={employeeId}
      employeeName={employeeName}
      current={current}
      heads={heads}
      states={taxes.data ?? []}
    />
  );
}

function StructureForm({
  onClose,
  employeeId,
  employeeName,
  current,
  heads,
  states,
}: {
  onClose: () => void;
  employeeId: string;
  employeeName: string;
  current: SalaryStructureDto | null;
  heads: SalaryComponentDto[];
  states: { stateCode: string; stateName: string }[];
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [busy, setBusy] = useState(false);
  const [linesError, setLinesError] = useState<string | null>(null);
  const { year, month } = currentMonth();
  const [values, setValues] = useState<Record<string, string>>(() =>
    Object.fromEntries(
      heads.map((head) => {
        const existing = current?.lines.find((line) => line.salaryComponentId === head.id);
        const initial = existing ? existing.value : head.calculationType === 'PercentOfBasic' ? head.defaultValue : 0;
        return [head.id, initial ? String(initial) : ''];
      }),
    ),
  );
  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: {
      effectiveFrom: addMonths(monthStart(year, month), 1),
      annualCtc: current?.annualCtc ?? null,
      monthlyTds: current?.monthlyTds ?? 0,
      pfApplicable: current?.pfApplicable ?? true,
      pfOnActualBasic: current?.pfOnActualBasic ?? false,
      esiApplicable: current?.esiApplicable ?? true,
      professionalTaxState: current?.professionalTaxState ?? states[0]?.stateCode ?? null,
      notes: '',
    },
  });

  const preview = useMemo(
    () =>
      structurePreview(
        heads.map((head) => ({
          code: head.code,
          kind: head.kind,
          calculationType: head.calculationType,
          value: Number(values[head.id] ?? 0) || 0,
        })),
      ),
    [heads, values],
  );

  const onValid = async (header: Values) => {
    const lines = heads
      .map((head) => ({ salaryComponentId: head.id, value: Number(values[head.id] ?? '') }))
      .filter((line) => Number.isFinite(line.value) && line.value > 0);
    const basic = heads.find((head) => head.code === 'BASIC');
    if (!basic || !lines.some((line) => line.salaryComponentId === basic.id)) {
      setLinesError('Enter the basic salary.');
      return;
    }
    if (heads.some((head) => head.calculationType === 'PercentOfBasic' && Number(values[head.id] ?? 0) > 100)) {
      setLinesError('A percentage of basic cannot exceed 100.');
      return;
    }
    setLinesError(null);
    setBusy(true);
    try {
      await hrApi.createSalaryStructure(employeeId, {
        effectiveFrom: header.effectiveFrom,
        annualCtc: header.annualCtc,
        monthlyTds: header.monthlyTds,
        pfApplicable: header.pfApplicable,
        pfOnActualBasic: header.pfOnActualBasic,
        esiApplicable: header.esiApplicable,
        professionalTaxState: header.professionalTaxState,
        notes: header.notes.trim() || null,
        lines,
      });
      notify.success('Salary revised', `${employeeName} · from ${header.effectiveFrom}`);
      void queryClient.invalidateQueries({ queryKey: hrKeys.payroll });
      onClose();
    } catch (error) {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the salary structure');
      }
    } finally {
      setBusy(false);
    }
  };

  const earnings = heads.filter((head) => head.kind === 'Earning');
  const deductions = heads.filter((head) => head.kind === 'Deduction');

  const table = (rows: SalaryComponentDto[], caption: string) => (
    <Table size="small" aria-label={caption}>
      <TableHeader>
        <TableRow>
          <TableHeaderCell>{caption}</TableHeaderCell>
          <TableHeaderCell style={{ width: 170 }}>Value</TableHeaderCell>
          <TableHeaderCell style={{ width: 140, textAlign: 'end' }}>Per month</TableHeaderCell>
        </TableRow>
      </TableHeader>
      <TableBody>
        {rows.map((head) => (
          <TableRow key={head.id}>
            <TableCell>
              {head.name} <Caption1 className={styles.muted}>({head.code})</Caption1>
            </TableCell>
            <TableCell>
              <Input
                className={styles.input}
                type="number"
                min={0}
                step={head.calculationType === 'PercentOfBasic' ? 0.5 : 1}
                value={values[head.id] ?? ''}
                aria-label={`${head.name} ${head.calculationType === 'PercentOfBasic' ? 'percent of basic' : 'rupees per month'}`}
                contentBefore={head.calculationType === 'Fixed' ? '₹' : undefined}
                contentAfter={head.calculationType === 'PercentOfBasic' ? '% of basic' : undefined}
                onChange={(_, data) => setValues((prev) => ({ ...prev, [head.id]: data.value }))}
              />
            </TableCell>
            <TableCell style={{ textAlign: 'end' }}>{formatInr(preview.amounts[head.code] ?? 0)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );

  return (
    <form onSubmit={form.handleSubmit(onValid)} noValidate>
      <DialogBody>
        <DialogTitle>
          Revise salary
          <Caption1 block>{employeeName}</Caption1>
        </DialogTitle>
        <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
          <FormGrid columns={3} minColumnWidth={200}>
            <DateField
              control={form.control}
              name="effectiveFrom"
              label="Applies from"
              required
              hint="The previous structure ends the day before."
            />
            <NumberField
              control={form.control}
              name="annualCtc"
              label="Annual CTC (₹)"
              min={0}
              step={1000}
              hint="For reference on the offer; payroll uses the heads below."
            />
            <NumberField
              control={form.control}
              name="monthlyTds"
              label="Monthly TDS (₹)"
              required
              min={0}
              step={100}
              hint="Projected income tax per month."
            />
            <SwitchField control={form.control} name="pfApplicable" label="Provident fund (PF)" />
            <SwitchField
              control={form.control}
              name="pfOnActualBasic"
              label="PF on actual basic (above ₹15,000 ceiling)"
            />
            <SwitchField control={form.control} name="esiApplicable" label="ESI (when gross is ₹21,000 or less)" />
            <SelectField
              control={form.control}
              name="professionalTaxState"
              label="Professional tax state"
              placeholder="Not applicable"
              options={states.map((state) => ({
                value: state.stateCode,
                label: `${state.stateName} (${state.stateCode})`,
              }))}
            />
          </FormGrid>
          {table(earnings, 'Earnings')}
          {deductions.length > 0 && table(deductions, 'Fixed monthly deductions')}
          {linesError && (
            <Field validationMessage={linesError} validationState="error">
              <span />
            </Field>
          )}
          <div className={styles.summary} role="status">
            <Text>
              Gross per month: <strong>{formatInr(preview.gross)}</strong>
            </Text>
            <Text>Basic: {formatInr(preview.basic)}</Text>
            {preview.deductions > 0 && <Text>Fixed deductions: {formatInr(preview.deductions)}</Text>}
            <Caption1 className={styles.muted}>
              PF, ESI, professional tax and TDS are worked out by payroll every month.
            </Caption1>
          </div>
          <TextAreaField
            control={form.control}
            name="notes"
            label="Notes"
            rows={2}
            maxLength={500}
            placeholder="For example: annual increment 2026"
          />
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={busy}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={busy}
            icon={busy ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            Save salary
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
