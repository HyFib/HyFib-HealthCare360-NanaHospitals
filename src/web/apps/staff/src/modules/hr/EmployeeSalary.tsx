import {
  Badge,
  Button,
  Caption1,
  makeStyles,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  tokens,
} from '@fluentui/react-components';
import { MoneyRegular } from '@fluentui/react-icons';
import { formatDateOnly } from '@hc360/core';
import { DataTable, EmptyState, ErrorState, LoadingState, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { hrQueries, type SalaryStructureDto } from './api';
import { HR_PERMISSIONS } from './labels';
import { SalaryStructureDialog } from './SalaryStructureDialog';
import { Detail, DetailGrid } from './shared/DetailGrid';
import { Amount, RevealButton, Stack, Toolbar } from './shared/layout';

const useStyles = makeStyles({
  right: { textAlign: 'end' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/**
 * Salary structures of one employee (payroll staff only). Amounts stay hidden until "Show amounts" is pressed, so a
 * profile open on a shared screen does not reveal pay.
 */
export function EmployeeSalary({
  employeeId,
  employeeName,
  separated,
}: {
  employeeId: string;
  employeeName: string;
  separated: boolean;
}) {
  const styles = useStyles();
  const canRevise = usePermission(HR_PERMISSIONS.payrollProcess) && !separated;
  const [revealed, setRevealed] = useState(false);
  const [revising, setRevising] = useState(false);
  const structures = useQuery(hrQueries.salaryStructures(employeeId));

  if (structures.isLoading) {
    return <LoadingState />;
  }
  if (structures.error) {
    return <ErrorState error={structures.error} onRetry={() => void structures.refetch()} />;
  }

  const list = structures.data ?? [];
  const current = list.find((s) => s.effectiveTo === null) ?? list[0] ?? null;
  const hidden = !revealed;

  return (
    <Stack gap={16}>
      <Toolbar spaced={false}>
        <RevealButton revealed={revealed} onChange={setRevealed} />
        {canRevise && (
          <Button appearance="primary" icon={<MoneyRegular />} onClick={() => setRevising(true)}>
            {current ? 'Revise salary' : 'Set salary'}
          </Button>
        )}
      </Toolbar>

      {!current ? (
        <EmptyState
          icon={<MoneyRegular />}
          title="No salary structure yet"
          description={
            canRevise
              ? 'Set the salary so the employee is included in payroll.'
              : 'Payroll staff set the salary structure.'
          }
        />
      ) : (
        <Section title={`Current salary · from ${formatDateOnly(current.effectiveFrom)}`}>
          <Stack gap={14}>
            <DetailGrid minColumnWidth={180}>
              <Detail label="Gross per month">
                <Amount value={current.monthlyGross} hidden={hidden} />
              </Detail>
              <Detail label="Annual CTC">
                {current.annualCtc === null ? null : <Amount value={current.annualCtc} hidden={hidden} whole />}
              </Detail>
              <Detail label="Monthly TDS">
                <Amount value={current.monthlyTds} hidden={hidden} />
              </Detail>
              <Detail label="Statutory">
                <span style={{ display: 'inline-flex', gap: 4, flexWrap: 'wrap' }}>
                  <Badge appearance="tint" color={current.pfApplicable ? 'success' : 'informative'}>
                    PF {current.pfApplicable ? (current.pfOnActualBasic ? 'on actual basic' : 'on') : 'off'}
                  </Badge>
                  <Badge appearance="tint" color={current.esiApplicable ? 'success' : 'informative'}>
                    ESI {current.esiApplicable ? 'on' : 'off'}
                  </Badge>
                  <Badge appearance="tint">PT {current.professionalTaxState ?? 'none'}</Badge>
                </span>
              </Detail>
              {current.notes && <Detail label="Notes">{current.notes}</Detail>}
            </DetailGrid>
            <StructureLines structure={current} hidden={hidden} />
          </Stack>
        </Section>
      )}

      {list.length > 1 && (
        <Section title="Earlier salary" flush>
          <DataTable
            ariaLabel="Salary history"
            rows={list.filter((s) => s.id !== current?.id)}
            getRowId={(s) => s.id}
            columns={[
              { key: 'from', header: 'From', render: (s) => formatDateOnly(s.effectiveFrom) },
              { key: 'to', header: 'To', render: (s) => formatDateOnly(s.effectiveTo) },
              {
                key: 'gross',
                header: 'Gross per month',
                align: 'end',
                render: (s) => <Amount value={s.monthlyGross} hidden={hidden} />,
              },
              { key: 'notes', header: 'Notes', priority: 'low', render: (s) => s.notes ?? '—' },
            ]}
            size="small"
          />
        </Section>
      )}

      <Caption1 className={styles.muted}>
        PF, ESI, professional tax and TDS are calculated by payroll every month from these figures and the attendance.
      </Caption1>

      <SalaryStructureDialog
        open={revising}
        onClose={() => setRevising(false)}
        employeeId={employeeId}
        employeeName={employeeName}
        current={current}
      />
    </Stack>
  );
}

function StructureLines({ structure, hidden }: { structure: SalaryStructureDto; hidden: boolean }) {
  const styles = useStyles();
  return (
    <Table size="small" aria-label="Pay heads">
      <TableHeader>
        <TableRow>
          <TableHeaderCell>Pay head</TableHeaderCell>
          <TableHeaderCell>Basis</TableHeaderCell>
          <TableHeaderCell className={styles.right}>Per month</TableHeaderCell>
        </TableRow>
      </TableHeader>
      <TableBody>
        {structure.lines.map((line) => (
          <TableRow key={line.salaryComponentId}>
            <TableCell>
              <Text>{line.name}</Text> <Caption1 className={styles.muted}>{line.code}</Caption1>
              {line.kind === 'Deduction' && (
                <Badge size="small" appearance="tint" color="warning" style={{ marginLeft: 6 }}>
                  deduction
                </Badge>
              )}
            </TableCell>
            <TableCell>{line.calculationType === 'PercentOfBasic' ? `${line.value}% of basic` : 'Fixed'}</TableCell>
            <TableCell className={styles.right}>
              <Amount value={line.monthlyAmount} hidden={hidden} />
            </TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
