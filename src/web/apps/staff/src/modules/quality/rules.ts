/**
 * Pure quality rules mirrored from the server (IncidentWorkflow, SentinelRules, Capa, RiskMatrix, IndicatorMath) so the
 * screens can guide staff before they press a button. The server remains the authority and checks everything again.
 */
import type {
  CapaStatus,
  IncidentSeverity,
  IncidentStatus,
  IndicatorDirection,
  IndicatorUnit,
  RcaMethod,
  RiskRating,
  SentinelEventKind,
  TrendDirection,
} from './labels';

type Tone = 'danger' | 'warning' | 'success' | 'brand' | 'informative' | 'important';

// ------------------------------------------------------------------ incidents

const SEVERITY_RANK: Record<IncidentSeverity, number> = {
  NearMiss: 0,
  NoHarm: 1,
  Mild: 2,
  Moderate: 3,
  Severe: 4,
  Death: 5,
};

export function severityRank(severity: IncidentSeverity): number {
  return SEVERITY_RANK[severity];
}

export function severityTone(severity: IncidentSeverity): Tone {
  switch (severity) {
    case 'Death':
    case 'Severe':
      return 'danger';
    case 'Moderate':
      return 'warning';
    case 'Mild':
      return 'important';
    default:
      return 'informative';
  }
}

/** Sentinel event: death or severe harm, a never event, or flagged by a person (SentinelRules.IsSentinel). */
export function isSentinel(
  severity: IncidentSeverity,
  kind: SentinelEventKind | null | undefined,
  flagged: boolean,
): boolean {
  return flagged || Boolean(kind) || severity === 'Severe' || severity === 'Death';
}

export const INCIDENT_TRANSITIONS: Record<IncidentStatus, IncidentStatus[]> = {
  Reported: ['UnderReview', 'Closed'],
  UnderReview: ['Investigation', 'CapaInProgress', 'Closed'],
  Investigation: ['CapaInProgress', 'Closed'],
  CapaInProgress: ['Investigation', 'Closed'],
  Closed: ['UnderReview'],
};

/** Closing needs an outcome summary and reopening needs a reason. */
export function transitionNeedsNote(from: IncidentStatus, to: IncidentStatus): boolean {
  return to === 'Closed' || from === 'Closed';
}

/** Button wording for a move in the incident workflow. */
export function transitionLabel(from: IncidentStatus, to: IncidentStatus): string {
  if (from === 'Closed') {
    return 'Reopen';
  }
  switch (to) {
    case 'UnderReview':
      return 'Start review';
    case 'Investigation':
      return from === 'CapaInProgress' ? 'Back to investigation' : 'Start investigation';
    case 'CapaInProgress':
      return 'Move to actions';
    case 'Closed':
      return from === 'Reported' ? 'Close (duplicate or invalid)' : 'Close incident';
    default:
      return to;
  }
}

export interface IncidentProgress {
  isSentinel: boolean;
  rcaCompleted: boolean;
  capaCount: number;
  openCapaCount: number;
}

/**
 * Why a move is not possible yet (null when it is), with the same rules as IncidentWorkflow.CanMove except the note,
 * which the dialog asks for.
 */
export function transitionBlocker(from: IncidentStatus, to: IncidentStatus, p: IncidentProgress): string | null {
  if (!INCIDENT_TRANSITIONS[from].includes(to)) {
    return 'This move is not allowed.';
  }
  if (from === 'Investigation' && (to === 'CapaInProgress' || to === 'Closed') && !p.rcaCompleted) {
    return 'Complete the root-cause analysis first.';
  }
  if (to === 'Closed' && p.isSentinel && !p.rcaCompleted && from !== 'Reported') {
    return 'A sentinel event can be closed only after a completed root-cause analysis.';
  }
  if (to === 'CapaInProgress' && p.capaCount === 0) {
    return 'Add at least one corrective or preventive action first.';
  }
  if (from === 'CapaInProgress' && to === 'Closed' && p.openCapaCount > 0) {
    return `${p.openCapaCount} action(s) are still open.`;
  }
  return null;
}

/** What is still missing before the analysis can be completed (Investigation.Complete). */
export function investigationGaps(input: {
  method: RcaMethod;
  problemStatement: string;
  findings: string;
  whys: string[];
  factors: { description: string; isRootCause: boolean }[];
}): string[] {
  const gaps: string[] = [];
  if (!input.problemStatement.trim() || !input.findings.trim()) {
    gaps.push('Record the problem statement and the findings.');
  }
  if (!input.factors.some((factor) => factor.isRootCause && factor.description.trim())) {
    gaps.push('Mark at least one contributing factor as a root cause.');
  }
  if (input.method === 'FiveWhys' && input.whys.filter((why) => why.trim()).length < 3) {
    gaps.push("Ask 'why?' at least 3 times.");
  }
  if (input.method === 'Fishbone' && input.factors.filter((factor) => factor.description.trim()).length < 2) {
    gaps.push('Record at least 2 contributing factors on the fishbone.');
  }
  return gaps;
}

// ------------------------------------------------------------------ CAPA

export type CapaAction = 'start' | 'complete' | 'verify' | 'cancel' | 'edit';

/** Actions offered on a CAPA (the owner works it; the quality team verifies, but never their own action). */
export function capaActions(status: CapaStatus, who: { isOwner: boolean; isManager: boolean }): CapaAction[] {
  const actions: CapaAction[] = [];
  const open = status === 'Open' || status === 'InProgress';
  if (status === 'Open' && (who.isOwner || who.isManager)) {
    actions.push('start');
  }
  if (open && (who.isOwner || who.isManager)) {
    actions.push('complete');
  }
  if (status === 'Completed' && who.isManager && !who.isOwner) {
    actions.push('verify');
  }
  if (open && who.isManager) {
    actions.push('edit', 'cancel');
  }
  return actions;
}

export function capaTone(status: CapaStatus, isOverdue: boolean): Tone {
  if (isOverdue) {
    return 'danger';
  }
  switch (status) {
    case 'Verified':
      return 'success';
    case 'Completed':
      return 'brand';
    case 'Cancelled':
      return 'informative';
    case 'InProgress':
      return 'brand';
    default:
      return 'warning';
  }
}

// ------------------------------------------------------------------ risk matrix

export const RISK_LEVELS = [1, 2, 3, 4, 5] as const;

export function riskScore(likelihood: number, impact: number): number {
  return likelihood * impact;
}

/** 1–4 Low, 5–9 Medium, 10–16 High, 20–25 Extreme (RiskMatrix.Rate). */
export function riskRating(score: number): RiskRating {
  if (score >= 20) {
    return 'Extreme';
  }
  if (score >= 10) {
    return 'High';
  }
  if (score >= 5) {
    return 'Medium';
  }
  return 'Low';
}

export function ratingTone(rating: RiskRating): Tone {
  switch (rating) {
    case 'Extreme':
      return 'danger';
    case 'High':
      return 'important';
    case 'Medium':
      return 'warning';
    default:
      return 'success';
  }
}

/** Residual risk is scored as a pair and can never exceed the inherent risk. Returns the problem, or null. */
export function residualProblem(
  likelihood: number,
  impact: number,
  residualLikelihood: number | null,
  residualImpact: number | null,
): string | null {
  if ((residualLikelihood === null) !== (residualImpact === null)) {
    return 'Score the residual risk with both likelihood and impact, or leave both empty.';
  }
  if (
    residualLikelihood !== null &&
    residualImpact !== null &&
    riskScore(residualLikelihood, residualImpact) > riskScore(likelihood, impact)
  ) {
    return 'The residual risk (after controls) cannot be higher than the inherent risk.';
  }
  return null;
}

// ------------------------------------------------------------------ indicators

export function defaultMultiplier(unit: IndicatorUnit): number {
  return unit === 'Percent' ? 100 : unit === 'PerThousand' ? 1000 : 1;
}

/** numerator ÷ denominator × multiplier, to 2 decimals (IndicatorMath.Value); null when it cannot be computed. */
export function indicatorValue(
  numerator: number | null,
  denominator: number | null,
  unit: IndicatorUnit,
  multiplier?: number | null,
): number | null {
  if (numerator === null || denominator === null || !(denominator > 0) || numerator < 0) {
    return null;
  }
  const value = (numerator / denominator) * (multiplier ?? defaultMultiplier(unit));
  return Math.round(value * 100) / 100;
}

export function meetsTarget(
  value: number,
  target: number | null | undefined,
  direction: IndicatorDirection,
): boolean | null {
  if (target === null || target === undefined) {
    return null;
  }
  return direction === 'HigherIsBetter' ? value >= target : value <= target;
}

/** "92.5%", "3.2 per 1,000", "12 min". */
export function formatIndicatorValue(value: number | null | undefined, unit: IndicatorUnit): string {
  if (value === null || value === undefined) {
    return '—';
  }
  const number = value.toLocaleString('en-IN', { maximumFractionDigits: 2 });
  switch (unit) {
    case 'Percent':
      return `${number}%`;
    case 'PerThousand':
      return `${number} per 1,000`;
    case 'Minutes':
      return `${number} min`;
    case 'Hours':
      return `${number} h`;
    case 'Days':
      return `${number} day${value === 1 ? '' : 's'}`;
    default:
      return number;
  }
}

export function trendText(trend: TrendDirection): string {
  switch (trend) {
    case 'Improving':
      return 'Improving';
    case 'Worsening':
      return 'Getting worse';
    case 'Stable':
      return 'Stable';
    default:
      return 'Not enough data yet';
  }
}

export function trendTone(trend: TrendDirection): Tone {
  return trend === 'Improving' ? 'success' : trend === 'Worsening' ? 'danger' : 'informative';
}
