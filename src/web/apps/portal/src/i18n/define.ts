/**
 * The portal's tiny typed i18n. Every feature keeps its own strings in `messages/<feature>.ts` with English and Hindi
 * side by side; `defineMessages` makes TypeScript reject a Hindi table that misses (or adds) a key, and
 * `i18n.test.ts` checks the composed dictionary at runtime (no empty strings, same placeholders, no duplicate keys).
 */

export const LANGUAGES = ['en', 'hi'] as const;
export type Language = (typeof LANGUAGES)[number];

/** BCP 47 tags used for `<html lang>` and for `Intl` formatting. */
export const LOCALES: Record<Language, string> = { en: 'en-IN', hi: 'hi-IN' };

/** The language's own name, shown in the language switch (always in that language, so people can find theirs). */
export const LANGUAGE_NAMES: Record<Language, string> = { en: 'English', hi: 'हिन्दी' };

export type MessageTable<K extends string> = Readonly<Record<K, string>>;

export interface MessageSet<K extends string> {
  readonly en: MessageTable<K>;
  readonly hi: MessageTable<K>;
}

/** Declares one feature's strings. Hindi must have exactly the English keys. */
export function defineMessages<E extends Record<string, string>>(messages: {
  en: E;
  hi: { [K in keyof E]: string };
}): MessageSet<Extract<keyof E, string>> {
  return messages as MessageSet<Extract<keyof E, string>>;
}

export type MessageVars = Readonly<Record<string, string | number>>;

const PLACEHOLDER = /\{(\w+)\}/g;

/** Replaces `{name}` placeholders; unknown placeholders stay visible so a missing value is noticed, never hidden. */
export function interpolate(template: string, vars?: MessageVars): string {
  if (!vars) {
    return template;
  }
  return template.replace(PLACEHOLDER, (match, name: string) =>
    Object.hasOwn(vars, name) ? String(vars[name]) : match,
  );
}

/** Sorted placeholder names of a template (used by the dictionary tests). */
export function placeholdersOf(template: string): string[] {
  return [...template.matchAll(PLACEHOLDER)].map((match) => match[1] ?? '').sort();
}

export function isLanguage(value: unknown): value is Language {
  return typeof value === 'string' && (LANGUAGES as readonly string[]).includes(value);
}
