import {
  Badge,
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
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Radio,
  RadioGroup,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { TagPercentRegular } from '@fluentui/react-icons';
import { formatInr } from '@hc360/core';
import { useNotify, useUnsavedChanges, useValuesChanged } from '@hc360/ui';
import { useMutation } from '@tanstack/react-query';
import { useState } from 'react';
import { billingApi, type BillDetail, type DiscountOutcome, type LineDiscountInput } from './api';
import {
  allowedWithoutApproval,
  lineDiscountable,
  previewDiscount,
  requiresApproval,
  type DiscountDraft,
} from './billTotals';
import { useDiscountAuthority, useRefreshBilling } from './hooks';
import { amountInputText, formatPercent, parsePercent, parseRupees } from './money';
import { ActionError, Amount, SummaryList, SummaryRow } from './ui';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '14px' },
  row: { display: 'flex', gap: '12px', alignItems: 'end', flexWrap: 'wrap' },
  table: {
    maxHeight: '260px',
    overflowY: 'auto',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  input: { width: '110px' },
  preview: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))',
    gap: '12px 24px',
    padding: '12px 14px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
  },
  payable: {
    fontSize: '30px',
    lineHeight: '36px',
    fontWeight: tokens.fontWeightBold,
    color: tokens.colorBrandForeground1,
  },
  muted: { color: tokens.colorNeutralForeground3 },
  strike: { textDecorationLine: 'line-through', color: tokens.colorNeutralForeground3, marginRight: '8px' },
});

type Unit = 'amount' | 'percent';

interface DraftState {
  billUnit: Unit;
  billValue: string;
  lineUnit: Unit;
  lineValues: Record<string, string>;
  reason: string;
}

function initialState(bill: BillDetail): DraftState {
  const lineValues: Record<string, string> = {};
  for (const line of bill.lines) {
    if (line.lineDiscountAmount > 0) {
      lineValues[line.id] = amountInputText(line.lineDiscountAmount);
    }
  }
  return {
    billUnit: bill.billDiscountPercent !== null ? 'percent' : 'amount',
    billValue:
      bill.billDiscountPercent !== null
        ? String(bill.billDiscountPercent)
        : bill.billDiscountValue !== null
          ? amountInputText(bill.billDiscountValue)
          : '',
    lineUnit: 'amount',
    lineValues,
    reason: bill.discountReason ?? '',
  };
}

type Parsed =
  | {
      ok: true;
      draft: DiscountDraft;
      request: {
        billDiscountPercent: number | null;
        billDiscountAmount: number | null;
        lineDiscounts: LineDiscountInput[];
      };
    }
  | { ok: false; error: string };

function parseDraft(state: DraftState): Parsed {
  let billPercent: number | null = null;
  let billAmount: number | null = null;
  if (state.billValue.trim()) {
    if (state.billUnit === 'percent') {
      const parsed = parsePercent(state.billValue, { allowZero: true });
      if (!parsed.ok) {
        return { ok: false, error: `Bill discount: ${parsed.error}` };
      }
      billPercent = parsed.percent > 0 ? parsed.percent : null;
    } else {
      const parsed = parseRupees(state.billValue, { allowZero: true });
      if (!parsed.ok) {
        return { ok: false, error: `Bill discount: ${parsed.error}` };
      }
      billAmount = parsed.rupees > 0 ? parsed.rupees : null;
    }
  }
  const lines: LineDiscountInput[] = [];
  for (const [lineId, text] of Object.entries(state.lineValues)) {
    if (!text.trim()) {
      continue;
    }
    const parsed =
      state.lineUnit === 'percent' ? parsePercent(text, { allowZero: true }) : parseRupees(text, { allowZero: true });
    if (!parsed.ok) {
      return { ok: false, error: `Service discount: ${parsed.error}` };
    }
    const value = 'percent' in parsed ? parsed.percent : parsed.rupees;
    if (value > 0) {
      lines.push({
        lineId,
        percent: state.lineUnit === 'percent' ? value : null,
        amount: state.lineUnit === 'amount' ? value : null,
      });
    }
  }
  return {
    ok: true,
    draft: { billPercent, billAmount, lines },
    request: { billDiscountPercent: billPercent, billDiscountAmount: billAmount, lineDiscounts: lines },
  };
}

/**
 * Staff discount on a draft bill (MB-05, OPB-04): per service and/or on the whole bill, with a live preview of the new
 * amount payable (the server's own arithmetic) and the user's limit. Above the limit the discount is sent for
 * approval and the bill waits; within it, it applies at once.
 */
export function DiscountDialog({
  bill,
  open,
  onClose,
  onDone,
}: {
  bill: BillDetail;
  open: boolean;
  onClose: () => void;
  onDone?: (outcome: DiscountOutcome) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const refresh = useRefreshBilling();
  const { authority } = useDiscountAuthority();
  const [state, setState] = useState<DraftState>(() => initialState(bill));
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setState(initialState(bill));
    }
  }

  const discountable = bill.lines.filter(
    (line) => line.isDiscountAllowed && !line.isReturn && lineDiscountable(line) > 0,
  );
  const parsed = parseDraft(state);
  const preview = parsed.ok ? previewDiscount(bill, parsed.draft) : null;
  const totals = preview?.ok ? preview.totals : null;
  const allowed = totals ? allowedWithoutApproval(totals.discountBase, authority) : null;
  const needsApproval = totals
    ? requiresApproval(totals.userDiscount, totals.discountBase, authority, bill.approvedDiscountAmount)
    : false;
  const reasonOk = state.reason.trim().length >= 5;

  const apply = useMutation({
    mutationFn: () => {
      if (!parsed.ok) {
        throw new Error(parsed.error);
      }
      return billingApi.applyDiscount(bill.id, {
        ...parsed.request,
        lineDiscounts: parsed.request.lineDiscounts,
        reason: state.reason.trim(),
        version: bill.version,
      });
    },
    onSuccess: (outcome) => {
      if (outcome.applied) {
        notify.success(
          outcome.requestedDiscount > 0 ? 'Discount applied' : 'Discount removed',
          outcome.requestedDiscount > 0 ? formatInr(outcome.requestedDiscount) : undefined,
        );
      } else {
        notify.warning(
          'Sent for approval',
          `A discount of ${formatInr(outcome.requestedDiscount)} is above your limit of ${formatInr(outcome.allowedWithoutApproval)}. The bill can be finalised once it is approved.`,
        );
      }
      void refresh();
      onDone?.(outcome);
      onClose();
    },
  });

  const set = (patch: Partial<DraftState>) => setState((s) => ({ ...s, ...patch }));
  const clearAll = () => set({ billValue: '', lineValues: {}, reason: state.reason || 'Discount removed' });

  const guard = useUnsavedChanges({
    dirty: useValuesChanged(state, open),
    busy: apply.isPending,
    open,
    onClose,
  });

  return (
    <Dialog open={open} onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 760, width: '100%' }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (parsed.ok && reasonOk && !apply.isPending) {
              apply.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Discount</DialogTitle>
            <DialogContent className={styles.body}>
              {bill.pendingDiscount && (
                <MessageBar intent="warning">
                  <MessageBarBody>
                    <MessageBarTitle>
                      A discount of {formatInr(bill.pendingDiscount.requestedAmount)} is awaiting approval
                    </MessageBarTitle>
                    A new request replaces it.
                  </MessageBarBody>
                </MessageBar>
              )}

              <div className={styles.row}>
                <Field label="Discount on the whole bill">
                  <RadioGroup
                    layout="horizontal"
                    value={state.billUnit}
                    onChange={(_, data) => set({ billUnit: data.value as Unit })}
                  >
                    <Radio value="amount" label="₹ amount" />
                    <Radio value="percent" label="% of bill" />
                  </RadioGroup>
                </Field>
                <Field label={state.billUnit === 'percent' ? 'Percent' : 'Amount (₹)'}>
                  <Input
                    className={styles.input}
                    value={state.billValue}
                    onChange={(_, data) => set({ billValue: data.value })}
                    inputMode="decimal"
                    contentAfter={state.billUnit === 'percent' ? <Text>%</Text> : undefined}
                    contentBefore={state.billUnit === 'amount' ? <Text>₹</Text> : undefined}
                    autoFocus
                  />
                </Field>
              </div>

              {discountable.length > 0 && (
                <div>
                  <div className={styles.row} style={{ marginBottom: 6 }}>
                    <Text weight="semibold">Discount on individual services</Text>
                    <RadioGroup
                      layout="horizontal"
                      value={state.lineUnit}
                      onChange={(_, data) => set({ lineUnit: data.value as Unit, lineValues: {} })}
                      aria-label="Service discount unit"
                    >
                      <Radio value="amount" label="₹" />
                      <Radio value="percent" label="%" />
                    </RadioGroup>
                  </div>
                  <div className={styles.table}>
                    <Table size="small" aria-label="Service discounts">
                      <TableHeader>
                        <TableRow>
                          <TableHeaderCell>Service</TableHeaderCell>
                          <TableHeaderCell style={{ width: 130, textAlign: 'end' }}>Can discount</TableHeaderCell>
                          <TableHeaderCell style={{ width: 140 }}>Discount</TableHeaderCell>
                        </TableRow>
                      </TableHeader>
                      <TableBody>
                        {discountable.map((line) => (
                          <TableRow key={line.id}>
                            <TableCell>
                              {line.description}
                              <Caption1 block className={styles.muted}>
                                {line.serviceCode}
                              </Caption1>
                            </TableCell>
                            <TableCell style={{ textAlign: 'end' }}>
                              <Amount value={lineDiscountable(line)} />
                            </TableCell>
                            <TableCell>
                              <Input
                                className={styles.input}
                                value={state.lineValues[line.id] ?? ''}
                                onChange={(_, data) =>
                                  set({ lineValues: { ...state.lineValues, [line.id]: data.value } })
                                }
                                inputMode="decimal"
                                aria-label={`Discount on ${line.description}`}
                                contentAfter={state.lineUnit === 'percent' ? <Text>%</Text> : undefined}
                                contentBefore={state.lineUnit === 'amount' ? <Text>₹</Text> : undefined}
                              />
                            </TableCell>
                          </TableRow>
                        ))}
                      </TableBody>
                    </Table>
                  </div>
                  {bill.lines.some((line) => !line.isDiscountAllowed) && (
                    <Caption1 className={styles.muted}>
                      Services marked as not discountable (for example medicines at MRP) are left out.
                    </Caption1>
                  )}
                </div>
              )}

              {!parsed.ok && (
                <MessageBar intent="error">
                  <MessageBarBody>{parsed.error}</MessageBarBody>
                </MessageBar>
              )}
              {preview && !preview.ok && (
                <MessageBar intent={preview.kind === 'invalid' ? 'error' : 'info'}>
                  <MessageBarBody>{preview.message}</MessageBarBody>
                </MessageBar>
              )}

              {totals && (
                <div className={styles.preview} aria-live="polite">
                  <SummaryList>
                    <SummaryRow label="Discount given">
                      <Amount value={totals.userDiscount} strong />
                    </SummaryRow>
                    <SummaryRow
                      label="Of the discountable value"
                      hint={
                        totals.discountBase > 0
                          ? formatPercent((totals.userDiscount / totals.discountBase) * 100)
                          : undefined
                      }
                    >
                      <Amount value={totals.discountBase} />
                    </SummaryRow>
                    <SummaryRow
                      label={authority.canApprove ? 'You can approve any discount' : 'You can give without approval'}
                    >
                      {authority.canApprove ? '—' : <Amount value={allowed} />}
                    </SummaryRow>
                    <div>
                      {totals.userDiscount === 0 ? (
                        <Badge appearance="tint">No discount</Badge>
                      ) : needsApproval ? (
                        <Badge appearance="filled" color="warning" icon={<TagPercentRegular />}>
                          Needs approval
                        </Badge>
                      ) : (
                        <Badge appearance="filled" color="success">
                          Within your limit
                        </Badge>
                      )}
                    </div>
                  </SummaryList>
                  <div>
                    <Caption1 block className={styles.muted}>
                      {totals.payerAmount > 0
                        ? 'Payable by the patient after discount'
                        : 'Amount payable after discount'}
                    </Caption1>
                    {totals.patientAmount !== bill.patientAmount && (
                      <span className={styles.strike}>{formatInr(bill.patientAmount)}</span>
                    )}
                    <span className={styles.payable}>{formatInr(totals.patientAmount)}</span>
                    {totals.tax > 0 && (
                      <Caption1 block className={styles.muted}>
                        Includes GST {formatInr(totals.tax)} (CGST {formatInr(totals.cgst)} + SGST{' '}
                        {formatInr(totals.sgst)})
                      </Caption1>
                    )}
                  </div>
                </div>
              )}

              <Field
                label="Reason for the discount"
                required
                hint="Shown to the approver and printed on the bill. At least 5 characters."
              >
                <Textarea
                  value={state.reason}
                  onChange={(_, data) => set({ reason: data.value })}
                  maxLength={300}
                  rows={2}
                  placeholder="e.g. Senior citizen concession, staff relative, doctor's request"
                />
              </Field>
              <ActionError error={apply.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={clearAll} disabled={apply.isPending}>
                Remove all discounts
              </Button>
              <Button onClick={guard.requestClose} disabled={apply.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={
                  !parsed.ok ||
                  !reasonOk ||
                  apply.isPending ||
                  (preview !== null && !preview.ok && preview.kind === 'invalid')
                }
                icon={apply.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                {needsApproval ? 'Request approval' : 'Apply discount'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
