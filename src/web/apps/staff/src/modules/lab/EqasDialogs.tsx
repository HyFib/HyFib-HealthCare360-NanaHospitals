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
  Input,
  makeStyles,
  Select,
  Spinner,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { todayIso } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { eqasApi, eqasKeys, eqasQueries, judge, nextStep, zScore, type EqasRoundDto } from './eqas';
import { TestLookup, type PickedTestRef } from './TestLookup';

const useStyles = makeStyles({
  content: { display: 'flex', flexDirection: 'column', gap: '12px' },
  preview: {
    padding: '8px 12px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
  },
});

const number = (text: string) => (text.trim() === '' ? Number.NaN : Number(text));

/** Schedules a round of a scheme for one numeric analyte, with its due date (and a new scheme, when there is none). */
export function ScheduleEqasRoundDialog({ onClose }: { onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const schemes = useQuery(eqasQueries.schemes());
  const [schemeId, setSchemeId] = useState('');
  const [newScheme, setNewScheme] = useState({ name: '', provider: '' });
  const [test, setTest] = useState<PickedTestRef | null>(null);
  const [roundCode, setRoundCode] = useState('');
  const [dueDate, setDueDate] = useState(todayIso());
  const [received, setReceived] = useState('');
  const creating = schemes.data?.length === 0 || schemeId === 'new';

  const save = useMutation({
    mutationFn: async () => {
      let scheme = schemeId;
      if (creating) {
        scheme = (
          await eqasApi.saveScheme({
            ...newScheme,
            participantCode: null,
            roundsPerYear: 12,
            isActive: true,
            version: null,
          })
        ).id;
      }
      return eqasApi.schedule({
        schemeId: scheme,
        labTestId: test!.id,
        roundCode,
        dueDate,
        sampleReceivedOn: received || null,
      });
    },
    onSuccess: () => {
      notify.success('EQAS round scheduled');
      void queryClient.invalidateQueries({ queryKey: eqasKeys.all });
      onClose();
    },
    onError: (error) => notify.error(error, 'Could not schedule the round'),
  });
  const valid =
    test !== null &&
    roundCode.trim() !== '' &&
    dueDate !== '' &&
    (creating ? newScheme.name.trim() !== '' && newScheme.provider.trim() !== '' : schemeId !== '');

  return (
    <Dialog open onOpenChange={(_, data) => !data.open && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <DialogBody>
          <DialogTitle>Schedule an EQAS round</DialogTitle>
          <DialogContent className={styles.content}>
            {schemes.data && schemes.data.length > 0 && (
              <Field label="Scheme" required>
                <Select value={schemeId} onChange={(_, data) => setSchemeId(data.value)}>
                  <option value="">Choose a scheme</option>
                  {schemes.data.map((scheme) => (
                    <option key={scheme.id} value={scheme.id}>
                      {scheme.name} ({scheme.provider})
                    </option>
                  ))}
                  <option value="new">A new scheme…</option>
                </Select>
              </Field>
            )}
            {creating && (
              <>
                <Field label="Scheme name" required>
                  <Input
                    value={newScheme.name}
                    onChange={(_, data) => setNewScheme((s) => ({ ...s, name: data.value }))}
                  />
                </Field>
                <Field label="Provider" required hint="For example CMC Vellore, AIIMS, Bio-Rad EQAS">
                  <Input
                    value={newScheme.provider}
                    onChange={(_, data) => setNewScheme((s) => ({ ...s, provider: data.value }))}
                  />
                </Field>
              </>
            )}
            <TestLookup
              label="Analyte"
              required
              value={test}
              onChange={(picked) => setTest(picked)}
              filter={(t) => !t.isPanel && t.resultType === 'Numeric'}
            />
            <Field label="Round and sample" required hint="As the provider names it, for example 2026/11 sample 2">
              <Input value={roundCode} onChange={(_, data) => setRoundCode(data.value)} />
            </Field>
            <Field label="Result due" required>
              <Input type="date" value={dueDate} onChange={(_, data) => setDueDate(data.value)} />
            </Field>
            <Field label="Sample received">
              <Input type="date" value={received} onChange={(_, data) => setReceived(data.value)} />
            </Field>
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>Cancel</Button>
            <Button
              appearance="primary"
              disabled={!valid || save.isPending}
              icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              onClick={() => save.mutate()}
            >
              Schedule
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

/** The round's next step: the result reported, the provider's evaluation, the corrective action, or acceptance. */
export function EqasStepDialog({ round, onClose }: { round: EqasRoundDto; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const step = nextStep(round);
  const [value, setValue] = useState('');
  const [assigned, setAssigned] = useState('');
  const [sd, setSd] = useState('');
  const [peer, setPeer] = useState('');
  const [capa, setCapa] = useState('');
  const [note, setNote] = useState('');
  const today = todayIso();
  const preview =
    step === 'evaluate' && round.reportedValue !== null
      ? zScore(round.reportedValue, number(assigned), number(sd))
      : null;

  const save = useMutation({
    mutationFn: () => {
      switch (step) {
        case 'report':
          return eqasApi.report(round.id, number(value), today, round.version);
        case 'evaluate':
          return eqasApi.evaluate(round.id, number(assigned), number(sd), peer.trim() || null, today, round.version);
        case 'capa':
          return eqasApi.linkCapa(round.id, capa.trim(), round.version);
        default:
          return eqasApi.accept(round.id, note.trim() || null, round.version);
      }
    },
    onSuccess: (saved) => {
      notify.success(
        saved.performance && step === 'evaluate'
          ? `Evaluated: ${saved.performance} (z = ${saved.zScore?.toFixed(2)})`
          : 'Saved',
      );
      void queryClient.invalidateQueries({ queryKey: eqasKeys.all });
      onClose();
    },
    onError: (error) => notify.error(error, 'Could not save the round'),
  });

  const valid =
    step === 'report'
      ? Number.isFinite(number(value))
      : step === 'evaluate'
        ? preview !== null
        : step === 'capa'
          ? capa.trim() !== ''
          : round.performance === 'Acceptable' || note.trim().length > 0;
  const titles = {
    report: 'Result reported',
    evaluate: "Provider's evaluation",
    capa: 'Corrective action',
    accept: 'Accept the evaluation',
    done: '',
  };

  return (
    <Dialog open onOpenChange={(_, data) => !data.open && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <DialogBody>
          <DialogTitle>
            {titles[step]}: {round.testName}, {round.roundCode}
          </DialogTitle>
          <DialogContent className={styles.content}>
            {step === 'report' && (
              <Field label={`Value reported to ${round.provider}${round.unit ? ` (${round.unit})` : ''}`} required>
                <Input value={value} onChange={(_, data) => setValue(data.value)} inputMode="decimal" />
              </Field>
            )}
            {step === 'evaluate' && (
              <>
                <Caption1>
                  Reported {round.reportedValue} {round.unit}
                </Caption1>
                <Field label="Assigned value (target or peer mean)" required>
                  <Input value={assigned} onChange={(_, data) => setAssigned(data.value)} inputMode="decimal" />
                </Field>
                <Field label="Standard deviation for assessment" required>
                  <Input value={sd} onChange={(_, data) => setSd(data.value)} inputMode="decimal" />
                </Field>
                <Field label="Peer group" hint="For example: Roche Cobas c311, enzymatic">
                  <Input value={peer} onChange={(_, data) => setPeer(data.value)} />
                </Field>
                {preview !== null && (
                  <div className={styles.preview} aria-live="polite">
                    z = {preview.toFixed(2)}: {judge(preview)}
                  </div>
                )}
              </>
            )}
            {step === 'capa' && (
              <Field
                label="CAPA number"
                required
                hint={`z = ${round.zScore?.toFixed(2)}: unacceptable. The quality manager raises the corrective action in Quality › CAPA; enter its number.`}
              >
                <Input value={capa} onChange={(_, data) => setCapa(data.value)} />
              </Field>
            )}
            {step === 'accept' && (
              <Field
                label="What was found"
                required={round.performance !== 'Acceptable'}
                hint={round.capaNumber ? `Corrective action ${round.capaNumber}` : undefined}
              >
                <Textarea
                  value={note}
                  onChange={(_, data) => setNote(data.value.slice(0, 1000))}
                  rows={3}
                  resize="vertical"
                />
              </Field>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>Cancel</Button>
            <Button
              appearance="primary"
              disabled={!valid || save.isPending}
              icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              onClick={() => save.mutate()}
            >
              Save
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
