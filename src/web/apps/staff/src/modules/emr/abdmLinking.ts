import { isApiError } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation } from '@tanstack/react-query';
import { api } from '@/lib/api';

/** A record the server links to an ABHA (`AbdmRecordType` on the server), by its own id. */
export type AbdmLinkRecordType =
  'DischargeSummary' | 'LabReport' | 'OpConsultation' | 'Prescription' | 'RadiologyReport' | 'CardiologyReport';

export interface AbdmLinkRecord {
  type: AbdmLinkRecordType;
  id: string;
}

/** Where a request has got to: a token asked for, the records sent, then ABDM's answer. */
export type AbdmLinkStatus = 'AwaitingToken' | 'Sent' | 'Linked' | 'Failed';

export interface AbdmLinkView {
  id: string;
  patientId: string;
  abhaAddress: string;
  status: AbdmLinkStatus;
  records: { type: AbdmLinkRecordType; id: string; referenceNumber: string; display: string }[];
  createdAt: string;
  sentAt: string | null;
  answeredAt: string | null;
  errorCode: string | null;
  error: string | null;
}

export const abdmLinksPath = '/api/v1/abdm/links';

/** How long, and how often, the screen asks after a request ABDM has not answered yet. */
export const FOLLOW_EVERY_MS = 5_000;
export const FOLLOW_TIMES = 24;

const waiting = (status: AbdmLinkStatus) => status === 'AwaitingToken' || status === 'Sent';

/**
 * Linking a record to the patient's ABHA (ABDM M2, HIP-initiated linking): the server sends it to the ABDM gateway as a
 * care context on the ABHA address in the patient's details, where the patient sees it in their PHR app. The gateway
 * answers later, on a callback, so the request is asked after every few seconds for two minutes, and the outcome told
 * then, or that ABDM has not answered yet. The server says at once why it cannot link a record (no ABHA address, the
 * gateway not set up).
 */
export function useAbdmLink() {
  const notify = useNotify();

  const tell = (view: AbdmLinkView) => {
    if (view.status === 'Linked') {
      notify.success("Linked to the patient's ABHA", `The patient sees it in their PHR app, on ${view.abhaAddress}.`);
    } else if (view.status === 'Failed') {
      notify.warning('ABDM did not link the record', view.error ?? view.errorCode ?? 'Link it again later.');
    } else {
      notify.info(
        'ABDM has not answered yet',
        `The patient sees the record in their PHR app once ABDM links it to ${view.abhaAddress}.`,
      );
    }
  };

  const follow = async (view: AbdmLinkView) => {
    for (let time = 0; time < FOLLOW_TIMES && waiting(view.status); time++) {
      await new Promise((done) => setTimeout(done, FOLLOW_EVERY_MS));
      try {
        view = await api.get<AbdmLinkView>(`${abdmLinksPath}/${view.id}`);
      } catch (error) {
        // A dropped WiFi connection or a busy server is asked again; a refusal (signed out, the record's chart no
        // longer open to this user) ends the asking.
        if (isApiError(error) && error.status > 0 && error.status < 500) {
          return;
        }
      }
    }

    tell(view);
  };

  return useMutation({
    mutationFn: (record: AbdmLinkRecord) => api.post<AbdmLinkView>(abdmLinksPath, { records: [record] }),
    onSuccess: (view) => {
      if (!waiting(view.status)) {
        tell(view);
        return;
      }

      notify.info('Sent to ABDM', `It links the record to ${view.abhaAddress}. You are told here when it answers.`);
      void follow(view);
    },
    onError: (error) => notify.error(error, "Could not link the record to the patient's ABHA"),
  });
}
