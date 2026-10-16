import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Core from '@hc360/core';
import { ApiError, bridge, BridgeError, useSession, type AuthUser } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { DigitalSignatureBadge, DigitalSignatureSection, SignedPrescriptionNotice } from './DigitalSignature';
import { afterRefill, desktopSigningProblem, digitalSignatureBadge, signWithDsc } from './dscSigning';
import type * as PrescriptionsApi from './prescriptionsApi';
import { prescriptionsApi, type PrescriptionDigitalSignatureDto, type PrescriptionDto } from './prescriptionsApi';

// The server and HC360 Desktop are outside the test: the server calls are replaced, and the desktop bridge can be
// switched on and made to sign (or refuse).
const desktop = vi.hoisted(() => ({ on: true }));
vi.mock('@hc360/core', async (importOriginal) => {
  const core = await importOriginal<typeof Core>();
  return {
    ...core,
    saveFile: vi.fn(),
    bridge: {
      ...core.bridge,
      get isDesktop() {
        return desktop.on;
      },
      signPrescription: vi.fn(),
    },
  };
});
const readSignature = vi.hoisted(() => vi.fn());
vi.mock('./prescriptionsApi', async (importOriginal) => {
  const actual = await importOriginal<typeof PrescriptionsApi>();
  return {
    ...actual,
    prescriptionsApi: { ...actual.prescriptionsApi, signingContent: vi.fn(), attachSignature: vi.fn() },
    prescriptionQueries: {
      ...actual.prescriptionQueries,
      signature: (id: string) => ({
        queryKey: ['emr', 'prescription', id, 'signature'],
        queryFn: () => readSignature(),
      }),
    },
  };
});
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const RX_ID = '0192a000-0000-7000-8000-00000000rx01';
const HASH = '2eacf6b6cd60daa5cf8e87fa9a5709f8f7d3316e3921e7f6b4783c60d027569d';

function signature(patch: Partial<PrescriptionDigitalSignatureDto>): PrescriptionDigitalSignatureDto {
  return {
    state: 'NotSigned',
    policy: 'Optional',
    signedAt: null,
    signerName: null,
    signerSubject: null,
    certificateSerialNumber: null,
    certificateIssuer: null,
    certificateThumbprint: null,
    contentHash: null,
    contentMatchesRecord: false,
    chainStatus: null,
    chainTrusted: false,
    message: null,
    ...patch,
  };
}

const VALID = signature({
  state: 'Valid',
  signedAt: '2026-10-16T07:57:15Z',
  signerName: 'Dr Meera Shah',
  signerSubject: 'C=IN, O=Personal, CN=Dr Meera Shah',
  certificateSerialNumber: '18B3D635546B372D',
  certificateIssuer: 'CN=e-Mudhra Sub CA for Class 3 Individual 2022',
  certificateThumbprint: 'A1B2C3',
  contentHash: HASH,
  contentMatchesRecord: true,
  chainStatus: 'Trusted',
  chainTrusted: true,
  message: 'The signature is valid.',
});

const PRESCRIBER_ID = '0192a000-0000-7000-8000-0000000doc01';

const RX = {
  id: RX_ID,
  status: 'Signed',
  prescriptionNumber: 'NHRX260000123',
  practitionerId: PRESCRIBER_ID,
  practitionerName: 'Dr Meera Shah',
  isInpatient: false,
} as PrescriptionDto;

/** Signs in as a doctor: the prescription's prescriber unless told otherwise. */
function signInAs(practitionerId: string | null = PRESCRIBER_ID) {
  useSession.getState().setAuth({
    accessToken: 'test-token',
    expiresAt: '2099-01-01T00:00:00Z',
    user: {
      id: 'user-1',
      displayName: 'Doctor',
      practitionerId,
      permissions: ['clinical.prescriptions.sign'],
    } as AuthUser,
  });
}

function renderWith(node: React.ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <FluentProvider theme={webLightTheme}>{node}</FluentProvider>
    </QueryClientProvider>,
  );
}

describe('signing a prescription with a DSC', () => {
  beforeEach(() => {
    desktop.on = true;
    signInAs();
    readSignature.mockReset();
    vi.mocked(bridge.signPrescription).mockReset();
    vi.mocked(prescriptionsApi.signingContent).mockReset();
    vi.mocked(prescriptionsApi.attachSignature).mockReset();
    Object.values(notify).forEach((spy) => spy.mockClear());
  });

  it('signs the bytes the server hands out and sends the signature back', async () => {
    vi.mocked(prescriptionsApi.signingContent).mockResolvedValue({
      prescriptionId: RX_ID,
      prescriptionNumber: 'NHRX260000123',
      format: 'hc360.prescription.v1',
      content: 'eyJmb3JtYXQiOiJoYzM2MC5wcmVzY3JpcHRpb24udjEifQ==',
      contentHash: HASH,
      patientName: 'Asha Rao',
      patientUhid: 'NH-000123',
      itemCount: 1,
    });
    vi.mocked(bridge.signPrescription).mockResolvedValue({
      signature: 'MIIB',
      certificate: {} as Core.SignerCertificate,
    });
    vi.mocked(prescriptionsApi.attachSignature).mockResolvedValue(VALID);

    await expect(signWithDsc(RX_ID)).resolves.toBe(VALID);
    expect(bridge.signPrescription).toHaveBeenCalledWith('eyJmb3JtYXQiOiJoYzM2MC5wcmVzY3JpcHRpb24udjEifQ==');
    expect(prescriptionsApi.attachSignature).toHaveBeenCalledWith(RX_ID, 'MIIB');
  });

  it('tells a cancelled dialog and an old desktop apart from a server refusal', () => {
    expect(desktopSigningProblem(new BridgeError('cancelled', 'No certificate was chosen.'))?.intent).toBe('info');
    expect(desktopSigningProblem(new BridgeError('method_not_found', 'x'))?.title).toBe('Update HC360 Desktop');
    expect(desktopSigningProblem(new BridgeError('signing_failed', 'Check the PIN.'))).toEqual({
      intent: 'warning',
      title: 'The DSC could not sign',
      body: 'Check the PIN.',
    });
    expect(desktopSigningProblem(new ApiError({ status: 422, title: 'Refused' }))).toBeNull();
  });

  it('shows a badge only once the hospital uses DSC signing', () => {
    expect(digitalSignatureBadge(signature({ policy: 'Off' }))).toBeNull();
    expect(digitalSignatureBadge(signature({ policy: 'Optional' }))?.label).toBe('Not DSC signed');
    expect(digitalSignatureBadge(signature({ policy: 'Required' }))?.color).toBe('warning');
    expect(digitalSignatureBadge(signature({ state: 'Valid', policy: 'Off' }))?.label).toBe('DSC signed');
    expect(digitalSignatureBadge(signature({ state: 'Invalid' }))?.color).toBe('danger');
    expect(digitalSignatureBadge(signature({ state: 'Untrusted' }))).toEqual({
      label: 'DSC issuer not verified',
      color: 'warning',
    });
  });

  it('offers "Sign with DSC" in HC360 Desktop and shows the signature once signed', async () => {
    const user = userEvent.setup();
    readSignature.mockResolvedValueOnce(signature({ policy: 'Required' }));
    vi.mocked(prescriptionsApi.signingContent).mockResolvedValue({
      content: 'AAAA',
    } as PrescriptionsApi.PrescriptionSigningContent);
    vi.mocked(bridge.signPrescription).mockResolvedValue({
      signature: 'MIIB',
      certificate: {} as Core.SignerCertificate,
    });
    vi.mocked(prescriptionsApi.attachSignature).mockResolvedValue(VALID);
    renderWith(<DigitalSignatureSection prescription={RX} canSign />);

    expect(await screen.findByText(/only once the doctor has signed them with their DSC/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Sign with DSC' }));

    await waitFor(() => expect(notify.success).toHaveBeenCalledWith('Signed with your DSC', 'The signature is valid.'));
    expect(await screen.findByText('Signed with a DSC')).toBeInTheDocument();
    expect(screen.getByText('18B3D635546B372D')).toBeInTheDocument();
    expect(screen.getByText(HASH)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Signature (.p7s)' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Sign with DSC' })).not.toBeInTheDocument();
  });

  it('offers "Sign with DSC" only to the doctor who prescribed it, and tells anyone else who must sign', async () => {
    signInAs('0192a000-0000-7000-8000-0000000doc02');
    readSignature.mockResolvedValue(signature({ policy: 'Required' }));
    renderWith(<DigitalSignatureSection prescription={RX} canSign />);

    expect(
      await screen.findByText(
        'Only Dr Meera Shah, who prescribed it, can sign it with their DSC, and it can be printed or given to the patient only then.',
      ),
    ).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Sign with DSC' })).not.toBeInTheDocument();
  });

  it('says a DSC that no longer checks out keeps the prescription from being issued where one is required', async () => {
    readSignature.mockResolvedValue({
      ...VALID,
      policy: 'Required',
      state: 'Untrusted',
      chainTrusted: false,
      finalCopyAvailable: false,
      message: 'The prescription is as the doctor signed it, but HC360 could not confirm the certificate as a DSC.',
    });
    renderWith(<DigitalSignatureSection prescription={RX} canSign={false} />);

    expect(await screen.findByText(/not printed or shared until this is resolved/)).toBeInTheDocument();
  });

  it('says where to sign when the page is open in a browser', async () => {
    desktop.on = false;
    readSignature.mockResolvedValue(signature({ policy: 'Optional' }));
    renderWith(<DigitalSignatureSection prescription={RX} canSign />);

    expect(await screen.findByText(/open this prescription in HC360 Desktop/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Sign with DSC' })).not.toBeInTheDocument();
  });

  it('a cancelled certificate dialog is not an error', async () => {
    const user = userEvent.setup();
    readSignature.mockResolvedValue(signature({ policy: 'Optional' }));
    vi.mocked(prescriptionsApi.signingContent).mockResolvedValue({
      content: 'AAAA',
    } as PrescriptionsApi.PrescriptionSigningContent);
    vi.mocked(bridge.signPrescription).mockRejectedValue(new BridgeError('cancelled', 'No certificate was chosen.'));
    renderWith(<DigitalSignatureSection prescription={RX} canSign />);

    await user.click(await screen.findByRole('button', { name: 'Sign with DSC' }));

    await waitFor(() => expect(notify.info).toHaveBeenCalledWith('Not signed', 'No certificate was chosen.'));
    expect(prescriptionsApi.attachSignature).not.toHaveBeenCalled();
    expect(notify.error).not.toHaveBeenCalled();
  });

  it('shows nothing while the hospital has DSC signing off', async () => {
    readSignature.mockResolvedValue(signature({ policy: 'Off' }));
    const { container } = renderWith(
      <>
        <DigitalSignatureBadge prescription={RX} />
        <DigitalSignatureSection prescription={RX} canSign />
      </>,
    );

    await waitFor(() => expect(readSignature).toHaveBeenCalled());
    expect(container.firstElementChild).toBeEmptyDOMElement();
  });

  it('warns loudly when the signature no longer checks out', async () => {
    readSignature.mockResolvedValue({
      ...VALID,
      state: 'Invalid',
      contentMatchesRecord: false,
      message: 'The prescription was changed after it was signed. Do not dispense it.',
    });
    renderWith(<DigitalSignatureSection prescription={RX} canSign={false} />);

    expect(await screen.findByText('The DSC signature does not check out')).toBeInTheDocument();
    expect(screen.getByText(/Do not dispense it/)).toBeInTheDocument();
  });

  it('does not call it a DSC signature once the certificate no longer chains to an installed root', async () => {
    readSignature.mockResolvedValue({
      ...VALID,
      state: 'Untrusted',
      chainTrusted: false,
      chainStatus: 'No CCA India root certificates are installed for HC360 to check DSCs against',
      message: 'The prescription is as the doctor signed it, but HC360 could not confirm the certificate as a DSC.',
    });
    renderWith(<DigitalSignatureSection prescription={RX} canSign={false} />);

    expect(await screen.findByText('The certificate is not confirmed as a DSC')).toBeInTheDocument();
    expect(screen.queryByText('Signed with a DSC')).not.toBeInTheDocument();
    expect(screen.getByText(/No CCA India root certificates are installed/)).toBeInTheDocument();
  });
});

describe('after signing in HC360', () => {
  it('offers print and PDF where the prescription can be issued as it is', () => {
    const dismiss = vi.fn();
    renderWith(
      <SignedPrescriptionNotice
        prescription={{ ...RX, finalCopyAvailable: true }}
        onSignWithDsc={vi.fn()}
        onDismiss={dismiss}
      />,
    );

    expect(screen.getByText('Prescription NHRX260000123 signed')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Print (A5)' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Download PDF' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Sign with DSC…' })).not.toBeInTheDocument();
  });

  it('asks for the DSC instead of offering a print the server would refuse, where the DSC is required', async () => {
    const user = userEvent.setup();
    const sign = vi.fn();
    renderWith(
      <SignedPrescriptionNotice
        prescription={{ ...RX, finalCopyAvailable: false }}
        onSignWithDsc={sign}
        onDismiss={vi.fn()}
      />,
    );

    expect(screen.getByText(/now sign it with your DSC/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Print (A5)' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Download PDF' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Sign with DSC…' }));
    expect(sign).toHaveBeenCalledTimes(1);
  });
});

describe('after issuing a refill', () => {
  const REFILL = { ...RX, prescriptionNumber: 'NHRX260000124' } as PrescriptionDto;

  it('prints it at once where it can be issued as it is', () => {
    expect(afterRefill({ ...REFILL, finalCopyAvailable: true }, null)).toEqual({
      print: true,
      openToSign: false,
      title: 'Refill NHRX260000124 issued',
      body: 'It is now in the pharmacy queue.',
    });
    expect(afterRefill(REFILL, null).print).toBe(true); // an older server does not say
  });

  it('takes the prescriber to sign it with their DSC, where the DSC is required, instead of printing', () => {
    const next = afterRefill({ ...REFILL, finalCopyAvailable: false }, PRESCRIBER_ID);
    expect(next.print).toBe(false);
    expect(next.openToSign).toBe(true);
    expect(next.title).toBe('Refill NHRX260000124 issued: sign it with your DSC');
  });

  it('tells a pharmacist or a colleague which doctor must sign it', () => {
    const next = afterRefill({ ...REFILL, finalCopyAvailable: false }, null);
    expect(next.print).toBe(false);
    expect(next.openToSign).toBe(false);
    expect(next.body).toContain('Dr Meera Shah must sign it before it can be printed or given to the patient.');
  });
});
