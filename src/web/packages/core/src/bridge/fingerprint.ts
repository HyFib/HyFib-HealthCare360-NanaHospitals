/**
 * Fingerprint readers (SRS INT-BIO). A UIDAI-certified reader's RD service runs on the workstation; HC360 Desktop finds
 * it and asks it for a capture (`bridge.captureFingerprint`), which the device encrypts for UIDAI alone. The staff app
 * passes it, unopened, to the server, which asks UIDAI through the hospital's AUA whether the finger is the holder's.
 */

/** What the server set up for capturing, or why identity cannot be checked here (`GET /api/v1/patients/identity-checks/setup`). */
export interface AadhaarCaptureSetup {
  /** `uidai`, or `simulator` for training; null when Aadhaar authentication is not set up. */
  provider: string | null;
  problem: string | null;
  /** The UIDAI environment the device encrypts for: `P`, `PP` or `S`. */
  environment: string;
  /** 0 minutiae (FMR), 1 image (FIR), 2 both. */
  fingerDataType: number;
  fingerCount: number;
  timeoutSeconds: number;
  /** The training simulator stands in for UIDAI: a training capture may be used where no reader is connected. */
  training: boolean;
}

/** The certified device an RD service serves. */
export interface FingerprintDevice {
  providerId: string;
  modelId: string;
  serialNumber: string | null;
  serviceId: string;
  serviceVersion: string;
}

/**
 * Why HC360 Desktop could not capture (`FingerprintReader.Code` in HC360 Desktop), as a `BridgeError` code. The bridge
 * itself may also answer `not_desktop` (a browser), `method_not_found` (an older HC360 Desktop) or `timeout`.
 */
export type FingerprintErrorCode =
  'no_reader' | 'not_ready' | 'busy' | 'timeout' | 'capture_failed' | 'reader_error' | 'bad_request';

/** Whether this computer's fingerprint reader can capture now, or why not. */
export interface FingerprintReaderStatus {
  ready: boolean;
  code: FingerprintErrorCode | null;
  problem: string | null;
  device: FingerprintDevice | null;
}

/** The capture the server set up for this facility (from `AadhaarCaptureSetup`). */
export type FingerprintCaptureRequest = Pick<
  AadhaarCaptureSetup,
  'fingerCount' | 'fingerDataType' | 'timeoutSeconds' | 'environment'
>;

/** A capture as the device encrypted it (`PidData`): only UIDAI can open it, so it is passed on as it came. */
export interface FingerprintCapture {
  pidData: string;
  /** The device's quality score, 0 to 100. */
  quality: number | null;
  device: FingerprintDevice | null;
}

/**
 * A capture for training with the simulator where no fingerprint reader is connected, standing for a finger that
 * matches or one that does not. It holds no fingerprint, and only the simulator accepts it: the server refuses to send
 * it to UIDAI. The same document as `RdService.TrainingCapture` (HyFib.HealthCare360.WindowsInterop).
 */
export function trainingCapture(matches: boolean): string {
  return (
    '<PidData><Resp errCode="0" errInfo="Training capture" fCount="1" fType="2" nmPoints="0" qScore="80" />' +
    '<DeviceInfo dpId="HC360.TRAINING" rdsId="HC360.TRAINING.RD" rdsVer="1.0" mi="TRAINING" mc="TRAINING" dc="TRAINING" />' +
    `<Skey ci="20991231">${matches ? 'TRAINING-MATCH' : 'TRAINING-NO-MATCH'}</Skey><Hmac>TRAINING</Hmac><Data type="X">TRAINING</Data></PidData>`
  );
}
