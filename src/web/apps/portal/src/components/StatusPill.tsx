import { Badge } from '@fluentui/react-components';
import type { ReactElement, ReactNode } from 'react';
import type { Tone } from './ListRow';

const COLOR: Record<Tone, 'brand' | 'success' | 'warning' | 'danger' | 'informative'> = {
  brand: 'brand',
  success: 'success',
  warning: 'warning',
  danger: 'danger',
  neutral: 'informative',
};

/** A status in words (never colour alone), e.g. "Confirmed", "Paid", "Waiting for the doctor". */
export function StatusPill({ tone, children, icon }: { tone: Tone; children: ReactNode; icon?: ReactElement }) {
  return (
    <Badge appearance="tint" color={COLOR[tone]} shape="rounded" size="large" icon={icon}>
      {children}
    </Badge>
  );
}
