import type { DietOrderDto, DietTypeDto, PlannedMealDto, ProductionSummary, WardMealPlan } from '@/modules/dietary/api';
import type { FakeAnswers } from '../fakeApi';
import { RECORD_ID, WARD_ID } from '../roleAccessIds';

// Diet orders, meal plans and the kitchen's production list: an active order that needs review, an active one and a
// stopped one; planned, delivered and not-taken meals on one ward.

const now = Date.now();
const today = new Date(now).toISOString().slice(0, 10);
const daysAgo = (days: number) => new Date(now - days * 86_400_000).toISOString();
const dateAgo = (days: number) => daysAgo(days).slice(0, 10);

const DIABETIC = '0192a000-0000-7000-8000-00000000d101';
const SOFT = '0192a000-0000-7000-8000-00000000d102';
const NORMAL = '0192a000-0000-7000-8000-00000000d103';

const DIET_TYPES: DietTypeDto[] = [
  {
    id: DIABETIC,
    code: 'DM',
    name: 'Diabetic diet (1600 kcal)',
    description: 'No added sugar, complex carbohydrates, six small meals',
    category: 'Therapeutic',
    chargeServiceItemId: null,
    chargeServiceCode: 'DIET-TH',
    defaultMealSlots: 'EarlyMorning, Breakfast, Lunch, Evening, Dinner',
    defaultMeals: ['EarlyMorning', 'Breakfast', 'Lunch', 'Evening', 'Dinner'],
    displayOrder: 1,
    isActive: true,
    version: 2,
  },
  {
    id: SOFT,
    code: 'SOFT',
    name: 'Soft diet',
    description: 'Khichdi, dal, curd rice, soft idli',
    category: 'ModifiedConsistency',
    chargeServiceItemId: null,
    chargeServiceCode: null,
    defaultMealSlots: 'Breakfast, Lunch, Dinner',
    defaultMeals: ['Breakfast', 'Lunch', 'Dinner'],
    displayOrder: 2,
    isActive: true,
    version: 1,
  },
  {
    id: NORMAL,
    code: 'NORM',
    name: 'Normal hospital diet',
    description: null,
    category: 'Regular',
    chargeServiceItemId: null,
    chargeServiceCode: 'DIET-REG',
    defaultMealSlots: 'EarlyMorning, Breakfast, Lunch, Evening, Dinner',
    defaultMeals: ['EarlyMorning', 'Breakfast', 'Lunch', 'Evening', 'Dinner'],
    displayOrder: 3,
    isActive: true,
    version: 1,
  },
];

const order = (
  overrides: Partial<DietOrderDto> & Pick<DietOrderDto, 'id' | 'admissionId' | 'patientId' | 'patientName' | 'uhid'>,
): DietOrderDto => ({
  admissionNumber: 'IP2600412',
  wardName: 'General Ward — Male',
  bedLabel: 'GW-04',
  dietTypeId: DIABETIC,
  dietCode: 'DM',
  dietName: 'Diabetic diet (1600 kcal)',
  preference: 'Vegetarian',
  restrictions: ['No sugar'],
  allergyNotes: null,
  instructions: null,
  fluidRestrictionMl: null,
  calorieTargetKcal: 1600,
  mealSlots: 'EarlyMorning, Breakfast, Lunch, Evening, Dinner',
  meals: ['EarlyMorning', 'Breakfast', 'Lunch', 'Evening', 'Dinner'],
  startDate: dateAgo(2),
  endDate: null,
  status: 'Active',
  source: 'Dietitian',
  orderedByName: 'Priya Kulkarni',
  needsReview: false,
  createdAt: daysAgo(2),
  stoppedAt: null,
  stopReason: null,
  ...overrides,
});

const ORDERS: DietOrderDto[] = [
  order({
    id: RECORD_ID,
    admissionId: '0192a000-0000-7000-8000-00000000d201',
    patientId: '0192a000-0000-7000-8000-000000000002',
    patientName: 'Ravi Shankar',
    uhid: 'NH26000124',
    source: 'ClinicalOrder',
    orderedByName: 'Dr. Meera Iyer',
    needsReview: true,
    allergyNotes: 'Peanuts',
  }),
  order({
    id: '0192a000-0000-7000-8000-00000000d002',
    admissionId: '0192a000-0000-7000-8000-00000000d202',
    patientId: '0192a000-0000-7000-8000-000000000006',
    patientName: 'Suresh Kumar',
    uhid: 'NH26000077',
    bedLabel: 'GW-07',
    dietTypeId: SOFT,
    dietCode: 'SOFT',
    dietName: 'Soft diet',
    preference: 'NonVegetarian',
    restrictions: [],
    calorieTargetKcal: null,
    mealSlots: 'Breakfast, Lunch, Dinner',
    meals: ['Breakfast', 'Lunch', 'Dinner'],
    source: 'Doctor',
    orderedByName: 'Dr. Anil Rao',
  }),
  order({
    id: '0192a000-0000-7000-8000-00000000d003',
    admissionId: '0192a000-0000-7000-8000-00000000d203',
    patientId: '0192a000-0000-7000-8000-000000000007',
    patientName: 'Mohan Deshpande',
    uhid: 'NH26000141',
    bedLabel: 'GW-09',
    status: 'Stopped',
    stoppedAt: daysAgo(1),
    stopReason: 'Nil by mouth for surgery',
  }),
];

const meal = (
  overrides: Partial<PlannedMealDto> & Pick<PlannedMealDto, 'id' | 'slot' | 'serviceTime' | 'status'>,
): PlannedMealDto => ({
  planDate: today,
  admissionId: '0192a000-0000-7000-8000-00000000d201',
  patientId: '0192a000-0000-7000-8000-000000000002',
  uhid: 'NH26000124',
  patientName: 'Ravi Shankar',
  wardId: WARD_ID,
  bedLabel: 'GW-04',
  dietCode: 'DM',
  dietName: 'Diabetic diet (1600 kcal)',
  preference: 'Vegetarian',
  restrictions: ['No sugar'],
  allergyNotes: 'Peanuts',
  instructions: null,
  deliveredAt: null,
  deliveredByName: null,
  remarks: null,
  version: 1,
  ...overrides,
});

const MEAL_PLANS: WardMealPlan[] = [
  {
    wardId: WARD_ID,
    wardName: 'General Ward — Male',
    meals: 3,
    items: [
      meal({ id: '0192a000-0000-7000-8000-00000000d301', slot: 'Lunch', serviceTime: '13:00', status: 'Planned' }),
      meal({
        id: '0192a000-0000-7000-8000-00000000d302',
        slot: 'Breakfast',
        serviceTime: '08:00',
        status: 'Delivered',
        deliveredAt: new Date(now - 4 * 3_600_000).toISOString(),
        deliveredByName: 'Ramesh Jadhav',
        version: 2,
      }),
      meal({
        id: '0192a000-0000-7000-8000-00000000d303',
        slot: 'Breakfast',
        serviceTime: '08:00',
        status: 'NotTaken',
        admissionId: '0192a000-0000-7000-8000-00000000d202',
        patientId: '0192a000-0000-7000-8000-000000000006',
        patientName: 'Suresh Kumar',
        uhid: 'NH26000077',
        bedLabel: 'GW-07',
        dietCode: 'SOFT',
        dietName: 'Soft diet',
        preference: 'NonVegetarian',
        restrictions: [],
        allergyNotes: null,
        remarks: 'Away for CT scan',
        version: 2,
      }),
    ],
  },
];

const PRODUCTION: ProductionSummary = {
  date: today,
  totalMeals: 46,
  lines: [
    {
      slot: 'Breakfast',
      serviceTime: '08:00',
      dietCode: 'DM',
      dietName: 'Diabetic diet (1600 kcal)',
      vegetarian: 6,
      nonVegetarian: 2,
      eggetarian: 1,
      jain: 1,
      total: 10,
      specialNotes: ['Bed GW-04: peanut allergy'],
    },
    {
      slot: 'Lunch',
      serviceTime: '13:00',
      dietCode: 'SOFT',
      dietName: 'Soft diet',
      vegetarian: 8,
      nonVegetarian: 3,
      eggetarian: 0,
      jain: 1,
      total: 12,
      specialNotes: [],
    },
    {
      slot: 'Dinner',
      serviceTime: '19:30',
      dietCode: 'NORM',
      dietName: 'Normal hospital diet',
      vegetarian: 14,
      nonVegetarian: 9,
      eggetarian: 1,
      jain: 0,
      total: 24,
      specialNotes: [],
    },
  ],
};

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/dietary/diet-types': DIET_TYPES,
  'GET /api/v1/dietary/orders': ORDERS,
  'GET /api/v1/dietary/admissions/*': ORDERS.slice(0, 1),
  'GET /api/v1/dietary/meal-plans': MEAL_PLANS,
  'GET /api/v1/dietary/meal-plans/production': PRODUCTION,
};
