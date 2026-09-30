import { Caption1, makeStyles, mergeClasses, Text, tokens } from '@fluentui/react-components';
import type { RiskMatrixCell } from './api';
import { IMPACT_LABELS, LIKELIHOOD_LABELS, type RiskRating } from './labels';
import { riskRating, riskScore } from './rules';

const useStyles = makeStyles({
  wrap: { display: 'grid', gridTemplateColumns: 'auto minmax(0, 1fr)', gap: '6px', alignItems: 'stretch' },
  yAxis: {
    writingMode: 'vertical-rl',
    transform: 'rotate(180deg)',
    textAlign: 'center',
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  grid: { display: 'grid', gridTemplateColumns: 'minmax(84px, auto) repeat(5, minmax(56px, 1fr))', gap: '4px' },
  rowLabel: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'flex-end',
    paddingRight: '6px',
    textAlign: 'end',
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },
  colLabel: {
    textAlign: 'center',
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
    paddingTop: '4px',
  },
  cell: {
    minHeight: '54px',
    borderRadius: tokens.borderRadiusMedium,
    border: '2px solid transparent',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    cursor: 'pointer',
    color: tokens.colorNeutralForeground1,
    ':hover': { boxShadow: tokens.shadow8 },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '2px' },
  },
  empty: { opacity: 0.55 },
  selected: { border: `2px solid ${tokens.colorNeutralForeground1}` },
  count: {
    fontSize: tokens.fontSizeBase500,
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: tokens.lineHeightBase500,
  },
  score: { fontSize: tokens.fontSizeBase100 },
  Low: { backgroundColor: tokens.colorPaletteGreenBackground2 },
  Medium: { backgroundColor: tokens.colorPaletteYellowBackground2 },
  High: { backgroundColor: tokens.colorPaletteDarkOrangeBackground2 },
  Extreme: { backgroundColor: tokens.colorPaletteRedBackground2 },
  legend: { display: 'flex', gap: '12px', flexWrap: 'wrap', marginTop: '8px', alignItems: 'center' },
  swatch: {
    display: 'inline-block',
    width: '14px',
    height: '14px',
    borderRadius: '3px',
    marginRight: '4px',
    verticalAlign: 'middle',
  },
});

const RATINGS: RiskRating[] = ['Low', 'Medium', 'High', 'Extreme'];

/**
 * 5 × 5 heat map: likelihood (rows, highest at the top) by impact (columns). Each cell shows how many open risks sit
 * there; the colour is backed by the score and rating in the label, so colour is never the only signal.
 */
export function RiskMatrix({
  cells,
  selected,
  onSelect,
  residual,
}: {
  cells: RiskMatrixCell[];
  selected: { likelihood: number; impact: number } | null;
  onSelect: (cell: { likelihood: number; impact: number } | null) => void;
  residual: boolean;
}) {
  const styles = useStyles();
  const count = (likelihood: number, impact: number) =>
    cells.find((c) => c.likelihood === likelihood && c.impact === impact)?.count ?? 0;

  return (
    <div>
      <div className={styles.wrap}>
        <span className={styles.yAxis}>Likelihood</span>
        <div className={styles.grid} role="grid" aria-label={`${residual ? 'Residual' : 'Inherent'} risk heat map`}>
          {[5, 4, 3, 2, 1].map((likelihood) => (
            <div key={likelihood} role="row" style={{ display: 'contents' }}>
              <span className={styles.rowLabel} role="rowheader">
                {likelihood} · {LIKELIHOOD_LABELS[likelihood - 1]}
              </span>
              {[1, 2, 3, 4, 5].map((impact) => {
                const score = riskScore(likelihood, impact);
                const rating = riskRating(score);
                const n = count(likelihood, impact);
                const isSelected = selected?.likelihood === likelihood && selected.impact === impact;
                return (
                  <button
                    key={impact}
                    type="button"
                    role="gridcell"
                    aria-pressed={isSelected}
                    aria-label={`${LIKELIHOOD_LABELS[likelihood - 1]} likelihood, ${IMPACT_LABELS[impact - 1]} impact: score ${score}, ${rating}. ${n} risk${n === 1 ? '' : 's'}.`}
                    className={mergeClasses(
                      styles.cell,
                      styles[rating],
                      n === 0 && styles.empty,
                      isSelected && styles.selected,
                    )}
                    onClick={() => onSelect(isSelected ? null : { likelihood, impact })}
                  >
                    <span className={styles.count}>{n}</span>
                    <span className={styles.score}>score {score}</span>
                  </button>
                );
              })}
            </div>
          ))}
          <span />
          {[1, 2, 3, 4, 5].map((impact) => (
            <span key={impact} className={styles.colLabel}>
              {impact} · {IMPACT_LABELS[impact - 1]}
            </span>
          ))}
        </div>
      </div>
      <div className={styles.legend}>
        <Caption1>Impact →</Caption1>
        {RATINGS.map((rating) => (
          <Text key={rating} size={200}>
            <span className={mergeClasses(styles.swatch, styles[rating])} aria-hidden />
            {rating} ({rating === 'Low' ? '1–4' : rating === 'Medium' ? '5–9' : rating === 'High' ? '10–16' : '20–25'})
          </Text>
        ))}
      </div>
    </div>
  );
}
