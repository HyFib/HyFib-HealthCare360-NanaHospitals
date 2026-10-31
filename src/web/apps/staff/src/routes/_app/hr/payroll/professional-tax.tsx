import {
  Button,
  Caption1,
  Checkbox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Select,
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
import { AddRegular, DeleteRegular, EditRegular, MoneyRegular } from '@fluentui/react-icons';
import { formatInr } from '@hc360/core';
import {
  CheckboxGroup,
  EmptyState,
  ErrorState,
  LoadingState,
  Page,
  PageHeader,
  Section,
  useNotify,
  useUnsavedChanges,
  useValuesChanged,
} from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { hrApi, hrKeys, hrQueries, type ProfessionalTaxScheduleDto } from '@/modules/hr/api';
import { HR_PERMISSIONS as P, PAYROLL_VIEWERS, type ProfessionalTaxFrequency } from '@/modules/hr/labels';
import { PAYROLL_NAV } from '@/modules/hr/nav';
import { validatePtBands, withUpperLimits } from '@/modules/hr/payroll';
import { MONTH_NAMES } from '@/modules/hr/shared/dates';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { SubNav } from '@/modules/hr/shared/SubNav';
import { Columns } from '@/modules/hr/shared/layout';

export const Route = createFileRoute('/_app/hr/payroll/professional-tax')({
  component: () => (
    <RequirePermission permission={PAYROLL_VIEWERS} what="professional tax slabs">
      <ProfessionalTaxPage />
    </RequirePermission>
  ),
});

const useStyles = makeStyles({
  right: { textAlign: 'end' },
  muted: { color: tokens.colorNeutralForeground3 },
  months: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(110px, 1fr))', gap: '2px 8px' },
  bandInput: { maxWidth: '150px' },
});

function ProfessionalTaxPage() {
  const styles = useStyles();
  const canEdit = usePermission(P.payrollApprove);
  const schedules = useQuery(hrQueries.professionalTax());
  const [editing, setEditing] = useState<ProfessionalTaxScheduleDto | 'new' | null>(null);

  return (
    <Page maxWidth={1300}>
      <PageHeader
        title="Professional tax"
        subtitle="State slab tables used by payroll. Verify them with the local body before go-live and whenever rates are notified."
        icon={<MoneyRegular />}
        actions={
          canEdit && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
              Add state
            </Button>
          )
        }
      />
      <SubNav items={PAYROLL_NAV} ariaLabel="Payroll screens" />
      {schedules.isLoading && <LoadingState />}
      {schedules.error && <ErrorState error={schedules.error} onRetry={() => void schedules.refetch()} />}
      {schedules.data && schedules.data.length === 0 && (
        <Section>
          <EmptyState
            title="No professional tax slabs"
            description="Add the slab table for the state where the hospital is registered."
          />
        </Section>
      )}
      <Columns>
        {(schedules.data ?? []).map((schedule) => (
          <Section
            key={schedule.stateCode}
            title={`${schedule.stateName} (${schedule.stateCode})`}
            description={
              schedule.frequency === 'Monthly'
                ? 'Deducted every month from that month’s gross'
                : `Half-yearly, deducted in ${schedule.deductionMonths.map((m) => MONTH_NAMES[m - 1]).join(' and ')}`
            }
            actions={
              canEdit && (
                <Button appearance="subtle" icon={<EditRegular />} onClick={() => setEditing(schedule)}>
                  Edit slabs
                </Button>
              )
            }
          >
            <Table size="small" aria-label={`Professional tax slabs for ${schedule.stateName}`}>
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>
                    {schedule.frequency === 'Monthly' ? 'Monthly gross' : 'Half-year gross'}
                  </TableHeaderCell>
                  <TableHeaderCell className={styles.right}>Tax</TableHeaderCell>
                  <TableHeaderCell className={styles.right}>February</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {schedule.bands.map((band) => (
                  <TableRow key={band.incomeFrom}>
                    <TableCell>
                      {formatInr(band.incomeFrom, { whole: true })}{' '}
                      {band.incomeTo !== null ? `to ${formatInr(band.incomeTo, { whole: true })}` : 'and above'}
                    </TableCell>
                    <TableCell className={styles.right}>{formatInr(band.amount, { whole: true })}</TableCell>
                    <TableCell className={styles.right}>
                      {band.februaryAmount !== null ? formatInr(band.februaryAmount, { whole: true }) : '—'}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </Section>
        ))}
      </Columns>
      {editing && <ScheduleDialog schedule={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
    </Page>
  );
}

interface BandRow {
  key: number;
  incomeFrom: string;
  amount: string;
  februaryAmount: string;
}

function ScheduleDialog({ schedule, onClose }: { schedule: ProfessionalTaxScheduleDto | null; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [stateCode, setStateCode] = useState(schedule?.stateCode ?? '');
  const [stateName, setStateName] = useState(schedule?.stateName ?? '');
  const [frequency, setFrequency] = useState<ProfessionalTaxFrequency>(schedule?.frequency ?? 'Monthly');
  const [months, setMonths] = useState<number[]>(schedule?.deductionMonths ?? [9, 3]);
  const [rows, setRows] = useState<BandRow[]>(() =>
    (schedule?.bands ?? [{ incomeFrom: 0, incomeTo: null, amount: 0, februaryAmount: null }]).map((band, index) => ({
      key: index,
      incomeFrom: String(band.incomeFrom),
      amount: String(band.amount),
      februaryAmount: band.februaryAmount === null ? '' : String(band.februaryAmount),
    })),
  );
  const [busy, setBusy] = useState(false);
  const bands = rows.map((row) => ({
    incomeFrom: Number(row.incomeFrom) || 0,
    amount: Number(row.amount) || 0,
    februaryAmount: row.februaryAmount === '' ? null : Number(row.februaryAmount),
  }));
  const errors = [
    ...(/^[A-Z]{2,4}$/.test(stateCode) ? [] : ['Enter the state code, for example TN or KA.']),
    ...(stateName.trim() ? [] : ['Enter the state name.']),
    ...validatePtBands(bands, frequency, months),
  ];

  const update = (key: number, patch: Partial<BandRow>) =>
    setRows((prev) => prev.map((row) => (row.key === key ? { ...row, ...patch } : row)));

  const save = async () => {
    setBusy(true);
    try {
      await hrApi.saveProfessionalTax(stateCode, {
        stateName: stateName.trim(),
        frequency,
        deductionMonths: frequency === 'HalfYearly' ? months : [],
        bands: withUpperLimits(bands),
        version: schedule?.stateCode === stateCode ? schedule.version : undefined,
      });
      notify.success(
        'Professional tax saved',
        `${stateName} (${stateCode}). The change is recorded in the audit trail.`,
      );
      void queryClient.invalidateQueries({ queryKey: hrKeys.payroll });
      onClose();
    } catch (error) {
      notify.error(error, 'Could not save the slabs');
    } finally {
      setBusy(false);
    }
  };

  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ stateCode, stateName, frequency, months, rows }),
    busy,
    onClose,
  });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 760, width: '100%' }}>
        <DialogBody>
          <DialogTitle>{schedule ? `Edit ${schedule.stateName} slabs` : 'Add professional tax state'}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <MessageBar intent="warning">
              <MessageBarBody>
                The whole table is replaced. Use the rates notified by the state or local body; payroll uses them from
                the next processing.
              </MessageBarBody>
            </MessageBar>
            <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap' }}>
              <Field label="State code" required>
                <Input
                  value={stateCode}
                  onChange={(_, d) =>
                    setStateCode(
                      d.value
                        .toUpperCase()
                        .replace(/[^A-Z]/g, '')
                        .slice(0, 4),
                    )
                  }
                  disabled={Boolean(schedule)}
                  autoFocus={!schedule}
                />
              </Field>
              <Field label="State name" required>
                <Input value={stateName} onChange={(_, d) => setStateName(d.value)} maxLength={64} />
              </Field>
              <Field label="Collected">
                <Select value={frequency} onChange={(_, d) => setFrequency(d.value as ProfessionalTaxFrequency)}>
                  <option value="Monthly">Every month</option>
                  <option value="HalfYearly">Half-yearly (e.g. Tamil Nadu)</option>
                </Select>
              </Field>
            </div>
            {frequency === 'HalfYearly' && (
              <CheckboxGroup label="Deducted in">
                <div className={styles.months}>
                  {MONTH_NAMES.map((name, index) => (
                    <Checkbox
                      key={name}
                      label={name.slice(0, 3)}
                      checked={months.includes(index + 1)}
                      onChange={(_, d) =>
                        setMonths((prev) => (d.checked ? [...prev, index + 1] : prev.filter((m) => m !== index + 1)))
                      }
                    />
                  ))}
                </div>
              </CheckboxGroup>
            )}
            <Table size="small" aria-label="Income bands">
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>
                    {frequency === 'Monthly' ? 'Monthly gross from (₹)' : 'Half-year gross from (₹)'}
                  </TableHeaderCell>
                  <TableHeaderCell>Tax (₹)</TableHeaderCell>
                  <TableHeaderCell>February (₹, optional)</TableHeaderCell>
                  <TableHeaderCell style={{ width: 48 }} aria-label="Remove" />
                </TableRow>
              </TableHeader>
              <TableBody>
                {rows.map((row) => (
                  <TableRow key={row.key}>
                    <TableCell>
                      <Input
                        className={styles.bandInput}
                        type="number"
                        min={0}
                        value={row.incomeFrom}
                        onChange={(_, d) => update(row.key, { incomeFrom: d.value })}
                        aria-label="Income from"
                      />
                    </TableCell>
                    <TableCell>
                      <Input
                        className={styles.bandInput}
                        type="number"
                        min={0}
                        value={row.amount}
                        onChange={(_, d) => update(row.key, { amount: d.value })}
                        aria-label="Tax"
                      />
                    </TableCell>
                    <TableCell>
                      <Input
                        className={styles.bandInput}
                        type="number"
                        min={0}
                        value={row.februaryAmount}
                        onChange={(_, d) => update(row.key, { februaryAmount: d.value })}
                        aria-label="February tax"
                      />
                    </TableCell>
                    <TableCell>
                      <Button
                        appearance="subtle"
                        icon={<DeleteRegular />}
                        aria-label="Remove band"
                        disabled={rows.length === 1}
                        onClick={() => setRows((prev) => prev.filter((r) => r.key !== row.key))}
                      />
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            <div>
              <Button
                icon={<AddRegular />}
                disabled={rows.length >= 20}
                onClick={() =>
                  setRows((prev) => [
                    ...prev,
                    { key: Math.max(0, ...prev.map((r) => r.key)) + 1, incomeFrom: '', amount: '', februaryAmount: '' },
                  ])
                }
              >
                Add band
              </Button>
            </div>
            {errors.length > 0 && (
              <MessageBar intent="error" layout="multiline">
                <MessageBarBody>
                  <ul style={{ margin: 0, paddingLeft: 18 }}>
                    {errors.map((e) => (
                      <li key={e}>{e}</li>
                    ))}
                  </ul>
                </MessageBarBody>
              </MessageBar>
            )}
            <Caption1 className={styles.muted}>
              <Text size={200}>
                Each band applies from its income up to one rupee below the next band; the last band has no upper limit.
              </Text>
            </Caption1>
          </DialogContent>
          <DialogActions>
            <Button onClick={guard.requestClose} disabled={busy}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={busy || errors.length > 0}
              icon={busy ? <Spinner size="tiny" aria-hidden /> : undefined}
              onClick={() => void save()}
            >
              Save slabs
            </Button>
          </DialogActions>
        </DialogBody>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
