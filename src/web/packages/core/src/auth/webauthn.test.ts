// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  assertionJson,
  attestationJson,
  creationOptions,
  forgetUnlockKeys,
  rememberedUnlockKeys,
  rememberUnlockKey,
  requestOptions,
  toBase64Url,
  webAuthnErrorMessage,
  webAuthnUnavailableReason,
} from './webauthn';

const bytes = (...values: number[]) => new Uint8Array(values).buffer;

describe('webauthn', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    localStorage.clear();
  });

  it("turns the server's options for setting up a device into what the browser takes", () => {
    const options = creationOptions({
      rp: { id: 'hc360-srv', name: 'HealthCare360' },
      user: { id: 'AQID', name: 'asha', displayName: 'Asha Rao' },
      challenge: '_-8',
      pubKeyCredParams: [{ type: 'public-key', alg: -7 }],
      authenticatorSelection: { authenticatorAttachment: 'platform', userVerification: 'required' },
      excludeCredentials: [{ type: 'public-key', id: 'BAU', transports: ['internal'] }],
      extensions: null,
    });

    expect(new Uint8Array(options.challenge as ArrayBuffer)).toEqual(new Uint8Array([0xff, 0xef]));
    expect(new Uint8Array(options.user.id as ArrayBuffer)).toEqual(new Uint8Array([1, 2, 3]));
    expect(new Uint8Array(options.excludeCredentials![0]!.id as ArrayBuffer)).toEqual(new Uint8Array([4, 5]));
    expect(options.authenticatorSelection?.userVerification).toBe('required');
    expect('extensions' in options).toBe(false);
  });

  it("turns the server's options for unlocking into what the browser takes", () => {
    const options = requestOptions({
      challenge: 'AQID',
      rpId: 'hc360-srv',
      allowCredentials: [{ type: 'public-key', id: 'BAU', transports: ['internal'] }],
      userVerification: 'required',
    });

    expect(new Uint8Array(options.challenge as ArrayBuffer)).toEqual(new Uint8Array([1, 2, 3]));
    expect(toBase64Url(options.allowCredentials![0]!.id as ArrayBuffer)).toBe('BAU');
    expect(options.rpId).toBe('hc360-srv');
  });

  it("sends the device's answers as the server reads them", () => {
    const created = {
      id: 'AQID',
      rawId: bytes(1, 2, 3),
      type: 'public-key',
      response: { clientDataJSON: bytes(9), attestationObject: bytes(8), getTransports: () => ['internal'] },
      getClientExtensionResults: () => ({}),
    } as unknown as PublicKeyCredential;
    const signed = {
      id: 'AQID',
      rawId: bytes(1, 2, 3),
      type: 'public-key',
      response: { clientDataJSON: bytes(9), authenticatorData: bytes(7), signature: bytes(6), userHandle: null },
      getClientExtensionResults: () => ({}),
    } as unknown as PublicKeyCredential;

    expect(attestationJson(created)).toEqual({
      id: 'AQID',
      rawId: 'AQID',
      type: 'public-key',
      response: { clientDataJSON: 'CQ', attestationObject: 'CA', transports: ['internal'] },
      clientExtensionResults: {},
    });
    expect(assertionJson(signed)).toEqual({
      id: 'AQID',
      rawId: 'AQID',
      type: 'public-key',
      response: { clientDataJSON: 'CQ', authenticatorData: 'Bw', signature: 'Bg', userHandle: undefined },
      clientExtensionResults: {},
    });
  });

  it('remembers which of the keys of each user this browser holds, and forgets removed ones', () => {
    rememberUnlockKey('u1', 'AQID');
    rememberUnlockKey('u1', 'BAU');
    rememberUnlockKey('u2', 'CQ');

    expect(rememberedUnlockKeys('u1')).toEqual(['BAU', 'AQID']);
    forgetUnlockKeys('u1', ['AQID', 'other']);
    expect(rememberedUnlockKeys('u1')).toEqual(['AQID']);
    forgetUnlockKeys('u1');
    expect(rememberedUnlockKeys('u1')).toEqual([]);
    expect(rememberedUnlockKeys('u2')).toEqual(['CQ']);
  });

  it('says why a page opened by IP address or without WebAuthn cannot use Windows Hello', () => {
    vi.stubGlobal('isSecureContext', true);
    vi.stubGlobal('PublicKeyCredential', function PublicKeyCredential() {});
    vi.stubGlobal('navigator', { ...navigator, credentials: {} });

    expect(webAuthnUnavailableReason({ hostname: 'hc360-srv' })).toBeNull();
    expect(webAuthnUnavailableReason({ hostname: '192.168.1.20' })).toMatch(/not by its IP address/);
    expect(webAuthnUnavailableReason({ hostname: '[fe80::1]' })).toMatch(/not by its IP address/);

    vi.stubGlobal('PublicKeyCredential', undefined);
    expect(webAuthnUnavailableReason({ hostname: 'hc360-srv' })).toMatch(/cannot use Windows Hello/);
  });

  it('explains a cancelled or timed-out prompt without blaming the person', () => {
    expect(webAuthnErrorMessage(new DOMException('cancelled', 'NotAllowedError'))).toMatch(/cancelled or timed out/);
    expect(webAuthnErrorMessage(new DOMException('exists', 'InvalidStateError'))).toMatch(/already set up/);
  });
});
