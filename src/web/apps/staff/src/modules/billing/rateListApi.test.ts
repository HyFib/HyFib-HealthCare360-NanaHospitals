import { describe, expect, it } from 'vitest';
import { LANGUAGE_SETTING, languageNotes, type RateListView } from './rateListApi';

type Languages = Pick<RateListView, 'language' | 'localLanguage' | 'unknownSetting' | 'unknownState'>;

const tamil: Languages = {
  language: 'ta',
  localLanguage: { code: 'ta', name: 'Tamil' },
  unknownSetting: null,
  unknownState: null,
};

describe('what the rate list screen says about its languages', () => {
  it('names the local language the list is printed in', () => {
    expect(languageNotes(tamil)).toEqual([{ intent: 'info', text: expect.stringContaining('in English and Tamil') }]);
  });

  it('says when HC360 does not yet write the local language, whose service names still print', () => {
    const [note] = languageNotes({ ...tamil, language: null, localLanguage: { code: 'kn', name: 'Kannada' } });
    expect(note?.intent).toBe('warning');
    expect(note?.text).toContain('not yet in Kannada');
    expect(note?.text).toContain("Each service's name in Kannada prints under its English name");
  });

  it('quotes a state it does not know, and says where to correct it', () => {
    const [note] = languageNotes({ ...tamil, language: null, localLanguage: null, unknownState: 'Tamizh Naadu' });
    expect(note?.intent).toBe('warning');
    expect(note?.text).toContain('"Tamizh Naadu"');
    expect(note?.text).toContain('Setup › Facilities');
  });

  it('asks for the language where the state has none of its own', () => {
    const [note] = languageNotes({ ...tamil, language: null, localLanguage: null });
    expect(note?.text).toContain(`Administration › Settings (${LANGUAGE_SETTING})`);
  });

  it('says when the hospital chose a language HC360 does not know, beside the one it uses meanwhile', () => {
    const notes = languageNotes({ ...tamil, unknownSetting: 'Tamizh' });
    expect(notes.map((n) => n.intent)).toEqual(['warning', 'info']);
    expect(notes[0]?.text).toContain('("Tamizh") names no language');
  });
});
