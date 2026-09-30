import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  tokens,
} from '@fluentui/react-components';
import { PrintRegular } from '@fluentui/react-icons';
import { formatInr, humanize } from '@hc360/core';
import { ErrorState, LoadingState, StatusBadge, useNotify } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { hrQueries, type PayslipDto, type PayslipLineDto } from './api';
import { amountInWords, printPayslip } from './print';
import { Detail, DetailGrid } from './shared/DetailGrid';
import { monthLabel } from './shared/dates';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '14px' },
  cols: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1fr)',
    gap: '16px',
    '@media (max-width: 760px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  net: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
    gap: '12px',
    flexWrap: 'wrap',
    padding: '12px 16px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorBrandBackground2,
  },
  netAmount: {
    fontSize: tokens.fontSizeHero700,
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: tokens.lineHeightHero700,
  },
  muted: { color: tokens.colorNeutralForeground3 },
  right: { textAlign: 'end' },
  total: { fontWeight: tokens.fontWeightSemibold },
});

function LinesTable({ title, lines, total }: { title: string; lines: PayslipLineDto[]; total: number }) {
  const styles = useStyles();
  return (
    <Table size="small" aria-label={title}>
      <TableHeader>
        <TableRow>
          <TableHeaderCell>{title}</TableHeaderCell>
          <TableHeaderCell className={styles.right}>Amount</TableHeaderCell>
        </TableRow>
      </TableHeader>
      <TableBody>
        {lines.map((line) => (
          <TableRow key={line.code}>
            <TableCell>
              {line.name}
              {line.amount !== line.fullAmount && (
                <Caption1 className={styles.muted}> (of {formatInr(line.fullAmount)})</Caption1>
              )}
            </TableCell>
            <TableCell className={styles.right}>{formatInr(line.amount)}</TableCell>
          </TableRow>
        ))}
        {lines.length === 0 && (
          <TableRow>
            <TableCell colSpan={2}>
              <Caption1 className={styles.muted}>None</Caption1>
            </TableCell>
          </TableRow>
        )}
        <TableRow>
          <TableCell className={styles.total}>Total</TableCell>
          <TableCell className={mergeClasses(styles.right, styles.total)}>{formatInr(total)}</TableCell>
        </TableRow>
      </TableBody>
    </Table>
  );
}

/** On-screen payslip: the same figures as the printed slip, identifiers masked. */
export function PayslipView({ payslip: p }: { payslip: PayslipDto }) {
  const styles = useStyles();
  return (
    <div className={styles.root}>
      {p.hasErrors && (
        <MessageBar intent="error" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>This payslip has errors</MessageBarTitle>
            Correct the inputs or salary structure and process the payroll again. Payroll cannot be approved until it is
            fixed.
          </MessageBarBody>
        </MessageBar>
      )}
      {p.warnings.length > 0 && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Check before approving</MessageBarTitle>
            <ul style={{ margin: '4px 0 0', paddingLeft: 18 }}>
              {p.warnings.map((warning) => (
                <li key={warning}>{warning}</li>
              ))}
            </ul>
          </MessageBarBody>
        </MessageBar>
      )}
      <DetailGrid minColumnWidth={180}>
        <Detail label="Employee">
          {p.employeeName} ({p.employeeCode})
        </Detail>
        <Detail label="Designation">{p.designationName}</Detail>
        <Detail label="Department">{p.departmentName}</Detail>
        <Detail label="PAN">{p.panMasked}</Detail>
        <Detail label="UAN">{p.uan}</Detail>
        <Detail label="Paid by">
          {humanize(p.paymentMode)}
          {p.bankAccountMasked ? ` · ${p.bankAccountMasked}` : ''}
        </Detail>
        <Detail label="Days in month">{p.daysInMonth}</Detail>
        <Detail label="Paid days">{p.payableDays}</Detail>
        <Detail label="Loss of pay">
          {p.lopDays} day{p.lopDays === 1 ? '' : 's'}
          {p.missingAttendanceDays > 0 ? ` (${p.missingAttendanceDays} without attendance)` : ''}
        </Detail>
      </DetailGrid>
      <div className={styles.cols}>
        <LinesTable title="Earnings" lines={p.earnings} total={p.grossEarnings} />
        <LinesTable title="Deductions" lines={p.deductions} total={p.totalDeductions} />
      </div>
      <div className={styles.net}>
        <div>
          <Text weight="semibold">Net pay</Text>
          <Caption1 block>{amountInWords(p.netPay)}</Caption1>
        </div>
        <span className={styles.netAmount}>{formatInr(p.netPay)}</span>
      </div>
      <Caption1 className={styles.muted}>
        Employer contributions (not deducted): PF {formatInr(p.employerPf)} on a PF wage of {formatInr(p.pfWage)} · ESI{' '}
        {formatInr(p.employerEsi)} on an ESI wage of {formatInr(p.esiWage)}.
      </Caption1>
    </div>
  );
}

/** Payslip in a dialog with printing (A4). `mine` reads the signed-in employee's own payslip. */
export function PayslipDialog({
  payslipId,
  mine,
  onClose,
}: {
  payslipId: string | null;
  mine?: boolean;
  onClose: () => void;
}) {
  const notify = useNotify();
  const hrPayslip = useQuery({ ...hrQueries.payslip(payslipId ?? ''), enabled: payslipId !== null && !mine });
  const ownPayslip = useQuery({
    ...hrQueries.myPayslip(payslipId ?? ''),
    enabled: payslipId !== null && Boolean(mine),
  });
  const payslip = mine ? ownPayslip : hrPayslip;
  const p = payslip.data;

  return (
    <Dialog open={payslipId !== null} onOpenChange={(_, data) => !data.open && onClose()}>
      <DialogSurface style={{ maxWidth: 920, width: '100%' }}>
        <DialogBody>
          <DialogTitle>
            {p ? `Salary slip · ${monthLabel(p.year, p.month)}` : 'Salary slip'}{' '}
            {p && <StatusBadge status={p.runStatus} />}
          </DialogTitle>
          <DialogContent>
            {payslip.isLoading && <LoadingState />}
            {payslip.error && <ErrorState error={payslip.error} onRetry={() => void payslip.refetch()} />}
            {p && <PayslipView payslip={p} />}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>Close</Button>
            <Button
              appearance="primary"
              icon={<PrintRegular />}
              disabled={!p}
              onClick={() =>
                p && void printPayslip(p).catch((error: unknown) => notify.error(error, 'Printing failed'))
              }
            >
              Print
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
