import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  OverlayDrawer,
  Radio,
  RadioGroup,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkCircleRegular, DismissCircleRegular, DismissRegular, DropRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import {
  formatDateOnly,
  formatDateTime,
  formatTime,
  isIndianMobile,
  normalizePhone,
  todayIso,
  type BloodGroup,
} from '@hc360/core';
import {
  ChoiceField,
  DateField,
  FormGrid,
  FullRow,
  LoadingState,
  NumberField,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { fromIstInput, istDate } from '@/modules/ot/shared/datetime';
import { applyFormErrors } from '@/modules/ot/shared/errors';
import { ActionError } from '@/modules/ot/shared/feedback';
import { DateTimeField } from '@/modules/ot/shared/fields';
import { useNow } from '@/modules/ot/shared/hooks';
import { INDIAN_STATES } from '@/modules/patients/validation';
import { PatientPicker } from '@/modules/patients/PatientPicker';
import {
  BAG_TYPES,
  bloodApi,
  bloodKeys,
  bloodQueries,
  DONATION_TYPES,
  KNOWN_GROUPS,
  type DonorDetail,
  type DonorScreeningDto,
  type ScreeningAnswer,
} from './api';
import { OutcomeBadge } from './BloodBadges';
import { evaluateDonor, missingAnswers, questionsFor, vitalChecks, type PartialVitals } from './donorRules';
import { bagTypeLabel, donationTypeLabel, groupLabel } from './labels';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  layout: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1.4fr) minmax(260px, 1fr)',
    gap: '16px',
    '@media (max-width: 900px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  vitals: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(150px, 1fr))', gap: '8px 12px' },
  check: { display: 'inline-flex', gap: '4px', alignItems: 'center', fontSize: tokens.fontSizeBase200 },
  pass: { color: tokens.colorPaletteGreenForeground1 },
  fail: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
  muted: { color: tokens.colorNeutralForeground3 },
  questions: { display: 'flex', flexDirection: 'column', gap: '6px' },
  question: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) auto',
    gap: '4px 12px',
    alignItems: 'center',
    padding: '6px 8px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  questionYes: {
    border: `1px solid ${tokens.colorPaletteDarkOrangeBorder2}`,
    backgroundColor: tokens.colorPaletteDarkOrangeBackground1,
  },
  preview: { position: 'sticky', top: 0, display: 'flex', flexDirection: 'column', gap: '8px' },
  reasons: { margin: 0, paddingLeft: '18px' },
});

// ---------------------------------------------------------------- register / edit

const donorSchema = z
  .object({
    firstName: z.string().trim().min(1, 'Enter the first name.').max(100),
    lastName: z.string().trim().max(100),
    gender: z.enum(['Male', 'Female', 'Other']),
    dateOfBirth: z.string().nullable(),
    bloodGroup: z.string().nullable(),
    phone: z.string().trim(),
    email: z.string().trim().max(256),
    line1: z.string().trim().max(200),
    city: z.string().trim().max(100),
    district: z.string().trim().max(100),
    state: z.string().nullable(),
    pinCode: z.string().trim(),
    occupation: z.string().trim().max(100),
    consentToNotify: z.boolean(),
    notes: z.string().trim().max(1000),
    isActive: z.boolean(),
  })
  .superRefine((v, ctx) => {
    if (!v.dateOfBirth || v.dateOfBirth >= todayIso() || v.dateOfBirth < '1900-01-01') {
      ctx.addIssue({ code: 'custom', path: ['dateOfBirth'], message: 'Enter a valid date of birth.' });
    }
    if (!isIndianMobile(v.phone)) {
      ctx.addIssue({
        code: 'custom',
        path: ['phone'],
        message: 'A 10-digit mobile number is needed to recall the donor.',
      });
    }
    if (v.email && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(v.email)) {
      ctx.addIssue({ code: 'custom', path: ['email'], message: 'Enter a valid email address.' });
    }
    if (v.pinCode && !/^[1-9]\d{5}$/.test(v.pinCode)) {
      ctx.addIssue({ code: 'custom', path: ['pinCode'], message: 'Enter a 6-digit PIN code.' });
    }
  });

type DonorValues = z.infer<typeof donorSchema>;

/** Registers a donor or edits their details. The self-reported group is replaced by the lab group after grouping. */
export function DonorDrawer({
  donor,
  onClose,
  onSaved,
}: {
  donor: DonorDetail | null;
  onClose: () => void;
  onSaved?: (id: string) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<DonorValues>({
    resolver: zodResolver(donorSchema),
    defaultValues: {
      firstName: donor?.firstName ?? '',
      lastName: donor?.lastName ?? '',
      gender: donor?.gender === 'Female' || donor?.gender === 'Other' ? donor.gender : 'Male',
      dateOfBirth: donor?.dateOfBirth ?? null,
      bloodGroup: donor && donor.bloodGroup !== 'Unknown' ? donor.bloodGroup : null,
      phone: donor?.phone ?? '',
      email: donor?.email ?? '',
      line1: donor?.address.line1 ?? '',
      city: donor?.address.city ?? '',
      district: donor?.address.district ?? '',
      state: donor?.address.state ?? null,
      pinCode: donor?.address.pinCode ?? '',
      occupation: donor?.occupation ?? '',
      consentToNotify: donor?.consentToNotify ?? true,
      notes: donor?.notes ?? '',
      isActive: donor?.isActive ?? true,
    },
  });

  const save = useMutation({
    mutationFn: (v: DonorValues) =>
      bloodApi.saveDonor(donor?.id ?? null, {
        firstName: v.firstName,
        lastName: v.lastName || null,
        gender: v.gender,
        dateOfBirth: v.dateOfBirth ?? '',
        bloodGroup: (v.bloodGroup as BloodGroup | null) ?? null,
        phone: normalizePhone(v.phone) ?? v.phone,
        email: v.email || null,
        address: {
          line1: v.line1 || null,
          city: v.city || null,
          district: v.district || null,
          state: v.state,
          pinCode: v.pinCode || null,
          country: 'India',
        },
        occupation: v.occupation || null,
        consentToNotify: v.consentToNotify,
        notes: v.notes || null,
        isActive: v.isActive,
        version: donor?.version ?? null,
      }),
    onSuccess: (id) => {
      notify.success(donor ? 'Donor details saved' : 'Donor registered');
      void queryClient.invalidateQueries({ queryKey: bloodKeys.all });
      onSaved?.(id);
      onClose();
    },
    onError: (error) => {
      applyFormErrors(error, form.setError);
    },
  });

  return (
    <OverlayDrawer open position="end" size="medium" onOpenChange={(_, d) => !d.open && onClose()}>
      <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate style={{ display: 'contents' }}>
        <DrawerHeader>
          <DrawerHeaderTitle
            action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}
          >
            {donor ? `${donor.fullName} · ${donor.donorNumber}` : 'Register donor'}
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          <div className={styles.form}>
            <FormGrid columns={2}>
              <TextField
                control={form.control}
                name="firstName"
                label="First name"
                required
                autoFocus
                maxLength={100}
              />
              <TextField control={form.control} name="lastName" label="Last name" maxLength={100} />
              <ChoiceField
                control={form.control}
                name="gender"
                label="Gender"
                required
                options={[
                  { value: 'Male', label: 'Male' },
                  { value: 'Female', label: 'Female' },
                  { value: 'Other', label: 'Other' },
                ]}
              />
              <DateField control={form.control} name="dateOfBirth" label="Date of birth" required max={todayIso()} />
              <TextField
                control={form.control}
                name="phone"
                label="Mobile number"
                required
                type="tel"
                hint="Used to call the donor back (for example after a reactive test)."
              />
              <SelectField
                control={form.control}
                name="bloodGroup"
                label="Blood group (as told by the donor)"
                placeholder="Not known"
                options={KNOWN_GROUPS.map((g) => ({ value: g, label: groupLabel(g) }))}
                hint={
                  donor?.bloodGroupConfirmed ? 'Confirmed by the laboratory.' : 'Confirmed later by the laboratory.'
                }
                disabled={donor?.bloodGroupConfirmed}
              />
              <TextField control={form.control} name="email" label="Email" type="email" />
              <TextField control={form.control} name="occupation" label="Occupation" maxLength={100} />
              <FullRow>
                <TextField control={form.control} name="line1" label="Address" maxLength={200} />
              </FullRow>
              <TextField control={form.control} name="city" label="City / town" maxLength={100} />
              <TextField control={form.control} name="district" label="District" maxLength={100} />
              <SelectField
                control={form.control}
                name="state"
                label="State"
                placeholder="—"
                options={INDIAN_STATES.map((s) => ({ value: s, label: s }))}
              />
              <TextField control={form.control} name="pinCode" label="PIN code" maxLength={6} />
            </FormGrid>
            <SwitchField
              control={form.control}
              name="consentToNotify"
              label="Donor agrees to be contacted for future donation drives"
            />
            <TextAreaField control={form.control} name="notes" label="Notes" rows={2} maxLength={1000} />
            {donor && <SwitchField control={form.control} name="isActive" label="Active donor" />}
            <ActionError error={save.error} />
          </div>
        </DrawerBody>
        <DrawerFooter>
          <Button
            type="submit"
            appearance="primary"
            disabled={save.isPending}
            icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            {donor ? 'Save' : 'Register donor'}
          </Button>
          <Button onClick={onClose}>Cancel</Button>
        </DrawerFooter>
      </form>
    </OverlayDrawer>
  );
}

// ---------------------------------------------------------------- screening

const screeningSchema = z.object({
  haemoglobinGdl: z.number().nullable(),
  weightKg: z.number().nullable(),
  systolic: z.number().nullable(),
  diastolic: z.number().nullable(),
  pulse: z.number().nullable(),
  temperatureC: z.number().nullable(),
});

type ScreeningValues = z.infer<typeof screeningSchema>;

function Check({ ok, rule }: { ok: boolean | null; rule: string }) {
  const styles = useStyles();
  if (ok === null) {
    return <Caption1 className={styles.muted}>{rule}</Caption1>;
  }
  return ok ? (
    <span className={mergeClasses(styles.check, styles.pass)}>
      <CheckmarkCircleRegular aria-hidden /> Pass · {rule}
    </span>
  ) : (
    <span className={mergeClasses(styles.check, styles.fail)}>
      <DismissCircleRegular aria-hidden /> Fail · {rule}
    </span>
  );
}

/**
 * Donor screening (NBTC 2017): haemoglobin, weight, BP, pulse and temperature, and the questionnaire. Every value
 * shows pass or fail as it is typed and the likely outcome with its reasons; the server records the decision.
 */
export function ScreeningDialog({
  donor,
  onClose,
  onCollect,
}: {
  donor: DonorDetail;
  onClose: () => void;
  onCollect?: (screening: DonorScreeningDto) => void;
}) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const questions = useQuery(bloodQueries.donorQuestions());
  const [answers, setAnswers] = useState<Record<string, boolean>>({});
  const [result, setResult] = useState<DonorScreeningDto | null>(null);
  const form = useForm<ScreeningValues>({
    resolver: zodResolver(screeningSchema),
    defaultValues: {
      haemoglobinGdl: null,
      weightKg: null,
      systolic: null,
      diastolic: null,
      pulse: null,
      temperatureC: null,
    },
  });
  const values = useWatch({ control: form.control });
  const vitals: PartialVitals = {
    haemoglobinGdl: values.haemoglobinGdl ?? null,
    weightKg: values.weightKg ?? null,
    systolic: values.systolic ?? null,
    diastolic: values.diastolic ?? null,
    pulse: values.pulse ?? null,
    temperatureC: values.temperatureC ?? null,
  };
  const checks = vitalChecks(vitals);
  const applicable = questionsFor(questions.data ?? [], donor.gender);
  const answerList: ScreeningAnswer[] = Object.entries(answers).map(([key, yes]) => ({ key, yes }));
  const missing = missingAnswers(questions.data ?? [], donor.gender, answerList);
  const complete = checks.every((c) => c.ok !== null) && missing.length === 0;
  const preview = complete
    ? evaluateDonor({
        gender: donor.gender,
        dateOfBirth: donor.dateOfBirth,
        isFirstDonation: donor.donationCount === 0,
        lastDonationDate: donor.lastDonationAt ? istDate(donor.lastDonationAt) : null,
        vitals: {
          haemoglobinGdl: vitals.haemoglobinGdl ?? 0,
          weightKg: vitals.weightKg ?? 0,
          systolic: vitals.systolic ?? 0,
          diastolic: vitals.diastolic ?? 0,
          pulse: vitals.pulse ?? 0,
          temperatureC: vitals.temperatureC ?? 0,
        },
        answers: answerList,
        questions: questions.data ?? [],
        today: todayIso(),
      })
    : null;

  const screen = useMutation({
    mutationFn: (v: ScreeningValues) =>
      bloodApi.screenDonor(donor.id, {
        haemoglobinGdl: v.haemoglobinGdl ?? 0,
        weightKg: v.weightKg ?? 0,
        systolic: v.systolic ?? 0,
        diastolic: v.diastolic ?? 0,
        pulse: v.pulse ?? 0,
        temperatureC: v.temperatureC ?? 0,
        answers: answerList,
      }),
    onSuccess: (screening) => {
      setResult(screening);
      void queryClient.invalidateQueries({ queryKey: bloodKeys.donor(donor.id) });
      void queryClient.invalidateQueries({ queryKey: ['blood-bank', 'donors'] });
    },
    onError: (error) => {
      applyFormErrors(error, form.setError);
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !screen.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 1000, width: '95vw' }}>
        {result ? (
          <DialogBody>
            <DialogTitle>Screening result · {donor.fullName}</DialogTitle>
            <DialogContent className={styles.body}>
              <div>
                <OutcomeBadge outcome={result.outcome} />
              </div>
              {result.outcome === 'Eligible' ? (
                <MessageBar intent="success">
                  <MessageBarBody>
                    <MessageBarTitle>Eligible to donate</MessageBarTitle>
                    Collect up to {result.maxBagVolumeMl ?? 350} ml within 12 hours of this screening.
                  </MessageBarBody>
                </MessageBar>
              ) : (
                <MessageBar intent={result.outcome === 'PermanentlyDeferred' ? 'error' : 'warning'} layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>
                      {result.outcome === 'PermanentlyDeferred'
                        ? 'Permanently deferred'
                        : `Deferred until ${formatDateOnly(result.deferredUntil)}`}
                    </MessageBarTitle>
                    <ul className={styles.reasons}>
                      {result.reasons.map((reason) => (
                        <li key={reason}>{reason}</li>
                      ))}
                    </ul>
                    Explain the reason to the donor kindly. A reactive-history deferral needs counselling and referral.
                  </MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              {/* Takes focus from Save screening, which the result replaces. */}
              <Button onClick={onClose} autoFocus>
                Close
              </Button>
              {result.outcome === 'Eligible' && onCollect && (
                <Button appearance="primary" icon={<DropRegular />} onClick={() => onCollect(result)}>
                  Collect donation now
                </Button>
              )}
            </DialogActions>
          </DialogBody>
        ) : (
          <form onSubmit={form.handleSubmit((v) => !screen.isPending && screen.mutate(v))} noValidate>
            <DialogBody>
              <DialogTitle>
                Screen donor · {donor.fullName}
                <Caption1 block className={styles.muted}>
                  {donor.donorNumber} · {donor.age} · {donor.gender} ·{' '}
                  {donor.donationCount === 0 ? 'first donation' : `${donor.donationCount} earlier donations`}
                  {donor.lastDonationAt ? ` · last donated ${formatDateOnly(istDate(donor.lastDonationAt))}` : ''}
                </Caption1>
              </DialogTitle>
              <DialogContent>
                {questions.isLoading ? (
                  <LoadingState label="Loading the questionnaire…" />
                ) : (
                  <div className={styles.layout}>
                    <div className={styles.body}>
                      <Text weight="semibold">Measurements</Text>
                      <div className={styles.vitals}>
                        {checks.map((check) => (
                          <div key={check.key}>
                            <NumberField
                              control={form.control}
                              name={check.key}
                              label={
                                check.key === 'haemoglobinGdl'
                                  ? 'Haemoglobin (g/dL)'
                                  : check.key === 'weightKg'
                                    ? 'Weight (kg)'
                                    : check.key === 'temperatureC'
                                      ? 'Temperature (°C)'
                                      : check.key === 'pulse'
                                        ? 'Pulse (/min)'
                                        : `${check.label} (mmHg)`
                              }
                              required
                              step={
                                check.key === 'haemoglobinGdl' ||
                                check.key === 'weightKg' ||
                                check.key === 'temperatureC'
                                  ? 0.1
                                  : 1
                              }
                            />
                            <Check ok={check.ok} rule={check.rule} />
                          </div>
                        ))}
                      </div>
                      <Text weight="semibold">Questionnaire (ask each question)</Text>
                      <div className={styles.questions}>
                        {applicable.map((q) => (
                          <div
                            key={q.key}
                            className={mergeClasses(styles.question, answers[q.key] === true && styles.questionYes)}
                          >
                            <span>
                              <Text>{q.text}</Text>
                              <Caption1 block className={styles.muted}>
                                Yes means{' '}
                                {q.permanentDeferral
                                  ? 'permanent deferral'
                                  : `deferral for ${q.deferralDays} day${q.deferralDays === 1 ? '' : 's'}`}
                                .
                              </Caption1>
                            </span>
                            <RadioGroup
                              layout="horizontal"
                              aria-label={q.text}
                              value={answers[q.key] === undefined ? '' : answers[q.key] ? 'yes' : 'no'}
                              onChange={(_, d) => setAnswers((current) => ({ ...current, [q.key]: d.value === 'yes' }))}
                            >
                              <Radio value="no" label="No" />
                              <Radio value="yes" label="Yes" />
                            </RadioGroup>
                          </div>
                        ))}
                      </div>
                    </div>
                    <aside className={styles.preview} aria-live="polite">
                      <Text weight="semibold">Likely outcome</Text>
                      {!preview ? (
                        <Caption1 className={styles.muted}>
                          {missing.length > 0
                            ? `${missing.length} question${missing.length === 1 ? '' : 's'} still to answer.`
                            : 'Enter every measurement.'}
                        </Caption1>
                      ) : preview.outcome === 'Eligible' ? (
                        <MessageBar intent="success">
                          <MessageBarBody>
                            <MessageBarTitle>Eligible</MessageBarTitle>
                            Up to {preview.maxBagVolumeMl} ml.
                          </MessageBarBody>
                        </MessageBar>
                      ) : (
                        <MessageBar
                          intent={preview.outcome === 'PermanentlyDeferred' ? 'error' : 'warning'}
                          layout="multiline"
                        >
                          <MessageBarBody>
                            <MessageBarTitle>
                              {preview.outcome === 'PermanentlyDeferred'
                                ? 'Permanent deferral'
                                : `Deferral until ${formatDateOnly(preview.deferredUntil)}`}
                            </MessageBarTitle>
                            <ul className={styles.reasons}>
                              {preview.reasons.map((reason) => (
                                <li key={reason}>{reason}</li>
                              ))}
                            </ul>
                          </MessageBarBody>
                        </MessageBar>
                      )}
                      <Caption1 className={styles.muted}>
                        A deferral is recorded on the donor when you save. The server makes the final decision.
                      </Caption1>
                    </aside>
                  </div>
                )}
                <ActionError error={screen.error} />
              </DialogContent>
              <DialogActions>
                <Button onClick={onClose} disabled={screen.isPending}>
                  Cancel
                </Button>
                <Button
                  type="submit"
                  appearance="primary"
                  disabled={!complete || screen.isPending}
                  // Keeps focus while busy (a disabled button drops it); Enter can still submit, which is ignored.
                  disabledFocusable={screen.isPending}
                  icon={screen.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
                >
                  Save screening
                </Button>
              </DialogActions>
            </DialogBody>
          </form>
        )}
      </DialogSurface>
    </Dialog>
  );
}

// ---------------------------------------------------------------- collection

const collectSchema = z
  .object({
    bagNumber: z.string().trim().max(24),
    bagType: z.enum(BAG_TYPES),
    volumeMl: z.enum(['350', '450']),
    type: z.enum(DONATION_TYPES),
    patient: z.object({ id: z.string(), uhid: z.string(), fullName: z.string() }).nullable(),
    collectedAt: z.string().nullable(),
    donorReaction: z.string().trim().max(500),
    notes: z.string().trim().max(1000),
  })
  .superRefine((v, ctx) => {
    if (v.bagNumber && !/^[A-Za-z0-9-]+$/.test(v.bagNumber)) {
      ctx.addIssue({
        code: 'custom',
        path: ['bagNumber'],
        message: 'Scan or type the bag barcode (letters, digits and hyphens).',
      });
    }
    if (v.type === 'Replacement' && !v.patient) {
      ctx.addIssue({
        code: 'custom',
        path: ['patient'],
        message: 'Choose the patient this replacement donation is for.',
      });
    }
    const at = fromIstInput(v.collectedAt);
    if (v.collectedAt && !at) {
      ctx.addIssue({ code: 'custom', path: ['collectedAt'], message: 'Enter a valid date and time.' });
    }
  });

type CollectValues = z.infer<typeof collectSchema>;

/** Records the collection against an eligible screening (valid 12 hours); 450 ml only for donors of 55 kg or more. */
export function CollectDialog({
  donor,
  screening,
  onClose,
}: {
  donor: DonorDetail;
  screening: DonorScreeningDto;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const now = useNow(60_000);
  const maxVolume = screening.maxBagVolumeMl ?? 350;
  const validUntil = Date.parse(screening.screenedAt) + 12 * 3_600_000;
  const stale = now > validUntil;
  const form = useForm<CollectValues>({
    resolver: zodResolver(collectSchema),
    defaultValues: {
      bagNumber: '',
      bagType: 'Triple',
      volumeMl: maxVolume >= 450 ? '450' : '350',
      type: 'Voluntary',
      patient: null,
      collectedAt: null,
      donorReaction: '',
      notes: '',
    },
  });
  const [type] = useWatch({ control: form.control, name: ['type'] });

  const collect = useMutation({
    mutationFn: (v: CollectValues) =>
      bloodApi.collect(donor.id, {
        screeningId: screening.id,
        bagNumber: v.bagNumber ? v.bagNumber.toUpperCase() : null,
        bagType: v.bagType,
        volumeMl: Number(v.volumeMl),
        type: v.type,
        replacementForPatientId: v.type === 'Replacement' ? (v.patient?.id ?? null) : null,
        collectedAt: fromIstInput(v.collectedAt),
        donorReaction: v.donorReaction || null,
        notes: v.notes || null,
      }),
    onSuccess: (created) => {
      notify.success(
        'Donation recorded',
        'Separate the components and send the samples for grouping and TTI screening.',
      );
      void queryClient.invalidateQueries({ queryKey: bloodKeys.all });
      onClose();
      void navigate({ to: '/blood-bank/donations/$id', params: { id: created.id } });
    },
    onError: (error) => {
      applyFormErrors(error, form.setError);
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !collect.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 720 }}>
        <form onSubmit={form.handleSubmit((v) => !stale && !collect.isPending && collect.mutate(v))} noValidate>
          <DialogBody>
            <DialogTitle>
              Collect donation · {donor.fullName}
              <Caption1 block className={styles.muted}>
                Screened {formatDateTime(screening.screenedAt)} · valid until{' '}
                {formatTime(new Date(validUntil).toISOString())} · up to {maxVolume} ml
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.body}>
              {stale && (
                <MessageBar intent="error">
                  <MessageBarBody>
                    The screening is older than 12 hours. Screen the donor again before collecting.
                  </MessageBarBody>
                </MessageBar>
              )}
              <FormGrid columns={2}>
                <TextField
                  control={form.control}
                  name="bagNumber"
                  label="Bag number"
                  autoFocus
                  maxLength={24}
                  hint="Scan the pre-printed bag barcode, or leave blank to generate a number."
                />
                <SelectField
                  control={form.control}
                  name="bagType"
                  label="Bag"
                  options={BAG_TYPES.map((b) => ({ value: b, label: `${bagTypeLabel[b]} bag` }))}
                />
                <Controller
                  control={form.control}
                  name="volumeMl"
                  render={({ field }) => (
                    <Field label="Volume" hint={maxVolume < 450 ? 'Donor weighs under 55 kg: 350 ml only.' : undefined}>
                      <RadioGroup layout="horizontal" value={field.value} onChange={(_, d) => field.onChange(d.value)}>
                        <Radio value="350" label="350 ml" />
                        <Radio value="450" label="450 ml" disabled={maxVolume < 450} />
                      </RadioGroup>
                    </Field>
                  )}
                />
                <ChoiceField
                  control={form.control}
                  name="type"
                  label="Donation"
                  options={DONATION_TYPES.map((t) => ({ value: t, label: donationTypeLabel[t] }))}
                />
                {type === 'Replacement' && (
                  <FullRow>
                    <Controller
                      control={form.control}
                      name="patient"
                      render={({ field, fieldState }) => (
                        <PatientPicker
                          value={field.value}
                          onChange={field.onChange}
                          label="Replacement for patient"
                          required
                          validationMessage={fieldState.error?.message}
                        />
                      )}
                    />
                  </FullRow>
                )}
                <DateTimeField
                  control={form.control}
                  name="collectedAt"
                  label="Collected at"
                  hint="Leave blank for now."
                />
                <TextField
                  control={form.control}
                  name="donorReaction"
                  label="Donor reaction"
                  maxLength={500}
                  placeholder="e.g. mild vasovagal, recovered"
                />
                <FullRow>
                  <TextAreaField control={form.control} name="notes" label="Notes" rows={2} maxLength={1000} />
                </FullRow>
              </FormGrid>
              <ActionError error={collect.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={collect.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={stale || collect.isPending}
                // Keeps focus while recording, and when the screening goes out of date: a disabled button drops it.
                // Enter can still submit, which is ignored.
                disabledFocusable={stale || collect.isPending}
                icon={collect.isPending ? <Spinner size="tiny" aria-hidden /> : <DropRegular />}
              >
                Record donation
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

// ---------------------------------------------------------------- deferral

const deferSchema = z
  .object({
    permanent: z.boolean(),
    until: z.string().nullable(),
    reason: z.string().trim().min(3, 'Give the reason.').max(500),
  })
  .superRefine((v, ctx) => {
    if (!v.permanent && (!v.until || v.until <= todayIso())) {
      ctx.addIssue({
        code: 'custom',
        path: ['until'],
        message: 'Give a future date until which the donor is deferred.',
      });
    }
  });

type DeferValues = z.infer<typeof deferSchema>;

/** Defers a donor outside screening (for example after a reaction or a medical report). */
export function DeferDialog({ donor, onClose }: { donor: DonorDetail; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<DeferValues>({
    resolver: zodResolver(deferSchema),
    defaultValues: { permanent: false, until: null, reason: '' },
  });
  const [permanent] = useWatch({ control: form.control, name: ['permanent'] });
  const defer = useMutation({
    mutationFn: (v: DeferValues) => bloodApi.deferDonor(donor.id, v.permanent ? null : v.until, v.permanent, v.reason),
    onSuccess: () => {
      notify.success('Donor deferred');
      void queryClient.invalidateQueries({ queryKey: bloodKeys.all });
      onClose();
    },
    onError: (error) => {
      applyFormErrors(error, form.setError);
    },
  });
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !defer.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <form onSubmit={form.handleSubmit((v) => !defer.isPending && defer.mutate(v))} noValidate>
          <DialogBody>
            <DialogTitle>Defer {donor.fullName}</DialogTitle>
            <DialogContent className={styles.body}>
              <SwitchField control={form.control} name="permanent" label="Permanent deferral" />
              {!permanent && (
                <DateField control={form.control} name="until" label="Deferred until" required min={todayIso(1)} />
              )}
              <TextAreaField control={form.control} name="reason" label="Reason" required rows={3} maxLength={500} />
              <ActionError error={defer.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={defer.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={defer.isPending}
                // Keeps focus while busy (a disabled button drops it); Enter can still submit, which is ignored.
                disabledFocusable={defer.isPending}
                icon={defer.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Defer donor
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
