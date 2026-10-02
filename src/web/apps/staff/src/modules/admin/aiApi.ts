import type { PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import { istDayEnd, istDayStart } from './integrationsApi';

/**
 * AI administration (GET /api/v1/ai/usage, permission `ai.settings.manage`). Usage rows record who, when, what for,
 * tokens and time, never the text. Status comes from `aiQueries.status()` in `@/lib/ai`.
 */

/** AiUsageDto: one recorded AI call. */
export interface AiUsageDto {
  id: string;
  createdAt: string;
  userId: string | null;
  patientId: string | null;
  purpose: string;
  provider: string;
  model: string | null;
  inputTokens: number;
  outputTokens: number;
  latencyMs: number;
  success: boolean;
  error: string | null;
}

/** AiPrompts purposes on the server. */
export const AI_PURPOSES = ['assistant.command', 'clinical.structure-note', 'voice.transcribe'] as const;
export type AiPurpose = (typeof AI_PURPOSES)[number];

const PURPOSE_LABELS: Record<AiPurpose, string> = {
  'assistant.command': 'Assistant command',
  'clinical.structure-note': 'Note structuring',
  'voice.transcribe': 'Speech to text',
};

export function isAiPurpose(value: unknown): value is AiPurpose {
  return typeof value === 'string' && (AI_PURPOSES as readonly string[]).includes(value);
}

export function purposeLabel(purpose: string): string {
  return isAiPurpose(purpose) ? PURPOSE_LABELS[purpose] : purpose;
}

const AI_PROVIDER_LABELS: Record<string, string> = {
  anthropic: 'Anthropic Claude',
  whisper: 'Whisper speech server',
  none: 'Off',
  disabled: 'Off',
};

export function aiProviderLabel(provider: string | null | undefined): string {
  return provider ? (AI_PROVIDER_LABELS[provider.toLowerCase()] ?? provider) : '—';
}

export interface AiUsageFilters {
  purpose?: AiPurpose;
  userId?: string;
  from?: string;
  to?: string;
  page: number;
}

export const AI_USAGE_PAGE_SIZE = 50;

export const aiUsageQueries = {
  usage: (filters: AiUsageFilters) =>
    queryOptions({
      queryKey: ['ai', 'usage', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<AiUsageDto>>('/api/v1/ai/usage', {
          query: {
            purpose: filters.purpose,
            userId: filters.userId,
            from: filters.from ? istDayStart(filters.from) : undefined,
            to: filters.to ? istDayEnd(filters.to) : undefined,
            page: filters.page,
            pageSize: AI_USAGE_PAGE_SIZE,
          },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
};
