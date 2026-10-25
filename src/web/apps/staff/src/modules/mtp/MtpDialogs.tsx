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
  Select,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular } from '@fluentui/react-icons';
import { todayIso } from '@hc360/core';
import { useNotify, useUnsavedChanges, useValuesChanged } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { errorMessage } from '@/modules/lab/common/errors';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';
import {
  MTP_CATEGORIES,
  MTP_GROUNDS,
  MTP_METHODS,
  mtpApi,
  mtpCategoryLabel,
  mtpGroundLabel,
  mtpKeys,
  mtpMethodLabel,
  type MtpCaseDto,
  type MtpGround,
  type MtpMethod,
  type MtpOpinion,
  type MtpRule3BCategory,
} from './api';
import {
  certificationProblems,
  consentByFor,
  gestationRule,
  MAX_OPINIONS,
  MAX_WEEKS_OF_GESTATION,
  ONE_OPINION_WEEKS,
  opinionsNeeded,
  TWO_OPINIONS_WEEKS,
} from './logic';

const useStyles = makeStyles({
  content: { display: 'flex', flexDirection: 'column', gap: '12px' },
  row: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', gap: '12px' },
  opinion: { display: 'grid', gridTemplateColumns: '2fr 1.4fr 1fr auto', gap: '8px', alignItems: 'end' },
  muted: { color: tokens.colorNeutralForeground3 },
  grounds: { display: 'flex', flexDirection: 'column' },
  box: {
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: '8px 12px',
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
});

const blankOpinion = (): MtpOpinion => ({ doctorName: '', registrationNumber: '', opinionOn: todayIso() });

/**
 * The woman's consent (Form C) and the doctors' opinion (Form I): opens a case for the woman picked, or corrects an
 * open case before the termination is done. The length of the pregnancy decides how many doctors, and what else, the
 * law asks for; the server checks it again.
 */
export function CaseDialog({ existing, onClose }: { existing: MtpCaseDto | null; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [patient, setPatient] = useState<PickedPatient | null>(
    existing ? { id: existing.patientId, uhid: existing.uhid, fullName: existing.patientName } : null,
  );
  const [age, setAge] = useState(existing ? String(existing.ageYears) : '');
  const [mentallyIll, setMentallyIll] = useState(existing?.mentallyIll ?? false);
  const [guardianName, setGuardianName] = useState(existing?.guardianName ?? '');
  const [guardianRelation, setGuardianRelation] = useState(existing?.guardianRelation ?? '');
  const [consentOn, setConsentOn] = useState(existing?.consentOn ?? todayIso());
  const [weeks, setWeeks] = useState(existing ? String(existing.weeksOfGestation) : '');
  const [grounds, setGrounds] = useState<MtpGround[]>(existing?.grounds ?? []);
  const [category, setCategory] = useState<MtpRule3BCategory | null>(existing?.category ?? null);
  const [board, setBoard] = useState(existing?.medicalBoardReference ?? '');
  const [opinions, setOpinions] = useState<MtpOpinion[]>(existing?.opinions ?? [blankOpinion()]);

  const ageYears = Number.parseInt(age, 10);
  const weeksValue = Number.parseInt(weeks, 10);
  const consentBy = consentByFor(ageYears, mentallyIll);
  const needed = opinionsNeeded(Number.isInteger(weeksValue) ? weeksValue : 0);
  const problems = {
    patient: patient ? null : 'Find the woman.',
    ...certificationProblems(
      {
        ageYears,
        mentallyIll,
        guardianName,
        guardianRelation,
        consentOn,
        weeksOfGestation: weeksValue,
        grounds,
        category,
        medicalBoardReference: board,
        opinions,
      },
      todayIso(),
    ),
  };
  const valid = Object.values(problems).every((p) => p === null);
  const [tried, setTried] = useState(false);
  const show = (problem: string | null) => (tried ? (problem ?? undefined) : undefined);

  const save = useMutation({
    mutationFn: () => {
      const request = {
        patientId: patient!.id,
        admissionId: existing?.admissionId ?? null,
        ageYears,
        mentallyIll,
        consentBy,
        guardianName: consentBy === 'Guardian' ? guardianName.trim() : null,
        guardianRelation: consentBy === 'Guardian' ? guardianRelation.trim() : null,
        consentOn,
        consentFileId: existing?.consentFileId ?? null,
        weeksOfGestation: weeksValue,
        grounds,
        category: weeksValue > ONE_OPINION_WEEKS ? category : null,
        medicalBoardReference: weeksValue > TWO_OPINIONS_WEEKS ? board.trim() : null,
        opinions: opinions.map((o) => ({
          doctorName: o.doctorName.trim(),
          registrationNumber: o.registrationNumber.trim(),
          opinionOn: o.opinionOn,
        })),
      };
      return existing
        ? mtpApi.correct(existing.id, { ...request, version: existing.version })
        : mtpApi.open({ ...request, version: null });
    },
    onSuccess: (saved) => {
      queryClient.setQueryData(mtpKeys.case(saved.id), saved);
      void queryClient.invalidateQueries({ queryKey: mtpKeys.cases });
      void queryClient.invalidateQueries({ queryKey: ['mtp', 'register'] });
      notify.success(
        existing ? 'Case corrected' : 'MTP case opened',
        `${saved.serialNumber}: the termination may be done and recorded.`,
      );
      onClose();
      if (!existing) {
        void navigate({ to: '/mtp/$caseId', params: { caseId: saved.id } });
      }
    },
  });
  const guard = useUnsavedChanges({
    dirty: useValuesChanged({
      patient,
      age,
      mentallyIll,
      guardianName,
      guardianRelation,
      consentOn,
      weeks,
      grounds,
      category,
      board,
      opinions,
    }),
    busy: save.isPending,
    onClose,
  });

  const setOpinion = (index: number, patch: Partial<MtpOpinion>) =>
    setOpinions((all) => all.map((o, i) => (i === index ? { ...o, ...patch } : o)));

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
              {existing ? `Correct MTP case ${existing.serialNumber}` : 'Open MTP case'}
              <Caption1 block className={styles.muted}>
                {existing
                  ? `${existing.patientName} · consent (Form C) and opinion (Form I)`
                  : 'The woman’s consent (Form C) and the doctors’ opinion (Form I)'}
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.content}>
              {save.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(save.error, 'The case could not be saved.')}</MessageBarBody>
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
                  label="Woman"
                  required
                  validationMessage={show(problems.patient)}
                  hint="Opening the case puts her record in the confidential MTP category."
                  autoFocus
                />
              )}
              <div className={styles.row}>
                <Field label="Age (years)" required validationMessage={show(problems.ageYears)}>
                  <Input type="number" min={10} max={60} value={age} onChange={(_, d) => setAge(d.value)} />
                </Field>
                <Field label="Length of pregnancy (weeks)" required validationMessage={show(problems.weeksOfGestation)}>
                  <Input
                    type="number"
                    min={1}
                    max={MAX_WEEKS_OF_GESTATION}
                    value={weeks}
                    onChange={(_, d) => setWeeks(d.value)}
                  />
                </Field>
                <Field label="Date of consent" required validationMessage={show(problems.consentOn)}>
                  <Input type="date" value={consentOn} max={todayIso()} onChange={(_, d) => setConsentOn(d.value)} />
                </Field>
              </div>
              <Caption1 className={styles.muted}>{gestationRule(weeksValue)}</Caption1>
              <Checkbox
                checked={mentallyIll}
                onChange={(_, d) => setMentallyIll(Boolean(d.checked))}
                label="She is mentally ill"
              />
              <div className={styles.box}>
                <Text weight="semibold">
                  {consentBy === 'Guardian' ? 'Consent of her guardian' : 'Her own consent'}
                </Text>
                <Caption1 className={styles.muted}>
                  {consentBy === 'Guardian'
                    ? 'A woman under 18 or mentally ill needs her guardian’s written consent (s.3(4)(a)).'
                    : 'An adult woman who is not mentally ill gives her own written consent; no one else’s is asked.'}
                </Caption1>
                {consentBy === 'Guardian' && (
                  <div className={styles.row}>
                    <Field label="Guardian's name" required validationMessage={show(problems.guardian)}>
                      <Input value={guardianName} maxLength={200} onChange={(_, d) => setGuardianName(d.value)} />
                    </Field>
                    <Field label="Relation to the woman" required>
                      <Input
                        value={guardianRelation}
                        maxLength={100}
                        onChange={(_, d) => setGuardianRelation(d.value)}
                      />
                    </Field>
                  </div>
                )}
              </div>
              <Field label="Grounds of the opinion" required validationMessage={show(problems.grounds)}>
                <div className={styles.grounds} role="group" aria-label="Grounds of the opinion">
                  {MTP_GROUNDS.map((ground) => (
                    <Checkbox
                      key={ground}
                      label={mtpGroundLabel[ground]}
                      checked={grounds.includes(ground)}
                      onChange={(_, d) =>
                        setGrounds((current) =>
                          d.checked
                            ? MTP_GROUNDS.filter((g) => g === ground || current.includes(g))
                            : current.filter((g) => g !== ground),
                        )
                      }
                    />
                  ))}
                </div>
              </Field>
              {weeksValue > ONE_OPINION_WEEKS && (
                <Field
                  label="Category (rule 3B)"
                  required={weeksValue <= TWO_OPINIONS_WEEKS}
                  validationMessage={show(problems.category)}
                >
                  <Select
                    value={category ?? ''}
                    onChange={(_, d) => setCategory((d.value || null) as MtpRule3BCategory | null)}
                  >
                    <option value="">Choose the category</option>
                    {MTP_CATEGORIES.map((c) => (
                      <option key={c} value={c}>
                        {mtpCategoryLabel[c]}
                      </option>
                    ))}
                  </Select>
                </Field>
              )}
              {weeksValue > TWO_OPINIONS_WEEKS && (
                <Field
                  label="Medical Board's opinion (reference)"
                  required
                  validationMessage={show(problems.medicalBoardReference)}
                >
                  <Input value={board} maxLength={500} onChange={(_, d) => setBoard(d.value)} />
                </Field>
              )}
              <Field
                label={needed === 1 ? 'Doctor who formed the opinion' : 'Doctors who formed the opinion'}
                required
                validationMessage={show(problems.opinions)}
              >
                <div className={styles.content}>
                  {opinions.map((opinion, index) => (
                    <div key={index} className={styles.opinion}>
                      <Field label="Doctor" size="small">
                        <Input
                          value={opinion.doctorName}
                          maxLength={200}
                          onChange={(_, d) => setOpinion(index, { doctorName: d.value })}
                        />
                      </Field>
                      <Field label="Registration no." size="small">
                        <Input
                          value={opinion.registrationNumber}
                          maxLength={64}
                          onChange={(_, d) => setOpinion(index, { registrationNumber: d.value })}
                        />
                      </Field>
                      <Field label="Opinion on" size="small">
                        <Input
                          type="date"
                          value={opinion.opinionOn}
                          max={todayIso()}
                          onChange={(_, d) => setOpinion(index, { opinionOn: d.value })}
                        />
                      </Field>
                      <Button
                        appearance="subtle"
                        icon={<DeleteRegular />}
                        aria-label={`Remove doctor ${index + 1}`}
                        disabled={opinions.length <= 1}
                        onClick={() => setOpinions((all) => all.filter((_, i) => i !== index))}
                      />
                    </div>
                  ))}
                  {opinions.length < MAX_OPINIONS && (
                    <div>
                      <Button
                        icon={<AddRegular />}
                        appearance={opinions.length < needed ? 'primary' : 'secondary'}
                        onClick={() => setOpinions((all) => [...all, blankOpinion()])}
                      >
                        Add a doctor
                      </Button>
                    </div>
                  )}
                </div>
              </Field>
              <Caption1 className={styles.muted}>
                Keep the signed consent and Form I in the case file. Under s.5A of the MTP Act her name and particulars
                are revealed to no one but a person authorised by law.
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
                {existing ? 'Save' : 'Open case'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}

/** The termination done, for the admission register (Form III). Once recorded the case cannot change. */
export function TerminationDialog({ mtp, onClose }: { mtp: MtpCaseDto; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [terminatedOn, setTerminatedOn] = useState(todayIso());
  const [method, setMethod] = useState<MtpMethod | ''>('');
  const [performedBy, setPerformedBy] = useState('');
  const [dischargedOn, setDischargedOn] = useState('');
  const [outcome, setOutcome] = useState('');
  const [remarks, setRemarks] = useState('');
  // The termination comes on or after the consent and every opinion.
  const earliest = [mtp.consentOn, ...mtp.opinions.map((o) => o.opinionOn)].sort().at(-1) ?? mtp.consentOn;
  const problems = {
    terminatedOn: !terminatedOn
      ? 'Enter the date of the termination.'
      : terminatedOn < earliest
        ? 'The termination cannot come before the consent and the doctors’ opinion.'
        : terminatedOn > todayIso()
          ? 'The termination cannot be dated after today.'
          : null,
    method: method ? null : 'Choose the method.',
    performedBy: performedBy.trim() ? null : 'Enter the doctor who performed the termination.',
    dischargedOn:
      !dischargedOn || (dischargedOn >= terminatedOn && dischargedOn <= todayIso())
        ? null
        : 'The discharge comes on or after the termination, and not after today.',
    remarks: method === 'Other' && !remarks.trim() ? 'Name the method in the remarks.' : null,
  };
  const valid = Object.values(problems).every((p) => p === null);
  const [tried, setTried] = useState(false);
  const show = (problem: string | null) => (tried ? (problem ?? undefined) : undefined);

  const record = useMutation({
    mutationFn: () =>
      mtpApi.terminate(mtp.id, {
        terminatedOn,
        method: method as MtpMethod,
        performedBy: performedBy.trim(),
        dischargedOn: dischargedOn || null,
        outcome: outcome.trim() || null,
        remarks: remarks.trim() || null,
        version: mtp.version,
      }),
    onSuccess: (saved) => {
      queryClient.setQueryData(mtpKeys.case(mtp.id), saved);
      void queryClient.invalidateQueries({ queryKey: mtpKeys.cases });
      void queryClient.invalidateQueries({ queryKey: ['mtp', 'register'] });
      void queryClient.invalidateQueries({ queryKey: ['mtp', 'monthly-report'] });
      notify.success('Termination recorded', `${saved.serialNumber} is entered in the admission register.`);
      onClose();
    },
  });
  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ terminatedOn, method, performedBy, dischargedOn, outcome, remarks }),
    busy: record.isPending,
    onClose,
  });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 640 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            setTried(true);
            if (valid && !record.isPending) {
              record.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>
              Record the termination
              <Caption1 block className={styles.muted}>
                {mtp.serialNumber} · {mtp.patientName}
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.content}>
              {record.error && (
                <MessageBar intent="error">
                  <MessageBarBody>
                    {errorMessage(record.error, 'The termination could not be recorded.')}
                  </MessageBarBody>
                </MessageBar>
              )}
              <div className={styles.row}>
                <Field label="Date of termination" required validationMessage={show(problems.terminatedOn)}>
                  <Input
                    type="date"
                    value={terminatedOn}
                    min={earliest}
                    max={todayIso()}
                    onChange={(_, d) => setTerminatedOn(d.value)}
                    autoFocus
                  />
                </Field>
                <Field label="Method" required validationMessage={show(problems.method)}>
                  <Select value={method} onChange={(_, d) => setMethod(d.value as MtpMethod | '')}>
                    <option value="">Choose the method</option>
                    {MTP_METHODS.map((m) => (
                      <option key={m} value={m}>
                        {mtpMethodLabel[m]}
                      </option>
                    ))}
                  </Select>
                </Field>
              </div>
              <div className={styles.row}>
                <Field label="Performed by" required validationMessage={show(problems.performedBy)}>
                  <Input value={performedBy} maxLength={200} onChange={(_, d) => setPerformedBy(d.value)} />
                </Field>
                <Field label="Date of discharge" validationMessage={show(problems.dischargedOn)}>
                  <Input
                    type="date"
                    value={dischargedOn}
                    min={terminatedOn || undefined}
                    max={todayIso()}
                    onChange={(_, d) => setDischargedOn(d.value)}
                  />
                </Field>
              </div>
              <Field label="Result and condition at discharge">
                <Textarea value={outcome} maxLength={500} rows={2} onChange={(_, d) => setOutcome(d.value)} />
              </Field>
              <Field label="Remarks" validationMessage={show(problems.remarks)}>
                <Textarea value={remarks} maxLength={2000} rows={2} onChange={(_, d) => setRemarks(d.value)} />
              </Field>
              <Caption1 className={styles.muted}>
                Once recorded, the termination is entered in the admission register and the case cannot change.
              </Caption1>
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={record.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={record.isPending}
                disabledFocusable={record.isPending}
                icon={record.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Record termination
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
