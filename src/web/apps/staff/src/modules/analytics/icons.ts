import {
  BeakerRegular,
  BedRegular,
  ClockRegular,
  DataAreaRegular,
  DataBarVerticalRegular,
  DataHistogramRegular,
  DataLineRegular,
  DataPieRegular,
  DataTrendingRegular,
  EmojiSmileSlightRegular,
  GaugeRegular,
  HeartPulseRegular,
  MoneyRegular,
  NumberSymbolSquareRegular,
  PeopleTeamRegular,
  PillRegular,
  ReceiptMoneyRegular,
  ShieldCheckmarkRegular,
  StethoscopeRegular,
  TableRegular,
  TimerRegular,
  type FluentIcon,
} from '@fluentui/react-icons';
import type { ReportCategory, ReportChartType } from './api';

export const CATEGORY_ICONS: Record<ReportCategory, FluentIcon> = {
  OutPatient: StethoscopeRegular,
  InPatient: BedRegular,
  Revenue: MoneyRegular,
  Diagnostics: BeakerRegular,
  Pharmacy: PillRegular,
  SpecialtyCare: HeartPulseRegular,
  Quality: ShieldCheckmarkRegular,
  Experience: EmojiSmileSlightRegular,
  People: PeopleTeamRegular,
};

export const VIEW_ICONS: Record<ReportChartType, FluentIcon> = {
  Bar: DataBarVerticalRegular,
  StackedBar: DataHistogramRegular,
  Line: DataLineRegular,
  Area: DataAreaRegular,
  Pie: DataPieRegular,
  Table: TableRegular,
  Summary: NumberSymbolSquareRegular,
};

/** Icons of the executive KPIs by key (unknown keys fall back to `DEFAULT_KPI_ICON`). */
export const KPI_ICONS: Partial<Record<string, FluentIcon>> = {
  opVisits: StethoscopeRegular,
  admissions: BedRegular,
  occupancy: GaugeRegular,
  alos: ClockRegular,
  revenue: MoneyRegular,
  collections: ReceiptMoneyRegular,
  labTat: TimerRegular,
  nps: EmojiSmileSlightRegular,
};

export const DEFAULT_KPI_ICON: FluentIcon = DataTrendingRegular;
