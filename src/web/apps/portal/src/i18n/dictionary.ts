import type { Language } from './define';
import { appointmentMessages } from './messages/appointments';
import { authMessages } from './messages/auth';
import { billMessages } from './messages/bills';
import { common } from './messages/common';
import { doctorMessages } from './messages/doctors';
import { feedbackMessages } from './messages/feedback';
import { homeMessages } from './messages/home';
import { myPrivacyMessages } from './messages/myPrivacy';
import { prescriptionMessages } from './messages/prescriptions';
import { profileMessages } from './messages/profile';
import { recordMessages } from './messages/records';
import { reportMessages } from './messages/reports';
import { verifyMessages } from './messages/verify';
import { videoMessages } from './messages/video';

/**
 * Every feature's message set. Adding a feature = add its `messages/<feature>.ts` here; the dictionary tests then
 * check it (both languages complete, same placeholders, no key defined twice).
 */
export const MESSAGE_SETS = {
  common,
  auth: authMessages,
  doctors: doctorMessages,
  appointments: appointmentMessages,
  video: videoMessages,
  reports: reportMessages,
  prescriptions: prescriptionMessages,
  bills: billMessages,
  records: recordMessages,
  feedback: feedbackMessages,
  profile: profileMessages,
  myPrivacy: myPrivacyMessages,
  verify: verifyMessages,
  home: homeMessages,
} as const;

type Sets = typeof MESSAGE_SETS;
type KeysOf<T> = T extends { en: infer E } ? keyof E : never;

export type MessageKey = { [K in keyof Sets]: KeysOf<Sets[K]> }[keyof Sets] & string;

function compose(language: Language): Readonly<Record<MessageKey, string>> {
  const table: Record<string, string> = {};
  for (const set of Object.values(MESSAGE_SETS)) {
    Object.assign(table, set[language]);
  }
  return table as Record<MessageKey, string>;
}

export const DICTIONARIES: Readonly<Record<Language, Readonly<Record<MessageKey, string>>>> = {
  en: compose('en'),
  hi: compose('hi'),
};
