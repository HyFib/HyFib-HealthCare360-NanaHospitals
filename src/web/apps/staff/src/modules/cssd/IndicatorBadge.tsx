import { Badge } from '@fluentui/react-components';
import type { IndicatorResult } from './api';

/** Chemical / biological indicator result as a labelled badge (word and colour). */
export function IndicatorBadge({ result, label }: { result: IndicatorResult; label: string }) {
  return (
    <Badge
      appearance="tint"
      size="small"
      color={result === 'Pass' ? 'success' : result === 'Fail' ? 'danger' : 'warning'}
    >
      {label} {result === 'Pass' ? 'passed' : result === 'Fail' ? 'failed' : 'pending'}
    </Badge>
  );
}
