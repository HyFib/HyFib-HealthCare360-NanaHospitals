import type { FluentIcon } from '@fluentui/react-icons';
import { BeakerRegular, HeartPulseRegular, XrayRegular } from '@fluentui/react-icons';
import type { Tone } from '@/components/ListRow';
import type { MessageKey } from '@/i18n';
import type { DiagnosticKind } from '@/lib/types';

export const REPORT_KIND: Record<DiagnosticKind, { icon: FluentIcon; tone: Tone; label: MessageKey }> = {
  Laboratory: { icon: BeakerRegular, tone: 'brand', label: 'reports.kind.Laboratory' },
  Radiology: { icon: XrayRegular, tone: 'neutral', label: 'reports.kind.Radiology' },
  Cardiology: { icon: HeartPulseRegular, tone: 'danger', label: 'reports.kind.Cardiology' },
};
