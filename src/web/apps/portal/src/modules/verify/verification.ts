/**
 * Reading what a person scanned, pasted or typed to check a printed document (pure; unit-tested).
 * Printed QR codes hold the API address, e.g. https://host/api/v1/public/prescriptions/verify/{token} or
 * https://host/api/v1/lab/reports/verify/{code}; portal links look like /portal/verify/{kind}/{code}.
 */

export const VERIFY_KINDS = ['prescription', 'lab', 'radiology', 'cardiology'] as const;
export type VerifyKind = (typeof VERIFY_KINDS)[number];

/** Report codes: 20 symbols without 0/O/1/I (so they can be typed from paper without mistakes). */
const REPORT_CODE = /^[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{20}$/;
/** Prescription tokens: 16–64 URL-safe characters (case matters). */
const PRESCRIPTION_TOKEN = /^[A-Za-z0-9_-]{16,64}$/;

const PATH_PATTERNS: [RegExp, VerifyKind][] = [
  [/\/api\/v1\/public\/prescriptions\/verify\/([^/?#]+)/i, 'prescription'],
  [/\/api\/v1\/lab\/reports\/verify\/([^/?#]+)/i, 'lab'],
  [/\/api\/v1\/radiology\/reports\/verify\/([^/?#]+)/i, 'radiology'],
  [/\/api\/v1\/cardiology\/reports\/verify\/([^/?#]+)/i, 'cardiology'],
];

export function isVerifyKind(value: unknown): value is VerifyKind {
  return typeof value === 'string' && (VERIFY_KINDS as readonly string[]).includes(value);
}

/** Uppercases a typed report code and drops spaces and dashes people add while copying. */
export function normalizeReportCode(value: string): string {
  return value.replace(/[\s-]/g, '').toUpperCase();
}

export function isWellFormedCode(kind: VerifyKind, value: string): boolean {
  return kind === 'prescription' ? PRESCRIPTION_TOKEN.test(value) : REPORT_CODE.test(value);
}

export type VerificationInput = { kind: VerifyKind; code: string } | { error: 'empty' | 'unrecognised' };

function safeDecode(value: string): string {
  try {
    return decodeURIComponent(value);
  } catch {
    return value;
  }
}

/**
 * Works out which document and code were given. A link identifies the kind by itself; a bare code uses the kind the
 * person selected.
 */
export function parseVerificationInput(input: string, selectedKind: VerifyKind): VerificationInput {
  const text = input.trim();
  if (!text) {
    return { error: 'empty' };
  }

  for (const [pattern, kind] of PATH_PATTERNS) {
    const match = pattern.exec(text);
    if (match?.[1]) {
      const code = kind === 'prescription' ? safeDecode(match[1]) : normalizeReportCode(safeDecode(match[1]));
      return isWellFormedCode(kind, code) ? { kind, code } : { error: 'unrecognised' };
    }
  }

  const portal = /\/verify\/([a-z]+)\/([^/?#]+)/i.exec(text);
  if (portal?.[1] && portal[2]) {
    const kind = portal[1].toLowerCase();
    if (isVerifyKind(kind)) {
      const code = kind === 'prescription' ? safeDecode(portal[2]) : normalizeReportCode(safeDecode(portal[2]));
      return isWellFormedCode(kind, code) ? { kind, code } : { error: 'unrecognised' };
    }
  }

  if (/^[a-z]+:\/\//i.test(text) || text.includes('/')) {
    return { error: 'unrecognised' };
  }

  const code = selectedKind === 'prescription' ? text : normalizeReportCode(text);
  return isWellFormedCode(selectedKind, code) ? { kind: selectedKind, code } : { error: 'unrecognised' };
}

/** The portal page that shows the answer for a code (used in QR codes the portal prints). */
export function verificationPagePath(kind: VerifyKind, code: string): string {
  return `/verify/${kind}/${encodeURIComponent(code)}`;
}
