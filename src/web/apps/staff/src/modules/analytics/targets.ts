import type { KpiTargetItem, KpiUnit, SaveKpiTargetRequest } from './api';

/** The KPI target form as typed (numbers as text until saved). */
export interface TargetForm {
  target: string;
  amberThreshold: string;
  redThreshold: string;
  ownerRole: string;
}

export type TargetFormErrors = Partial<Record<keyof TargetForm, string>>;

/** The form for a KPI: its target as stored, or empty with the hospital administrator as the owner. */
export function targetFormDefaults(item: KpiTargetItem, roles: readonly string[]): TargetForm {
  const target = item.target;
  return {
    target: target ? String(target.target) : '',
    amberThreshold: target ? String(target.amberThreshold) : '',
    redThreshold: target ? String(target.redThreshold) : '',
    ownerRole: target?.ownerRole ?? (roles.includes('HospitalAdmin') ? 'HospitalAdmin' : (roles[0] ?? '')),
  };
}

const NUMBER = /^-?\d+(\.\d{1,2})?$/;

/**
 * Checks the form as the server does (numbers with at most two decimals; thresholds on the bad side of the target, red
 * beyond amber) so most mistakes are named before saving. Returns the request or the errors by field.
 */
export function parseTargetForm(
  form: TargetForm,
  item: Pick<KpiTargetItem, 'higherIsBetter' | 'target'>,
): { request: SaveKpiTargetRequest; errors?: undefined } | { request?: undefined; errors: TargetFormErrors } {
  const errors: TargetFormErrors = {};
  const read = (key: 'target' | 'amberThreshold' | 'redThreshold', label: string) => {
    const text = form[key].replace(/,/g, '').trim();
    if (!NUMBER.test(text)) {
      errors[key] = `Enter the ${label} as a number, with at most two decimals.`;
      return null;
    }
    return Number(text);
  };
  const target = read('target', 'target');
  const amber = read('amberThreshold', 'amber threshold');
  const red = read('redThreshold', 'red threshold');
  if (!form.ownerRole) {
    errors.ownerRole = 'Choose the role told when the KPI is missed.';
  }
  if (target !== null && amber !== null && red !== null) {
    if (item.higherIsBetter) {
      if (amber > target) {
        errors.amberThreshold = 'Higher is better for this KPI, so the amber threshold must be at or below the target.';
      } else if (red > amber) {
        errors.redThreshold = 'The red threshold must be at or below the amber threshold.';
      }
    } else if (amber < target) {
      errors.amberThreshold = 'Lower is better for this KPI, so the amber threshold must be at or above the target.';
    } else if (red < amber) {
      errors.redThreshold = 'The red threshold must be at or above the amber threshold.';
    }
  }
  if (Object.keys(errors).length > 0 || target === null || amber === null || red === null) {
    return { errors };
  }
  return {
    request: {
      target,
      amberThreshold: amber,
      redThreshold: red,
      ownerRole: form.ownerRole,
      ...(item.target ? { version: item.target.version } : {}),
    },
  };
}

/** How the target is read: per month for counts and money (prorated to the period shown), else as it is. */
export function targetBasis(item: Pick<KpiTargetItem, 'monthly' | 'higherIsBetter'>): string {
  return `${item.monthly ? 'A monthly figure, prorated to the period shown' : 'Compared as it is'}; ${
    item.higherIsBetter ? 'higher is better' : 'lower is better'
  }.`;
}

/** The unit's word for the target field ("visits per month", "%", "days"). */
export function targetUnitHint(unit: KpiUnit, monthly: boolean): string {
  const per = monthly ? ' per month' : '';
  switch (unit) {
    case 'inr':
      return `Rupees${per}`;
    case 'count':
      return `Count${per}`;
    case 'percent':
      return 'Percent';
    case 'days':
      return 'Days';
    case 'minutes':
      return 'Minutes';
    default:
      return 'Score';
  }
}
