/** Quality (IRMS) enums exactly as the server sends them, with plain words for the screens. */

export const QUALITY_PERMISSIONS = {
  incidentsReport: 'quality.incidents.report',
  incidentsManage: 'quality.incidents.manage',
  capaManage: 'quality.capa.manage',
  risksManage: 'quality.risks.manage',
  indicatorsManage: 'quality.indicators.manage',
} as const;

const Q = QUALITY_PERMISSIONS;

/** Any of these opens the quality dashboard. */
export const QUALITY_MANAGERS = [Q.incidentsManage, Q.capaManage, Q.risksManage, Q.indicatorsManage] as const;

export const INCIDENT_TYPES = [
  'Clinical',
  'MedicationError',
  'PatientFall',
  'NeedleStick',
  'EquipmentFailure',
  'Security',
  'Fire',
  'Complaint',
  'NearMiss',
  'AdverseDrugReaction',
  'HospitalAcquiredInfection',
  'Other',
] as const;
export type IncidentType = (typeof INCIDENT_TYPES)[number];
export const incidentTypeLabel: Record<IncidentType, string> = {
  Clinical: 'Clinical care',
  MedicationError: 'Medication error',
  PatientFall: 'Patient fall',
  NeedleStick: 'Needle-stick or sharps injury',
  EquipmentFailure: 'Equipment failure',
  Security: 'Security (theft, violence, missing person)',
  Fire: 'Fire or electrical',
  Complaint: 'Complaint about care',
  NearMiss: 'Near miss',
  AdverseDrugReaction: 'Adverse drug reaction',
  HospitalAcquiredInfection: 'Hospital-acquired infection',
  Other: 'Something else',
};

export const INCIDENT_SEVERITIES = ['NearMiss', 'NoHarm', 'Mild', 'Moderate', 'Severe', 'Death'] as const;
export type IncidentSeverity = (typeof INCIDENT_SEVERITIES)[number];
export const severityLabel: Record<IncidentSeverity, string> = {
  NearMiss: 'Near miss',
  NoHarm: 'No harm',
  Mild: 'Mild harm',
  Moderate: 'Moderate harm',
  Severe: 'Severe harm',
  Death: 'Death',
};
export const severityHelp: Record<IncidentSeverity, string> = {
  NearMiss: 'Caught before it reached the patient or staff member',
  NoHarm: 'It happened, but nobody was hurt',
  Mild: 'Minor harm, little or no extra treatment',
  Moderate: 'Needed extra treatment or a longer stay',
  Severe: 'Permanent harm or life-threatening',
  Death: 'The event caused or contributed to a death',
};

export const SENTINEL_KINDS = [
  'WrongSiteSurgery',
  'WrongPatient',
  'WrongProcedure',
  'RetainedForeignObject',
  'InfantAbductionOrWrongDischarge',
  'PatientSuicide',
  'AboIncompatibleTransfusion',
  'SexualAssault',
  'UnexpectedDeath',
  'Other',
] as const;
export type SentinelEventKind = (typeof SENTINEL_KINDS)[number];
export const sentinelKindLabel: Record<SentinelEventKind, string> = {
  WrongSiteSurgery: 'Surgery on the wrong site',
  WrongPatient: 'Procedure on the wrong patient',
  WrongProcedure: 'Wrong procedure',
  RetainedForeignObject: 'Object left in the body after surgery',
  InfantAbductionOrWrongDischarge: 'Infant abduction or discharge to the wrong family',
  PatientSuicide: 'Patient suicide or attempt',
  AboIncompatibleTransfusion: 'ABO-incompatible blood transfusion',
  SexualAssault: 'Sexual assault',
  UnexpectedDeath: 'Unexpected death',
  Other: 'Other never event',
};

export const INCIDENT_STATUSES = ['Reported', 'UnderReview', 'Investigation', 'CapaInProgress', 'Closed'] as const;
export type IncidentStatus = (typeof INCIDENT_STATUSES)[number];
export const incidentStatusLabel: Record<IncidentStatus, string> = {
  Reported: 'Reported',
  UnderReview: 'Under review',
  Investigation: 'Investigation',
  CapaInProgress: 'Actions in progress',
  Closed: 'Closed',
};

export const RCA_METHODS = ['FiveWhys', 'Fishbone'] as const;
export type RcaMethod = (typeof RCA_METHODS)[number];
export const rcaMethodLabel: Record<RcaMethod, string> = {
  FiveWhys: '5 whys',
  Fishbone: 'Fishbone (cause and effect)',
};

export const FACTOR_CATEGORIES = ['People', 'Process', 'Equipment', 'Environment', 'Communication', 'Policy'] as const;
export type ContributingFactorCategory = (typeof FACTOR_CATEGORIES)[number];

export type InvestigationStatus = 'InProgress' | 'Completed';

export const CAPA_TYPES = ['Corrective', 'Preventive'] as const;
export type CapaType = (typeof CAPA_TYPES)[number];

export const CAPA_STATUSES = ['Open', 'InProgress', 'Completed', 'Verified', 'Cancelled'] as const;
export type CapaStatus = (typeof CAPA_STATUSES)[number];
export const capaStatusLabel: Record<CapaStatus, string> = {
  Open: 'Open',
  InProgress: 'In progress',
  Completed: 'Awaiting check',
  Verified: 'Verified effective',
  Cancelled: 'Cancelled',
};

export const CAPA_SOURCES = ['Incident', 'Risk', 'Audit', 'Complaint', 'IndicatorBreach', 'Other'] as const;
export type CapaSource = (typeof CAPA_SOURCES)[number];
export const capaSourceLabel: Record<CapaSource, string> = {
  Incident: 'Incident',
  Risk: 'Risk register',
  Audit: 'Audit finding',
  Complaint: 'Complaint',
  IndicatorBreach: 'Quality indicator off target',
  Other: 'Other',
};

export const RISK_CATEGORIES = [
  'Clinical',
  'PatientSafety',
  'InfectionControl',
  'Operational',
  'Financial',
  'Compliance',
  'FireAndSafety',
  'Environmental',
  'InformationSecurity',
  'Reputational',
  'Strategic',
] as const;
export type RiskCategory = (typeof RISK_CATEGORIES)[number];

export const RISK_RATINGS = ['Low', 'Medium', 'High', 'Extreme'] as const;
export type RiskRating = (typeof RISK_RATINGS)[number];

export const RISK_STATUSES = ['Open', 'Mitigating', 'Accepted', 'Closed'] as const;
export type RiskStatus = (typeof RISK_STATUSES)[number];
export const riskStatusLabel: Record<RiskStatus, string> = {
  Open: 'Open',
  Mitigating: 'Being reduced',
  Accepted: 'Accepted (within appetite)',
  Closed: 'Closed',
};

export const LIKELIHOOD_LABELS = ['Rare', 'Unlikely', 'Possible', 'Likely', 'Almost certain'] as const;
export const IMPACT_LABELS = ['Negligible', 'Minor', 'Moderate', 'Major', 'Catastrophic'] as const;

export const INDICATOR_UNITS = ['Percent', 'PerThousand', 'Minutes', 'Hours', 'Days', 'Ratio', 'Count'] as const;
export type IndicatorUnit = (typeof INDICATOR_UNITS)[number];
export const indicatorUnitLabel: Record<IndicatorUnit, string> = {
  Percent: 'Percentage (%)',
  PerThousand: 'Per 1,000',
  Minutes: 'Minutes',
  Hours: 'Hours',
  Days: 'Days',
  Ratio: 'Ratio',
  Count: 'Count',
};

export const INDICATOR_DIRECTIONS = ['HigherIsBetter', 'LowerIsBetter'] as const;
export type IndicatorDirection = (typeof INDICATOR_DIRECTIONS)[number];

export const INDICATOR_FREQUENCIES = ['Monthly', 'Quarterly', 'Annually'] as const;
export type IndicatorFrequency = (typeof INDICATOR_FREQUENCIES)[number];

export type TrendDirection = 'InsufficientData' | 'Improving' | 'Stable' | 'Worsening';
