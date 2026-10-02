import { describe, expect, it } from 'vitest';
import { changedMessage, stateLabel, turnOffQuestion } from './api';

const video = {
  name: 'Video consultations',
  whenOff: 'Video consultations disappear from the menus and nobody can join one.',
};

describe('feature switch words', () => {
  it('asks before turning a feature off, saying what stops', () => {
    expect(turnOffQuestion(video)).toEqual({
      title: 'Turn off video consultations?',
      body: 'Video consultations disappear from the menus and nobody can join one. You can turn it back on here at any time.',
      confirmLabel: 'Turn off',
    });
    expect(turnOffQuestion({ name: 'Patient portal', whenOff: 'Patients cannot sign in.' }).title).toBe(
      'Turn off patient portal?',
    );
  });

  it('says what changed', () => {
    expect(changedMessage(video, false)).toBe('Video consultations turned off');
    expect(changedMessage(video, true)).toBe('Video consultations turned on');
  });

  it('labels a switch On or Off', () => {
    expect(stateLabel(true)).toBe('On');
    expect(stateLabel(false)).toBe('Off');
  });
});
