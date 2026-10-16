/**
 * Search over the KPI and report definitions (BI-01..05). Every word must appear somewhere in the entry — its name,
 * formula, data source or the report it drills into — so "occupancy beds" or "tat median" narrow the list.
 */
import type { KpiDefinition, KpiDefinitions, KpiUnit, ReportDefinition } from './api';

export const kpiUnitLabel: Record<KpiUnit, string> = {
  count: 'Count',
  percent: 'Percentage',
  days: 'Days',
  minutes: 'Minutes',
  inr: 'Rupees',
  score: 'Score (−100 to 100)',
};

function words(query: string | undefined): string[] {
  return (query ?? '').toLowerCase().split(/\s+/).filter(Boolean);
}

function kpiText(kpi: KpiDefinition): string {
  return `${kpi.label} ${kpi.key} ${kpi.formula} ${kpi.source} ${kpi.reportTitle ?? ''} ${kpiUnitLabel[kpi.unit]}`.toLowerCase();
}

function reportText(report: ReportDefinition): string {
  return `${report.title} ${report.description} ${report.definition} ${report.categoryLabel} ${report.id.replaceAll('-', ' ')}`.toLowerCase();
}

/** The KPIs and reports that match every word of the search, in their original order. */
export function filterDefinitions(definitions: KpiDefinitions, query: string | undefined): KpiDefinitions {
  const terms = words(query);
  if (terms.length === 0) {
    return definitions;
  }
  return {
    kpis: definitions.kpis.filter((k) => terms.every((t) => kpiText(k).includes(t))),
    reports: definitions.reports.filter((r) => terms.every((t) => reportText(r).includes(t))),
  };
}

/** "Higher is better" or "Lower is better", with the target when the hospital has set one. */
export function kpiDirection(kpi: Pick<KpiDefinition, 'higherIsBetter' | 'target'>): string {
  const direction = kpi.higherIsBetter ? 'Higher is better' : 'Lower is better';
  return kpi.target ? `${direction} · target ${kpi.target}` : direction;
}
