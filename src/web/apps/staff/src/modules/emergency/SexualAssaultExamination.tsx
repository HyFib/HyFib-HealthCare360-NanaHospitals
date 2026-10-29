import {
  Button,
  Caption1,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Select,
  Text,
} from '@fluentui/react-components';
import { formatDateTime } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { PractitionerPicker, type PickedDoctor } from '@/modules/doctor-fees/PractitionerPicker';
import { errorMessage } from '@/modules/lab/common/errors';
import { isoToLocal, localToIso } from '@/modules/quality/downtimeApi';
import { ATTENDANT_LABELS, erApi, erKeys, type EmergencyCase, type ExaminationAttendant } from './api';

/**
 * The medical examination of a victim of sexual assault, as the Bharatiya Nagarik Suraksha Sanhita 2023, s.184 asks it
 * recorded: by whom, its exact start and end, with whose consent, and its report to the investigating officer within
 * seven days. For a child, the POCSO Act 2012, s.27: a girl is examined by a woman doctor, and a parent, a person the
 * child trusts or a woman the head of the hospital nominates is present.
 */
export function SexualAssaultExamination({ c, canMlc }: { c: EmergencyCase; canMlc: boolean }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const m = c.mlc!;
  const e = m.examination;
  const [doctor, setDoctor] = useState<PickedDoctor | null>(e ? { id: e.examinerId, name: e.examinerName } : null);
  const [startedAt, setStartedAt] = useState(e ? isoToLocal(new Date(e.startedAt)) : '');
  const [endedAt, setEndedAt] = useState(e ? isoToLocal(new Date(e.endedAt)) : '');
  const [consent, setConsent] = useState(e?.consentGivenBy ?? '');
  const [attendantKind, setAttendantKind] = useState<ExaminationAttendant>(e?.attendantKind ?? 'Parent');
  const [attendantName, setAttendantName] = useState(e?.attendantName ?? '');
  const [officer, setOfficer] = useState('');
  const refresh = () => void queryClient.invalidateQueries({ queryKey: erKeys.all });
  const girl = m.isChild && c.gender === 'Female';

  const record = useMutation({
    mutationFn: () =>
      erApi.recordExamination(c.id, {
        examinerId: doctor!.id,
        startedAt: localToIso(startedAt)!,
        endedAt: localToIso(endedAt)!,
        consentGivenBy: consent.trim(),
        attendantKind: m.isChild ? attendantKind : null,
        attendantName: m.isChild ? attendantName.trim() : null,
        version: m.version,
      }),
    onSuccess: () => {
      notify.success('Examination recorded', 'Send its report to the investigating officer within seven days.');
      refresh();
    },
  });
  const forward = useMutation({
    mutationFn: () => erApi.forwardReport(c.id, new Date().toISOString(), officer.trim(), m.version),
    onSuccess: refresh,
  });

  const valid =
    doctor !== null &&
    localToIso(startedAt) !== null &&
    localToIso(endedAt) !== null &&
    consent.trim().length > 0 &&
    (!m.isChild || attendantName.trim().length > 0);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
      <Text weight="semibold">Medical examination (BNSS 2023, s.184)</Text>
      {m.isPocso && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>A child: the POCSO Act 2012 applies</MessageBarTitle>
            {girl ? 'A girl is examined by a woman doctor. ' : ''}A parent, a person the child trusts, or a woman the
            head of the hospital nominates is present (s.27).
          </MessageBarBody>
        </MessageBar>
      )}
      {e ? (
        <Text>
          Examined by {e.examinerName}
          {e.examinerRegistration ? ` (Reg. ${e.examinerRegistration})` : ''} from {formatDateTime(e.startedAt)} to{' '}
          {formatDateTime(e.endedAt)}; consent given by {e.consentGivenBy}
          {e.attendantName && e.attendantKind
            ? `; present: ${e.attendantName} (${ATTENDANT_LABELS[e.attendantKind].toLowerCase()})`
            : ''}
          .
        </Text>
      ) : (
        <Caption1>Not yet recorded.</Caption1>
      )}
      {m.reportForwardedAt ? (
        <Text>
          Report sent to {m.investigatingOfficer} on {formatDateTime(m.reportForwardedAt)}.
        </Text>
      ) : m.reportDueAt ? (
        <Text>
          Report to the investigating officer due by {formatDateTime(m.reportDueAt)}
          {m.reportOverdue ? ': overdue' : ''}.
        </Text>
      ) : null}
      {canMlc && !m.reportForwardedAt && (
        <>
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 8 }}>
            <PractitionerPicker
              value={doctor}
              onChange={setDoctor}
              label="Examining doctor"
              required
              hint={girl ? 'A girl child is examined by a woman doctor (POCSO s.27(2)).' : undefined}
            />
            <Field
              label="Consent given by"
              required
              hint="The patient, or a person competent to consent for them, with their relation."
            >
              <Input value={consent} maxLength={200} onChange={(_, d) => setConsent(d.value)} />
            </Field>
            <Field label="Examination began" required>
              <Input type="datetime-local" value={startedAt} onChange={(_, d) => setStartedAt(d.value)} />
            </Field>
            <Field label="Examination ended" required>
              <Input type="datetime-local" value={endedAt} onChange={(_, d) => setEndedAt(d.value)} />
            </Field>
            {m.isChild && (
              <>
                <Field label="Who was present" required>
                  <Select value={attendantKind} onChange={(_, d) => setAttendantKind(d.value as ExaminationAttendant)}>
                    {Object.entries(ATTENDANT_LABELS).map(([value, label]) => (
                      <option key={value} value={value}>
                        {label}
                      </option>
                    ))}
                  </Select>
                </Field>
                <Field label="Their name" required>
                  <Input value={attendantName} maxLength={200} onChange={(_, d) => setAttendantName(d.value)} />
                </Field>
              </>
            )}
          </div>
          {record.error && (
            <MessageBar intent="error">
              <MessageBarBody>{errorMessage(record.error, 'The examination could not be recorded.')}</MessageBarBody>
            </MessageBar>
          )}
          <Button
            appearance="primary"
            style={{ alignSelf: 'flex-start' }}
            disabled={!valid || record.isPending}
            onClick={() => record.mutate()}
          >
            {e ? 'Correct the examination' : 'Record the examination'}
          </Button>
        </>
      )}
      {canMlc && e && !m.reportForwardedAt && (
        <div style={{ display: 'grid', gridTemplateColumns: '2fr auto', gap: 8, alignItems: 'end' }}>
          <Field label="Investigating officer (rank, name and police station)" required>
            <Input value={officer} maxLength={200} onChange={(_, d) => setOfficer(d.value)} />
          </Field>
          <Button disabled={!officer.trim() || forward.isPending} onClick={() => forward.mutate()}>
            Report sent now
          </Button>
        </div>
      )}
      {forward.error && (
        <MessageBar intent="error">
          <MessageBarBody>{errorMessage(forward.error, 'The report could not be recorded.')}</MessageBarBody>
        </MessageBar>
      )}
    </div>
  );
}
