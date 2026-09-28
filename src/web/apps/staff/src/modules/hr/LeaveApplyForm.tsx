import { Button, Caption1, Field, makeStyles, MessageBar, MessageBarBody, Spinner, Text, tokens } from '@fluentui/react-components';
import { AttachRegular, SendRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { todayIso } from '@hc360/core';
import { applyServerErrors, ChoiceField, DateField, FormGrid, SelectField, TextAreaField, useNotify } from '@hc360/ui';
import { useState, type ReactNode } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import type { ApplyLeaveRequest, LeaveBalanceDto, LeaveTypeDto } from './api';
import { balanceFor, daysText, estimateLeaveDays, needsDocument } from './leave';
import { DOCUMENT_ACCEPT, MAX_UPLOAD_BYTES, uploadFile } from './shared/files';

const schema = z
  .object({
    leaveTypeId: z.string().nullable(),
    fromDate: z.string({ error: 'Choose the first day.' }).min(1, 'Choose the first day.'),
    fromSession: z.enum(['FirstHalf', 'SecondHalf']),
    toDate: z.string({ error: 'Choose the last day.' }).min(1, 'Choose the last day.'),
    toSession: z.enum(['FirstHalf', 'SecondHalf']),
    reason: z.string().trim().min(1, 'Give a reason.').max(500),
  })
  .superRefine((v, ctx) => {
    if (!v.leaveTypeId) {
      ctx.addIssue({ code: 'custom', path: ['leaveTypeId'], message: 'Choose the type of leave.' });
    }
    if (v.fromDate && v.toDate && v.toDate < v.fromDate) {
      ctx.addIssue({ code: 'custom', path: ['toDate'], message: 'The leave ends before it starts.' });
    } else if (v.fromDate && v.toDate && v.fromDate.slice(0, 4) !== v.toDate.slice(0, 4)) {
      ctx.addIssue({ code: 'custom', path: ['toDate'], message: 'Leave that crosses the new year must be applied as two requests.' });
    } else if (v.fromDate === v.toDate && v.fromSession === 'SecondHalf' && v.toSession === 'FirstHalf') {
      ctx.addIssue({ code: 'custom', path: ['toSession'], message: 'A one-day leave cannot run from the second half to the first half.' });
    }
  });

type Values = z.infer<typeof schema>;

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '14px' },
  info: { display: 'flex', flexDirection: 'column', gap: '2px', padding: '10px 12px', borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground3 },
  estimate: { fontWeight: tokens.fontWeightSemibold },
  muted: { color: tokens.colorNeutralForeground3 },
  actions: { display: 'flex', gap: '8px', justifyContent: 'flex-end', flexWrap: 'wrap' },
});

/**
 * Leave application: type (with the balance left), dates with half days, reason and, when the type asks for it, a
 * supporting document. Used by staff for themselves and by HR on an employee's behalf.
 */
export function LeaveApplyForm({
  leaveTypes,
  balances,
  submit,
  submitLabel = 'Apply for leave',
  onCancel,
  header,
  defaultFromDate,
}: {
  leaveTypes: LeaveTypeDto[];
  balances?: LeaveBalanceDto[];
  /** Sends the request; reject to show the error on the form. */
  submit: (request: ApplyLeaveRequest) => Promise<unknown>;
  submitLabel?: string;
  onCancel?: () => void;
  header?: ReactNode;
  defaultFromDate?: string;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const [file, setFile] = useState<File | null>(null);
  const [fileError, setFileError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const start = defaultFromDate ?? todayIso(1);
  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: { leaveTypeId: leaveTypes.length === 1 ? (leaveTypes[0]?.id ?? null) : null, fromDate: start, fromSession: 'FirstHalf', toDate: start, toSession: 'SecondHalf', reason: '' },
  });
  const [leaveTypeId, fromDate, fromSession, toDate, toSession] = useWatch({ control: form.control, name: ['leaveTypeId', 'fromDate', 'fromSession', 'toDate', 'toSession'] });
  const type = leaveTypes.find((t) => t.id === leaveTypeId);
  const balance = balanceFor(balances, leaveTypeId);
  const halfDays = type?.allowHalfDay ?? true;
  const estimate = estimateLeaveDays(fromDate, halfDays ? fromSession : 'FirstHalf', toDate, halfDays ? toSession : 'SecondHalf');
  const documentNeeded = needsDocument(type, estimate);

  const onValid = async (values: Values) => {
    if (documentNeeded && !file) {
      setFileError(`Attach a supporting document for more than ${type?.documentRequiredAfterDays} days.`);
      return;
    }
    setBusy(true);
    try {
      const attachment = file ? await uploadFile(file, 'hr-leave') : null;
      await submit({
        leaveTypeId: values.leaveTypeId ?? '',
        fromDate: values.fromDate,
        fromSession: halfDays ? values.fromSession : 'FirstHalf',
        toDate: values.toDate,
        toSession: halfDays ? values.toSession : 'SecondHalf',
        reason: values.reason.trim(),
        attachmentFileId: attachment?.id ?? null,
      });
    } catch (error) {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not apply for leave');
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={form.handleSubmit(onValid)} noValidate className={styles.form}>
      {header}
      <SelectField
        control={form.control}
        name="leaveTypeId"
        label="Type of leave"
        required
        placeholder="Choose"
        options={leaveTypes.map((t) => {
          const available = balanceFor(balances, t.id);
          return {
            value: t.id,
            label: `${t.name} (${t.code})${t.tracksBalance && available ? ` · ${daysText(available.available)} available` : ''}${t.isPaid ? '' : ' · unpaid'}`,
          };
        })}
      />
      {type && (
        <div className={styles.info} role="note">
          {type.tracksBalance && balance ? (
            <Text>
              <strong>{daysText(balance.available)}</strong> available this year
              {balance.pending > 0 ? ` (${daysText(balance.pending)} waiting for approval)` : ''}.
            </Text>
          ) : (
            <Text>{type.tracksBalance ? 'Balance not credited yet for this year.' : 'This leave does not use a yearly balance.'}</Text>
          )}
          <Caption1 className={styles.muted}>
            {[
              type.isPaid ? 'Paid leave' : 'Unpaid leave (loss of pay)',
              type.allowHalfDay ? 'half days allowed' : 'full days only',
              type.maxDaysPerRequest ? `at most ${daysText(type.maxDaysPerRequest)} at a time` : '',
              type.documentRequiredAfterDays !== null ? `document needed above ${daysText(type.documentRequiredAfterDays)}` : '',
            ]
              .filter(Boolean)
              .join(' · ')}
          </Caption1>
        </div>
      )}
      <FormGrid columns={2} minColumnWidth={200}>
        <DateField control={form.control} name="fromDate" label="From" required />
        {halfDays ? (
          <ChoiceField
            control={form.control}
            name="fromSession"
            label="Starting"
            options={[
              { value: 'FirstHalf', label: 'Morning' },
              { value: 'SecondHalf', label: 'Afternoon (second half)' },
            ]}
          />
        ) : (
          <span />
        )}
        <DateField control={form.control} name="toDate" label="To" required min={fromDate || undefined} />
        {halfDays ? (
          <ChoiceField
            control={form.control}
            name="toSession"
            label="Ending"
            options={[
              { value: 'SecondHalf', label: 'Evening' },
              { value: 'FirstHalf', label: 'Noon (first half)' },
            ]}
          />
        ) : (
          <span />
        )}
      </FormGrid>
      {estimate !== null && (
        <Text className={styles.estimate} role="status">
          {type?.countsNonWorkingDays
            ? `${daysText(estimate)} (calendar days)`
            : `Up to ${daysText(estimate)}. Holidays and weekly offs in between are not counted.`}
        </Text>
      )}
      {type && balance && type.tracksBalance && estimate !== null && estimate > balance.available && (
        <MessageBar intent="warning">
          <MessageBarBody>This may be more than the {daysText(balance.available)} available. HR checks the balance again when approving.</MessageBarBody>
        </MessageBar>
      )}
      <TextAreaField control={form.control} name="reason" label="Reason" required rows={3} maxLength={500} />
      <Field
        label={documentNeeded ? 'Supporting document (required)' : 'Supporting document (optional)'}
        hint="PDF or photo, up to 10 MB, for example a medical certificate."
        validationMessage={fileError ?? undefined}
        validationState={fileError ? 'error' : 'none'}
        required={documentNeeded}
      >
        <input
          type="file"
          accept={DOCUMENT_ACCEPT}
          aria-label="Supporting document"
          onChange={(event) => {
            const chosen = event.target.files?.[0] ?? null;
            if (chosen && chosen.size > MAX_UPLOAD_BYTES) {
              setFileError('The file is larger than 10 MB.');
              setFile(null);
              return;
            }
            setFileError(null);
            setFile(chosen);
          }}
        />
      </Field>
      {file && (
        <Caption1 className={styles.muted}>
          <AttachRegular aria-hidden /> {file.name}
        </Caption1>
      )}
      <div className={styles.actions}>
        {onCancel && (
          <Button onClick={onCancel} disabled={busy}>
            Cancel
          </Button>
        )}
        <Button type="submit" appearance="primary" disabled={busy} icon={busy ? <Spinner size="tiny" /> : <SendRegular />}>
          {submitLabel}
        </Button>
      </div>
    </form>
  );
}
