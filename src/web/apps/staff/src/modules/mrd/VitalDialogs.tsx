import {
  Button,
  Caption1,
  Checkbox,
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
  Radio,
  RadioGroup,
  Select,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { PrintRegular } from '@fluentui/react-icons';
import { formatDateTime, todayIso, useSession } from '@hc360/core';
import { ErrorState, LoadingState, useNotify, useUnsavedChanges, useValuesChanged } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { useServerDocuments } from '@/lib/documents';
import { usePermission } from '@/lib/permissions';
import { errorMessage } from '@/modules/lab/common/errors';
import { toIstInput, fromIstInput, nowIstInput } from '@/modules/hr/shared/dates';
import { patientQueries } from '@/modules/patients/api';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';
import {
  BIRTH_ATTENDANTS,
  BIRTH_SEXES,
  birthAttendantLabel,
  birthOutcomeLabel,
  birthSexLabel,
  causeLineLabel,
  DEATH_MANNERS,
  deathMannerLabel,
  DELIVERY_METHODS,
  deliveryMethodLabel,
  PREGNANCIES_AT_DEATH,
  pregnancyLabel,
  vitalApi,
  vitalKeys,
  vitalQueries,
  type BirthAttendant,
  type BirthOutcome,
  type BirthRecordDto,
  type BirthSex,
  type CauseOfDeath,
  type DeathManner,
  type DeathRecordDto,
  type DeliveryMethod,
  type PregnancyAtDeath,
  type VitalKind,
} from './vital';
import {
  ageOn,
  birthProblems,
  causesToSend,
  deathProblems,
  MAX_CAUSES,
  MAX_GESTATION_WEEKS,
  MAX_WEIGHT_GRAMS,
  MIN_GESTATION_WEEKS,
  MIN_WEIGHT_GRAMS,
  optionalWhole,
  REPORT_WITHIN_DAYS,
  reportProblems,
  STILL_BIRTH_WEEKS,
  womanOfReproductiveAge,
} from './vitalRules';

const useStyles = makeStyles({
  content: { display: 'flex', flexDirection: 'column', gap: '12px' },
  row: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', gap: '12px' },
  cause: { display: 'grid', gridTemplateColumns: '150px 2fr 1fr', gap: '8px', alignItems: 'center' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** Print of a birth's Form 1 (Form 3) or a death's MCCD, as the server makes it. */
export function PrintVitalButton({ path, label }: { path: string; label: string }) {
  const { print } = useServerDocuments();
  return (
    <Button
      icon={print.isPending ? <Spinner size="tiny" aria-hidden /> : <PrintRegular />}
      disabledFocusable={print.isPending}
      onClick={() => print.mutate(path)}
    >
      {label}
    </Button>
  );
}

const invalidateRegister = (queryClient: ReturnType<typeof useQueryClient>, kind: VitalKind) =>
  void queryClient.invalidateQueries({ queryKey: vitalKeys.registers(kind) });

// ------------------------------------------------------------------------------------------------ birth

/**
 * A birth in the hospital (Form 1, or Form 3 for a still birth), recorded against the mother picked; or the
 * correction of one until it is reported to the Registrar. The server checks the rules again.
 */
export function BirthDialog({ existing, onClose }: { existing: BirthRecordDto | null; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [mother, setMother] = useState<PickedPatient | null>(
    existing ? { id: existing.motherPatientId, uhid: existing.motherUhid, fullName: existing.motherName } : null,
  );
  const [baby, setBaby] = useState<PickedPatient | null>(
    existing?.babyPatientId
      ? { id: existing.babyPatientId, uhid: existing.babyUhid ?? '', fullName: existing.childName ?? 'Baby' }
      : null,
  );
  const [bornAt, setBornAt] = useState(existing ? toIstInput(existing.bornAt) : '');
  const [outcome, setOutcome] = useState<BirthOutcome>(existing?.outcome ?? 'LiveBirth');
  const [sex, setSex] = useState<BirthSex | ''>(existing?.sex ?? '');
  const [weight, setWeight] = useState(existing?.weightGrams ? String(existing.weightGrams) : '');
  const [weeks, setWeeks] = useState(existing?.weeksOfGestation ? String(existing.weeksOfGestation) : '');
  const [method, setMethod] = useState<DeliveryMethod | ''>(existing?.method ?? '');
  const [attendant, setAttendant] = useState<BirthAttendant | ''>(existing?.attendant ?? '');
  const [childName, setChildName] = useState(existing?.childName ?? '');
  const [fatherName, setFatherName] = useState(existing?.fatherName ?? '');
  const [motherAge, setMotherAge] = useState(existing ? String(existing.motherAgeYears) : '');
  const [liveChildren, setLiveChildren] = useState(
    existing?.liveChildren !== null && existing?.liveChildren !== undefined ? String(existing.liveChildren) : '',
  );
  const [informant, setInformant] = useState(existing?.informantName ?? '');
  const [cause, setCause] = useState(existing?.stillBirthCause ?? '');

  const still = outcome === 'StillBirth';
  const problems = birthProblems(
    {
      motherPicked: Boolean(mother),
      bornAt,
      outcome,
      sex,
      weight,
      weeks,
      method,
      attendant,
      motherAge,
      liveChildren,
      informant,
      stillBirthCause: cause,
      babyIsMother: !still && Boolean(baby && mother && baby.id === mother.id),
    },
    nowIstInput(),
  );
  const valid = Object.values(problems).every((p) => p === null);
  const [tried, setTried] = useState(false);
  const show = (problem: string | null) => (tried ? (problem ?? undefined) : undefined);

  const save = useMutation({
    mutationFn: () => {
      const request = {
        motherPatientId: mother!.id,
        babyPatientId: still ? null : (baby?.id ?? null),
        admissionId: existing?.admissionId ?? null,
        bornAt: fromIstInput(bornAt)!,
        outcome,
        sex: sex as BirthSex,
        weightGrams: optionalWhole(weight),
        weeksOfGestation: optionalWhole(weeks),
        method: method as DeliveryMethod,
        attendant: attendant as BirthAttendant,
        childName: still ? null : childName.trim() || null,
        fatherName: fatherName.trim() || null,
        motherAgeYears: Number(motherAge),
        liveChildren: optionalWhole(liveChildren),
        informantName: informant.trim(),
        stillBirthCause: still ? cause.trim() : null,
      };
      return existing
        ? vitalApi.correctBirth(existing.id, { ...request, version: existing.version })
        : vitalApi.recordBirth({ ...request, version: null });
    },
    onSuccess: (saved) => {
      queryClient.setQueryData(vitalKeys.birth(saved.id), saved);
      invalidateRegister(queryClient, 'birth');
      notify.success(
        existing ? 'Birth corrected' : 'Birth recorded',
        `${saved.serialNumber}: report it to the Registrar within ${REPORT_WITHIN_DAYS} days.`,
      );
      onClose();
      if (!existing) {
        void navigate({ to: '/mrd/births/$birthId', params: { birthId: saved.id } });
      }
    },
  });
  const guard = useUnsavedChanges({
    dirty: useValuesChanged({
      mother,
      baby,
      bornAt,
      outcome,
      sex,
      weight,
      weeks,
      method,
      attendant,
      childName,
      fatherName,
      motherAge,
      liveChildren,
      informant,
      cause,
    }),
    busy: save.isPending,
    onClose,
  });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 760 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            setTried(true);
            if (valid && !save.isPending) {
              save.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>
              {existing ? `Correct birth ${existing.serialNumber}` : 'Record a birth'}
              <Caption1 block className={styles.muted}>
                {still ? 'Still birth report (Form 3)' : 'Birth report (Form 1)'}
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.content}>
              {save.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(save.error, 'The birth could not be saved.')}</MessageBarBody>
                </MessageBar>
              )}
              {existing ? (
                <Text>
                  Mother: <Text weight="semibold">{existing.motherName}</Text> · {existing.motherUhid}
                </Text>
              ) : (
                <PatientPicker
                  value={mother}
                  onChange={setMother}
                  label="Mother"
                  required
                  validationMessage={show(problems.mother)}
                  hint="The birth is recorded against the mother's record."
                  autoFocus
                />
              )}
              <div className={styles.row}>
                <Field
                  label="Date and time of birth"
                  required
                  hint="Hospital time"
                  validationMessage={show(problems.bornAt)}
                >
                  <Input
                    type="datetime-local"
                    value={bornAt}
                    max={nowIstInput()}
                    onChange={(_, d) => setBornAt(d.value)}
                  />
                </Field>
                <Field label="Outcome" required>
                  <RadioGroup
                    layout="horizontal"
                    value={outcome}
                    onChange={(_, d) => setOutcome(d.value as BirthOutcome)}
                  >
                    <Radio value="LiveBirth" label={birthOutcomeLabel.LiveBirth} />
                    <Radio value="StillBirth" label={birthOutcomeLabel.StillBirth} />
                  </RadioGroup>
                </Field>
              </div>
              <div className={styles.row}>
                <Field label="Sex" required validationMessage={show(problems.sex)}>
                  <Select value={sex} onChange={(_, d) => setSex(d.value as BirthSex | '')}>
                    <option value="">Choose</option>
                    {BIRTH_SEXES.map((s) => (
                      <option key={s} value={s}>
                        {birthSexLabel[s]}
                      </option>
                    ))}
                  </Select>
                </Field>
                <Field label="Birth weight (g)" validationMessage={show(problems.weight)}>
                  <Input
                    type="number"
                    min={MIN_WEIGHT_GRAMS}
                    max={MAX_WEIGHT_GRAMS}
                    value={weight}
                    onChange={(_, d) => setWeight(d.value)}
                  />
                </Field>
                <Field label="Length of pregnancy (weeks)" required={still} validationMessage={show(problems.weeks)}>
                  <Input
                    type="number"
                    min={MIN_GESTATION_WEEKS}
                    max={MAX_GESTATION_WEEKS}
                    value={weeks}
                    onChange={(_, d) => setWeeks(d.value)}
                  />
                </Field>
              </div>
              {still && (
                <Caption1 className={styles.muted}>
                  A still birth is a foetus of at least {STILL_BIRTH_WEEKS} weeks born dead (RBD Act s.2(1)(i)). An
                  earlier loss is not reported as a birth.
                </Caption1>
              )}
              <div className={styles.row}>
                <Field label="Method of delivery" required validationMessage={show(problems.method)}>
                  <Select value={method} onChange={(_, d) => setMethod(d.value as DeliveryMethod | '')}>
                    <option value="">Choose</option>
                    {DELIVERY_METHODS.map((m) => (
                      <option key={m} value={m}>
                        {deliveryMethodLabel[m]}
                      </option>
                    ))}
                  </Select>
                </Field>
                <Field label="Attended by" required validationMessage={show(problems.attendant)}>
                  <Select value={attendant} onChange={(_, d) => setAttendant(d.value as BirthAttendant | '')}>
                    <option value="">Choose</option>
                    {BIRTH_ATTENDANTS.map((a) => (
                      <option key={a} value={a}>
                        {birthAttendantLabel[a]}
                      </option>
                    ))}
                  </Select>
                </Field>
              </div>
              {still ? (
                <Field label="Cause of the foetal death" required validationMessage={show(problems.stillBirthCause)}>
                  <Textarea value={cause} maxLength={500} rows={2} onChange={(_, d) => setCause(d.value)} />
                </Field>
              ) : (
                <div className={styles.row}>
                  <Field label="Child's name" hint="If the child is named; it may be added at registration.">
                    <Input value={childName} maxLength={200} onChange={(_, d) => setChildName(d.value)} />
                  </Field>
                  <PatientPicker
                    value={baby}
                    onChange={setBaby}
                    label="Baby's own record"
                    validationMessage={show(problems.baby)}
                    hint="Once the baby is registered as a patient."
                  />
                </div>
              )}
              <div className={styles.row}>
                <Field label="Father's name">
                  <Input value={fatherName} maxLength={200} onChange={(_, d) => setFatherName(d.value)} />
                </Field>
                <Field label="Mother's age (years)" required validationMessage={show(problems.motherAge)}>
                  <Input type="number" min={10} max={60} value={motherAge} onChange={(_, d) => setMotherAge(d.value)} />
                </Field>
                <Field
                  label="Children born alive to her, including this one"
                  validationMessage={show(problems.liveChildren)}
                >
                  <Input
                    type="number"
                    min={0}
                    max={20}
                    value={liveChildren}
                    onChange={(_, d) => setLiveChildren(d.value)}
                  />
                </Field>
              </div>
              <Field
                label="Informant"
                required
                hint="The hospital's officer in charge, or a relative, who gives the information."
                validationMessage={show(problems.informant)}
              >
                <Input value={informant} maxLength={200} onChange={(_, d) => setInformant(d.value)} />
              </Field>
              <Caption1 className={styles.muted}>
                Report the birth to the Registrar within {REPORT_WITHIN_DAYS} days. Once reported it cannot be changed
                here; a correction then goes through the Registrar.
              </Caption1>
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={save.isPending}
                disabledFocusable={save.isPending}
                icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                {existing ? 'Save' : 'Record birth'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}

// ------------------------------------------------------------------------------------------------ death

const blankCauses = (): CauseOfDeath[] => Array.from({ length: MAX_CAUSES }, () => ({ condition: '', interval: '' }));

/**
 * A death's medical certificate of cause of death (MCCD, Form 4), certified by the signed-in doctor; or its correction
 * until it is reported. Certifying it marks the patient deceased. The server checks the rules again.
 */
export function DeathDialog({ existing, onClose }: { existing: DeathRecordDto | null; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const doctor = useSession((s) => s.user?.displayName);
  const canReadPatients = usePermission('patients.read');
  const [patient, setPatient] = useState<PickedPatient | null>(
    existing ? { id: existing.patientId, uhid: existing.uhid, fullName: existing.patientName } : null,
  );
  const [diedAt, setDiedAt] = useState(existing ? toIstInput(existing.diedAt) : '');
  const [ageYears, setAgeYears] = useState('');
  const [causes, setCauses] = useState<CauseOfDeath[]>(() =>
    existing ? blankCauses().map((blank, i) => existing.causes[i] ?? blank) : blankCauses(),
  );
  const [otherConditions, setOtherConditions] = useState(existing?.otherConditions ?? '');
  const [manner, setManner] = useState<DeathManner | ''>(existing?.manner ?? '');
  const [medicoLegal, setMedicoLegal] = useState(existing?.medicoLegal ?? false);
  const [pregnancy, setPregnancy] = useState<PregnancyAtDeath>(existing?.pregnancy ?? 'NotApplicable');
  // A correction by the doctor who certified keeps their number; another doctor certifies it anew with theirs.
  const [registration, setRegistration] = useState(
    existing && existing.certifiedBy === doctor ? existing.certifierRegistrationNumber : '',
  );

  // The patient's sex and date of birth decide whether pregnancy is asked, and whether the age is.
  const details = useQuery({ ...patientQueries.detail(patient?.id ?? ''), enabled: canReadPatients && !!patient });
  const dateOfBirth = details.data?.dateOfBirth ?? null;
  const askAge = Boolean(patient) && (details.data ? !dateOfBirth : !details.isLoading);
  const age =
    ageOn(dateOfBirth, diedAt.slice(0, 10)) ??
    optionalWhole(ageYears) ??
    (existing && !details.data ? existing.ageYears : null);
  const reproductiveAge = womanOfReproductiveAge(details.data?.gender ?? existing?.gender, age);
  const pregnancySent: PregnancyAtDeath = reproductiveAge === false ? 'NotApplicable' : pregnancy;

  const problems = deathProblems(
    {
      patientPicked: Boolean(patient),
      diedAt,
      dateOfBirth,
      ageYears,
      causes,
      manner,
      medicoLegal,
      pregnancy: pregnancySent,
      reproductiveAge,
      certifierRegistrationNumber: registration,
    },
    nowIstInput(),
  );
  const valid = Object.values(problems).every((p) => p === null);
  const [tried, setTried] = useState(false);
  const show = (problem: string | null) => (tried ? (problem ?? undefined) : undefined);

  const save = useMutation({
    mutationFn: () => {
      const request = {
        patientId: patient!.id,
        admissionId: existing?.admissionId ?? null,
        diedAt: fromIstInput(diedAt)!,
        ageYears: dateOfBirth ? null : optionalWhole(ageYears),
        causes: causesToSend(causes),
        otherConditions: otherConditions.trim() || null,
        manner: manner as DeathManner,
        medicoLegal,
        pregnancy: pregnancySent,
        certifierRegistrationNumber: registration.trim(),
      };
      return existing
        ? vitalApi.correctDeath(existing.id, { ...request, version: existing.version })
        : vitalApi.certifyDeath({ ...request, version: null });
    },
    onSuccess: (saved) => {
      queryClient.setQueryData(vitalKeys.death(saved.id), saved);
      invalidateRegister(queryClient, 'death');
      void queryClient.invalidateQueries({ queryKey: ['patients', 'detail', saved.patientId] });
      notify.success(
        existing ? 'Certificate corrected' : 'Death certified',
        `${saved.serialNumber}: report it to the Registrar within ${REPORT_WITHIN_DAYS} days.`,
      );
      onClose();
      if (!existing) {
        void navigate({ to: '/mrd/deaths/$deathId', params: { deathId: saved.id } });
      }
    },
  });
  const guard = useUnsavedChanges({
    dirty: useValuesChanged({
      patient,
      diedAt,
      ageYears,
      causes,
      otherConditions,
      manner,
      medicoLegal,
      pregnancy,
      registration,
    }),
    busy: save.isPending,
    onClose,
  });

  const setCause = (index: number, patch: Partial<CauseOfDeath>) =>
    setCauses((all) => all.map((c, i) => (i === index ? { ...c, ...patch } : c)));

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 760 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            setTried(true);
            if (valid && !save.isPending) {
              save.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>
              {existing ? `Correct certificate ${existing.serialNumber}` : 'Certify a death'}
              <Caption1 block className={styles.muted}>
                Medical certificate of cause of death (Form 4)
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.content}>
              {save.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(save.error, 'The certificate could not be saved.')}</MessageBarBody>
                </MessageBar>
              )}
              {existing ? (
                <Text>
                  <Text weight="semibold">{existing.patientName}</Text> · {existing.uhid}
                </Text>
              ) : (
                <PatientPicker
                  value={patient}
                  onChange={setPatient}
                  label="Deceased"
                  required
                  validationMessage={show(problems.patient)}
                  hint="Certifying the death marks the patient deceased."
                  autoFocus
                />
              )}
              <div className={styles.row}>
                <Field
                  label="Date and time of death"
                  required
                  hint="Hospital time"
                  validationMessage={show(problems.diedAt)}
                >
                  <Input
                    type="datetime-local"
                    value={diedAt}
                    max={nowIstInput()}
                    onChange={(_, d) => setDiedAt(d.value)}
                  />
                </Field>
                {askAge && (
                  <Field
                    label="Age (years)"
                    hint="The record has no date of birth."
                    validationMessage={show(problems.ageYears)}
                  >
                    <Input type="number" min={0} max={130} value={ageYears} onChange={(_, d) => setAgeYears(d.value)} />
                  </Field>
                )}
              </div>
              <Field
                label="Part I: the disease or condition that led directly to death, and those that gave rise to it"
                required
                hint="Line (a) is the immediate cause; the underlying cause, which started the train of events, is entered last."
                validationMessage={show(problems.causes)}
              >
                <div className={styles.content}>
                  {causes.map((c, index) => (
                    <div key={index} className={styles.cause}>
                      <Caption1>{causeLineLabel[index]}</Caption1>
                      <Input
                        aria-label={`${causeLineLabel[index]}: condition`}
                        placeholder="Condition"
                        value={c.condition}
                        maxLength={300}
                        onChange={(_, d) => setCause(index, { condition: d.value })}
                      />
                      <Input
                        aria-label={`${causeLineLabel[index]}: interval between onset and death`}
                        placeholder="Onset before death"
                        value={c.interval ?? ''}
                        maxLength={50}
                        onChange={(_, d) => setCause(index, { interval: d.value })}
                      />
                    </div>
                  ))}
                </div>
              </Field>
              <Field label="Part II: other significant conditions contributing to death">
                <Textarea
                  value={otherConditions}
                  maxLength={1000}
                  rows={2}
                  onChange={(_, d) => setOtherConditions(d.value)}
                />
              </Field>
              <div className={styles.row}>
                <Field label="Manner of death" required validationMessage={show(problems.manner)}>
                  <Select value={manner} onChange={(_, d) => setManner(d.value as DeathManner | '')}>
                    <option value="">Choose</option>
                    {DEATH_MANNERS.map((m) => (
                      <option key={m} value={m}>
                        {deathMannerLabel[m]}
                      </option>
                    ))}
                  </Select>
                </Field>
                <Field label="Medico-legal case">
                  <Checkbox
                    checked={medicoLegal}
                    onChange={(_, d) => setMedicoLegal(Boolean(d.checked))}
                    label="The police were informed (MLC)"
                  />
                </Field>
              </div>
              {reproductiveAge !== false && (
                <Field
                  label="If a woman aged 15 to 49, pregnancy"
                  required={reproductiveAge === true}
                  validationMessage={show(problems.pregnancy)}
                >
                  <Select value={pregnancy} onChange={(_, d) => setPregnancy(d.value as PregnancyAtDeath)}>
                    {PREGNANCIES_AT_DEATH.map((p) => (
                      <option key={p} value={p}>
                        {pregnancyLabel[p]}
                      </option>
                    ))}
                  </Select>
                </Field>
              )}
              <div className={styles.row}>
                <Field label="Certified by">
                  <Text>{doctor ?? 'You'}</Text>
                </Field>
                <Field
                  label="Your medical council registration no."
                  required
                  validationMessage={show(problems.certifierRegistrationNumber)}
                >
                  <Input value={registration} maxLength={64} onChange={(_, d) => setRegistration(d.value)} />
                </Field>
              </div>
              <Caption1 className={styles.muted}>
                The doctor who attended the patient in the last illness certifies the cause (RBD Act s.10(3)). Report
                the death to the Registrar within {REPORT_WITHIN_DAYS} days; once reported the certificate cannot be
                changed here.
              </Caption1>
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={save.isPending}
                disabledFocusable={save.isPending}
                icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                {existing ? 'Save' : 'Certify death'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}

// ------------------------------------------------------------------------------------------------ report

/** What the report dialog needs of a birth or a death. */
interface Reportable {
  serialNumber: string;
  name: string;
  /** The birth or the death, from whose day (hospital time) the 21 days run. */
  at: string;
  version: number;
}

const reportableBirth = (b: BirthRecordDto): Reportable => ({
  serialNumber: b.serialNumber,
  name: b.motherName,
  at: b.bornAt,
  version: b.version,
});

const reportableDeath = (d: DeathRecordDto): Reportable => ({
  serialNumber: d.serialNumber,
  name: d.patientName,
  at: d.diedAt,
  version: d.version,
});

/**
 * The Registrar's acknowledgement of a birth or death reported through CRS: the day and the registration number it
 * gave. The record is read first for its version (the register's rows do not carry it).
 */
export function ReportDialog({ kind, id, onClose }: { kind: VitalKind; id: string; onClose: () => void }) {
  const birth = useQuery({ ...vitalQueries.birth(id), enabled: kind === 'birth' });
  const death = useQuery({ ...vitalQueries.death(id), enabled: kind === 'death' });
  const record = kind === 'birth' ? birth : death;
  const reportable =
    birth.data && kind === 'birth'
      ? reportableBirth(birth.data)
      : death.data && kind === 'death'
        ? reportableDeath(death.data)
        : null;

  if (!reportable) {
    return (
      <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
        <DialogSurface style={{ maxWidth: 520 }}>
          <DialogBody>
            <DialogTitle>Report to the Registrar</DialogTitle>
            <DialogContent>
              {record.error ? (
                <ErrorState error={record.error} onRetry={() => void record.refetch()} />
              ) : (
                <LoadingState label="Opening the entry…" />
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose}>Cancel</Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    );
  }
  return <ReportForm kind={kind} id={id} record={reportable} onClose={onClose} />;
}

function ReportForm({
  kind,
  id,
  record,
  onClose,
}: {
  kind: VitalKind;
  id: string;
  record: Reportable;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const eventDate = toIstInput(record.at).slice(0, 10);
  const [reportedOn, setReportedOn] = useState(todayIso());
  const [registrationNumber, setRegistrationNumber] = useState('');
  const problems = reportProblems({ reportedOn, registrationNumber }, eventDate, todayIso());
  const valid = Object.values(problems).every((p) => p === null);
  const [tried, setTried] = useState(false);
  const show = (problem: string | null) => (tried ? (problem ?? undefined) : undefined);

  const report = useMutation({
    mutationFn: async () => {
      const request = { reportedOn, registrationNumber: registrationNumber.trim(), version: record.version };
      if (kind === 'birth') {
        const saved = await vitalApi.reportBirth(id, request);
        queryClient.setQueryData(vitalKeys.birth(id), saved);
        return saved.serialNumber;
      }
      const saved = await vitalApi.reportDeath(id, request);
      queryClient.setQueryData(vitalKeys.death(id), saved);
      return saved.serialNumber;
    },
    onSuccess: (serialNumber) => {
      invalidateRegister(queryClient, kind);
      notify.success('Reported to the Registrar', `${serialNumber} is registered as ${registrationNumber.trim()}.`);
      onClose();
    },
  });
  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ reportedOn, registrationNumber }),
    busy: report.isPending,
    onClose,
  });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            setTried(true);
            if (valid && !report.isPending) {
              report.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>
              Report {record.serialNumber} to the Registrar
              <Caption1 block className={styles.muted}>
                {record.name} · {kind === 'birth' ? 'born' : 'died'} {formatDateTime(record.at)}
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.content}>
              {report.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(report.error, 'The report could not be recorded.')}</MessageBarBody>
                </MessageBar>
              )}
              <div className={styles.row}>
                <Field label="Reported on" required validationMessage={show(problems.reportedOn)}>
                  <Input
                    type="date"
                    value={reportedOn}
                    min={eventDate}
                    max={todayIso()}
                    onChange={(_, d) => setReportedOn(d.value)}
                  />
                </Field>
                <Field
                  label="Registration no."
                  required
                  hint="As the Civil Registration System (CRS) gave it."
                  validationMessage={show(problems.registrationNumber)}
                >
                  <Input
                    value={registrationNumber}
                    maxLength={64}
                    onChange={(_, d) => setRegistrationNumber(d.value)}
                    autoFocus
                  />
                </Field>
              </div>
              <Caption1 className={styles.muted}>
                Once reported, the entry cannot be changed here; a correction goes through the Registrar (RBD Act s.15).
              </Caption1>
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={report.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={report.isPending}
                disabledFocusable={report.isPending}
                icon={report.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Record report
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
