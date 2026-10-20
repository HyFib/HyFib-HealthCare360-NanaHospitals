import { describe, expect, it } from 'vitest';
import { bridge, BridgeError } from './bridge';
import { trainingCapture } from './fingerprint';

describe('fingerprint readers', () => {
  it('makes the training capture the server knows, letter for letter', () => {
    // RdServiceTests.The_staff_apps_training_capture_is_the_one_the_server_knows checks the same documents.
    expect(trainingCapture(true)).toBe(
      '<PidData><Resp errCode="0" errInfo="Training capture" fCount="1" fType="2" nmPoints="0" qScore="80" />' +
        '<DeviceInfo dpId="HC360.TRAINING" rdsId="HC360.TRAINING.RD" rdsVer="1.0" mi="TRAINING" mc="TRAINING" dc="TRAINING" />' +
        '<Skey ci="20991231">TRAINING-MATCH</Skey><Hmac>TRAINING</Hmac><Data type="X">TRAINING</Data></PidData>',
    );
    expect(trainingCapture(false)).toBe(trainingCapture(true).replace('TRAINING-MATCH', 'TRAINING-NO-MATCH'));
  });

  it('is only in HC360 Desktop', async () => {
    await expect(bridge.fingerprintStatus()).rejects.toEqual(expect.objectContaining({ code: 'not_desktop' }));
    await expect(
      bridge.captureFingerprint({ fingerCount: 1, fingerDataType: 2, timeoutSeconds: 15, environment: 'P' }),
    ).rejects.toBeInstanceOf(BridgeError);
  });
});
