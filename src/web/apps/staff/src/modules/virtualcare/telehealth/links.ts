/**
 * Links into the EMR (built by another team). They are plain addresses, not typed routes, so this module does not
 * depend on the EMR route tree; align them here when the EMR screens land. Only ids go into the address.
 */
export const emrLinks = {
  chart: (patientId: string) => `/emr/patients/${encodeURIComponent(patientId)}`,
  prescribe: (patientId: string, visitId: string) =>
    `/emr/patients/${encodeURIComponent(patientId)}/prescriptions/new?visitId=${encodeURIComponent(visitId)}`,
};
