import { Button, Caption1, Field, Rating, Textarea, makeStyles, tokens } from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { aspectQuestion, ratingWord, type FeedbackAspect } from './labels';
import { npsGroup, type FeedbackAnswers } from './rules';

const useStyles = makeStyles({
  aspect: { display: 'flex', alignItems: 'center', gap: '12px', flexWrap: 'wrap' },
  word: { minWidth: '72px', color: tokens.colorNeutralForeground2 },
  nps: { display: 'grid', gridTemplateColumns: 'repeat(11, minmax(32px, 1fr))', gap: '4px', maxWidth: '520px' },
  npsButton: { minWidth: '32px', paddingLeft: 0, paddingRight: 0 },
  npsScale: { display: 'flex', justifyContent: 'space-between', maxWidth: '520px', color: tokens.colorNeutralForeground3 },
});

/** One 1–5 star question with the word for the chosen rating and a way to clear it. */
export function AspectRating({
  aspect,
  value,
  onChange,
  disabled,
}: {
  aspect: FeedbackAspect;
  value: number | null | undefined;
  onChange: (value: number | null) => void;
  disabled?: boolean;
}) {
  const styles = useStyles();
  return (
    <Field label={aspectQuestion[aspect]}>
      <div className={styles.aspect}>
        <Rating
          value={value ?? 0}
          onChange={(_, data) => {
            if (!disabled) {
              onChange(data.value);
            }
          }}
          max={5}
          color="marigold"
          size="large"
          itemLabel={(n) => `${n} out of 5, ${ratingWord(n)}`}
        />
        <Caption1 className={styles.word} aria-live="polite">
          {value ? ratingWord(value) : 'Not answered'}
        </Caption1>
        {value ? (
          <Button size="small" appearance="subtle" icon={<DismissRegular />} onClick={() => onChange(null)} disabled={disabled}>
            Clear
          </Button>
        ) : null}
      </div>
    </Field>
  );
}

const NPS_COLORS = {
  Detractor: tokens.colorPaletteRedBackground3,
  Passive: tokens.colorPaletteMarigoldBackground3,
  Promoter: tokens.colorPaletteGreenBackground3,
} as const;

/** "How likely are you to recommend us?" 0–10, as a radio group of number buttons. */
export function NpsPicker({
  value,
  onChange,
  disabled,
  validationMessage,
}: {
  value: number | null;
  onChange: (value: number | null) => void;
  disabled?: boolean;
  validationMessage?: string;
}) {
  const styles = useStyles();
  return (
    <Field
      label="How likely is the patient to recommend the hospital to family and friends?"
      validationMessage={validationMessage}
      validationState={validationMessage ? 'error' : 'none'}
    >
      <div role="radiogroup" aria-label="Recommendation score from 0 to 10" className={styles.nps}>
        {Array.from({ length: 11 }, (_, score) => {
          const selected = value === score;
          return (
            <Button
              key={score}
              role="radio"
              aria-checked={selected}
              className={styles.npsButton}
              appearance={selected ? 'primary' : 'outline'}
              disabled={disabled}
              style={selected ? { backgroundColor: NPS_COLORS[npsGroup(score)], color: tokens.colorNeutralForegroundOnBrand } : undefined}
              onClick={() => onChange(selected ? null : score)}
            >
              {score}
            </Button>
          );
        })}
      </div>
      <div className={styles.npsScale}>
        <Caption1>0 · Not at all likely</Caption1>
        <Caption1>10 · Extremely likely</Caption1>
      </div>
    </Field>
  );
}

/** The questions of a feedback form: star ratings per aspect, the 0–10 recommendation score and a comment. */
export function FeedbackQuestions({
  aspects,
  includesNps,
  allowsComment,
  answers,
  onChange,
  disabled,
  errors = {},
}: {
  aspects: readonly FeedbackAspect[];
  includesNps: boolean;
  allowsComment: boolean;
  answers: FeedbackAnswers;
  onChange: (answers: FeedbackAnswers) => void;
  disabled?: boolean;
  errors?: Partial<Record<'ratings' | 'nps' | 'comment', string>>;
}) {
  return (
    <>
      {aspects.map((aspect) => (
        <AspectRating
          key={aspect}
          aspect={aspect}
          value={answers.ratings[aspect]}
          disabled={disabled}
          onChange={(value) => onChange({ ...answers, ratings: { ...answers.ratings, [aspect]: value } })}
        />
      ))}
      {errors.ratings && (
        <Caption1 role="alert" style={{ color: tokens.colorPaletteRedForeground1 }}>
          {errors.ratings}
        </Caption1>
      )}
      {includesNps && <NpsPicker value={answers.nps} onChange={(nps) => onChange({ ...answers, nps })} disabled={disabled} validationMessage={errors.nps} />}
      {allowsComment && (
        <Field
          label="Comments in the patient's own words"
          hint="Do not add clinical details; the comment is read by the quality team."
          validationMessage={errors.comment}
          validationState={errors.comment ? 'error' : 'none'}
        >
          <Textarea
            value={answers.comment}
            onChange={(_, d) => onChange({ ...answers, comment: d.value })}
            rows={3}
            maxLength={2000}
            disabled={disabled}
            resize="vertical"
          />
        </Field>
      )}
    </>
  );
}
