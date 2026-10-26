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
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Text,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkCircleRegular, DocumentArrowLeftRegular, PrintRegular } from '@fluentui/react-icons';
import { formatRelative, isApiError, useSession } from '@hc360/core';
import { applyServerErrors, LEAVE_KEEPING_DRAFT, Page, PageHeader, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useMemo, useState } from 'react';
import type { UseFormReturn } from 'react-hook-form';
import { printUhidCard } from '@/lib/print';
import { useFormDraft } from '@/lib/useFormDraft';
import { abhaShareApi, abhaShareQueries, formFromShare, tokenLabel } from '@/modules/front-office/abhaShares';
import { CheckInDialog } from '@/modules/front-office/CheckInDialog';
import { patientApi, patientKeys, type DuplicateCandidate } from '@/modules/patients/api';
import { PatientForm } from '@/modules/patients/PatientForm';
import {
  emptyPatientForm,
  toPatientPayload,
  whatsAppOptInOf,
  type PatientFormValues,
} from '@/modules/patients/validation';

interface RegisterSearch {
  name?: string;
  phone?: string;
  /** An ABHA profile shared at the counter (scan and share): the form is filled in from it. */
  share?: string;
}

export const Route = createFileRoute('/_app/patients/register')({
  validateSearch: (search: Record<string, unknown>): RegisterSearch => ({
    name: typeof search.name === 'string' ? search.name : undefined,
    phone: typeof search.phone === 'string' ? search.phone : undefined,
    share: typeof search.share === 'string' ? search.share : undefined,
  }),
  component: RegisterPatientPage,
});

const useStyles = makeStyles({
  done: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '6px',
    textAlign: 'center',
    padding: '8px 0',
  },
  uhid: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: '32px',
    lineHeight: '40px',
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorBrandForeground1,
  },
  muted: { color: tokens.colorNeutralForeground3 },
  candidates: { display: 'flex', flexDirection: 'column', gap: '8px', marginTop: '12px' },
  candidate: {
    padding: '8px 12px',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
});

interface Registered {
  id: string;
  uhid: string;
  fullName: string;
  ageSex: string;
  phone: string | null;
}

function fullNameOf(values: PatientFormValues): string {
  return [values.title, values.firstName, values.middleName, values.lastName]
    .map((v) => v.trim())
    .filter(Boolean)
    .join(' ');
}

function ageSexOf(values: PatientFormValues): string {
  const sex = values.gender.charAt(0);
  if (values.ageMode === 'age') {
    return `${values.ageYears ?? 0}Y${values.ageMonths ? ` ${values.ageMonths}M` : ''} / ${sex}`;
  }
  const dob = new Date(values.dateOfBirth);
  const years = Number.isNaN(dob.getTime())
    ? null
    : Math.floor((Date.now() - dob.getTime()) / (365.25 * 24 * 3600 * 1000));
  return `${years ?? '—'}Y / ${sex}`;
}

function RegisterPatientPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const search = Route.useSearch();
  const facilityName = useSession((s) => s.user?.facilityName) ?? 'Nana Hospitals';
  const [form, setForm] = useState<UseFormReturn<PatientFormValues> | null>(null);
  const draft = useFormDraft(form, 'patient-register');
  const [duplicates, setDuplicates] = useState<{ values: PatientFormValues; candidates: DuplicateCandidate[] } | null>(
    null,
  );
  const [registered, setRegistered] = useState<Registered | null>(null);
  const [checkIn, setCheckIn] = useState(false);

  const [firstName, ...rest] = (search.name ?? '').trim().split(/\s+/);
  const [defaults] = useState<PatientFormValues>(() => ({
    ...emptyPatientForm,
    firstName: firstName ?? '',
    lastName: rest.join(' '),
    phone: search.phone ?? '',
  }));

  // The share as it was when the page opened: registering takes it off the queue, which must not blank the form.
  const share = useQuery({
    ...abhaShareQueries.detail(search.share ?? ''),
    enabled: Boolean(search.share),
    staleTime: Infinity,
  });
  const fromShare = share.data?.status === 'Waiting' ? share.data : undefined;
  const formDefaults = useMemo(() => (fromShare ? formFromShare(fromShare) : defaults), [fromShare, defaults]);

  const register = useMutation({
    mutationFn: ({ values, confirmed }: { values: PatientFormValues; confirmed: boolean }) =>
      patientApi.register({
        ...toPatientPayload(values),
        whatsAppOptIn: whatsAppOptInOf(values),
        privacyNoticeGiven: values.privacyNoticeGiven,
        confirmedNotDuplicate: confirmed,
      }),
    onSuccess: (result, { values }) => {
      draft.clear();
      // Saved: what the form shows is no longer an unsaved change, so leaving from the result does not ask.
      form?.reset(values);
      setDuplicates(null);
      setRegistered({
        id: result.id,
        uhid: result.uhid,
        fullName: fullNameOf(values),
        ageSex: ageSexOf(values),
        phone: values.phone || null,
      });
      void queryClient.invalidateQueries({ queryKey: patientKeys.all });
      if (fromShare) {
        // The share is done: the patient leaves the scan-and-share queue.
        abhaShareApi
          .registered(fromShare.id, result.id)
          .then(() => queryClient.invalidateQueries({ queryKey: ['abdm', 'shares'], exact: true }))
          .catch((error: unknown) => notify.error(error, 'Registered, but the ABHA share is still on the queue'));
      }
    },
    onError: (error, { values }) => {
      if (isApiError(error) && error.code === 'patient.possible_duplicate' && Array.isArray(error.data)) {
        setDuplicates({ values, candidates: error.data as DuplicateCandidate[] });
      } else if (!(form && applyServerErrors(error, form.setError))) {
        notify.error(error, 'Could not register the patient');
      }
    },
  });

  const startAnother = () => {
    setRegistered(null);
    setCheckIn(false);
    form?.reset(emptyPatientForm);
    setTimeout(() => form?.setFocus('firstName'), 50);
  };

  const printCard = () => {
    if (registered) {
      void printUhidCard({
        facilityName,
        uhid: registered.uhid,
        fullName: registered.fullName,
        ageSex: registered.ageSex,
        phone: registered.phone,
      }).catch((error: unknown) => notify.error(error, 'Printing failed'));
    }
  };

  return (
    <Page maxWidth={1400}>
      <PageHeader
        title="Register patient"
        subtitle="A UHID is issued on saving. Press Ctrl+S to save."
        breadcrumbs={[{ label: 'Patients', onClick: () => void navigate({ to: '/patients' }) }, { label: 'Register' }]}
      />

      {draft.saved && (
        <MessageBar intent="info" icon={<DocumentArrowLeftRegular />}>
          <MessageBarBody>
            An unsaved registration from {formatRelative(new Date(draft.saved.savedAt).toISOString())} was found on this
            computer.
          </MessageBarBody>
          <MessageBarActions>
            <Button onClick={draft.restore}>Restore it</Button>
            <Button appearance="subtle" onClick={draft.discard}>
              Discard
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}

      {search.share && share.error && (
        <MessageBar intent="error">
          <MessageBarBody>The ABHA share could not be opened, so the form is blank.</MessageBarBody>
        </MessageBar>
      )}
      {share.data && !fromShare && (
        <MessageBar intent="warning">
          <MessageBarBody>
            Token {tokenLabel(share.data.tokenNumber)} has already left the queue, so the form is blank.
          </MessageBarBody>
        </MessageBar>
      )}
      {fromShare && (
        <MessageBar intent="info">
          <MessageBarBody>
            Filled in from the ABHA shared at token {tokenLabel(fromShare.tokenNumber)} ({fromShare.abhaAddress}). Check
            the details with the patient before saving.
          </MessageBarBody>
        </MessageBar>
      )}

      {!(search.share && share.isLoading) && (
        <PatientForm
          key={fromShare?.id ?? 'blank'}
          defaultValues={formDefaults}
          onSubmit={(values) => register.mutate({ values, confirmed: false })}
          busy={register.isPending}
          submitLabel="Register and issue UHID"
          checkDuplicates
          askWhatsAppOptIn
          onOpenCandidate={(c) => void navigate({ to: '/patients/$patientId', params: { patientId: c.id } })}
          onFormReady={setForm}
          leaveQuestion={LEAVE_KEEPING_DRAFT}
          secondaryActions={
            <Button appearance="subtle" onClick={() => void navigate({ to: '/patients' })}>
              Cancel
            </Button>
          }
        />
      )}

      <Dialog open={duplicates !== null} onOpenChange={(_, d) => !d.open && setDuplicates(null)}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>This patient may already be registered</DialogTitle>
            <DialogContent>
              <Text>
                Registering the same person twice splits their medical history. Please check these records first.
              </Text>
              <div className={styles.candidates}>
                {duplicates?.candidates.map((c) => (
                  <div key={c.id} className={styles.candidate}>
                    <Text weight="semibold">{c.fullName}</Text>
                    <Caption1 block>
                      {c.uhid} · {c.age ?? '—'} / {c.gender.charAt(0)}
                      {c.phoneMasked ? ` · ${c.phoneMasked}` : ''} · {c.reason}
                    </Caption1>
                    <Button
                      size="small"
                      appearance="subtle"
                      onClick={() => void navigate({ to: '/patients/$patientId', params: { patientId: c.id } })}
                    >
                      Open this record
                    </Button>
                  </div>
                ))}
              </div>
            </DialogContent>
            <DialogActions>
              <Button onClick={() => setDuplicates(null)}>Go back</Button>
              <Button
                appearance="primary"
                disabled={register.isPending}
                onClick={() => duplicates && register.mutate({ values: duplicates.values, confirmed: true })}
              >
                It's a different person, register
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>

      <Dialog open={registered !== null && !checkIn} onOpenChange={(_, d) => !d.open && startAnother()}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Patient registered</DialogTitle>
            <DialogContent>
              <div className={styles.done} role="status">
                <CheckmarkCircleRegular fontSize={40} color={tokens.colorPaletteGreenForeground1} />
                <Text size={400} weight="semibold">
                  {registered?.fullName}
                </Text>
                <Caption1 className={styles.muted}>UHID</Caption1>
                <span className={styles.uhid}>{registered?.uhid}</span>
              </div>
            </DialogContent>
            <DialogActions fluid>
              <Button icon={<PrintRegular />} onClick={printCard}>
                Print UHID card
              </Button>
              <Button
                onClick={() =>
                  registered && void navigate({ to: '/patients/$patientId', params: { patientId: registered.id } })
                }
              >
                Open profile
              </Button>
              <Button onClick={startAnother}>Register another</Button>
              <Button appearance="primary" onClick={() => setCheckIn(true)} autoFocus>
                Check in now
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>

      {registered && (
        <CheckInDialog
          patient={{ id: registered.id, uhid: registered.uhid, fullName: registered.fullName }}
          open={checkIn}
          onClose={startAnother}
        />
      )}
    </Page>
  );
}
