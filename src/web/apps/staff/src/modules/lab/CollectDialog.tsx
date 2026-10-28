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
  Switch,
  Text,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkCircleRegular, InfoRegular, PrintRegular } from '@fluentui/react-icons';
import { useNotify, useUnsavedChanges, useValuesChanged } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { PatientFlagsStrip } from '@/modules/emr/PatientFlagsStrip';
import { fromZonedInput, isInFuture, toZonedInput } from './common/datetime';
import { errorData, errorMessage, hasErrorCode } from './common/errors';
import type { OrderPriority } from './common/types';
import {
  labApi,
  labKeys,
  type ConsentBasis,
  type ConsentTest,
  type ContainerType,
  type LabSection,
  type SampleLabel,
  type SpecimenType,
  type UnpaidTest,
} from './api';
import { PriorityBadge, TubeChip } from './badges';
import { billingStateLabel, sectionLabel, specimenLabel } from './labels';
import { printSampleLabels } from './print';

export interface CollectTube {
  sampleId: string;
  barcode: string;
  container: ContainerType;
  specimenType: SpecimenType;
  section: LabSection;
  tests: string[];
  preparation?: string[];
  isRecollection?: boolean;
}

export interface CollectTarget {
  orderId: string;
  accessionNumber: string;
  /** Whose allergies and alerts (infection control above all) the collector sees before drawing blood. */
  patientId: string;
  patientName: string;
  uhid: string;
  ageSex?: string | null;
  priority?: OrderPriority;
  tubes: CollectTube[];
}

const SITE_KEY = 'hc360:lab-collection-site';

function rememberedSite(): string {
  try {
    return localStorage.getItem(SITE_KEY) ?? '';
  } catch {
    return '';
  }
}

function rememberSite(site: string): void {
  try {
    localStorage.setItem(SITE_KEY, site);
  } catch {
    // Workstation preference only.
  }
}

const useStyles = makeStyles({
  content: { display: 'flex', flexDirection: 'column', gap: '14px' },
  patient: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  tubes: { display: 'flex', flexDirection: 'column', gap: '8px' },
  tube: {
    display: 'grid',
    gridTemplateColumns: 'auto minmax(0, 1fr)',
    gap: '4px 10px',
    alignItems: 'start',
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  tubeHead: { display: 'flex', gap: '10px', alignItems: 'center', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  prep: { display: 'flex', gap: '6px', alignItems: 'center', color: tokens.colorPaletteMarigoldForeground1 },
  row: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))', gap: '12px' },
  timeRow: { display: 'flex', gap: '8px', alignItems: 'flex-end' },
  consent: { display: 'flex', flexDirection: 'column', gap: '10px' },
  done: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '8px',
    textAlign: 'center',
    padding: '8px 0',
  },
  mono: { fontFamily: tokens.fontFamilyMonospace },
});

/**
 * Sample collection with the tube plan (SRS LIS-02/03): which tubes to draw, patient preparation, collection time and
 * site. Collecting prints one barcode label per tube straight away. Out-patients and walk-ins must have paid first;
 * if not, the unpaid tests are listed so the patient can be sent to billing.
 */
export function CollectDialog({ target, onClose }: { target: CollectTarget; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [selected, setSelected] = useState<string[]>(() => target.tubes.map((tube) => tube.sampleId));
  const [collectedAt, setCollectedAt] = useState(() => toZonedInput(new Date()));
  const [site, setSite] = useState(rememberedSite);
  const [printLabels, setPrintLabels] = useState(true);
  const [labels, setLabels] = useState<SampleLabel[] | null>(null);
  const [printing, setPrinting] = useState(false);
  // HIV tests the server holds until consent is recorded (HIV and AIDS Act 2017, s.5), and the consent given for them.
  const [consentTests, setConsentTests] = useState<ConsentTest[] | null>(null);
  const [basis, setBasis] = useState<ConsentBasis>('Patient');
  const [givenBy, setGivenBy] = useState('');
  const [relation, setRelation] = useState('');
  const [reference, setReference] = useState('');
  const [counselled, setCounselled] = useState(false);

  const collectedIso = fromZonedInput(collectedAt);
  const timeProblem = !collectedIso
    ? 'Enter the collection time.'
    : isInFuture(collectedIso, new Date())
      ? 'The collection time cannot be in the future.'
      : null;
  const consentIncomplete =
    consentTests !== null &&
    (basis === 'CourtOrder'
      ? !reference.trim()
      : !counselled || (basis === 'Representative' && (!givenBy.trim() || !relation.trim())));

  const print = async (toPrint: SampleLabel[]) => {
    setPrinting(true);
    try {
      await printSampleLabels(toPrint);
    } catch (error) {
      notify.error(error, 'The labels could not be printed');
    } finally {
      setPrinting(false);
    }
  };

  const collect = useMutation({
    mutationFn: () =>
      labApi.collect(target.orderId, {
        sampleIds: selected.length === target.tubes.length ? null : selected,
        collectedAt: collectedIso,
        collectionSite: site.trim() || null,
        consents:
          consentTests?.map((test) => ({
            itemId: test.itemId,
            basis,
            givenBy: basis === 'Representative' ? givenBy.trim() : null,
            relation: basis === 'Representative' ? relation.trim() : null,
            reference: reference.trim() || null,
          })) ?? null,
      }),
    onError: (error) => {
      if (hasErrorCode(error, 'lab.consent_required')) {
        setConsentTests(errorData<{ tests?: ConsentTest[] }>(error)?.tests ?? []);
      }
    },
    onSuccess: async (result) => {
      rememberSite(site.trim());
      setLabels(result);
      void queryClient.invalidateQueries({ queryKey: labKeys.samples });
      void queryClient.invalidateQueries({ queryKey: labKeys.orders });
      if (printLabels) {
        await print(result);
      }
    },
  });

  const unpaid = hasErrorCode(collect.error, 'lab.payment_pending')
    ? (errorData<{ tests?: UnpaidTest[] }>(collect.error)?.tests ?? [])
    : null;
  const consentRefused = hasErrorCode(collect.error, 'lab.consent_required');

  const toggle = (sampleId: string, checked: boolean) =>
    setSelected((current) =>
      checked ? [...new Set([...current, sampleId])] : current.filter((id) => id !== sampleId),
    );

  const preparation = [...new Set(target.tubes.flatMap((tube) => tube.preparation ?? []))];

  const guard = useUnsavedChanges({
    dirty:
      useValuesChanged({ selected, collectedAt, site, printLabels, basis, givenBy, relation, reference, counselled }) &&
      labels === null,
    busy: collect.isPending,
    onClose,
  });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 720 }}>
        {labels ? (
          <DialogBody>
            <DialogTitle>Samples collected</DialogTitle>
            <DialogContent>
              <div className={styles.done} role="status">
                <CheckmarkCircleRegular fontSize={40} color={tokens.colorPaletteGreenForeground1} />
                <Text weight="semibold">
                  {labels.length} {labels.length === 1 ? 'tube' : 'tubes'} collected for {target.patientName}
                </Text>
                <Caption1 className={styles.muted}>
                  {printLabels
                    ? 'Labels were sent to the label printer. Stick each label on its tube before the patient leaves.'
                    : 'Print the labels before sending the tubes to the lab.'}
                </Caption1>
                <div className={styles.tubes} style={{ width: '100%' }}>
                  {labels.map((label) => (
                    <div key={label.sampleId} className={styles.tubeHead}>
                      <TubeChip container={label.container} />
                      <Text className={styles.mono}>{label.barcode}</Text>
                      <Caption1 className={styles.muted}>{label.tests.join(', ')}</Caption1>
                    </div>
                  ))}
                </div>
              </div>
            </DialogContent>
            <DialogActions>
              <Button
                icon={printing ? <Spinner size="tiny" aria-hidden /> : <PrintRegular />}
                // While printing it stays focusable (aria-disabled) and ignores clicks: a disabled button drops focus
                // out of the open dialog, and Fluent then returns it to a button behind the dialog.
                disabledFocusable={printing}
                onClick={() => void print(labels)}
              >
                {printLabels ? 'Print labels again' : 'Print labels'}
              </Button>
              <Button appearance="primary" onClick={onClose} autoFocus>
                Done
              </Button>
            </DialogActions>
          </DialogBody>
        ) : (
          <form
            onSubmit={(event) => {
              event.preventDefault();
              if (selected.length > 0 && !timeProblem && !consentIncomplete && !collect.isPending) {
                collect.mutate();
              }
            }}
          >
            <DialogBody>
              <DialogTitle>
                Collect samples
                <div className={styles.patient}>
                  <Caption1>
                    {target.patientName} · UHID {target.uhid}
                    {target.ageSex ? ` · ${target.ageSex}` : ''} · {target.accessionNumber}
                  </Caption1>
                  {target.priority && <PriorityBadge priority={target.priority} />}
                </div>
              </DialogTitle>
              <DialogContent className={styles.content}>
                <PatientFlagsStrip patientId={target.patientId} />
                {unpaid && (
                  <MessageBar intent="error" layout="multiline">
                    <MessageBarBody>
                      <MessageBarTitle>Payment pending</MessageBarTitle>
                      Send the patient to the billing counter, then collect.
                      {unpaid.length > 0 && (
                        <ul style={{ margin: '4px 0 0', paddingLeft: 18 }}>
                          {unpaid.map((test) => (
                            <li key={test.reference}>
                              {test.name}: {billingStateLabel[test.state] ?? test.state}
                            </li>
                          ))}
                        </ul>
                      )}
                    </MessageBarBody>
                  </MessageBar>
                )}
                {collect.error && !unpaid && !consentRefused && (
                  <MessageBar intent="error">
                    <MessageBarBody>
                      {errorMessage(collect.error, 'The samples could not be marked as collected.')}
                    </MessageBarBody>
                  </MessageBar>
                )}

                {consentTests && (
                  <div className={styles.consent} role="group" aria-label="Record the consent">
                    <MessageBar intent="warning" layout="multiline">
                      <MessageBarBody>
                        <MessageBarTitle>Consent for an HIV test</MessageBarTitle>
                        {consentTests.map((test) => test.name).join(', ')}: draw the sample only after pre-test
                        counselling and the patient&apos;s written consent (HIV and AIDS Act 2017, s.5).
                      </MessageBarBody>
                    </MessageBar>
                    <RadioGroup
                      aria-label="On what the test is done"
                      value={basis}
                      onChange={(_, data) => setBasis(data.value as ConsentBasis)}
                    >
                      <Radio value="Patient" label="The patient consented" />
                      <Radio
                        value="Representative"
                        label="A representative consented (for a child, or a patient who cannot)"
                      />
                      <Radio value="CourtOrder" label="A court ordered the test" />
                    </RadioGroup>
                    {basis === 'Representative' && (
                      <div className={styles.row}>
                        <Field label="Representative's name" required>
                          <Input value={givenBy} onChange={(_, data) => setGivenBy(data.value.slice(0, 200))} />
                        </Field>
                        <Field label="Relation to the patient" required>
                          <Input value={relation} onChange={(_, data) => setRelation(data.value.slice(0, 64))} />
                        </Field>
                      </div>
                    )}
                    <Field
                      label={basis === 'CourtOrder' ? 'Court order number' : 'Consent form number'}
                      required={basis === 'CourtOrder'}
                    >
                      <Input value={reference} onChange={(_, data) => setReference(data.value.slice(0, 64))} />
                    </Field>
                    {basis !== 'CourtOrder' && (
                      <Checkbox
                        checked={counselled}
                        onChange={(_, data) => setCounselled(data.checked === true)}
                        label="Pre-test counselling was given and the consent form is signed"
                      />
                    )}
                  </div>
                )}

                {preparation.length > 0 && (
                  <MessageBar intent="warning" icon={<InfoRegular />}>
                    <MessageBarBody>
                      <MessageBarTitle>Check patient preparation</MessageBarTitle>
                      {preparation.join(' · ')}
                    </MessageBarBody>
                  </MessageBar>
                )}

                <div className={styles.tubes} role="group" aria-label="Tubes to collect">
                  {target.tubes.map((tube) => (
                    <label key={tube.sampleId} className={styles.tube}>
                      <Checkbox
                        checked={selected.includes(tube.sampleId)}
                        onChange={(_, data) => toggle(tube.sampleId, data.checked === true)}
                        aria-label={`Collect ${tube.tests.join(', ')}`}
                      />
                      <span>
                        <span className={styles.tubeHead}>
                          <TubeChip container={tube.container} detail={specimenLabel[tube.specimenType]} />
                          <Caption1 className={styles.muted}>{sectionLabel[tube.section]}</Caption1>
                          <Caption1 className={styles.mono}>{tube.barcode}</Caption1>
                          {tube.isRecollection && (
                            <Caption1 style={{ color: tokens.colorPaletteRedForeground1, fontWeight: 600 }}>
                              Repeat sample
                            </Caption1>
                          )}
                        </span>
                        <Text block>{tube.tests.join(', ')}</Text>
                      </span>
                    </label>
                  ))}
                </div>

                <div className={styles.row}>
                  <Field
                    label="Collected at (hospital time)"
                    required
                    validationMessage={timeProblem ?? undefined}
                    validationState={timeProblem ? 'error' : 'none'}
                  >
                    <div className={styles.timeRow}>
                      <Input
                        type="datetime-local"
                        value={collectedAt}
                        onChange={(_, data) => setCollectedAt(data.value)}
                        style={{ flex: 1 }}
                      />
                      <Button onClick={() => setCollectedAt(toZonedInput(new Date()))}>Now</Button>
                    </div>
                  </Field>
                  <Field label="Collection site" hint="Remembered on this computer">
                    <Input
                      value={site}
                      onChange={(_, data) => setSite(data.value.slice(0, 100))}
                      placeholder="e.g. OP collection room, Ward 3"
                    />
                  </Field>
                </div>
                <Switch
                  checked={printLabels}
                  onChange={(_, data) => setPrintLabels(data.checked)}
                  label="Print barcode labels now"
                />
              </DialogContent>
              <DialogActions>
                <Button onClick={guard.requestClose} disabled={collect.isPending}>
                  Cancel
                </Button>
                <Button
                  type="submit"
                  appearance="primary"
                  disabled={selected.length === 0 || Boolean(timeProblem) || consentIncomplete}
                  // Likewise while collecting; the form ignores a submit while one is running.
                  disabledFocusable={collect.isPending}
                  icon={collect.isPending ? <Spinner size="tiny" aria-hidden /> : <PrintRegular />}
                >
                  {selected.length === target.tubes.length
                    ? printLabels
                      ? 'Collect and print labels'
                      : 'Mark as collected'
                    : `Collect ${selected.length} of ${target.tubes.length} tubes`}
                </Button>
              </DialogActions>
            </DialogBody>
          </form>
        )}
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
