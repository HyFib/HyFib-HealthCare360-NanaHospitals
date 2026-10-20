/**
 * Released lab, imaging and cardiology reports on the patient's record (IMG-02, IMG-03): which report a timeline result
 * or a completed order line opens, and who may open it. The chart opens a report through its department's endpoints,
 * which check the user's permissions and the restricted-record guard again.
 */

import { usePermission } from '@/lib/permissions';
import { CARDIOLOGY_STUDY_READERS } from '@/modules/cardiology/api';
import { LAB_PERMISSIONS } from '@/modules/lab/api';
import { RADIOLOGY_PERMISSIONS } from '@/modules/radiology/api';
import type { DiagnosticKind } from './api';
import type { OrderItemDto } from './ordersApi';
import type { TimelineEntry } from './timeline';

/** A released report: the department that made it and the report's id there (not an imaging or cardiology study's). */
export interface ReleasedResult {
  kind: DiagnosticKind;
  reportId: string;
  title: string;
  /** False when not known (an order line): the report itself shows its critical findings. */
  hasCriticalFindings: boolean;
  releasedAt: string | null;
}

/**
 * Who may open each department's reports, as its endpoints check: lab reports; imaging studies and their reports (the
 * permissions both allow); cardiology studies, which carry their report.
 */
export const RESULT_READERS: Record<DiagnosticKind, string | readonly string[]> = {
  Laboratory: LAB_PERMISSIONS.read,
  Radiology: [RADIOLOGY_PERMISSIONS.read, RADIOLOGY_PERMISSIONS.write],
  Cardiology: CARDIOLOGY_STUDY_READERS,
};

/** Whether the signed-in user may open each department's reports. */
export function useResultReaders(): Record<DiagnosticKind, boolean> {
  const laboratory = usePermission(RESULT_READERS.Laboratory);
  const radiology = usePermission(RESULT_READERS.Radiology);
  const cardiology = usePermission(RESULT_READERS.Cardiology);
  return { Laboratory: laboratory, Radiology: radiology, Cardiology: cardiology };
}

/** The report a timeline entry stands for: a result names its report and the report's department; others, none. */
export function timelineResult(entry: TimelineEntry): ReleasedResult | null {
  if (entry.type !== 'Result' || !entry.reportKind) {
    return null;
  }
  return {
    kind: entry.reportKind,
    reportId: entry.id,
    title: entry.title,
    hasCriticalFindings: entry.isCritical,
    releasedAt: entry.occurredAt,
  };
}

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

const REPORTED: Partial<Record<OrderItemDto['category'], DiagnosticKind>> = {
  Laboratory: 'Laboratory',
  Radiology: 'Radiology',
  Cardiology: 'Cardiology',
};

/**
 * The report of a completed lab, imaging or cardiology order line. The department completes the line when it releases
 * the report, with the report's id as the line's result reference; a line still pending, or completed with a reference
 * typed by hand, opens nothing.
 */
export function orderLineResult(
  item: Pick<OrderItemDto, 'category' | 'status' | 'resultReference' | 'serviceName' | 'completedAt'>,
): ReleasedResult | null {
  const kind = REPORTED[item.category];
  const reference = item.resultReference?.trim() ?? '';
  if (!kind || item.status !== 'Completed' || !GUID.test(reference)) {
    return null;
  }
  return {
    kind,
    reportId: reference.toLowerCase(),
    title: item.serviceName,
    hasCriticalFindings: false,
    releasedAt: item.completedAt,
  };
}
