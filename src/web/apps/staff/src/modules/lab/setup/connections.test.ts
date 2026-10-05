import { describe, expect, it } from 'vitest';
import type { LabAgentDto, LabConnectionDto, LabConnectionsDto, LabSerialPortDto } from '../api';
import {
  agentOptionLabel,
  connectionSchema,
  connectionStatus,
  connectionSummary,
  formFieldOf,
  isIpAddress,
  splitList,
  toConnectionForm,
  toSaveRequest,
  type ConnectionForm,
} from './connections';

const AGENT = '0192a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b';

const agent = (overrides: Partial<LabAgentDto> = {}): LabAgentDto => ({
  id: AGENT,
  name: 'Haematology PC',
  machineName: 'LAB-PC-1',
  location: null,
  lastSeenAt: '2026-10-03T05:00:00Z',
  online: true,
  appVersion: '1.0.0',
  lastIpAddress: '10.0.5.30',
  ...overrides,
});

const connection = (overrides: Partial<LabConnectionDto> = {}): LabConnectionDto => ({
  code: 'XN550',
  name: 'Haematology analyser',
  protocol: 'Astm',
  transport: 'Tcp',
  enabled: true,
  port: 5100,
  allowedIps: ['10.0.5.21'],
  encoding: null,
  sampleIdFields: [],
  orderMessage: null,
  agentDeviceId: null,
  agentName: null,
  serial: null,
  hostQueryRetiredOff: false,
  inLabSetup: true,
  bidirectional: true,
  problem: null,
  status: null,
  pendingOrders: 0,
  ...overrides,
});

const COM3: LabSerialPortDto = {
  portName: 'COM3',
  baudRate: 9600,
  dataBits: 8,
  parity: 'None',
  stopBits: 'One',
  handshake: 'None',
  dtrEnable: true,
  rtsEnable: true,
};

const serial = (overrides: Partial<LabConnectionDto> = {}) =>
  connection({
    transport: 'Serial',
    port: null,
    allowedIps: [],
    agentDeviceId: AGENT,
    agentName: 'Haematology PC',
    serial: COM3,
    ...overrides,
  });

const list = (overrides: Partial<LabConnectionsDto> = {}): LabConnectionsDto => ({
  gatewayEnabled: true,
  listVersion: 7,
  connections: [],
  agents: [agent()],
  ...overrides,
});

const problems = (values: ConnectionForm) => {
  const result = connectionSchema.safeParse(values);
  return result.success
    ? {}
    : Object.fromEntries(result.error.issues.map((issue) => [issue.path.join('.'), issue.message]));
};

describe('addresses and lists', () => {
  it('accepts IPv4 and IPv6 addresses only', () => {
    expect(isIpAddress('10.0.5.21')).toBe(true);
    expect(isIpAddress('fe80::1')).toBe(true);
    expect(isIpAddress('10.0.5.300')).toBe(false);
    expect(isIpAddress('analyser.local')).toBe(false);
  });

  it('reads items one per line or separated by commas, once each', () => {
    expect(splitList('10.0.5.21\n10.0.5.22, 10.0.5.21 ;')).toEqual(['10.0.5.21', '10.0.5.22']);
    expect(splitList('  ')).toEqual([]);
  });
});

describe('the form', () => {
  it('starts a new connection as a network port, and a serial line at 9600 8N1 with DTR and RTS', () => {
    const form = toConnectionForm(undefined);

    expect(form).toMatchObject({
      transport: 'Tcp',
      enabled: true,
      port: null,
      encoding: 'latin1',
      orderMessage: 'OML^O21',
    });
    expect(form).toMatchObject({ portName: 'COM1', baudRate: '9600', dataBits: '8', parity: 'None', stopBits: 'One' });
    expect(form).toMatchObject({ handshake: 'None', dtrEnable: true, rtsEnable: true });
  });

  it('shows a saved serial connection as it was saved', () => {
    const form = toConnectionForm(serial({ serial: { ...COM3, baudRate: 19200, dataBits: 7, parity: 'Even' } }));

    expect(form).toMatchObject({
      transport: 'Serial',
      agentDeviceId: AGENT,
      portName: 'COM3',
      baudRate: '19200',
      dataBits: '7',
    });
    expect(form.parity).toBe('Even');
  });

  it('needs a port and real addresses for a network analyser', () => {
    const tcp = toConnectionForm(undefined);

    expect(problems(tcp)).toEqual({ port: 'Enter the port the analyser connects to.' });
    expect(problems({ ...tcp, port: 70000 })).toEqual({ port: 'A port is a number from 1 to 65535.' });
    expect(problems({ ...tcp, port: 5100, allowedIps: '10.0.5.21\nlab-pc' })).toEqual({
      allowedIps: 'lab-pc is not an IP address.',
    });
    expect(problems({ ...tcp, port: 5100, allowedIps: '10.0.5.21' })).toEqual({});
  });

  it('needs the agent and a COM port for a serial analyser', () => {
    const line: ConnectionForm = {
      ...toConnectionForm(undefined),
      transport: 'Serial',
      agentDeviceId: '',
      portName: 'LPT1',
    };

    expect(problems(line)).toEqual({
      agentDeviceId: 'Choose the lab agent on the PC the analyser is cabled to.',
      portName: 'Enter a COM port, for example COM3.',
    });
    expect(problems({ ...line, agentDeviceId: AGENT, portName: 'com12' })).toEqual({});
  });

  it('checks where an HL7 analyser puts the barcode', () => {
    const tcp = { ...toConnectionForm(undefined), port: 5100 };

    expect(problems({ ...tcp, sampleIdFields: 'OBX-3' })).toEqual({
      sampleIdFields: 'Write fields like SPM-2, SAC-3, OBR-3 or OBR-2.',
    });
    expect(problems({ ...tcp, sampleIdFields: 'sac-3, OBR-2' })).toEqual({});
  });
});

describe('the save request', () => {
  it('sends only the network settings for a network analyser', () => {
    const values = {
      ...toConnectionForm(serial()),
      transport: 'Tcp' as const,
      port: 5200,
      allowedIps: '10.0.5.21, 10.0.5.22',
    };

    expect(toSaveRequest(values, false, 7)).toEqual({
      transport: 'Tcp',
      enabled: true,
      port: 5200,
      allowedIps: ['10.0.5.21', '10.0.5.22'],
      encoding: null,
      sampleIdFields: null,
      orderMessage: null,
      agentDeviceId: null,
      serial: null,
      listVersion: 7,
    });
  });

  it('sends the agent and the port settings for a serial analyser, leaving RTS to an RTS handshake', () => {
    const values: ConnectionForm = {
      ...toConnectionForm(serial()),
      portName: 'com4',
      handshake: 'RequestToSend',
      rtsEnable: true,
    };

    const request = toSaveRequest(values, true, 3);

    expect(request).toMatchObject({
      transport: 'Serial',
      port: null,
      allowedIps: null,
      agentDeviceId: AGENT,
      orderMessage: 'OML^O21',
    });
    expect(request.serial).toEqual({
      portName: 'COM4',
      baudRate: 9600,
      dataBits: 8,
      parity: 'None',
      stopBits: 'One',
      handshake: 'RequestToSend',
      dtrEnable: true,
      rtsEnable: false,
    });
  });

  it('puts server errors on the form fields', () => {
    expect(formFieldOf('serial.portName')).toBe('portName');
    expect(formFieldOf('port')).toBe('port');
    expect(formFieldOf('allowedIps[1]')).toBe('allowedIps');
    expect(formFieldOf('listVersion')).toBeNull();
  });
});

describe('the status shown', () => {
  it('summarises the connection', () => {
    expect(connectionSummary(connection())).toBe('Network · port 5100');
    expect(connectionSummary(serial())).toBe('Serial · COM3 on Haematology PC');
  });

  it('says when nothing is set up, it is turned off or the gateway is off', () => {
    expect(connectionStatus(undefined, list()).label).toBe('Not set up');
    expect(connectionStatus(connection({ enabled: false }), list()).label).toBe('Turned off');
    expect(connectionStatus(connection(), list({ gatewayEnabled: false })).label).toBe('Gateway off');
    expect(connectionStatus(connection({ problem: 'port must be between 1 and 65535' }), list()).tone).toBe('danger');
  });

  it('tells listening from connected and failed for a network analyser', () => {
    const status = {
      activeConnections: 0,
      connectedSince: null,
      lastMessageAt: null,
      lastErrorAt: null,
      lastError: null,
      messagesAccepted: 0,
      messagesRefused: 0,
    };

    expect(connectionStatus(connection({ status: { ...status, state: 'listening' } }), list()).label).toBe('Listening');
    expect(
      connectionStatus(connection({ status: { ...status, state: 'listening', activeConnections: 1 } }), list()).label,
    ).toBe('Connected');
    expect(
      connectionStatus(
        connection({
          status: { ...status, state: 'failed', lastError: 'Port 5100 could not be opened: AddressAlreadyInUse' },
        }),
        list(),
      ).detail,
    ).toContain('AddressAlreadyInUse');
    expect(connectionStatus(connection(), list()).label).toBe('Starting');
  });

  it('tells an offline agent from a connected relay and a COM port problem', () => {
    const status = {
      activeConnections: 0,
      connectedSince: null,
      lastMessageAt: null,
      lastErrorAt: null,
      lastError: null,
      messagesAccepted: 0,
      messagesRefused: 0,
    };

    expect(connectionStatus(serial({ status: { ...status, state: 'connected' } }), list()).label).toBe('Connected');
    expect(connectionStatus(serial({ status: { ...status, state: 'waiting' } }), list()).label).toBe('Connecting');
    expect(
      connectionStatus(
        serial({ status: { ...status, state: 'waiting' } }),
        list({ agents: [agent({ online: false })] }),
      ).label,
    ).toBe('Agent offline');
    expect(connectionStatus(serial(), list({ agents: [] })).label).toBe('No agent');
    expect(
      connectionStatus(
        serial({ status: { ...status, state: 'port_error', lastError: 'Access to the port is denied.' } }),
        list(),
      ).detail,
    ).toContain('Access to the port is denied.');
  });

  it('names agents with their PC and whether they are online', () => {
    expect(agentOptionLabel(agent())).toBe('Haematology PC (LAB-PC-1) · online');
    expect(agentOptionLabel(agent({ machineName: null, online: false }))).toBe('Haematology PC · offline');
  });
});
