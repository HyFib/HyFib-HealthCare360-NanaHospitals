import { describe, expect, it } from 'vitest';
import {
  blankDevice,
  deviceFromCharge,
  mvpiDraftProblems,
  mvpiMissing,
  mvpiSeriousnessNames,
  mvpiSeriousnessValue,
  type MvpiDevice,
} from './mvpi';

describe('a device adverse event report to MvPI', () => {
  const today = '2026-11-06';
  const knee: MvpiDevice = {
    ...blankDevice(),
    name: 'Attune',
    manufacturer: 'DePuy Synthes',
    lotNumber: 'KN2407',
    implantable: true,
    implantedOn: '2026-09-25',
    keptAt: 'Biomedical quarantine',
  };
  const draft = {
    patientId: 'p1',
    noPatient: false,
    eventOn: '2026-11-04',
    awareOn: '2026-11-05',
    outcome: 'NotRecovered' as const,
    seriousness: ['Hospitalisation'],
    deathOn: '',
    device: knee,
    today,
  };
  const sendable = {
    patientId: 'p1',
    noPatient: false,
    location: 'Ward 3',
    description: 'The tibial component loosened.',
    seriousness: ['Hospitalisation'],
    deathOn: '',
    device: knee,
    reporterDesignation: 'Quality manager',
  };

  it('keeps a draft’s facts together: dates in order, death with a fatal outcome, a patient or none', () => {
    expect(Object.values(mvpiDraftProblems(draft)).every((p) => p === null)).toBe(true);
    expect(mvpiDraftProblems({ ...draft, eventOn: '2026-11-07' }).eventOn).not.toBeNull();
    expect(mvpiDraftProblems({ ...draft, awareOn: '2026-11-03' }).awareOn).not.toBeNull();
    expect(mvpiDraftProblems({ ...draft, outcome: 'Fatal' }).outcome).not.toBeNull();
    expect(mvpiDraftProblems({ ...draft, outcome: 'Fatal', seriousness: ['Death'] }).outcome).toBeNull();
    expect(mvpiDraftProblems({ ...draft, noPatient: true }).patient).not.toBeNull();
    expect(mvpiDraftProblems({ ...draft, device: { ...knee, implantedOn: '2026-11-05' } }).implantedOn).not.toBeNull();
    expect(mvpiDraftProblems({ ...draft, device: { ...knee, explantedOn: '2026-09-01' } }).explantedOn).not.toBeNull();
  });

  it('lists what MvPI needs before the report can be sent', () => {
    expect(mvpiMissing(sendable)).toEqual([]);
    expect(
      mvpiMissing({
        ...sendable,
        patientId: null,
        description: ' ',
        seriousness: ['Death'],
        device: { ...blankDevice(), implantable: true },
        reporterDesignation: '',
      }),
    ).toEqual([
      'the patient, or that no patient was involved',
      'what happened',
      'the date of death',
      "the device's brand or trade name",
      "the device's manufacturer",
      "the device's model, catalogue, serial or lot number, or its UDI",
      'the date it was implanted',
      'where the device is kept',
      "the reporter's designation",
    ]);
    expect(mvpiMissing({ ...sendable, patientId: null, noPatient: true })).toEqual([]);
    expect(
      mvpiMissing({ ...sendable, device: { ...knee, availability: 'ReturnedToManufacturer', keptAt: null } }),
    ).toEqual(['the date it went back to the manufacturer']);
  });

  it('fills the device in from what the bill says of an implant', () => {
    const filled = deviceFromCharge(
      {
        description: 'Implant: Knee implant system',
        brand: 'Attune',
        manufacturer: 'DePuy Synthes',
        specification: 'Primary total knee, size 5',
        batch: 'KN2407',
        serial: 'SN-A1',
        isImplant: true,
        usedOn: '2026-09-25',
      },
      blankDevice(),
    );
    expect(filled).toMatchObject({
      name: 'Attune',
      genericName: 'Knee implant system: Primary total knee, size 5',
      manufacturer: 'DePuy Synthes',
      lotNumber: 'KN2407',
      serialNumber: 'SN-A1',
      implantable: true,
      implantedOn: '2026-09-25',
      use: 'SingleUse',
    });
  });

  it('sends the seriousness as the server names it', () => {
    expect(mvpiSeriousnessValue([])).toBe('None');
    expect(mvpiSeriousnessValue(['Death', 'Other'])).toBe('Death, Other');
    expect(mvpiSeriousnessNames('Hospitalisation, RequiredIntervention')).toEqual([
      'Hospitalisation',
      'RequiredIntervention',
    ]);
    expect(mvpiSeriousnessNames('None')).toEqual([]);
  });
});
