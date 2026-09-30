import { ApiError } from '@hc360/core';
import { describe, expect, it } from 'vitest';
import type { LabReportSection, PublicSlot } from '@/lib/types';
import { isDue, totalDue } from './bills/labels';
import {
  chooseDay,
  dayPartOf,
  firstFreeDay,
  freeSeatsByDay,
  groupByDayPart,
  stripDays,
  stripStartFor,
} from './doctors/slots';
import {
  checkAnswers,
  emptyAnswers,
  hasAnswer,
  linkProblem,
  ratingField,
  toFeedbackRequest,
  type FeedbackQuestions,
} from './feedback/model';
import { greetingKey } from './home/greeting';
import { frequencyText, medicineName } from './prescriptions/labels';
import { splitLines } from './records/labels';
import { parseRange, parseResultNumber, rangePosition, summarizeResults } from './reports/labResults';
import { reportVerifyUrl } from './reports/verifyLink';
import {
  canFallBackToAudio,
  classifyJoinError,
  classifyMediaError,
  detectPlatform,
  isInAppBrowser,
  mediaSupport,
} from './video/media';
import {
  isWellFormedCode,
  normalizeReportCode,
  parseVerificationInput,
  verificationPagePath,
  verificationPageUrl,
} from './verify/verification';

describe('slots', () => {
  const slot = (localTime: string, seatsLeft = 1): PublicSlot =>
    ({ startsAt: `2026-10-01T${localTime}+05:30`, localTime, seatsLeft }) as unknown as PublicSlot;

  it('splits the day into morning, afternoon and evening', () => {
    expect(dayPartOf('09:15:00')).toBe('morning');
    expect(dayPartOf('12:00:00')).toBe('afternoon');
    expect(dayPartOf('15:59:00')).toBe('afternoon');
    expect(dayPartOf('16:00:00')).toBe('evening');
  });

  it('groups free slots in time order and drops full ones', () => {
    const groups = groupByDayPart([slot('17:00:00'), slot('09:30:00'), slot('09:00:00'), slot('13:00:00', 0)]);
    expect(groups.map((group) => group.part)).toEqual(['morning', 'evening']);
    expect(groups[0]?.slots.map((s) => s.localTime)).toEqual(['09:00:00', '09:30:00']);
  });

  it('builds the date strip within the booking window', () => {
    expect(stripDays('2026-10-01', '2026-10-03')).toEqual(['2026-10-01', '2026-10-02', '2026-10-03']);
    expect(stripDays('2026-10-01', '2026-12-31')).toHaveLength(7);
    expect(stripStartFor('2026-10-09', '2026-10-01')).toBe('2026-10-08');
    expect(stripStartFor('2026-09-20', '2026-10-01')).toBe('2026-10-01');
  });

  it('keeps the chosen day while it is free, else moves to the first free day', () => {
    const counts = freeSeatsByDay([
      { date: '2026-10-01', slots: [slot('09:00:00', 0)] },
      { date: '2026-10-02', slots: [slot('09:00:00', 2), slot('10:00:00', 1)] },
      { date: '2026-10-03', slots: [slot('09:00:00', 1)] },
    ] as never);
    const days = ['2026-10-01', '2026-10-02', '2026-10-03'];
    expect(counts.get('2026-10-02')).toBe(3);
    expect(firstFreeDay(days, counts)).toBe('2026-10-02');
    expect(chooseDay('2026-10-03', days, counts)).toBe('2026-10-03');
    expect(chooseDay('2026-10-01', days, counts)).toBe('2026-10-02');
    expect(chooseDay(null, ['2026-10-01'], counts)).toBeNull();
  });
});

describe('lab results in plain language', () => {
  it('reads numbers written on reports', () => {
    expect(parseResultNumber('1,50,000')).toBe(150000);
    expect(parseResultNumber(' -0.5 ')).toBe(-0.5);
    expect(parseResultNumber('Negative')).toBeNull();
    expect(parseResultNumber('')).toBeNull();
  });

  it('reads reference ranges', () => {
    expect(parseRange('12.0 - 15.5')).toEqual({ low: 12, high: 15.5 });
    expect(parseRange('12–15.5 g/dL')).toEqual({ low: 12, high: 15.5 });
    expect(parseRange('4 to 11')).toEqual({ low: 4, high: 11 });
    expect(parseRange('< 200')).toEqual({ low: null, high: 200 });
    expect(parseRange('Up to 40')).toEqual({ low: null, high: 40 });
    expect(parseRange('≥ 40')).toEqual({ low: 40, high: null });
    expect(parseRange('Negative')).toBeNull();
    expect(parseRange('15 - 12')).toBeNull();
  });

  it('places a value on the range bar (normal range in the middle half)', () => {
    const range = { low: 10, high: 20 };
    expect(rangePosition(10, range)).toBeCloseTo(0.25);
    expect(rangePosition(15, range)).toBeCloseTo(0.5);
    expect(rangePosition(20, range)).toBeCloseTo(0.75);
    expect(rangePosition(-100, range)).toBe(0);
    expect(rangePosition(1000, range)).toBe(1);
    expect(rangePosition(100, { low: null, high: 200 })).toBeCloseTo(0.375);
  });

  it('counts results outside the range and spots critical ones', () => {
    const sections = [
      {
        tests: [
          {
            results: [
              { value: '13.2', flag: 'Normal' },
              { value: '2.1', flag: 'Low' },
              { value: '', flag: 'High' },
              { value: '900', flag: 'CriticalHigh' },
            ],
          },
        ],
      },
    ] as unknown as LabReportSection[];
    expect(summarizeResults(sections)).toEqual({ total: 3, outOfRange: 2, critical: true });
  });
});

describe('report QR codes', () => {
  it('opens the public check page: the server address when known, else this address', () => {
    const code = 'ABCDEFGHJKLMNPQRSTUV';
    expect(
      reportVerifyUrl(
        'lab',
        { verificationCode: code, verificationUrl: `https://care.example/portal/verify/lab/${code}` },
        'https://lan.local',
      ),
    ).toBe(`https://care.example/portal/verify/lab/${code}`);
    expect(reportVerifyUrl('radiology', { verificationCode: code, verificationUrl: null }, 'https://lan.local')).toBe(
      `https://lan.local/portal/verify/radiology/${code}`,
    );
    expect(
      reportVerifyUrl('lab', { verificationCode: code, verificationUrl: 'javascript:alert(1)' }, 'https://h'),
    ).toBe(`https://h/portal/verify/lab/${code}`);
    expect(reportVerifyUrl('cardiology', { verificationCode: null }, 'https://h')).toBeNull();
  });

  it('gives prescriptions the same choice of address', () => {
    const token = 'k3P9xLr0bPz7Kd2Nf5Hs';
    expect(
      verificationPageUrl(
        'prescription',
        token,
        `https://care.example/portal/verify/prescription/${token}`,
        'https://lan.local',
      ),
    ).toBe(`https://care.example/portal/verify/prescription/${token}`);
    expect(verificationPageUrl('prescription', token, undefined, 'https://lan.local')).toBe(
      `https://lan.local/portal/verify/prescription/${token}`,
    );
    expect(verificationPageUrl('prescription', null, null, 'https://lan.local')).toBeNull();
  });
});

describe('video checks', () => {
  it('maps camera errors to one fixable problem', () => {
    expect(classifyMediaError({ name: 'NotAllowedError' })).toBe('denied');
    expect(classifyMediaError({ name: 'NotFoundError' })).toBe('notFound');
    expect(classifyMediaError({ name: 'NotReadableError' })).toBe('inUse');
    expect(classifyMediaError(null)).toBe('unknown');
    expect(canFallBackToAudio('denied')).toBe(false);
    expect(canFallBackToAudio('inUse')).toBe(true);
  });

  it('knows the platform and in-app browsers', () => {
    expect(detectPlatform('Mozilla/5.0 (Linux; Android 14)')).toBe('android');
    expect(detectPlatform('Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15)', 5)).toBe('ios');
    expect(detectPlatform('Mozilla/5.0 (Windows NT 10.0)')).toBe('desktop');
    expect(isInAppBrowser('Mozilla/5.0 ... [FBAN/FBIOS;FBAV/400.0]')).toBe(true);
    expect(isInAppBrowser('Mozilla/5.0 (Linux; Android 14) Chrome/130.0 Mobile Safari/537.36')).toBe(false);
  });

  it('needs a secure page, a camera API and WebRTC', () => {
    expect(mediaSupport({ isSecureContext: false, hasGetUserMedia: true, hasPeerConnection: true })).toBe('insecure');
    expect(mediaSupport({ isSecureContext: true, hasGetUserMedia: false, hasPeerConnection: true })).toBe(
      'unsupported',
    );
    expect(mediaSupport({ isSecureContext: true, hasGetUserMedia: true, hasPeerConnection: true })).toBe('ok');
  });

  it('explains refused joins', () => {
    expect(classifyJoinError('You can join 15 minutes before the appointment time.')).toBe('tooEarly');
    expect(classifyJoinError('This consultation was cancelled.')).toBe('cancelled');
    expect(classifyJoinError('The join link has expired.')).toBe('linkExpired');
    expect(classifyJoinError('Something else')).toBe('other');
  });
});

describe('document verification input', () => {
  const code = 'ABCDEFGHJKLMNPQRSTUV';

  it('reads printed QR links (API or portal addresses)', () => {
    expect(
      parseVerificationInput(`https://hospital.example/api/v1/lab/reports/verify/${code}`, 'prescription'),
    ).toEqual({ kind: 'lab', code });
    expect(
      parseVerificationInput('https://h.example/api/v1/public/prescriptions/verify/Abc_def-0123456789', 'lab'),
    ).toEqual({
      kind: 'prescription',
      code: 'Abc_def-0123456789',
    });
    expect(parseVerificationInput(`https://h.example/portal/verify/radiology/${code.toLowerCase()}`, 'lab')).toEqual({
      kind: 'radiology',
      code,
    });
  });

  it('reads a typed code with the chosen kind, forgiving spaces and dashes', () => {
    expect(parseVerificationInput('abcd-efgh jklm npqr stuv', 'cardiology')).toEqual({ kind: 'cardiology', code });
    expect(normalizeReportCode('ab-cd ef')).toBe('ABCDEF');
  });

  it('refuses anything else', () => {
    expect(parseVerificationInput('   ', 'lab')).toEqual({ error: 'empty' });
    expect(parseVerificationInput('https://evil.example/whatever', 'lab')).toEqual({ error: 'unrecognised' });
    expect(parseVerificationInput('ABCDEFGHJKLMNPQRSTU0', 'lab')).toEqual({ error: 'unrecognised' });
    expect(isWellFormedCode('prescription', 'short')).toBe(false);
  });

  it('builds the portal page for a code', () => {
    expect(verificationPagePath('lab', code)).toBe(`/verify/lab/${code}`);
    expect(verificationPagePath('prescription', 'a/b')).toBe('/verify/prescription/a%2Fb');
  });
});

describe('feedback', () => {
  const questions: FeedbackQuestions = { aspects: ['Overall', 'FrontDesk'], includesNps: true, allowsComment: true };

  it('needs at least one answer', () => {
    expect(hasAnswer(emptyAnswers(), questions)).toBe(false);
    expect(hasAnswer({ ...emptyAnswers(), nps: 0 }, questions)).toBe(true);
    expect(hasAnswer({ ...emptyAnswers(), comment: '  ' }, questions)).toBe(false);
    expect(hasAnswer({ ...emptyAnswers(), ratings: { Doctor: 5 } }, questions)).toBe(false);
    expect(checkAnswers(emptyAnswers(), questions).form).toBe('feedback.empty');
  });

  it('asks for a valid phone number only for a call-back', () => {
    const answers = { ...emptyAnswers(), ratings: { Overall: 2 }, wantsCallback: true, contactPhone: '12345' };
    expect(checkAnswers(answers, questions).contactPhone).toBe('feedback.phone.invalid');
    expect(checkAnswers({ ...answers, contactPhone: '' }, questions).contactPhone).toBe('feedback.phone.required');
    expect(checkAnswers({ ...answers, contactPhone: '98765 43210' }, questions)).toEqual({});
  });

  it('sends only what the form asks, and a phone number only for a call-back', () => {
    const body = toFeedbackRequest(
      {
        ratings: { Overall: 4, FrontDesk: 2, Food: 5 },
        nps: 11,
        comment: '  Long wait  ',
        wantsCallback: false,
        contactPhone: '9876543210',
      },
      questions,
    );
    expect(body).toEqual({
      ratings: { overall: 4, frontDesk: 2 },
      nps: null,
      comment: 'Long wait',
      wantsCallback: false,
      contactPhone: null,
    });
    const callback = toFeedbackRequest(
      { ...emptyAnswers('9876543210'), nps: 3, wantsCallback: true },
      { ...questions, allowsComment: false },
    );
    expect(callback).toEqual({
      ratings: null,
      nps: 3,
      comment: null,
      wantsCallback: true,
      contactPhone: '+919876543210',
    });
    expect(ratingField('FrontDesk')).toBe('frontDesk');
  });

  it('knows why a personal link cannot be used', () => {
    expect(linkProblem(new ApiError({ status: 404, title: 'x' }))).toBe('invalid');
    expect(linkProblem(new ApiError({ status: 422, title: 'x', code: 'feedback.link_used' }))).toBe('used');
    expect(linkProblem(new ApiError({ status: 409, title: 'x' }))).toBe('used');
    expect(linkProblem(new ApiError({ status: 422, title: 'x', code: 'feedback.link_expired' }))).toBe('expired');
    expect(linkProblem(new ApiError({ status: 500, title: 'x' }))).toBeNull();
    expect(linkProblem(new Error('x'))).toBeNull();
  });
});

describe('bills', () => {
  it('ignores rounding noise when deciding what is due', () => {
    expect(isDue(0.004)).toBe(false);
    expect(isDue(0.01)).toBe(true);
    expect(totalDue([{ dueAmount: 100.1 }, { dueAmount: 0.2 }, { dueAmount: -5 }])).toBe(100.3);
  });
});

describe('prescriptions', () => {
  it('writes directions in the patient language with the pattern alongside', () => {
    const line = { frequency: '1-0-1', frequencyEnglish: 'Twice a day', frequencyHindi: 'दिन में दो बार' };
    expect(frequencyText(line, 'en')).toBe('Twice a day (1-0-1)');
    expect(frequencyText(line, 'hi')).toBe('दिन में दो बार (1-0-1)');
    expect(frequencyText({ ...line, frequencyHindi: '' }, 'hi')).toBe('Twice a day (1-0-1)');
    expect(frequencyText({ frequency: 'SOS', frequencyEnglish: 'When needed (SOS)', frequencyHindi: '' }, 'en')).toBe(
      'When needed (SOS)',
    );
    expect(medicineName({ drugName: 'Paracetamol', strength: '500 mg', dosageForm: null })).toBe('Paracetamol 500 mg');
  });
});

describe('records', () => {
  it('splits a free-text list into its lines', () => {
    expect(splitLines('Dengue fever\r\n\n  Dehydration  \n')).toEqual(['Dengue fever', 'Dehydration']);
    expect(splitLines(null)).toEqual([]);
  });
});

describe('home', () => {
  it('greets by the hospital clock', () => {
    expect(greetingKey(Date.parse('2026-09-28T03:00:00Z'))).toBe('home.greeting.morning'); // 08:30 IST
    expect(greetingKey(Date.parse('2026-09-28T08:00:00Z'))).toBe('home.greeting.afternoon'); // 13:30 IST
    expect(greetingKey(Date.parse('2026-09-28T14:00:00Z'))).toBe('home.greeting.evening'); // 19:30 IST
    expect(greetingKey(Date.parse('2026-09-27T21:00:00Z'))).toBe('home.greeting.evening'); // 02:30 IST
  });
});
