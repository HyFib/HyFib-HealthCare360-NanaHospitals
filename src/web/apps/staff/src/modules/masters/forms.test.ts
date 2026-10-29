import { describe, expect, it } from 'vitest';
import type { DepartmentDto, FacilityDto, PractitionerDto, ServiceItemDto } from './api';
import {
  departmentSchema,
  displayNameOf,
  facilitySchema,
  isWebAddress,
  matchesSearch,
  nextDisplayOrder,
  optionsWithCurrent,
  practitionerSchema,
  serviceSchema,
  splitLanguages,
  toDepartmentForm,
  toDepartmentRequest,
  toFacilityForm,
  toFacilityRequest,
  toLetterheadRequest,
  toPractitionerForm,
  toPractitionerRequest,
  toServiceForm,
  toServiceRequest,
  withCurrentItem,
} from './forms';

const facility: FacilityDto = {
  id: 'f1',
  code: 'NH',
  name: 'Nana Hospitals',
  legalName: null,
  address: {
    line1: '12 Anna Salai',
    line2: null,
    city: 'Chennai',
    district: null,
    state: 'Tamil Nadu',
    pinCode: '600002',
    country: 'India',
  },
  phone: '+919876543210',
  email: null,
  website: null,
  gstin: '33ABCDE1234F1Z5',
  pan: null,
  registrationNumber: null,
  nabhNumber: 'PEH-2024-1234',
  hfrId: null,
  timeZone: 'Asia/Kolkata',
  currency: 'INR',
  logoFileId: 'logo-1',
  letterheadNote: null,
  isActive: true,
  version: 7,
};

const doctor: PractitionerDto = {
  id: 'p1',
  code: 'DR001',
  title: 'Dr.',
  firstName: 'Asha',
  lastName: 'Menon',
  displayName: 'Dr. Asha Menon',
  gender: 'Female',
  qualification: 'MBBS, MD',
  registrationCouncil: 'Tamil Nadu Medical Council',
  registrationNumber: 'TNMC 12345',
  hprId: '71-3016-8543-4357',
  specialtyId: 's1',
  specialtyName: 'General medicine',
  departmentId: 'd1',
  departmentName: 'General medicine',
  kind: 'Consultant',
  phone: null,
  email: null,
  consultationFeeNew: 500,
  consultationFeeFollowUp: 300,
  followUpValidityDays: 7,
  languages: ['English', 'Tamil'],
  bio: null,
  photoFileId: null,
  signatureFileId: 'sign-1',
  availableForOnlineBooking: true,
  availableForTelehealth: false,
  facilityIds: [],
  isActive: true,
  version: 3,
};

describe('facility form', () => {
  it('keeps the address together for the server and sends empty text as nothing', () => {
    const values = { ...toFacilityForm(facility), legalName: '  ', letterheadNote: ' NABH accredited hospital ' };

    const request = toFacilityRequest(values, facility, facility.version);

    expect(request.address).toEqual({
      line1: '12 Anna Salai',
      line2: null,
      city: 'Chennai',
      district: null,
      state: 'Tamil Nadu',
      pinCode: '600002',
      country: 'India',
    });
    expect(request.legalName).toBeNull();
    expect(request.letterheadNote).toBe('NABH accredited hospital');
    expect(request.version).toBe(7);
    expect(request.code).toBe('NH');
  });

  it('sends a new facility code in capitals, and an existing one unchanged', () => {
    const values = { ...toFacilityForm(null), code: 'nh', name: 'Nana Hospitals' };
    expect(toFacilityRequest(values, null, null).code).toBe('NH');
    expect(toFacilityRequest({ ...values, code: 'xx' }, facility, facility.version).code).toBe('NH');
  });

  it('sends the version the form was filled from, not a newer one read since (REL-04)', () => {
    const values = toFacilityForm(facility);
    const reread = { ...facility, name: 'Nana Hospitals (renamed elsewhere)', version: 8 };

    expect(toFacilityRequest(values, reread, facility.version).version).toBe(7);
    expect(toFacilityRequest({ ...values, code: 'NH' }, null, null).version).toBeNull();
  });

  it('previews the letterhead from what is being typed, logo included', () => {
    const preview = toLetterheadRequest({
      ...toFacilityForm(facility),
      name: 'Nana Hospitals Annexe',
      gstin: '33abcde1234f1z5',
    });
    expect(preview.name).toBe('Nana Hospitals Annexe');
    expect(preview.gstin).toBe('33ABCDE1234F1Z5');
    expect(preview.logoFileId).toBe('logo-1');
    expect(preview).not.toHaveProperty('code');
  });

  it('checks the code, PIN code, GSTIN and website as the server does', () => {
    const result = facilitySchema.safeParse({
      ...toFacilityForm(null),
      code: 'N1',
      name: 'Nana',
      pinCode: '012345',
      gstin: 'GSTIN',
      website: 'nanahospitals',
    });
    expect(result.success).toBe(false);
    const fields = result.error?.issues.map((issue) => issue.path[0]);
    expect(fields).toEqual(expect.arrayContaining(['code', 'pinCode', 'gstin', 'website']));
  });

  it.each([
    ['www.nanahospitals.in', true],
    ['https://nanahospitals.in/care', true],
    ['', true],
    ['nanahospitals', false],
    ['ftp://nanahospitals.in', false],
    ['www.nana hospitals.in', false],
  ])('treats %j as a web address: %s', (value, valid) => {
    expect(isWebAddress(value)).toBe(valid);
  });
});

describe('department form', () => {
  it('suggests the next position in tens, after the last department', () => {
    expect(nextDisplayOrder([{ displayOrder: 10 }, { displayOrder: 290 }])).toBe(300);
    expect(nextDisplayOrder([{ displayOrder: 295 }])).toBe(300);
    expect(nextDisplayOrder([])).toBe(10);
  });

  it('sends the token prefix in capitals and empty fields as nothing', () => {
    const department: DepartmentDto = {
      id: 'd1',
      facilityId: 'f1',
      code: 'GM',
      name: 'General medicine',
      kind: 'Clinical',
      specialtyId: null,
      headPractitionerId: null,
      location: null,
      phone: null,
      tokenPrefix: null,
      displayOrder: 10,
      isActive: true,
      version: 2,
    };
    const request = toDepartmentRequest(
      { ...toDepartmentForm(department), tokenPrefix: 'gm', location: ' ' },
      department,
      department.version,
    );
    expect(request.tokenPrefix).toBe('GM');
    expect(request.location).toBeNull();
    expect(request.version).toBe(2);
  });

  it('refuses a long token prefix and a position out of range', () => {
    const result = departmentSchema.safeParse({
      ...toDepartmentForm(null),
      code: 'GM',
      name: 'Medicine',
      tokenPrefix: 'GMEDI',
      displayOrder: 10_000,
    });
    expect(result.error?.issues.map((issue) => issue.path[0])).toEqual(
      expect.arrayContaining(['tokenPrefix', 'displayOrder']),
    );
  });
});

describe('practitioner form', () => {
  it('needs a council registration number for doctors only', () => {
    const values = { ...toPractitionerForm(doctor), registrationNumber: '' };
    expect(practitionerSchema.safeParse(values).error?.issues[0]?.path).toEqual(['registrationNumber']);
    expect(practitionerSchema.safeParse({ ...values, kind: 'Nurse' }).success).toBe(true);
  });

  it('takes the HPR ID as 14 digits, grouped or run together, or none', () => {
    const values = toPractitionerForm(doctor);
    expect(values.hprId).toBe('71-3016-8543-4357');
    for (const hprId of ['71 3016 8543 4357', '71301685434357', ' 71-3016-8543-4357 ', '']) {
      expect(practitionerSchema.safeParse({ ...values, hprId }).success).toBe(true);
    }
    for (const hprId of ['7130168543435', '713016854343570', '71/3016/8543/4357', 'drasha@hpr.abdm']) {
      expect(practitionerSchema.safeParse({ ...values, hprId }).error?.issues[0]?.path).toEqual(['hprId']);
    }
    expect(toPractitionerRequest({ ...values, hprId: ' ' }, doctor, doctor.version).hprId).toBeNull();
    expect(toPractitionerRequest(values, doctor, doctor.version).hprId).toBe('71-3016-8543-4357');
  });

  it('consults at every facility, or at least one chosen', () => {
    const values = { ...toPractitionerForm(doctor), allFacilities: false, facilityIds: [] };
    expect(practitionerSchema.safeParse(values).error?.issues[0]?.path).toEqual(['facilityIds']);
    expect(toPractitionerRequest({ ...values, facilityIds: ['f2'] }, doctor, doctor.version).facilityIds).toEqual([
      'f2',
    ]);
    expect(
      toPractitionerRequest({ ...values, allFacilities: true, facilityIds: ['f2'] }, doctor, doctor.version)
        .facilityIds,
    ).toEqual([]);
  });

  it('reads languages as a list, each once', () => {
    expect(splitLanguages(' English, tamil;Hindi , english,, ')).toEqual(['English', 'tamil', 'Hindi']);
    expect(toPractitionerForm(doctor).languages).toBe('English, Tamil');
  });

  it('refuses a fee with more than two decimal places', () => {
    const result = practitionerSchema.safeParse({ ...toPractitionerForm(doctor), consultationFeeNew: 500.555 });
    expect(result.error?.issues[0]?.path).toEqual(['consultationFeeNew']);
  });

  it('shows the name as lists and documents will', () => {
    expect(displayNameOf({ title: 'Dr.', firstName: ' Asha ', lastName: 'Menon' })).toBe('Dr. Asha Menon');
    expect(displayNameOf({ title: null, firstName: 'Ravi', lastName: '' })).toBe('Ravi');
  });

  it('sends no title when "None" was chosen', () => {
    expect(toPractitionerRequest({ ...toPractitionerForm(doctor), title: null }, doctor, doctor.version).title).toBe(
      '',
    );
  });
});

describe('service form', () => {
  const service: ServiceItemDto = {
    id: 'sv1',
    code: 'LAB-CBC',
    name: 'Complete blood count',
    shortName: 'CBC',
    category: 'Laboratory',
    departmentId: null,
    basePrice: 350,
    taxRate: 0,
    sacCode: '999312',
    isDiscountAllowed: true,
    isPriceEditable: false,
    description: null,
    isActive: true,
    version: 4,
    localName: 'முழு இரத்த எண்ணிக்கை',
  };

  it('round-trips the GST rate through the picker', () => {
    const values = toServiceForm(service);
    expect(values.taxRate).toBe('0');
    expect(toServiceRequest({ ...values, taxRate: '18' }, service, service.version).taxRate).toBe(18);
  });

  it('checks the SAC code and the code', () => {
    const result = serviceSchema.safeParse({ ...toServiceForm(null), code: 'LAB CBC', name: 'CBC', sacCode: '99931X' });
    expect(result.error?.issues.map((issue) => issue.path[0])).toEqual(expect.arrayContaining(['code', 'sacCode']));
  });

  it('keeps the name in the local language, and sends none for a blank one', () => {
    const values = toServiceForm(service);
    expect(values.localName).toBe('முழு இரத்த எண்ணிக்கை');
    expect(toServiceRequest(values, service, service.version).localName).toBe('முழு இரத்த எண்ணிக்கை');
    expect(toServiceRequest({ ...values, localName: '  ' }, service, service.version).localName).toBeNull();
    expect(serviceSchema.safeParse({ ...values, localName: 'x'.repeat(201) }).error?.issues[0]?.path).toEqual([
      'localName',
    ]);
  });

  it('starts a new service as exempt hospital service, discount allowed', () => {
    const values = toServiceForm(null);
    expect(values.sacCode).toBe('999312');
    expect(values.taxRate).toBe('0');
    expect(values.isDiscountAllowed).toBe(true);
  });
});

describe('pickers', () => {
  it('offer what is in use, keeping a current choice that is not', () => {
    const items = [
      { id: 'a', name: 'Cardiology', isActive: true },
      { id: 'b', name: 'Old speciality', isActive: false },
      { id: 'c', name: 'Another old one', isActive: false },
    ];
    expect(optionsWithCurrent(items, 'b')).toEqual([
      { value: 'a', label: 'Cardiology' },
      { value: 'b', label: 'Old speciality (not in use)' },
    ]);
    expect(optionsWithCurrent(undefined, null)).toEqual([]);
  });

  it('keep the current doctor in a look-up until a search is typed', () => {
    const head = { id: 'h', code: 'DR9', name: 'Dr. Head' };
    const others = [{ id: 'o', code: 'DR1', name: 'Dr. Other' }];
    expect(withCurrentItem(others, head, '')).toEqual([head, ...others]);
    expect(withCurrentItem(others, head, 'oth')).toEqual(others);
    expect(withCurrentItem([head], head, '')).toEqual([head]);
  });

  it('match a search in any of the texts, ignoring case', () => {
    expect(matchesSearch(['General medicine', 'GM'], 'gm')).toBe(true);
    expect(matchesSearch(['General medicine', null], 'surgery')).toBe(false);
    expect(matchesSearch(['General medicine'], '  ')).toBe(true);
  });
});
