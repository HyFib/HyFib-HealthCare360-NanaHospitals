import { interpolate, LOCALES, type Language, type MessageVars } from './define';
import { DICTIONARIES, type MessageKey } from './dictionary';
import { useLanguageStore } from './store';

export type { Language, MessageVars } from './define';
export { LANGUAGE_NAMES, LANGUAGES, LOCALES } from './define';
export type { MessageKey } from './dictionary';
export { applyDocumentLanguage, useLanguageStore } from './store';

export type Translate = (key: MessageKey, vars?: MessageVars) => string;

/** A translator for one language. English fills any gap (the tests keep Hindi complete, this is only a safety net). */
export function createTranslator(language: Language): Translate {
  const table = DICTIONARIES[language];
  const fallback = DICTIONARIES.en;
  return (key, vars) => interpolate(table[key] || fallback[key] || key, vars);
}

const translators: Record<Language, Translate> = { en: createTranslator('en'), hi: createTranslator('hi') };

/** Translates outside React (print templates, toasts built in event handlers). */
export function translate(
  key: MessageKey,
  vars?: MessageVars,
  language: Language = useLanguageStore.getState().language,
): string {
  return translators[language](key, vars);
}

/** `const t = useT(); t('nav.home')` — re-renders when the language changes. */
export function useT(): Translate {
  return translators[useLanguageStore((state) => state.language)];
}

export function useLanguage(): { language: Language; locale: string; setLanguage: (language: Language) => void } {
  const language = useLanguageStore((state) => state.language);
  const setLanguage = useLanguageStore((state) => state.setLanguage);
  return { language, locale: LOCALES[language], setLanguage };
}
