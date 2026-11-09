import {
  Button,
  Caption1,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Text,
} from '@fluentui/react-components';
import { formatDateOnly, formatDateTime, formatInr, todayIso } from '@hc360/core';
import { DataTable, Section, useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { gstReturnApi, type Gstr1Filing, type Gstr1LateEntry } from './gstReturnsApi';

const ARN = /^[A-Za-z0-9]{15}$/;

function monthWords(year: number, month: number) {
  return new Date(Date.UTC(year, month - 1, 1)).toLocaleString('en-IN', {
    month: 'long',
    year: 'numeric',
    timeZone: 'UTC',
  });
}

/**
 * Whether the month's GSTR-1 was filed on the portal: record it once with the ARN, correct it only with a reason. A
 * month with nothing recorded raises no warnings for late paper bills; the panel says so.
 */
export function GstFilingPanel({
  month,
  filing,
  lateEntries,
}: {
  month: string;
  filing: Gstr1Filing | null | undefined;
  lateEntries: Gstr1LateEntry[] | null | undefined;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [arn, setArn] = useState('');
  const [filedOn, setFiledOn] = useState(todayIso());
  const [reason, setReason] = useState('');
  const [correcting, setCorrecting] = useState(false);
  const done = () => {
    setArn('');
    setReason('');
    setCorrecting(false);
    void queryClient.invalidateQueries({ queryKey: ['gst-returns'] });
  };
  const mark = useMutation({
    mutationFn: () => gstReturnApi.markFiled(month, arn.trim(), filedOn),
    onSuccess: () => {
      notify.success('Filing recorded');
      done();
    },
    onError: (error) => notify.error(error, 'Could not record the filing'),
  });
  const correct = useMutation({
    mutationFn: () => gstReturnApi.correctFiling(month, arn.trim(), filedOn, reason.trim(), filing!.version),
    onSuccess: () => {
      notify.success('Filing corrected');
      done();
    },
    onError: (error) => notify.error(error, 'Could not correct the filing'),
  });
  const arnOk = ARN.test(arn.trim());
  const form = (withReason: boolean) => (
    <div style={{ display: 'flex', gap: 12, alignItems: 'flex-end', flexWrap: 'wrap' }}>
      <Field
        label="ARN"
        hint="15 letters and digits, from the portal"
        validationMessage={arn && !arnOk ? 'Not an ARN' : undefined}
      >
        <Input value={arn} onChange={(_, d) => setArn(d.value)} maxLength={15} />
      </Field>
      <Field label="Filed on">
        <Input type="date" value={filedOn} max={todayIso()} onChange={(_, d) => setFiledOn(d.value)} />
      </Field>
      {withReason && (
        <Field label="Reason for the correction" required>
          <Input value={reason} onChange={(_, d) => setReason(d.value)} maxLength={500} />
        </Field>
      )}
      {withReason ? (
        <>
          <Button
            appearance="primary"
            disabled={!arnOk || !reason.trim() || correct.isPending}
            onClick={() => correct.mutate()}
          >
            Save correction
          </Button>
          <Button onClick={() => setCorrecting(false)}>Cancel</Button>
        </>
      ) : (
        <Button appearance="primary" disabled={!arnOk || !filedOn || mark.isPending} onClick={() => mark.mutate()}>
          Record filing
        </Button>
      )}
    </div>
  );

  return (
    <Section title="Filing">
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        {filing ? (
          <>
            <Text>
              Filed on {formatDateOnly(filing.filedOn)}, ARN {filing.arn} · recorded {formatDateTime(filing.recordedAt)}
              {filing.recordedByName ? ` by ${filing.recordedByName}` : ''}
            </Text>
            {filing.correctedAt && (
              <Caption1>
                Corrected {formatDateTime(filing.correctedAt)}
                {filing.correctedByName ? ` by ${filing.correctedByName}` : ''}: {filing.correctionReason}
              </Caption1>
            )}
            {correcting ? (
              form(true)
            ) : (
              <div>
                <Button onClick={() => setCorrecting(true)}>Correct</Button>
              </div>
            )}
          </>
        ) : (
          <>
            <MessageBar intent="info" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>Filing not recorded</MessageBarTitle>
                Paper bills entered later for this month are not flagged until it is. Once GSTR-1 is filed on the
                portal, record the ARN here.
              </MessageBarBody>
            </MessageBar>
            {form(false)}
          </>
        )}
      </div>
      {lateEntries && lateEntries.length > 0 && (
        <div style={{ marginTop: 12 }}>
          <Text weight="semibold">Late paper entries: amend in this return</Text>
          <DataTable<Gstr1LateEntry>
            ariaLabel="Late paper entries"
            rows={lateEntries}
            size="small"
            getRowId={(l) => l.number}
            columns={[
              { key: 'number', header: 'Bill', width: 150, render: (l) => l.number },
              { key: 'date', header: 'Dated', width: 110, render: (l) => formatDateOnly(l.date) },
              { key: 'entered', header: 'Entered', width: 110, render: (l) => formatDateOnly(l.enteredOn) },
              {
                key: 'filed',
                header: 'Return already filed',
                render: (l) => `${monthWords(l.filedYear, l.filedMonth)} (ARN ${l.filedArn})`,
              },
              { key: 'value', header: 'Value', align: 'end', width: 120, render: (l) => formatInr(l.value) },
            ]}
          />
        </div>
      )}
    </Section>
  );
}
