import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { FoodPreference, MealSlot } from './rules';

export type DietCategory = 'Regular' | 'Therapeutic' | 'ModifiedConsistency' | 'Enteral' | 'NilByMouth';
export const DIET_CATEGORIES: readonly DietCategory[] = [
  'Regular',
  'Therapeutic',
  'ModifiedConsistency',
  'Enteral',
  'NilByMouth',
];
export const DIET_CATEGORY_LABEL: Record<DietCategory, string> = {
  Regular: 'Regular',
  Therapeutic: 'Therapeutic (diabetic, renal, cardiac…)',
  ModifiedConsistency: 'Modified consistency (soft, liquid…)',
  Enteral: 'Tube feed',
  NilByMouth: 'Nil by mouth',
};

export type DietOrderStatus = 'Active' | 'Stopped' | 'Completed';
export type DietOrderSource = 'Doctor' | 'Dietitian' | 'Nurse' | 'ClinicalOrder';
export type MealDeliveryStatus = 'Planned' | 'Delivered' | 'NotTaken' | 'Returned' | 'Cancelled';

export const DIET_SOURCE_LABEL: Record<DietOrderSource, string> = {
  Doctor: 'Doctor',
  Dietitian: 'Dietitian',
  Nurse: 'Nurse',
  ClinicalOrder: 'Clinical order',
};
export const DELIVERY_LABEL: Record<MealDeliveryStatus, string> = {
  Planned: 'To deliver',
  Delivered: 'Delivered',
  NotTaken: 'Not taken',
  Returned: 'Returned',
  Cancelled: 'Cancelled',
};

export interface DietTypeDto {
  id: string;
  code: string;
  name: string;
  description: string | null;
  category: DietCategory;
  chargeServiceItemId: string | null;
  chargeServiceCode: string | null;
  /** Flags value such as "EarlyMorning, Breakfast, Lunch"; use `defaultMeals`. */
  defaultMealSlots: string;
  defaultMeals: MealSlot[];
  displayOrder: number;
  isActive: boolean;
  version: number;
}

export interface SaveDietTypeRequest {
  code: string;
  name: string;
  description: string | null;
  category: DietCategory;
  chargeServiceItemId: string | null;
  /** Bit mask of meal rounds. */
  defaultMealSlots: number;
  displayOrder: number;
  isActive: boolean;
  version: number | null;
}

export interface DietOrderDto {
  id: string;
  admissionId: string;
  admissionNumber: string | null;
  patientId: string;
  uhid: string | null;
  patientName: string | null;
  wardName: string | null;
  bedLabel: string | null;
  dietTypeId: string;
  dietCode: string | null;
  dietName: string | null;
  preference: FoodPreference;
  restrictions: string[];
  allergyNotes: string | null;
  instructions: string | null;
  fluidRestrictionMl: number | null;
  calorieTargetKcal: number | null;
  mealSlots: string;
  meals: MealSlot[];
  startDate: string;
  endDate: string | null;
  status: DietOrderStatus;
  source: DietOrderSource;
  orderedByName: string | null;
  needsReview: boolean;
  createdAt: string;
  stoppedAt: string | null;
  stopReason: string | null;
}

export interface CreateDietOrderRequest {
  admissionId: string;
  dietTypeId: string;
  preference: FoodPreference;
  restrictions: string[] | null;
  allergyNotes: string | null;
  instructions: string | null;
  fluidRestrictionMl: number | null;
  calorieTargetKcal: number | null;
  /** Bit mask of meal rounds; null uses the diet type's default rounds. */
  mealSlots: number | null;
  startDate: string | null;
  orderedByPractitionerId: string | null;
}

export interface MealPlanResult {
  date: string;
  mealsAdded: number;
  mealsCancelled: number;
  mealsPlanned: number;
  chargesPosted: number;
}

export interface PlannedMealDto {
  id: string;
  planDate: string;
  slot: MealSlot;
  serviceTime: string;
  admissionId: string;
  patientId: string;
  uhid: string | null;
  patientName: string | null;
  wardId: string | null;
  bedLabel: string | null;
  dietCode: string;
  dietName: string | null;
  preference: FoodPreference;
  restrictions: string[];
  allergyNotes: string | null;
  instructions: string | null;
  status: MealDeliveryStatus;
  deliveredAt: string | null;
  deliveredByName: string | null;
  remarks: string | null;
  version: number;
}

export interface WardMealPlan {
  wardId: string | null;
  wardName: string | null;
  meals: number;
  items: PlannedMealDto[];
}

export interface ProductionLine {
  slot: MealSlot;
  serviceTime: string;
  dietCode: string;
  dietName: string | null;
  vegetarian: number;
  nonVegetarian: number;
  eggetarian: number;
  jain: number;
  total: number;
  specialNotes: string[];
}

export interface ProductionSummary {
  date: string;
  totalMeals: number;
  lines: ProductionLine[];
}

export const dietaryKeys = {
  all: ['dietary'] as const,
  dietTypes: (includeInactive: boolean) => ['dietary', 'diet-types', includeInactive] as const,
  orders: (filters: { status?: DietOrderStatus; wardId?: string; needsReview?: boolean }) =>
    ['dietary', 'orders', filters] as const,
  admissionOrders: (admissionId: string) => ['dietary', 'admission', admissionId] as const,
  mealPlans: (date: string, wardId: string | null, slot: MealSlot | null) =>
    ['dietary', 'meal-plans', date, wardId ?? 'all', slot ?? 'all'] as const,
  production: (date: string) => ['dietary', 'production', date] as const,
};

export const dietaryQueries = {
  dietTypes: (includeInactive = false) =>
    queryOptions({
      queryKey: dietaryKeys.dietTypes(includeInactive),
      queryFn: ({ signal }) =>
        api.get<DietTypeDto[]>('/api/v1/dietary/diet-types', {
          query: { includeInactive: includeInactive || undefined },
          signal,
        }),
      staleTime: 5 * 60_000,
    }),
  orders: (filters: { status?: DietOrderStatus; wardId?: string; needsReview?: boolean }) =>
    queryOptions({
      queryKey: dietaryKeys.orders(filters),
      queryFn: ({ signal }) => api.get<DietOrderDto[]>('/api/v1/dietary/orders', { query: { ...filters }, signal }),
      refetchInterval: 120_000,
    }),
  admissionOrders: (admissionId: string) =>
    queryOptions({
      queryKey: dietaryKeys.admissionOrders(admissionId),
      queryFn: ({ signal }) => api.get<DietOrderDto[]>(`/api/v1/dietary/admissions/${admissionId}/orders`, { signal }),
    }),
  mealPlans: (date: string, wardId: string | null, slot: MealSlot | null) =>
    queryOptions({
      queryKey: dietaryKeys.mealPlans(date, wardId, slot),
      queryFn: ({ signal }) =>
        api.get<WardMealPlan[]>('/api/v1/dietary/meal-plans', { query: { date, wardId, slot }, signal }),
      refetchInterval: 60_000,
    }),
  production: (date: string) =>
    queryOptions({
      queryKey: dietaryKeys.production(date),
      queryFn: ({ signal }) =>
        api.get<ProductionSummary>('/api/v1/dietary/meal-plans/production', { query: { date }, signal }),
      refetchInterval: 120_000,
    }),
  dietServices: () =>
    queryOptions({
      queryKey: ['dietary', 'diet-services'],
      queryFn: ({ signal }) =>
        api.get<{ id: string; code: string; name: string; basePrice: number }[]>('/api/v1/masters/lookups/services', {
          query: { category: 'Dietary' },
          signal,
        }),
      staleTime: 10 * 60_000,
    }),
};

export const dietaryApi = {
  createDietType: (body: SaveDietTypeRequest) => api.post<DietTypeDto>('/api/v1/dietary/diet-types', body),
  updateDietType: (id: string, body: SaveDietTypeRequest) => api.put<void>(`/api/v1/dietary/diet-types/${id}`, body),
  createOrder: (body: CreateDietOrderRequest) => api.post<DietOrderDto>('/api/v1/dietary/orders', body),
  stopOrder: (id: string, reason: string) => api.post<DietOrderDto>(`/api/v1/dietary/orders/${id}/stop`, { reason }),
  reviewOrder: (id: string) => api.post<DietOrderDto>(`/api/v1/dietary/orders/${id}/review`),
  generatePlan: (date: string) => api.post<MealPlanResult>('/api/v1/dietary/meal-plans/generate', { date }),
  recordDelivery: (
    mealId: string,
    body: { status: 'Delivered' | 'NotTaken' | 'Returned'; remarks: string | null; version: number },
  ) => api.post<PlannedMealDto>(`/api/v1/dietary/meals/${mealId}/delivery`, body),
  deliverRound: (body: { date: string; slot: MealSlot; wardId: string; status: 'Delivered' }) =>
    api.post<{ updated: number }>('/api/v1/dietary/meal-plans/deliveries', body),
};
