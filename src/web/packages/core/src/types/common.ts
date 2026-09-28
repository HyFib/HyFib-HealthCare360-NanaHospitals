/** `{ items, page, pageSize, totalCount }` returned by every list endpoint. */
export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages?: number;
}

export interface PageQuery {
  page?: number;
  pageSize?: number;
  search?: string;
  sort?: string;
  desc?: boolean;
}

export interface LookupItem {
  id: string;
  code: string;
  name: string;
}

export type Gender = 'Unknown' | 'Male' | 'Female' | 'Other';

export type BloodGroup =
  | 'Unknown'
  | 'APositive'
  | 'ANegative'
  | 'BPositive'
  | 'BNegative'
  | 'ABPositive'
  | 'ABNegative'
  | 'OPositive'
  | 'ONegative'
  | 'BombayPositive'
  | 'BombayNegative';

export type MaritalStatus = 'Unknown' | 'Single' | 'Married' | 'Divorced' | 'Widowed' | 'Separated';

export type PayerType = 'Self' | 'Insurance' | 'Corporate' | 'GovernmentScheme' | 'Staff' | 'Charity';

export interface Address {
  line1?: string | null;
  line2?: string | null;
  city?: string | null;
  district?: string | null;
  state?: string | null;
  pinCode?: string | null;
  country?: string | null;
}

export const bloodGroupLabel: Record<BloodGroup, string> = {
  Unknown: 'Unknown',
  APositive: 'A+',
  ANegative: 'A−',
  BPositive: 'B+',
  BNegative: 'B−',
  ABPositive: 'AB+',
  ABNegative: 'AB−',
  OPositive: 'O+',
  ONegative: 'O−',
  BombayPositive: 'Bombay (hh)+',
  BombayNegative: 'Bombay (hh)−',
};

export const genderShort: Record<Gender, string> = { Unknown: 'U', Male: 'M', Female: 'F', Other: 'O' };
