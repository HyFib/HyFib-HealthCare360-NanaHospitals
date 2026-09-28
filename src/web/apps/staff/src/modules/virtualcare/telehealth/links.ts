/**
 * Links into the EMR. They are plain addresses (opened in a new tab), not typed routes, so this module does not depend
 * on the EMR route tree. Only ids go into the address. Prescriptions are written inside the consultation (encounter)
 * workspace, which the doctor starts or continues from the chart's Encounters tab.
 */
export const emrLinks = {
  chart: (patientId: string) => `/emr/patients/${encodeURIComponent(patientId)}`,
  consultation: (patientId: string) => `/emr/patients/${encodeURIComponent(patientId)}?tab=encounters`,
};
