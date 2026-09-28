import { Caption1, makeStyles, tokens } from '@fluentui/react-components';
import type { NpsResult } from './api';
import { arcPoint, npsAngle, npsBand, npsText } from './rules';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '8px' },
  split: { display: 'flex', width: '100%', maxWidth: '280px', height: '10px', borderRadius: '5px', overflow: 'hidden' },
  legend: { display: 'flex', gap: '12px', flexWrap: 'wrap', justifyContent: 'center', color: tokens.colorNeutralForeground2 },
  dot: { display: 'inline-block', width: '8px', height: '8px', borderRadius: '50%', marginRight: '4px' },
});

const CX = 120;
const CY = 120;
const R = 96;

/** Score bands on the dial: poor (below 0), needs improvement (0–30), good (30–50), excellent (50+). */
const BANDS = [
  { from: -100, to: 0, color: tokens.colorPaletteRedBackground3 },
  { from: 0, to: 30, color: tokens.colorPaletteMarigoldBackground3 },
  { from: 30, to: 50, color: tokens.colorPaletteLightGreenBackground3 },
  { from: 50, to: 100, color: tokens.colorPaletteGreenBackground3 },
];

const SPLIT = {
  promoters: tokens.colorPaletteGreenBackground3,
  passives: tokens.colorPaletteMarigoldBackground3,
  detractors: tokens.colorPaletteRedBackground3,
};

function arc(from: number, to: number): string {
  const start = arcPoint(CX, CY, R, npsAngle(from));
  const end = arcPoint(CX, CY, R, npsAngle(to));
  return `M ${start.x} ${start.y} A ${R} ${R} 0 0 1 ${end.x} ${end.y}`;
}

function percent(part: number, total: number): number {
  return total === 0 ? 0 : Math.round((part * 100) / total);
}

/** Half-dial Net Promoter Score gauge (−100 to +100) with the promoter / passive / detractor split underneath. */
export function NpsGauge({ nps, size = 240 }: { nps: NpsResult; size?: number }) {
  const styles = useStyles();
  const score = nps.score;
  const band = npsBand(score);
  const needle = score === null ? null : arcPoint(CX, CY, R - 22, npsAngle(score));
  const label = score === null ? 'Net promoter score: not enough answers yet' : `Net promoter score ${npsText(score)}, ${band.label}`;

  return (
    <div className={styles.root}>
      <svg viewBox="0 0 240 140" width={size} height={(size * 140) / 240} role="img" aria-label={label}>
        {BANDS.map((b) => (
          <path key={b.from} d={arc(b.from, b.to)} fill="none" stroke={b.color} strokeWidth={18} />
        ))}
        {needle && (
          <>
            <line x1={CX} y1={CY} x2={needle.x} y2={needle.y} stroke={tokens.colorNeutralForeground1} strokeWidth={3} strokeLinecap="round" />
            <circle cx={CX} cy={CY} r={6} fill={tokens.colorNeutralForeground1} />
          </>
        )}
        <text x={CX} y={CY - 30} textAnchor="middle" fontSize={30} fontWeight={600} fill={tokens.colorNeutralForeground1}>
          {npsText(score)}
        </text>
        <text x={14} y={138} fontSize={11} fill={tokens.colorNeutralForeground3}>
          −100
        </text>
        <text x={226} y={138} fontSize={11} textAnchor="end" fill={tokens.colorNeutralForeground3}>
          +100
        </text>
      </svg>
      <Caption1>
        <strong>{band.label}</strong>
        {nps.responses > 0 && ` · ${nps.responses.toLocaleString('en-IN')} answers`}
      </Caption1>
      {nps.responses > 0 && (
        <>
          <div className={styles.split} aria-hidden>
            <span style={{ width: `${percent(nps.promoters, nps.responses)}%`, background: SPLIT.promoters }} />
            <span style={{ width: `${percent(nps.passives, nps.responses)}%`, background: SPLIT.passives }} />
            <span style={{ width: `${percent(nps.detractors, nps.responses)}%`, background: SPLIT.detractors }} />
          </div>
          <div className={styles.legend}>
            <Caption1>
              <span className={styles.dot} style={{ background: SPLIT.promoters }} />
              Promoters (9–10) {percent(nps.promoters, nps.responses)}%
            </Caption1>
            <Caption1>
              <span className={styles.dot} style={{ background: SPLIT.passives }} />
              Passives (7–8) {percent(nps.passives, nps.responses)}%
            </Caption1>
            <Caption1>
              <span className={styles.dot} style={{ background: SPLIT.detractors }} />
              Detractors (0–6) {percent(nps.detractors, nps.responses)}%
            </Caption1>
          </div>
        </>
      )}
    </div>
  );
}
