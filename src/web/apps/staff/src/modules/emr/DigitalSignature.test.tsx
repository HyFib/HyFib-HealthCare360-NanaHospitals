import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Core from '@hc360/core';
import { ApiError, bridge, BridgeError } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { DigitalSignatureBadge, DigitalSignatureSection } from './DigitalSignature';
import { desktopSigningProblem, digitalSignatureBadge, signWithDsc } from './dscSigning';
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
  message: 'The signature is valid.',
});

const RX = { id: RX_ID, status: 'Signed', prescriptionNumber: 'NHRX260000123' } as PrescriptionDto;

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
});
