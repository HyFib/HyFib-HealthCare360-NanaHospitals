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
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Select,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { PersonAvailableRegular } from '@fluentui/react-icons';
import { formatInr, formatTime, isApiError, todayIso } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { istDay } from '@/modules/emr/timeline';
import { patientQueries } from '@/modules/patients/api';
import { checkupApi, checkupKeys, checkupQueries, type BookingDto } from './api';
import { checkupProblem, type Wording } from './itinerary';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/**
 * Checks a booked patient in. Check-in adds the package fee to the patient's bill in the same step, so anything Billing
 * refuses (package without a billing item, merged UHID…) is explained here and the booking stays as it was. A corporate
 * batch's check-up is billed to the company instead, on a health check-up visit the server opens for it.
 */
export function CheckInCheckupDialog({
  booking,
  onClose,
  onCheckedIn,
}: {
  booking: BookingDto | null;
  onClose: () => void;
  onCheckedIn?: (booking: BookingDto) => void;
}) {
  return (
    <Dialog open={booking !== null} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        {booking && <CheckInBody key={booking.id} booking={booking} onClose={onClose} onCheckedIn={onCheckedIn} />}
      </DialogSurface>
    </Dialog>
  );
}

function CheckInBody({
  booking,
  onClose,
  onCheckedIn,
}: {
  booking: BookingDto;
  onClose: () => void;
  onCheckedIn?: (booking: BookingDto) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const pkg = useQuery(checkupQueries.package(booking.packageId));
  const company = booking.corporatePayerId ? (booking.corporateName ?? 'the company') : null;
  // Only a visit billed to the company could carry its check-up, and the server opens one, so there is none to choose.
  const visits = useQuery({ ...patientQueries.visits(booking.patientId, 1), enabled: company === null });
  const today = todayIso();
  const todaysVisits = (visits.data?.items ?? []).filter(
    (v) => (v.status === 'Open' || v.status === 'InProgress') && istDay(v.visitedAt) === today,
  );
  const [visitId, setVisitId] = useState<string>(booking.visitId ?? '');
  const [problem, setProblem] = useState<Wording | null>(null);

  const checkIn = useMutation({
    mutationFn: () => checkupApi.checkIn(booking.id, visitId || null),
    onSuccess: (updated) => {
      void queryClient.invalidateQueries({ queryKey: checkupKeys.all });
      notify.success(
        `${updated.patientName ?? 'Patient'} checked in`,
        company
          ? `The package fee is billed to ${company}. Send the patient to the first station.`
          : 'The package fee is on the bill. Send the patient to the first station.',
      );
      onCheckedIn?.(updated);
      onClose();
    },
    onError: (error) => {
      if (isApiError(error)) {
        setProblem(checkupProblem(error, 'check-in'));
      } else {
        notify.error(error, 'Could not check the patient in');
      }
    },
  });

  const submit = () => {
    setProblem(null);
    checkIn.mutate();
  };

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        if (!checkIn.isPending) {
          submit();
        }
      }}
      noValidate
    >
      <DialogBody>
        <DialogTitle>Check in for the health check-up</DialogTitle>
        <DialogContent className={styles.body}>
          <Text>
            <Text weight="semibold">{booking.patientName}</Text> · {booking.uhid}
          </Text>
          <Text>
            {booking.packageName} ({booking.packageCode})
          </Text>
          <MessageBar intent="info">
            <MessageBarBody>
              {company ? (
                <>
                  The package fee {pkg.data ? <strong>{formatInr(pkg.data.price)}</strong> : ''} is billed to{' '}
                  <strong>{company}</strong> when you check in, on a health check-up visit opened for it. The patient
                  pays nothing for the package, and the tests are covered by it.
                </>
              ) : (
                <>
                  The package fee {pkg.data ? <strong>{formatInr(pkg.data.price)}</strong> : ''} is added to the
                  patient's bill when you check in. The tests are covered by the package and not charged separately.
                </>
              )}
            </MessageBarBody>
          </MessageBar>
          {company === null && todaysVisits.length > 0 && (
            <Field
              label="Link to today's visit"
              hint="Optional. The bill follows the visit's payer (self, insurance or corporate)."
            >
              <Select value={visitId} onChange={(_, d) => setVisitId(d.value)}>
                <option value="">Do not link a visit</option>
                {todaysVisits.map((v) => (
                  <option key={v.id} value={v.id}>
                    {v.visitNumber} · {formatTime(v.visitedAt)}
                    {v.departmentName ? ` · ${v.departmentName}` : ''}
                  </option>
                ))}
              </Select>
            </Field>
          )}
          {problem && (
            <MessageBar intent="error" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>{problem.title}</MessageBarTitle>
                {problem.message}
              </MessageBarBody>
            </MessageBar>
          )}
          {problem && (
            <Caption1 className={styles.muted}>
              Nothing was changed: the booking is still waiting for check-in.
            </Caption1>
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={checkIn.isPending}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            icon={checkIn.isPending ? <Spinner size="tiny" aria-hidden /> : <PersonAvailableRegular />}
            disabled={checkIn.isPending}
          >
            Check in
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
