import { describe, expect, it } from 'vitest';
import { interpolate, isLanguage, LANGUAGES, placeholdersOf, type Language } from './define';
import { DICTIONARIES, MESSAGE_SETS, type MessageKey } from './dictionary';
import { createTranslator } from './index';
import { initialLanguage } from './store';

const sets = Object.entries(MESSAGE_SETS) as [string, Record<Language, Record<string, string>>][];
const translations = LANGUAGES.filter((language) => language !== 'en');

describe('message sets', () => {
  it.each(sets)('%s has the same keys in English, Hindi and Tamil', (_, set) => {
    for (const language of translations) {
      expect(Object.keys(set[language]).sort(), language).toEqual(Object.keys(set.en).sort());
    }
  });

  it.each(sets)('%s has no empty or untrimmed text', (_, set) => {
    for (const language of LANGUAGES) {
      for (const [key, value] of Object.entries(set[language])) {
        expect(typeof value, `${language} ${key}`).toBe('string');
        expect(value.trim().length, `${language} ${key} is empty`).toBeGreaterThan(0);
        expect(value, `${language} ${key} has spaces around it`).toBe(value.trim());
      }
    }
  });

  it.each(sets)('%s uses the same placeholders in every language', (_, set) => {
    for (const language of translations) {
      for (const [key, english] of Object.entries(set.en)) {
        expect(placeholdersOf(set[language][key] ?? ''), `${language} ${key}`).toEqual(placeholdersOf(english));
      }
    }
  });

  it('never defines a key in two sets (a later set would silently win)', () => {
    const owners = new Map<string, string>();
    const duplicates: string[] = [];
    for (const [name, set] of sets) {
      for (const key of Object.keys(set.en)) {
        const owner = owners.get(key);
        if (owner) {
          duplicates.push(`${key} (${owner} and ${name})`);
        }
        owners.set(key, name);
      }
    }
    expect(duplicates).toEqual([]);
  });

  it.each(translations)('actually translates: %s text differs from English for most keys', (language) => {
    const keys = Object.keys(DICTIONARIES.en) as MessageKey[];
    const same = keys.filter((key) => DICTIONARIES[language][key] === DICTIONARIES.en[key]);
    // A few strings are the same in both (UHID, product names); anything more means a table was copied untranslated.
    expect(same.length / keys.length).toBeLessThan(0.05);
  });

  it('keeps Hindi text in Devanagari (not transliterated English)', () => {
    const keys = Object.keys(DICTIONARIES.hi) as MessageKey[];
    const latinOnly = keys.filter((key) => {
      const text = DICTIONARIES.hi[key].replace(/\{\w+\}/g, '');
      return /[A-Za-z]{3,}/.test(text) && !/[ऀ-ॿ]/.test(text);
    });
    expect(latinOnly.length / keys.length).toBeLessThan(0.05);
  });

  it('keeps Tamil text in Tamil script (not transliterated English)', () => {
    const keys = Object.keys(DICTIONARIES.ta) as MessageKey[];
    const latinOnly = keys.filter((key) => {
      const text = DICTIONARIES.ta[key].replace(/\{\w+\}/g, '');
      return /[A-Za-z]{3,}/.test(text) && !/[\u0B80-\u0BFF]/.test(text);
    });
    expect(latinOnly.length / keys.length).toBeLessThan(0.05);
  });
});

describe('interpolate', () => {
  it('fills placeholders and leaves unknown ones visible', () => {
    expect(interpolate('Hello {name}, {count} new', { name: 'Asha', count: 3 })).toBe('Hello Asha, 3 new');
    expect(interpolate('Hello {name}', {})).toBe('Hello {name}');
    expect(interpolate('No placeholders')).toBe('No placeholders');
  });

  it('does not read inherited properties as values', () => {
    expect(interpolate('{constructor}', {})).toBe('{constructor}');
  });

  it('inserts values as plain text (React escapes them when rendering)', () => {
    expect(interpolate('{x}', { x: '<b>' })).toBe('<b>');
  });

  it('lists placeholders sorted', () => {
    expect(placeholdersOf('{b} and {a} and {b}')).toEqual(['a', 'b', 'b']);
  });
});

describe('translator', () => {
  it('translates into each language', () => {
    expect(createTranslator('en')('nav.home')).toBe('Home');
    expect(createTranslator('hi')('nav.home')).not.toBe('Home');
    expect(createTranslator('en')('common.forPerson', { name: 'Ravi' })).toBe('For Ravi');
  });
});

describe('language choice', () => {
  it('uses the stored choice first', () => {
    expect(initialLanguage('hi', ['en-US'])).toBe('hi');
    expect(initialLanguage('en', ['hi-IN'])).toBe('en');
  });

  it('uses Hindi or Tamil when the browser lists it, English otherwise', () => {
    expect(initialLanguage(null, ['hi-IN', 'en'])).toBe('hi');
    expect(initialLanguage(null, ['en-IN', 'hi'])).toBe('hi');
    expect(initialLanguage(null, ['ta-IN'])).toBe('ta');
    expect(initialLanguage(null, ['en-IN', 'ta_IN', 'hi'])).toBe('ta');
    expect(initialLanguage(null, ['mr-IN', 'en'])).toBe('en');
    expect(initialLanguage('fr', [])).toBe('en');
  });

  it('recognises supported languages only', () => {
    expect(isLanguage('en')).toBe(true);
    expect(isLanguage('hi')).toBe(true);
    expect(isLanguage('HI')).toBe(false);
    expect(isLanguage(undefined)).toBe(false);
  });
});
