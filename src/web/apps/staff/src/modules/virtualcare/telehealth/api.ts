import type { Gender } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

export const TELEHEALTH_STATUSES = ['Scheduled', 'WaitingRoom', 'InProgress', 'Completed', 'Cancelled', 'Missed'] as const;
export type TelehealthStatus = (typeof TELEHEALTH_STATUSES)[number];
export type TelehealthRole = 'Clinician' | 'Patient' | 'Guest';

export interface IceServerDto {
  urls: string[];
  username: string | null;
  credential: string | null;
}

export interface TelehealthConfig {
  iceServers: IceServerDto[];
  earlyJoinMinutes: number;
  hubPath: string;
}

export interface TelehealthSessionListItem {
  id: string;
  appointmentId: string | null;
  appointmentNumber: string | null;
  patientId: string;
  uhid: string;
  patientName: string;
  gender: Gender;
  age: string | null;
  practitionerId: string;
  practitionerName: string;
  status: TelehealthStatus;
  scheduledStartAt: string;
  scheduledEndAt: string;
  patientWaitingSince: string | null;
  startedAt: string | null;
  endedAt: string | null;
  durationSeconds: number | null;
  visitId: string | null;
  linkSent: boolean;
}

export interface TelehealthParticipantDto {
  id: string;
  role: TelehealthRole;
  displayName: string;
  joinedAt: string;
  admittedAt: string | null;
  leftAt: string | null;
  audioOnly: boolean;
  worstRoundTripMs: number | null;
  maxPacketLossPercent: number | null;
  lowestBitrateKbps: number | null;
}

export interface TelehealthSessionDetail {
  id: string;
  facilityId: string;
  appointmentId: string | null;
  appointmentNumber: string | null;
  patientId: string;
  uhid: string;
  patientName: string;
  gender: Gender;
  age: string | null;
  phoneMasked: string | null;
  practitionerId: string;
  practitionerName: string;
  departmentId: string | null;
  visitId: string | null;
  status: TelehealthStatus;
  scheduledStartAt: string;
  scheduledEndAt: string;
  patientWaitingSince: string | null;
  startedAt: string | null;
  endedAt: string | null;
  durationSeconds: number | null;
  linkSentAt: string | null;
  linkSendCount: number;
  joinTokenExpiresAt: string | null;
  recordingConsent: boolean;
  recordingConsentAt: string | null;
  audioOnlyUsed: boolean;
  qualityNotes: string | null;
  cancelReason: string | null;
  messageCount: number;
  participants: TelehealthParticipantDto[];
  version: number;
}

export interface TelehealthMessageDto {
  id: string;
  senderRole: TelehealthRole;
  senderName: string;
  text: string;
  sentAt: string;
}

export interface CreateTelehealthSessionRequest {
  appointmentId: string | null;
  patientId: string | null;
  practitionerId: string | null;
  scheduledStartAt: string | null;
  durationMinutes: number | null;
  sendLink: boolean;
}

/** The patient's join link: shown to staff so it can be read out when SMS/WhatsApp fails. */
export interface TelehealthLinkIssued {
  sessionId: string;
  joinUrl: string;
  expiresAt: string | null;
  sent: boolean;
}

// ------------------------------------------------------------------ signalling hub messages

export interface WaitingParticipant {
  participantId: string;
  role: TelehealthRole;
  displayName: string;
  joinedAt: string;
}

export interface TelehealthJoinResult {
  sessionId: string;
  participantId: string;
  role: TelehealthRole;
  status: TelehealthStatus;
  admitted: boolean;
  scheduledStartAt: string;
  practitionerName: string;
  patientName: string;
  iceServers: IceServerDto[];
  waiting: WaitingParticipant[];
  recentMessages: TelehealthMessageDto[];
}

export interface ConnectionQualitySample {
  roundTripMs: number | null;
  packetLossPercent: number | null;
  bitrateKbps: number | null;
  audioOnly: boolean;
}

// ------------------------------------------------------------------ queries and commands

export interface SessionFilters {
  date: string;
  to?: string;
  practitionerId?: string;
  status?: TelehealthStatus;
  mine?: boolean;
}

export const telehealthKeys = {
  all: ['telehealth'] as const,
  sessions: ['telehealth', 'sessions'] as const,
  session: (id: string) => ['telehealth', 'session', id] as const,
  messages: (id: string) => ['telehealth', 'messages', id] as const,
};

const BASE = '/api/v1/telehealth';

export const telehealthQueries = {
  config: () =>
    queryOptions({
      queryKey: ['telehealth', 'config'],
      queryFn: ({ signal }) => api.get<TelehealthConfig>(`${BASE}/config`, { signal }),
      staleTime: 10 * 60_000,
    }),
  sessions: (filters: SessionFilters) =>
    queryOptions({
      queryKey: [...telehealthKeys.sessions, filters],
      queryFn: ({ signal }) => api.get<TelehealthSessionListItem[]>(`${BASE}/sessions`, { query: { ...filters }, signal }),
      placeholderData: keepPreviousData,
      refetchInterval: 60_000,
    }),
  session: (id: string) =>
    queryOptions({
      queryKey: telehealthKeys.session(id),
      queryFn: ({ signal }) => api.get<TelehealthSessionDetail>(`${BASE}/sessions/${id}`, { signal }),
      refetchInterval: 30_000,
    }),
  messages: (id: string) =>
    queryOptions({
      queryKey: telehealthKeys.messages(id),
      queryFn: ({ signal }) => api.get<TelehealthMessageDto[]>(`${BASE}/sessions/${id}/messages`, { signal }),
    }),
};

export const telehealthApi = {
  create: (request: CreateTelehealthSessionRequest) => api.post<TelehealthLinkIssued>(`${BASE}/sessions`, request),
  start: (id: string) => api.post<TelehealthSessionDetail>(`${BASE}/sessions/${id}/start`),
  end: (id: string, qualityNotes: string | null) => api.post<TelehealthSessionDetail>(`${BASE}/sessions/${id}/end`, { qualityNotes }),
  cancel: (id: string, reason: string) => api.post<TelehealthSessionDetail>(`${BASE}/sessions/${id}/cancel`, { reason }),
  resendLink: (id: string) => api.post<TelehealthLinkIssued>(`${BASE}/sessions/${id}/resend-link`),
  recordingConsent: (id: string, granted: boolean) => api.post<void>(`${BASE}/sessions/${id}/recording-consent`, { granted }),
  postMessage: (id: string, text: string) => api.post<TelehealthMessageDto>(`${BASE}/sessions/${id}/messages`, { text }),
};
