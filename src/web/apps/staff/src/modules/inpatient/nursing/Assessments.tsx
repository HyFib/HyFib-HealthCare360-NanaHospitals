import {
  Badge,
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Radio,
  RadioGroup,
  Spinner,
  Subtitle2,
  Switch,
  Text,
  Textarea,
  tokens,
  ToggleButton,
} from '@fluentui/react-components';
import { EmojiSadRegular, PersonWalkingRegular, WarningRegular } from '@fluentui/react-icons';
import { formatDateTime, formatRelative, isApiError } from '@hc360/core';
import { DataTable, useLeaveGuard, useNotify, useValuesChanged } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type ReactElement } from 'react';
import {
  ASSESSMENT_LABEL,
  nursingApi,
  nursingKeys,
  nursingQueries,
  type AssessmentDto,
  type AssessmentType,
} from '../nursingApi';
import {
  AMBULATORY_AID_OPTIONS,
  BRADEN_ITEMS,
  bradenScore,
  GAIT_OPTIONS,
  morseScore,
  PAIN_SCALES,
  painScore,
  painScoreOptions,
  RISK_LABEL,
  riskTone,
  type BradenInput,
  type MorseInput,
  type PainScaleKind,
  type RiskScore,
} from '../riskScales';

const useStyles = makeStyles({
  cards: {
    display: 'grid',
    gap: '12px',
    gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))',
    marginBottom: '16px',
  },
  card: {
    appearance: 'none',
    margin: 0,
    textAlign: 'start',
    fontFamily: 'inherit',
    color: tokens.colorNeutralForeground1,
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    padding: '12px',
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-visible': { outline: `3px solid ${tokens.colorStrokeFocus2}` },
  },
  active: { border: `2px solid ${tokens.colorBrandStroke1}` },
  cardHead: { display: 'flex', alignItems: 'center', gap: '8px', justifyContent: 'space-between' },
  score: {
    fontSize: tokens.fontSizeHero700,
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: tokens.lineHeightHero700,
  },
  form: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    padding: '12px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground2,
    marginBottom: '16px',
  },
  items: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(260px, 1fr))' },
  painScores: {
    display: 'grid',
    gridTemplateColumns: 'repeat(11, minmax(36px, 1fr))',
    gap: '4px',
    '@media (max-width: 560px)': { gridTemplateColumns: 'repeat(6, minmax(36px, 1fr))' },
  },
  painButton: { minWidth: '36px', minHeight: '44px', fontWeight: tokens.fontWeightSemibold },
  muted: { color: tokens.colorNeutralForeground3 },
  result: { position: 'sticky', bottom: 0 },
});

const ICONS: Record<AssessmentType, ReactElement> = {
  MorseFall: <PersonWalkingRegular />,
  Braden: <WarningRegular />,
  Pain: <EmojiSadRegular />,
};

function ScorePreview({ score, max }: { score: RiskScore; max: number }) {
  const styles = useStyles();
  const tone = riskTone(score.risk);
  return (
    <div className={styles.result} aria-live="polite">
      <MessageBar
        intent={tone === 'danger' ? 'error' : tone === 'warning' ? 'warning' : tone === 'success' ? 'success' : 'info'}
        layout="multiline"
      >
        <MessageBarBody>
          <MessageBarTitle>
            Score {score.score} of {max} · {RISK_LABEL[score.risk]} risk
          </MessageBarTitle>
          {score.interpretation}
        </MessageBarBody>
      </MessageBar>
    </div>
  );
}

/** Nursing risk assessments with the latest score of each scale and the history. */
export function Assessments({ admissionId, canWrite }: { admissionId: string; canWrite: boolean }) {
  const styles = useStyles();
  const assessments = useQuery(nursingQueries.assessments(admissionId));
  const [form, setForm] = useState<AssessmentType | null>(null);
  const rows = assessments.data ?? [];
  const latest = (type: AssessmentType) => rows.find((a) => a.type === type);

  return (
    <div>
      <div className={styles.cards}>
        {(['MorseFall', 'Braden', 'Pain'] as const).map((type) => {
          const last = latest(type);
          return (
            <button
              key={type}
              type="button"
              className={form === type ? `${styles.card} ${styles.active}` : styles.card}
              onClick={() => canWrite && setForm(form === type ? null : type)}
              aria-expanded={form === type}
              disabled={!canWrite && !last}
            >
              <span className={styles.cardHead}>
                <Text weight="semibold">
                  {ICONS[type]} {ASSESSMENT_LABEL[type]}
                </Text>
                {last && (
                  <Badge appearance="tint" color={riskTone(last.risk)}>
                    {RISK_LABEL[last.risk]}
                  </Badge>
                )}
              </span>
              {last ? (
                <>
                  <span className={styles.score}>{last.score}</span>
                  <Caption1 className={styles.muted}>
                    {formatRelative(last.assessedAt)} by {last.assessedByName ?? 'nurse'}
                  </Caption1>
                </>
              ) : (
                <Caption1 className={styles.muted}>Not assessed yet</Caption1>
              )}
              {canWrite && (
                <Caption1 style={{ color: tokens.colorBrandForeground1 }}>
                  {form === type ? 'Close' : 'New assessment'}
                </Caption1>
              )}
            </button>
          );
        })}
      </div>

      {form === 'MorseFall' && <MorseForm admissionId={admissionId} onDone={() => setForm(null)} />}
      {form === 'Braden' && <BradenForm admissionId={admissionId} onDone={() => setForm(null)} />}
      {form === 'Pain' && <PainForm admissionId={admissionId} onDone={() => setForm(null)} />}

      <DataTable<AssessmentDto>
        ariaLabel="Assessment history"
        rows={assessments.data}
        loading={assessments.isLoading}
        error={assessments.error}
        onRetry={() => void assessments.refetch()}
        getRowId={(a) => a.id}
        size="small"
        columns={[
          { key: 'at', header: 'When', width: 170, render: (a) => formatDateTime(a.assessedAt) },
          { key: 'type', header: 'Scale', render: (a) => ASSESSMENT_LABEL[a.type] ?? a.type },
          {
            key: 'score',
            header: 'Score',
            width: 80,
            align: 'end',
            render: (a) => <Text weight="semibold">{a.score}</Text>,
          },
          {
            key: 'risk',
            header: 'Risk',
            width: 110,
            render: (a) => (
              <Badge appearance="tint" color={riskTone(a.risk)}>
                {RISK_LABEL[a.risk]}
              </Badge>
            ),
          },
          { key: 'meaning', header: 'Action', priority: 'low', render: (a) => a.interpretation },
          { key: 'by', header: 'By', priority: 'low', render: (a) => a.assessedByName ?? '—' },
        ]}
        emptyTitle="No assessments yet"
        emptyDescription={
          canWrite ? 'Assess fall risk and pressure-injury risk on admission and every shift.' : undefined
        }
      />
    </div>
  );
}

function useSaveAssessment(admissionId: string, onDone: () => void) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  return (label: string) => ({
    onSuccess: (a: AssessmentDto) => {
      notify.success(`${label} saved: score ${a.score}`, `${RISK_LABEL[a.risk]} risk.`);
      void queryClient.invalidateQueries({ queryKey: nursingKeys.admission(admissionId) });
      void queryClient.invalidateQueries({ queryKey: ['inpatient', 'board'] });
      onDone();
    },
  });
}

function ErrorBar({ error }: { error: unknown }) {
  if (!error) {
    return null;
  }
  return (
    <MessageBar intent="error">
      <MessageBarBody>{isApiError(error) ? error.userMessage : 'Could not save the assessment.'}</MessageBarBody>
    </MessageBar>
  );
}

function MorseForm({ admissionId, onDone }: { admissionId: string; onDone: () => void }) {
  const styles = useStyles();
  const handlers = useSaveAssessment(admissionId, onDone);
  const [input, setInput] = useState<MorseInput>({
    historyOfFalling: false,
    secondaryDiagnosis: false,
    ambulatoryAid: 'None',
    intravenousAccess: false,
    gait: 'Normal',
    overestimatesAbility: false,
  });
  const [notes, setNotes] = useState('');
  const score = morseScore(input);
  const save = useMutation({
    mutationFn: () => nursingApi.addMorse(admissionId, { ...input, notes: notes.trim() || null }),
    ...handlers('Fall risk'),
  });
  const toggle = (
    key: 'historyOfFalling' | 'secondaryDiagnosis' | 'intravenousAccess' | 'overestimatesAbility',
    label: string,
    points: number,
  ) => (
    <Switch
      checked={input[key]}
      onChange={(_, d) => setInput({ ...input, [key]: d.checked })}
      label={`${label} (${points})`}
    />
  );

  // Leaving the screen or closing the window with unsaved changes asks first (SRS UX-08).
  useLeaveGuard({ dirty: useValuesChanged({ input, notes }), busy: save.isPending });

  return (
    <section className={styles.form} aria-label="Morse fall scale">
      <Subtitle2 as="h3">Fall risk — Morse scale</Subtitle2>
      <div className={styles.items}>
        <div>
          {toggle('historyOfFalling', 'Fell in the last 3 months', 25)}
          {toggle('secondaryDiagnosis', 'More than one diagnosis', 15)}
          {toggle('intravenousAccess', 'IV line or heparin lock', 20)}
          {toggle('overestimatesAbility', 'Forgets own limits', 15)}
        </div>
        <Field label="Walking aid">
          <RadioGroup
            value={input.ambulatoryAid}
            onChange={(_, d) => setInput({ ...input, ambulatoryAid: d.value as MorseInput['ambulatoryAid'] })}
          >
            {AMBULATORY_AID_OPTIONS.map((o) => (
              <Radio key={o.value} value={o.value} label={`${o.label} (${o.points})`} />
            ))}
          </RadioGroup>
        </Field>
        <Field label="Gait">
          <RadioGroup
            value={input.gait}
            onChange={(_, d) => setInput({ ...input, gait: d.value as MorseInput['gait'] })}
          >
            {GAIT_OPTIONS.map((o) => (
              <Radio key={o.value} value={o.value} label={`${o.label} (${o.points})`} />
            ))}
          </RadioGroup>
        </Field>
      </div>
      <Field label="Notes">
        <Textarea value={notes} onChange={(_, d) => setNotes(d.value)} maxLength={1000} rows={2} />
      </Field>
      <ScorePreview score={score} max={125} />
      <ErrorBar error={save.error} />
      <div>
        <Button
          appearance="primary"
          disabled={save.isPending}
          icon={save.isPending ? <Spinner size="tiny" /> : undefined}
          onClick={() => save.mutate()}
        >
          Save fall risk ({score.score})
        </Button>
      </div>
    </section>
  );
}

function BradenForm({ admissionId, onDone }: { admissionId: string; onDone: () => void }) {
  const styles = useStyles();
  const handlers = useSaveAssessment(admissionId, onDone);
  const [input, setInput] = useState<Partial<BradenInput>>({});
  const [notes, setNotes] = useState('');
  const complete = BRADEN_ITEMS.every((item) => input[item.key] !== undefined);
  const score = complete ? bradenScore(input as BradenInput) : null;
  const save = useMutation({
    mutationFn: () => nursingApi.addBraden(admissionId, { ...(input as BradenInput), notes: notes.trim() || null }),
    ...handlers('Braden score'),
  });

  // Leaving the screen or closing the window with unsaved changes asks first (SRS UX-08).
  useLeaveGuard({ dirty: useValuesChanged({ input, notes }), busy: save.isPending });

  return (
    <section className={styles.form} aria-label="Braden scale">
      <Subtitle2 as="h3">Pressure-injury risk — Braden scale</Subtitle2>
      <Caption1 className={styles.muted}>Choose one answer for each item. Lower scores mean higher risk.</Caption1>
      <div className={styles.items}>
        {BRADEN_ITEMS.map((item) => (
          <Field key={item.key} label={item.label} required>
            <RadioGroup
              value={input[item.key] ? String(input[item.key]) : ''}
              onChange={(_, d) => setInput({ ...input, [item.key]: Number(d.value) })}
            >
              {item.options.map((option, index) => (
                <Radio key={option} value={String(index + 1)} label={`${index + 1} · ${option}`} />
              ))}
            </RadioGroup>
          </Field>
        ))}
      </div>
      <Field label="Notes">
        <Textarea value={notes} onChange={(_, d) => setNotes(d.value)} maxLength={1000} rows={2} />
      </Field>
      {score ? (
        <ScorePreview score={score} max={23} />
      ) : (
        <Caption1 className={styles.muted}>
          {BRADEN_ITEMS.filter((i) => input[i.key] === undefined).length} items left to answer.
        </Caption1>
      )}
      <ErrorBar error={save.error} />
      <div>
        <Button
          appearance="primary"
          disabled={!complete || save.isPending}
          icon={save.isPending ? <Spinner size="tiny" /> : undefined}
          onClick={() => save.mutate()}
        >
          Save Braden score{score ? ` (${score.score})` : ''}
        </Button>
      </div>
    </section>
  );
}

function PainForm({ admissionId, onDone }: { admissionId: string; onDone: () => void }) {
  const styles = useStyles();
  const handlers = useSaveAssessment(admissionId, onDone);
  const [scale, setScale] = useState<PainScaleKind>('Numeric');
  const [value, setValue] = useState<number | null>(null);
  const [location, setLocation] = useState('');
  const [character, setCharacter] = useState('');
  const [notes, setNotes] = useState('');
  const options = painScoreOptions(scale);
  const score = value !== null && options.includes(value) ? painScore(value) : null;
  const save = useMutation({
    mutationFn: () =>
      nursingApi.addPain(admissionId, {
        score: value ?? 0,
        scale,
        location: location.trim() || null,
        character: character.trim() || null,
        notes: notes.trim() || null,
      }),
    ...handlers('Pain score'),
  });

  // Leaving the screen or closing the window with unsaved changes asks first (SRS UX-08).
  useLeaveGuard({ dirty: useValuesChanged({ scale, value, location, character, notes }), busy: save.isPending });

  return (
    <section className={styles.form} aria-label="Pain assessment">
      <Subtitle2 as="h3">Pain</Subtitle2>
      <Field label="Scale">
        <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
          {PAIN_SCALES.map((s) => (
            <ToggleButton
              key={s.value}
              checked={scale === s.value}
              title={s.hint}
              onClick={() => {
                setScale(s.value);
                if (value !== null && !painScoreOptions(s.value).includes(value)) {
                  setValue(null);
                }
              }}
            >
              {s.label}
            </ToggleButton>
          ))}
        </div>
      </Field>
      <Field label="Score (0 = no pain, 10 = worst pain)" required>
        <div className={styles.painScores} role="radiogroup" aria-label="Pain score">
          {options.map((n) => (
            <Button
              key={n}
              role="radio"
              aria-checked={value === n}
              className={styles.painButton}
              appearance={value === n ? 'primary' : 'secondary'}
              onClick={() => setValue(n)}
            >
              {n}
            </Button>
          ))}
        </div>
      </Field>
      <div className={styles.items}>
        <Field label="Where">
          <Input
            value={location}
            onChange={(_, d) => setLocation(d.value)}
            maxLength={100}
            placeholder="e.g. Surgical wound, lower back"
          />
        </Field>
        <Field label="What it feels like">
          <Input
            value={character}
            onChange={(_, d) => setCharacter(d.value)}
            maxLength={100}
            placeholder="e.g. Throbbing, burning"
          />
        </Field>
      </div>
      <Field label="Notes">
        <Textarea value={notes} onChange={(_, d) => setNotes(d.value)} maxLength={1000} rows={2} />
      </Field>
      {score && <ScorePreview score={score} max={10} />}
      <ErrorBar error={save.error} />
      <div>
        <Button
          appearance="primary"
          disabled={score === null || save.isPending}
          icon={save.isPending ? <Spinner size="tiny" /> : undefined}
          onClick={() => save.mutate()}
        >
          Save pain score{score ? ` (${score.score})` : ''}
        </Button>
      </div>
    </section>
  );
}
