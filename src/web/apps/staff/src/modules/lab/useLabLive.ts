import { useDiagnosticsLive } from './common/live';

const LAB_SCOPE = ['lab'] as const;

/** Keeps a lab screen live: samples, results, reports, QC and outsourcing refresh as the bench works. */
export function useLabLive(onEvent?: (name: string, payload: unknown) => void): void {
  useDiagnosticsLive(LAB_SCOPE, onEvent);
}
