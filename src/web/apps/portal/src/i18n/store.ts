import { create } from 'zustand';
import { isLanguage, LOCALES, type Language } from './define';

const STORAGE_KEY = 'hc360:portal:language';

/**
 * First visit: Hindi when the phone or browser prefers Hindi, otherwise English. After that the patient's own choice
 * is remembered on this device (it is a display preference, not personal data).
 */
export function initialLanguage(stored: string | null, preferred: readonly string[]): Language {
  if (isLanguage(stored)) {
    return stored;
  }
  return preferred.some((tag) => tag.toLowerCase().startsWith('hi')) ? 'hi' : 'en';
}

function readStored(): string | null {
  try {
    return localStorage.getItem(STORAGE_KEY);
  } catch {
    return null;
  }
}

function browserLanguages(): readonly string[] {
  if (typeof navigator === 'undefined') {
    return [];
  }
  return navigator.languages?.length ? navigator.languages : [navigator.language ?? ''];
}

/** Keeps `<html lang>` in step with the UI so screen readers pick the right voice and browsers the right fonts. */
export function applyDocumentLanguage(language: Language): void {
  if (typeof document !== 'undefined') {
    document.documentElement.lang = LOCALES[language];
  }
}

interface LanguageState {
  language: Language;
  setLanguage(language: Language): void;
}

export const useLanguageStore = create<LanguageState>((set) => ({
  language: initialLanguage(readStored(), browserLanguages()),
  setLanguage: (language) => {
    try {
      localStorage.setItem(STORAGE_KEY, language);
    } catch {
      // Private mode or storage full: the choice still applies to this visit.
    }
    applyDocumentLanguage(language);
    set({ language });
  },
}));
