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
  MessageBarTitle,
  Radio,
  RadioGroup,
  Spinner,
  Text,
  Textarea,
  tokens,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import { formatDate, formatDateTime, todayIso } from '@hc360/core';
import {
  CheckboxGroup,
  ErrorState,
  LoadingState,
  Section,
  useNotify,
  useUnsavedChanges,
  useValuesChanged,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type ReactNode } from 'react';
import { DocumentButtons } from '@/lib/DocumentButtons';
import { usePermission } from '@/lib/permissions';
import { errorMessage } from '@/modules/lab/common/errors';
import {
  FORM_F_INDICATIONS,
  formFDocument,
  radiologyApi,
  radiologyKeys,
  radiologyQueries,
  RADIOLOGY_PERMISSIONS,
  type FormFDto,
  type FormFIndication,
  type FormFReferral,
  type ImagingStudyDetail,
} from './api';
import { formFIndicationLabel } from './labels';

const useStyles = makeStyles({
  content: { display: 'flex', flexDirection: 'column', gap: '12px' },
  row: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', gap: '12px' },
  facts: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(210px, 1fr))', gap: '12px 24px' },
  fact: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
  indications: { display: 'flex', flexDirection: 'column', maxHeight: '260px', overflowY: 'auto' },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'center' },
  declaration: {
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: '8px 12px',
  },
});

function Fact({ label, children }: { label: string; children: ReactNode }) {
  const styles = useStyles();
  if (children === null || children === undefined || children === '') {
    return null;
  }
  return (
    <div className={styles.fact}>
      <Caption1 className={styles.muted}>{label}</Caption1>
      <Text>{children}</Text>
    </div>
  );
}

/**
 * PCPNDT Form F on an ultrasound in pregnancy: the woman's details and declaration before the scan, then the result and
 * the performing doctor's declaration. The scan does not start, and the report is not signed, without it.
 */
export function FormFPanel({ study }: { study: ImagingStudyDetail }) {
  const styles = useStyles();
  const restoreFocusTarget = useRestoreFocusTarget();
  const canRecord = usePermission([RADIOLOGY_PERMISSIONS.formFRecord, RADIOLOGY_PERMISSIONS.formFSign]);
  const canSign = usePermission(RADIOLOGY_PERMISSIONS.formFSign);
  const canRead = usePermission([
    RADIOLOGY_PERMISSIONS.formFRecord,
    RADIOLOGY_PERMISSIONS.formFSign,
    RADIOLOGY_PERMISSIONS.formFRegister,
  ]);
  const form = useQuery({ ...radiologyQueries.formF(study.id), enabled: canRead && study.requiresFormF });
  const [dialog, setDialog] = useState<'declare' | 'complete' | null>(null);

  if (!study.requiresFormF || !canRead) {
    return null;
  }

  const data = form.data ?? null;
  // The woman's details are recorded before the scan and corrected until the form is completed or cancelled.
  const editable = study.status !== 'Cancelled' && (data ? data.status === 'Declared' : !study.acquiredAt);
  const scanned = Boolean(study.acquiredAt);
  return (
    <Section
      title="PCPNDT Form F"
      description="Every ultrasound in pregnancy needs Form F. Record the woman's details and declaration before the scan; the doctor who scanned completes it before signing the report."
    >
      {form.isLoading ? (
        <LoadingState label="Opening Form F…" />
      ) : form.error ? (
        <ErrorState error={form.error} onRetry={() => void form.refetch()} />
      ) : (
        <div className={styles.content}>
          {!data && (
            <MessageBar intent="warning">
              <MessageBarBody>
                <MessageBarTitle>Form F not recorded</MessageBarTitle>
                The scan cannot start until the woman&apos;s details and her declaration are recorded.
              </MessageBarBody>
            </MessageBar>
          )}
          {data?.status === 'Cancelled' && (
            <MessageBar intent="info">
              <MessageBarBody>
                <MessageBarTitle>Scan cancelled</MessageBarTitle>
                {data.cancelReason
                  ? `The form was closed with the scan: ${data.cancelReason}`
                  : 'The form was closed with the scan.'}
              </MessageBarBody>
            </MessageBar>
          )}
          {data?.status === 'Declared' && (
            <MessageBar intent="info">
              <MessageBarBody>
                <MessageBarTitle>Declared, not completed</MessageBarTitle>
                {scanned
                  ? 'The doctor who performed the ultrasound completes the form with the result before signing the report.'
                  : 'The scan may go ahead. Complete the form once the ultrasound is done.'}
              </MessageBarBody>
            </MessageBar>
          )}
          {data && <FormFFacts form={data} />}
          <div className={styles.actions}>
            {canRecord && editable && (
              <Button
                appearance={data ? 'secondary' : 'primary'}
                onClick={() => setDialog('declare')}
                {...restoreFocusTarget}
              >
                {data ? 'Correct the woman’s details' : 'Record Form F'}
              </Button>
            )}
            {canSign && data?.status === 'Declared' && scanned && (
              <Button appearance="primary" onClick={() => setDialog('complete')} {...restoreFocusTarget}>
                Complete Form F
              </Button>
            )}
            {data && <DocumentButtons path={formFDocument(study.id)} printLabel="Print Form F" />}
          </div>
        </div>
      )}
      {dialog === 'declare' && <DeclarationDialog study={study} form={data} onClose={() => setDialog(null)} />}
      {dialog === 'complete' && data && <CompleteDialog study={study} form={data} onClose={() => setDialog(null)} />}
    </Section>
  );
}

function FormFFacts({ form }: { form: FormFDto }) {
  const styles = useStyles();
  return (
    <div className={styles.facts}>
      <Fact label="Form">{`${form.formNumber} · registration ${form.registrationNumber}`}</Fact>
      <Fact label="Age">{`${form.ageYears} years`}</Fact>
      <Fact label="Husband or father">{form.husbandOrFatherName}</Fact>
      <Fact label="Living children">{`${form.livingSons} son(s), ${form.livingDaughters} daughter(s)`}</Fact>
      <Fact label="Period of pregnancy">
        {[
          form.weeksOfPregnancy ? `${form.weeksOfPregnancy} weeks` : null,
          form.lastMenstrualPeriod ? `LMP ${formatDate(form.lastMenstrualPeriod)}` : null,
        ]
          .filter(Boolean)
          .join(' · ')}
      </Fact>
      <Fact label="Referred by">
        {form.referral === 'Self'
          ? 'Self-referral by the doctor performing the ultrasound'
          : `${form.referringDoctorName}, ${form.referringDoctorAddress}`}
      </Fact>
      <Fact label="Indications">{form.indications.map((i) => formFIndicationLabel[i]).join('; ')}</Fact>
      <Fact label="Woman's declaration">
        {`${formatDateTime(form.declaredAt)}${form.declarationRecordedBy ? `, recorded by ${form.declarationRecordedBy}` : ''}`}
      </Fact>
      <Fact label="Result">{form.result}</Fact>
      <Fact label="Result given to">
        {form.resultConveyedTo
          ? `${form.resultConveyedTo}${form.resultConveyedOn ? ` on ${formatDate(form.resultConveyedOn)}` : ''}`
          : null}
      </Fact>
      <Fact label="Indication for MTP">
        {form.status === 'Completed' ? (form.mtpIndicated ? form.mtpIndication : 'None') : null}
      </Fact>
      <Fact label="Doctor's declaration">
        {form.completedAt ? `${form.doctorName}, ${formatDateTime(form.completedAt)}` : null}
      </Fact>
    </div>
  );
}

function DeclarationDialog({
  study,
  form,
  onClose,
}: {
  study: ImagingStudyDetail;
  form: FormFDto | null;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [age, setAge] = useState(form ? String(form.ageYears) : (study.age?.match(/^\d+/)?.[0] ?? ''));
  const [husband, setHusband] = useState(form?.husbandOrFatherName ?? '');
  const [address, setAddress] = useState(form?.address ?? '');
  const [phone, setPhone] = useState(form?.phone ?? '');
  const [sons, setSons] = useState(String(form?.livingSons ?? 0));
  const [daughters, setDaughters] = useState(String(form?.livingDaughters ?? 0));
  const [lmp, setLmp] = useState(form?.lastMenstrualPeriod ?? '');
  const [weeks, setWeeks] = useState(form?.weeksOfPregnancy ? String(form.weeksOfPregnancy) : '');
  const [referral, setReferral] = useState<FormFReferral>(form?.referral ?? 'Doctor');
  const [doctorName, setDoctorName] = useState(form?.referringDoctorName ?? study.orderingDoctor ?? '');
  const [doctorAddress, setDoctorAddress] = useState(form?.referringDoctorAddress ?? '');
  const [indications, setIndications] = useState<FormFIndication[]>(form?.indications ?? []);
  const [other, setOther] = useState(form?.otherIndication ?? '');
  const [declared, setDeclared] = useState(Boolean(form));

  const ageYears = Number.parseInt(age, 10);
  const weeksValue = weeks ? Number.parseInt(weeks, 10) : null;
  const problems = {
    age: Number.isInteger(ageYears) && ageYears >= 10 && ageYears <= 60 ? null : 'Enter her age in years.',
    husband: husband.trim() ? null : 'Enter the name of her husband or father.',
    address: address.trim() ? null : 'Enter her address.',
    period: lmp || weeksValue ? null : 'Enter the last menstrual period or the weeks of pregnancy.',
    weeks: weeksValue === null || (weeksValue >= 1 && weeksValue <= 45) ? null : 'Enter 1 to 45 weeks.',
    referral:
      referral === 'Self' || (doctorName.trim() && doctorAddress.trim())
        ? null
        : 'Enter the name and address of the referring doctor.',
    indications: indications.length > 0 ? null : 'Choose at least one indication.',
    declared: declared ? null : 'She must declare this before the ultrasound.',
  };
  const valid = Object.values(problems).every((p) => p === null);

  const save = useMutation({
    mutationFn: () =>
      radiologyApi.saveFormF(study.id, {
        ageYears,
        husbandOrFatherName: husband.trim(),
        address: address.trim(),
        phone: phone.trim() || null,
        livingSons: Number.parseInt(sons, 10) || 0,
        livingDaughters: Number.parseInt(daughters, 10) || 0,
        lastMenstrualPeriod: lmp || null,
        weeksOfPregnancy: weeksValue,
        referral,
        referringDoctorName: referral === 'Doctor' ? doctorName.trim() : null,
        referringDoctorAddress: referral === 'Doctor' ? doctorAddress.trim() : null,
        indications,
        otherIndication: other.trim() || null,
        signedFormFileId: form?.signedFormFileId ?? null,
        womanDeclared: declared,
        version: form?.version ?? null,
      }),
    onSuccess: (saved) => {
      queryClient.setQueryData(radiologyKeys.formF(study.id), saved);
      void queryClient.invalidateQueries({ queryKey: radiologyKeys.study(study.id) });
      notify.success('Form F recorded', `${saved.formNumber}: the scan may go ahead.`);
      onClose();
    },
  });
  const guard = useUnsavedChanges({
    dirty: useValuesChanged({
      age,
      husband,
      address,
      phone,
      sons,
      daughters,
      lmp,
      weeks,
      referral,
      doctorName,
      doctorAddress,
      indications,
      other,
      declared,
    }),
    busy: save.isPending,
    onClose,
  });
  const [tried, setTried] = useState(false);
  const show = (problem: string | null) => (tried ? (problem ?? undefined) : undefined);

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 720 }}>
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
              Form F: the woman&apos;s details
              <Caption1 block className={styles.muted}>
                {study.studyName} · {study.patientName}
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.content}>
              {save.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(save.error, 'Form F could not be saved.')}</MessageBarBody>
                </MessageBar>
              )}
              <div className={styles.row}>
                <Field label="Age (years)" required validationMessage={show(problems.age)}>
                  <Input type="number" min={10} max={60} value={age} onChange={(_, d) => setAge(d.value)} autoFocus />
                </Field>
                <Field label="Husband or father" required validationMessage={show(problems.husband)}>
                  <Input value={husband} maxLength={200} onChange={(_, d) => setHusband(d.value)} />
                </Field>
                <Field label="Phone">
                  <Input type="tel" value={phone} maxLength={20} onChange={(_, d) => setPhone(d.value)} />
                </Field>
              </div>
              <Field label="Address" required validationMessage={show(problems.address)}>
                <Textarea value={address} maxLength={500} rows={2} onChange={(_, d) => setAddress(d.value)} />
              </Field>
              <div className={styles.row}>
                <Field label="Living sons">
                  <Input type="number" min={0} max={20} value={sons} onChange={(_, d) => setSons(d.value)} />
                </Field>
                <Field label="Living daughters">
                  <Input type="number" min={0} max={20} value={daughters} onChange={(_, d) => setDaughters(d.value)} />
                </Field>
                <Field label="Last menstrual period" validationMessage={show(problems.period)}>
                  <Input type="date" value={lmp} max={todayIso()} onChange={(_, d) => setLmp(d.value)} />
                </Field>
                <Field label="Weeks of pregnancy" validationMessage={show(problems.weeks)}>
                  <Input type="number" min={1} max={45} value={weeks} onChange={(_, d) => setWeeks(d.value)} />
                </Field>
              </div>
              <Field label="Referred by">
                <RadioGroup
                  layout="horizontal"
                  value={referral}
                  onChange={(_, d) => setReferral(d.value as FormFReferral)}
                >
                  <Radio value="Doctor" label="Another doctor" />
                  <Radio value="Self" label="Self-referral by the doctor who scans" />
                </RadioGroup>
              </Field>
              {referral === 'Doctor' && (
                <div className={styles.row}>
                  <Field label="Referring doctor" required validationMessage={show(problems.referral)}>
                    <Input value={doctorName} maxLength={200} onChange={(_, d) => setDoctorName(d.value)} />
                  </Field>
                  <Field label="Referring doctor's address" required>
                    <Input value={doctorAddress} maxLength={500} onChange={(_, d) => setDoctorAddress(d.value)} />
                  </Field>
                </div>
              )}
              <CheckboxGroup label="Indications" required validationMessage={show(problems.indications)}>
                <div className={styles.indications}>
                  {FORM_F_INDICATIONS.map((indication) => (
                    <Checkbox
                      key={indication}
                      label={formFIndicationLabel[indication]}
                      checked={indications.includes(indication)}
                      onChange={(_, d) =>
                        setIndications((current) =>
                          d.checked
                            ? FORM_F_INDICATIONS.filter((i) => i === indication || current.includes(i))
                            : current.filter((i) => i !== indication),
                        )
                      }
                    />
                  ))}
                </div>
              </CheckboxGroup>
              <Field label="Other indication">
                <Input value={other} maxLength={500} onChange={(_, d) => setOther(d.value)} />
              </Field>
              <Field validationMessage={show(problems.declared)}>
                <div className={styles.declaration}>
                  <Checkbox
                    checked={declared}
                    onChange={(_, d) => setDeclared(Boolean(d.checked))}
                    label="She has declared, and signed the paper form, that she does not want to know the sex of her foetus."
                  />
                </div>
              </Field>
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
                Save
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}

function CompleteDialog({ study, form, onClose }: { study: ImagingStudyDetail; form: FormFDto; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [result, setResult] = useState('');
  const [conveyedTo, setConveyedTo] = useState('');
  const [conveyedOn, setConveyedOn] = useState(todayIso());
  const [mtp, setMtp] = useState(false);
  const [mtpIndication, setMtpIndication] = useState('');
  const [declared, setDeclared] = useState(false);
  const problems = {
    result: result.trim() ? null : 'Enter the result of the ultrasound.',
    conveyedTo: conveyedTo.trim() ? null : 'Enter whom the result was given to.',
    conveyedOn: conveyedOn ? null : 'Enter the date.',
    mtp: !mtp || mtpIndication.trim() ? null : 'Describe the abnormality.',
    declared: declared ? null : 'Make the declaration to complete the form.',
  };
  const valid = Object.values(problems).every((p) => p === null);
  const [tried, setTried] = useState(false);
  const show = (problem: string | null) => (tried ? (problem ?? undefined) : undefined);

  const complete = useMutation({
    mutationFn: () =>
      radiologyApi.completeFormF(study.id, {
        result: result.trim(),
        conveyedTo: conveyedTo.trim(),
        conveyedOn,
        mtpIndicated: mtp,
        mtpIndication: mtp ? mtpIndication.trim() : null,
        doctorDeclared: declared,
        version: form.version,
      }),
    onSuccess: (saved) => {
      queryClient.setQueryData(radiologyKeys.formF(study.id), saved);
      void queryClient.invalidateQueries({ queryKey: radiologyKeys.study(study.id) });
      notify.success('Form F completed', 'The report can now be signed.');
      onClose();
    },
  });
  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ result, conveyedTo, conveyedOn, mtp, mtpIndication, declared }),
    busy: complete.isPending,
    onClose,
  });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 640 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            setTried(true);
            if (valid && !complete.isPending) {
              complete.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>
              Complete Form F {form.formNumber}
              <Caption1 block className={styles.muted}>
                {study.studyName} · {study.patientName}
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.content}>
              {complete.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(complete.error, 'Form F could not be completed.')}</MessageBarBody>
                </MessageBar>
              )}
              <Field label="Result of the ultrasound" required validationMessage={show(problems.result)}>
                <Textarea value={result} maxLength={2000} rows={3} onChange={(_, d) => setResult(d.value)} autoFocus />
              </Field>
              <div className={styles.row}>
                <Field label="Result given to" required validationMessage={show(problems.conveyedTo)}>
                  <Input value={conveyedTo} maxLength={200} onChange={(_, d) => setConveyedTo(d.value)} />
                </Field>
                <Field label="On" required validationMessage={show(problems.conveyedOn)}>
                  <Input type="date" value={conveyedOn} max={todayIso()} onChange={(_, d) => setConveyedOn(d.value)} />
                </Field>
              </div>
              <Checkbox
                checked={mtp}
                onChange={(_, d) => setMtp(Boolean(d.checked))}
                label="The abnormality found indicates a termination of pregnancy"
              />
              {mtp && (
                <Field label="Abnormality indicating MTP" required validationMessage={show(problems.mtp)}>
                  <Textarea
                    value={mtpIndication}
                    maxLength={1000}
                    rows={2}
                    onChange={(_, d) => setMtpIndication(d.value)}
                  />
                </Field>
              )}
              <Field validationMessage={show(problems.declared)}>
                <div className={styles.declaration}>
                  <Checkbox
                    checked={declared}
                    onChange={(_, d) => setDeclared(Boolean(d.checked))}
                    label="I performed this ultrasound and declare that I have neither detected nor disclosed the sex of the foetus to anybody in any manner."
                  />
                </div>
              </Field>
              <Caption1 className={styles.muted}>Once completed, Form F cannot be changed.</Caption1>
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={complete.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={complete.isPending}
                disabledFocusable={complete.isPending}
                icon={complete.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Complete and sign
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
