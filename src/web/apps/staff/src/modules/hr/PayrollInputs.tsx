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
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Select,
  Spinner,
  Text,
  Textarea,
  Tooltip,
} from '@fluentui/react-components';
import { AddRegular, ArrowUploadRegular, DeleteRegular } from '@fluentui/react-icons';
import { formatInr, humanize, isApiError } from '@hc360/core';
import { ConfirmDialog, DataTable, useNotify, useUnsavedChanges, useValuesChanged } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { hrApi, hrKeys, hrQueries, type PayrollInputDto } from './api';
import { EmployeePicker, type PickedEmployee } from './EmployeePicker';
import { PAYROLL_INPUT_KINDS, payrollInputKindLabel, type PayrollInputKind } from './labels';
import { inputAmountLabel, previewInputsCsv, previewTotal } from './payroll';
import { readTextFile } from './shared/files';
import { Toolbar } from './shared/layout';

/**
 * The month's variable items: one-off earnings and recoveries, loss-of-pay and TDS overrides, and deductions posted by
 * other modules (canteen credit). Editable until the payroll is approved.
 */
export function PayrollInputsPanel({ runId, editable }: { runId: string; editable: boolean }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const inputs = useQuery(hrQueries.payrollInputs(runId));
  const [adding, setAdding] = useState(false);
  const [importing, setImporting] = useState(false);
  const [deleting, setDeleting] = useState<PayrollInputDto | null>(null);

  const remove = useMutation({
    mutationFn: (id: string) => hrApi.deleteInput(id),
    onSuccess: () => {
      setDeleting(null);
      notify.success('Input removed', 'Process the payroll again to update the payslip.');
      void queryClient.invalidateQueries({ queryKey: hrKeys.payroll });
    },
    onError: (error) => notify.error(error, 'Could not remove the input'),
  });

  const total = (inputs.data ?? []).reduce(
    (sum, input) => {
      if (input.kind === 'Earning') {
        sum.earnings += input.amount;
      } else if (input.kind === 'Deduction') {
        sum.deductions += input.amount;
      }
      return sum;
    },
    { earnings: 0, deductions: 0 },
  );

  return (
    <>
      <Toolbar>
        {editable && (
          <>
            <Button icon={<AddRegular />} onClick={() => setAdding(true)}>
              Add input
            </Button>
            <Button icon={<ArrowUploadRegular />} onClick={() => setImporting(true)}>
              Import CSV
            </Button>
          </>
        )}
        <Caption1>
          {inputs.data?.length ?? 0} item(s) · earnings {formatInr(total.earnings)} · deductions{' '}
          {formatInr(total.deductions)}
        </Caption1>
      </Toolbar>
      <DataTable
        ariaLabel="Payroll inputs"
        rows={inputs.data}
        loading={inputs.isLoading}
        error={inputs.error}
        onRetry={() => void inputs.refetch()}
        getRowId={(i) => i.id}
        columns={[
          {
            key: 'employee',
            header: 'Employee',
            render: (i) => (
              <span>
                <Text weight="semibold">{i.employeeName}</Text> <Caption1>{i.employeeCode}</Caption1>
              </span>
            ),
          },
          { key: 'kind', header: 'Kind', render: (i) => payrollInputKindLabel[i.kind] },
          { key: 'head', header: 'Pay head', width: 110, render: (i) => i.componentCode || '—' },
          { key: 'description', header: 'Description', priority: 'low', render: (i) => i.description },
          { key: 'amount', header: 'Amount', align: 'end', render: (i) => inputAmountLabel(i.kind, i.amount) },
          {
            key: 'source',
            header: 'Added by',
            priority: 'low',
            render: (i) =>
              i.source === 'Integration'
                ? `Other module${i.sourceReference ? ` (${i.sourceReference})` : ''}`
                : humanize(i.source),
          },
        ]}
        rowActions={
          editable
            ? (i) => (
                <Tooltip content="Remove this input" relationship="label">
                  <Button size="small" appearance="subtle" icon={<DeleteRegular />} onClick={() => setDeleting(i)} />
                </Tooltip>
              )
            : undefined
        }
        emptyTitle="No inputs this month"
        emptyDescription={editable ? 'Add arrears, incentives, recoveries or overrides before processing.' : undefined}
        size="small"
      />
      {adding && <AddInputDialog runId={runId} onClose={() => setAdding(false)} />}
      {importing && <ImportDialog runId={runId} onClose={() => setImporting(false)} />}
      <ConfirmDialog
        open={deleting !== null}
        title="Remove this input?"
        confirmLabel="Remove"
        destructive
        busy={remove.isPending}
        onConfirm={() => deleting && remove.mutate(deleting.id)}
        onCancel={() => setDeleting(null)}
      >
        {deleting
          ? `${deleting.employeeName}: ${payrollInputKindLabel[deleting.kind]} ${inputAmountLabel(deleting.kind, deleting.amount)}.`
          : null}
      </ConfirmDialog>
    </>
  );
}

function AddInputDialog({ runId, onClose }: { runId: string; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const components = useQuery(hrQueries.components());
  const [employee, setEmployee] = useState<PickedEmployee | null>(null);
  const [kind, setKind] = useState<PayrollInputKind>('Earning');
  const [componentCode, setComponentCode] = useState('');
  const [description, setDescription] = useState('');
  const [amount, setAmount] = useState('');
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const needsHead = kind === 'Earning' || kind === 'Deduction';
  const heads = useMemo(
    () =>
      (components.data ?? []).filter(
        (c) => c.isActive && !c.isStatutory && c.kind === (kind === 'Deduction' ? 'Deduction' : 'Earning'),
      ),
    [components.data, kind],
  );

  const submit = async () => {
    const value = Number(amount);
    const next: Record<string, string> = {};
    if (!employee) next.employeeId = 'Choose the employee.';
    if (needsHead && !componentCode) next.componentCode = 'Choose the pay head.';
    if (!description.trim()) next.description = 'Describe the item (shown on the payslip).';
    if (!amount || Number.isNaN(value) || value < 0 || (needsHead && value === 0))
      next.amount = 'Enter an amount above zero.';
    if (kind === 'LopDaysOverride' && value > 31) next.amount = 'Loss-of-pay days must be between 0 and 31.';
    setErrors(next);
    if (Object.keys(next).length > 0 || !employee) {
      return;
    }
    setBusy(true);
    try {
      await hrApi.addInput(runId, {
        employeeId: employee.id,
        kind,
        componentCode: needsHead ? componentCode : null,
        description: description.trim(),
        amount: value,
      });
      notify.success('Input added', 'Process the payroll again to include it.');
      void queryClient.invalidateQueries({ queryKey: hrKeys.payroll });
      onClose();
    } catch (error) {
      if (isApiError(error) && error.isValidation) {
        setErrors(
          Object.fromEntries(Object.entries(error.fieldErrors).map(([field, messages]) => [field, messages[0] ?? ''])),
        );
      } else {
        notify.error(error, 'Could not add the input');
      }
    } finally {
      setBusy(false);
    }
  };

  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ employee, kind, componentCode, description, amount }),
    busy,
    onClose,
  });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 600 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            void submit();
          }}
          noValidate
        >
          <DialogBody>
            <DialogTitle>Add payroll input</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <EmployeePicker
                value={employee}
                onChange={setEmployee}
                required
                autoFocus
                validationMessage={errors.employeeId}
              />
              <Field label="Kind" required>
                <Select
                  value={kind}
                  onChange={(_, d) => {
                    setKind(d.value as PayrollInputKind);
                    setComponentCode('');
                  }}
                >
                  {PAYROLL_INPUT_KINDS.map((k) => (
                    <option key={k} value={k}>
                      {payrollInputKindLabel[k]}
                    </option>
                  ))}
                </Select>
              </Field>
              {needsHead && (
                <Field
                  label="Pay head"
                  required
                  validationMessage={errors.componentCode}
                  validationState={errors.componentCode ? 'error' : 'none'}
                >
                  <Select value={componentCode} onChange={(_, d) => setComponentCode(d.value)}>
                    <option value="">Choose</option>
                    {heads.map((head) => (
                      <option key={head.id} value={head.code}>
                        {head.name} ({head.code})
                      </option>
                    ))}
                  </Select>
                </Field>
              )}
              <Field
                label="Description (shown on the payslip)"
                required
                validationMessage={errors.description}
                validationState={errors.description ? 'error' : 'none'}
              >
                <Input
                  value={description}
                  onChange={(_, d) => setDescription(d.value)}
                  maxLength={200}
                  placeholder="For example: night duty allowance, September"
                />
              </Field>
              <Field
                label={kind === 'LopDaysOverride' ? 'Loss-of-pay days' : 'Amount (₹)'}
                required
                hint={
                  kind === 'LopDaysOverride'
                    ? 'Replaces the loss of pay worked out from attendance.'
                    : kind === 'TdsOverride'
                      ? 'Replaces the projected TDS for this month.'
                      : undefined
                }
                validationMessage={errors.amount}
                validationState={errors.amount ? 'error' : 'none'}
              >
                <Input
                  type="number"
                  min={0}
                  step={kind === 'LopDaysOverride' ? 0.5 : 1}
                  value={amount}
                  onChange={(_, d) => setAmount(d.value)}
                />
              </Field>
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={busy}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={busy}
                icon={busy ? <Spinner size="tiny" /> : undefined}
              >
                Add
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}

function ImportDialog({ runId, onClose }: { runId: string; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [csv, setCsv] = useState('');
  const [busy, setBusy] = useState(false);
  const [serverErrors, setServerErrors] = useState<string[]>([]);
  const preview = useMemo(() => previewInputsCsv(csv), [csv]);
  const ready = csv.trim().length > 0 && preview.errors.length === 0 && preview.lines.length > 0;

  const submit = async () => {
    setBusy(true);
    setServerErrors([]);
    try {
      const result = await hrApi.importInputs(runId, csv);
      notify.success('Inputs imported', `${result.imported} line(s) added. Process the payroll again to include them.`);
      void queryClient.invalidateQueries({ queryKey: hrKeys.payroll });
      onClose();
    } catch (error) {
      if (isApiError(error) && (error.isValidation || error.status === 400)) {
        const messages = Object.entries(error.fieldErrors).flatMap(([field, list]) =>
          list.map((message) => (field && field !== 'csv' ? `${field}: ${message}` : message)),
        );
        setServerErrors(messages.length ? messages : [error.userMessage]);
      } else {
        notify.error(error, 'Could not import the inputs');
      }
    } finally {
      setBusy(false);
    }
  };

  const guard = useUnsavedChanges({ dirty: csv.trim() !== '', busy, onClose });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 760, width: '100%' }}>
        <DialogBody>
          <DialogTitle>Import payroll inputs</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Text>
              One line per item: <code>employeeCode,componentCode,amount,description</code>. The header line is
              optional. The whole file is imported or nothing is, so fix every line shown below first.
            </Text>
            <Field label="CSV file">
              <input
                type="file"
                accept=".csv,text/csv,text/plain"
                aria-label="CSV file"
                onChange={(event) => {
                  const file = event.target.files?.[0];
                  if (file) {
                    if (file.size > 1024 * 1024) {
                      notify.warning('File too large', 'The file must be at most 1 MB.');
                      return;
                    }
                    void readTextFile(file).then(setCsv);
                  }
                }}
              />
            </Field>
            <Field label="Or paste the lines here">
              <Textarea
                value={csv}
                onChange={(_, d) => setCsv(d.value)}
                rows={6}
                resize="vertical"
                placeholder={'NHEMP00042,INCENTIVE,1500,Referral incentive\nNHEMP00043,CANTEEN,640,Canteen September'}
              />
            </Field>
            {csv.trim() && (
              <MessageBar intent={preview.errors.length ? 'error' : 'success'} layout="multiline">
                <MessageBarBody>
                  <MessageBarTitle>
                    {preview.errors.length
                      ? `${preview.errors.length} line(s) need fixing`
                      : `${preview.lines.length} line(s) ready · total ${formatInr(previewTotal(preview))}`}
                  </MessageBarTitle>
                  {preview.errors.length > 0 && (
                    <ul style={{ margin: '4px 0 0', paddingLeft: 18, maxHeight: 140, overflowY: 'auto' }}>
                      {preview.errors.map((e) => (
                        <li key={e.line}>
                          Line {e.line}: {e.message}
                        </li>
                      ))}
                    </ul>
                  )}
                </MessageBarBody>
              </MessageBar>
            )}
            {serverErrors.length > 0 && (
              <MessageBar intent="error" layout="multiline">
                <MessageBarBody>
                  <MessageBarTitle>Nothing was imported</MessageBarTitle>
                  <ul style={{ margin: '4px 0 0', paddingLeft: 18, maxHeight: 160, overflowY: 'auto' }}>
                    {serverErrors.map((message) => (
                      <li key={message}>{message}</li>
                    ))}
                  </ul>
                </MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={guard.requestClose} disabled={busy}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={!ready || busy}
              icon={busy ? <Spinner size="tiny" /> : <ArrowUploadRegular />}
              onClick={() => void submit()}
            >
              Import {preview.lines.length || ''} line(s)
            </Button>
          </DialogActions>
        </DialogBody>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
