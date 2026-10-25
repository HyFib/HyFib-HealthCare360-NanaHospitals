/** Plain-language labels for radiology enums (pure data). */
import type { DiagnosticReportStatus } from '@/modules/lab/common/types';
import type { AiFindingStatus, FormFIndication, ImageRepeatReason, ImagingStudyStatus, Modality } from './api';

export const modalityLabel: Record<Modality, string> = {
  XR: 'X-ray',
  CT: 'CT scan',
  MR: 'MRI',
  US: 'Ultrasound',
  MG: 'Mammography',
  RF: 'Fluoroscopy',
  DX: 'Digital X-ray',
};

export const studyStatusLabel: Record<ImagingStudyStatus, string> = {
  Ordered: 'Ordered',
  Scheduled: 'Scheduled',
  Arrived: 'Patient arrived',
  InProgress: 'Scanning',
  Acquired: 'Awaiting report',
  Reported: 'Awaiting sign-off',
  Verified: 'Signed, not released',
  Released: 'Released',
  Cancelled: 'Cancelled',
};

export const reportStatusLabel: Record<DiagnosticReportStatus, string> = {
  Draft: 'Draft',
  Reported: 'Awaiting sign-off',
  Verified: 'Signed, not released',
  Released: 'Released',
};

export const repeatReasonLabel: Record<ImageRepeatReason, string> = {
  Positioning: 'Positioning',
  Exposure: 'Exposure',
  PatientMotion: 'Patient moved',
  Artefact: 'Artefact',
  EquipmentFault: 'Equipment fault',
  WrongProtocol: 'Wrong protocol',
  PatientRelated: 'Patient related',
  Other: 'Other reason',
};

export const aiStatusLabel: Record<AiFindingStatus, string> = {
  Preliminary: 'Awaiting review',
  Acknowledged: 'Accepted',
  Dismissed: 'Rejected',
};

/** Form F Section B's indications, as the form words them (server `FormFDocument.Indication`). */
export const formFIndicationLabel: Record<FormFIndication, string> = {
  IntraUterineOrEctopicPregnancy: 'To diagnose intra-uterine and/or ectopic pregnancy and confirm viability',
  GestationalAge: 'Estimation of gestational age (dating)',
  NumberOfFoetusesAndChorionicity: 'Detection of number of foetuses and their chorionicity',
  PregnancyWithIucdOrContraceptiveFailure:
    'Suspected pregnancy with IUCD in situ or after contraceptive or MTP failure',
  VaginalBleedingOrLeaking: 'Vaginal bleeding or leaking',
  FollowUpOfAbortion: 'Follow-up of cases of abortion',
  CervicalCanalAndInternalOs: 'Assessment of cervical canal and diameter of internal os',
  UterineSizeDiscrepancy: 'Discrepancy between uterine size and period of amenorrhoea',
  AdnexalOrUterinePathology: 'Any suspected adnexal or uterine pathology or abnormality',
  ChromosomalOrStructuralAbnormalities:
    'Detection of chromosomal abnormalities, foetal structural defects and other abnormalities',
  FoetalPresentationAndPosition: 'To evaluate foetal presentation and position',
  LiquorAmnii: 'Assessment of liquor amnii',
  PretermLabourOrRupturedMembranes: 'Preterm labour or preterm premature rupture of membranes',
  PlacentalEvaluation: 'Evaluation of placental position, thickness, grading and abnormalities',
  UmbilicalCordEvaluation: 'Evaluation of umbilical cord',
  PreviousCaesareanScars: 'Evaluation of previous caesarean section scars',
  FoetalGrowthAndWellBeing: 'Evaluation of foetal growth parameters, foetal weight and well-being',
  ColourFlowAndDoppler: 'Colour flow mapping and duplex Doppler studies',
  GuidedProcedures: 'Ultrasound-guided procedures (MTP, external cephalic version) and their follow-up',
  AdjunctToInvasiveInterventions: 'Adjunct to invasive interventions (CVS, amniocentesis, foetal blood sampling)',
  IntraPartumEvents: 'Observation of intra-partum events',
  ConditionsComplicatingPregnancy: 'Medical or surgical conditions complicating pregnancy',
  Research: 'Research or scientific studies in recognised institutions',
};
