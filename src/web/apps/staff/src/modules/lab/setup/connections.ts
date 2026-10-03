import { z } from 'zod';
import {
  HL7_ORDER_MESSAGES,
  SERIAL_BAUD_RATES,
  SERIAL_HANDSHAKES,
  SERIAL_PARITIES,
  SERIAL_STOP_BITS,
  type LabAgentDto,
  type LabConnectionDto,
  type LabConnectionsDto,
  type SaveLabConnectionRequest,
} from '../api';

/**
 * The analyser connection form (Lab setup › Analysers › Connection). Pure and unit-tested; the rules mirror the server's
 * `SaveLabConnectionRequestValidator`, which checks them again.
 */

const COM_PORT = /^COM([1-9]|[1-9][0-9]|1[0-9][0-9]|2[0-4][0-9]|25[0-6])$/i;
const SAMPLE_ID_FIELD = /^(SPM|SAC|OBR|PID)-[0-9]{1,2}$/;
const IPV4 = /^(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}$/;

/** An IPv4 address, or an IPv6 address in its usual forms. */
export function isIpAddress(text: string): boolean {
  const value = text.trim();
  if (IPV4.test(value)) {
    return true;
  }
  return value.includes(':') && /^[0-9a-f:.]+$/i.test(value) && URL.canParse(`http://[${value}]/`);
}

/** Items typed one per line or separated by commas or spaces. */
export function splitList(text: string): string[] {
  return [
    ...new Set(
      text
        .split(/[\s,;]+/)
        .map((item) => item.trim())
        .filter(Boolean),
    ),
  ];
}

export const connectionSchema = z
  .object({
    transport: z.enum(['Tcp', 'Serial']),
    enabled: z.boolean(),
    port: z.number().int('Enter a whole number.').nullable(),
    allowedIps: z.string().max(1000),
    encoding: z.enum(['latin1', 'utf-8']),
    sampleIdFields: z.string().max(100),
    orderMessage: z.enum(HL7_ORDER_MESSAGES),
    agentDeviceId: z.string(),
    portName: z.string().trim(),
    baudRate: z.string(),
    dataBits: z.enum(['7', '8']),
    parity: z.enum(SERIAL_PARITIES),
    stopBits: z.enum(SERIAL_STOP_BITS),
    handshake: z.enum(SERIAL_HANDSHAKES),
    dtrEnable: z.boolean(),
    rtsEnable: z.boolean(),
  })
  .superRefine((values, ctx) => {
    const problem = (path: string, message: string) => ctx.addIssue({ code: 'custom', path: [path], message });
    if (values.transport === 'Tcp') {
      if (values.port === null) {
        problem('port', 'Enter the port the analyser connects to.');
      } else if (values.port < 1 || values.port > 65535) {
        problem('port', 'A port is a number from 1 to 65535.');
      }
      const ips = splitList(values.allowedIps);
      const wrong = ips.find((ip) => !isIpAddress(ip));
      if (wrong) {
        problem('allowedIps', `${wrong} is not an IP address.`);
      } else if (ips.length > 16) {
        problem('allowedIps', 'List at most 16 addresses.');
      }
    } else {
      if (!values.agentDeviceId) {
        problem('agentDeviceId', 'Choose the lab agent on the PC the analyser is cabled to.');
      }
      if (!COM_PORT.test(values.portName)) {
        problem('portName', 'Enter a COM port, for example COM3.');
      }
      if (!(SERIAL_BAUD_RATES as readonly number[]).includes(Number(values.baudRate))) {
        problem('baudRate', 'Choose a baud rate.');
      }
    }
    const fields = splitList(values.sampleIdFields);
    if (fields.some((field) => !SAMPLE_ID_FIELD.test(field.toUpperCase()))) {
      problem('sampleIdFields', 'Write fields like SPM-2, SAC-3, OBR-3 or OBR-2.');
    } else if (fields.length > 6) {
      problem('sampleIdFields', 'List at most 6 fields.');
    }
  });

export type ConnectionForm = z.infer<typeof connectionSchema>;

/** What a new connection starts from: the analyser's network port, 9600 baud 8N1 for a serial line. */
export function toConnectionForm(connection: LabConnectionDto | undefined): ConnectionForm {
  const serial = connection?.serial;
  return {
    transport: connection?.transport ?? 'Tcp',
    enabled: connection?.enabled ?? true,
    port: connection?.port ?? null,
    allowedIps: (connection?.allowedIps ?? []).join('\n'),
    encoding: connection?.encoding === 'utf-8' ? 'utf-8' : 'latin1',
    sampleIdFields: (connection?.sampleIdFields ?? []).join(', '),
    orderMessage: connection?.orderMessage === 'ORM^O01' ? 'ORM^O01' : 'OML^O21',
    agentDeviceId: connection?.agentDeviceId ?? '',
    portName: serial?.portName ?? 'COM1',
    baudRate: String(serial?.baudRate ?? 9600),
    dataBits: serial?.dataBits === 7 ? '7' : '8',
    parity: serial?.parity ?? 'None',
    stopBits: serial?.stopBits ?? 'One',
    handshake: serial?.handshake ?? 'None',
    dtrEnable: serial?.dtrEnable ?? true,
    rtsEnable: serial?.rtsEnable ?? true,
  };
}

/** The handshakes in which the driver drives RTS (the form then leaves it alone). */
export function handshakeUsesRts(handshake: ConnectionForm['handshake']): boolean {
  return handshake === 'RequestToSend' || handshake === 'RequestToSendXOnXOff';
}

/** The save request: only what applies to the transport (and to HL7 analysers), against the list the person saw. */
export function toSaveRequest(values: ConnectionForm, isHl7: boolean, listVersion: number): SaveLabConnectionRequest {
  const tcp = values.transport === 'Tcp';
  const ips = splitList(values.allowedIps);
  const fields = splitList(values.sampleIdFields).map((field) => field.toUpperCase());
  return {
    transport: values.transport,
    enabled: values.enabled,
    port: tcp ? values.port : null,
    allowedIps: tcp && ips.length > 0 ? ips : null,
    encoding: values.encoding === 'utf-8' ? 'utf-8' : null,
    sampleIdFields: isHl7 && fields.length > 0 ? fields : null,
    orderMessage: isHl7 ? values.orderMessage : null,
    agentDeviceId: tcp ? null : values.agentDeviceId,
    serial: tcp
      ? null
      : {
          portName: values.portName.trim().toUpperCase(),
          baudRate: Number(values.baudRate),
          dataBits: Number(values.dataBits),
          parity: values.parity,
          stopBits: values.stopBits,
          handshake: values.handshake,
          dtrEnable: values.dtrEnable,
          rtsEnable: handshakeUsesRts(values.handshake) ? false : values.rtsEnable,
        },
    listVersion,
  };
}

/** The form field a server validation error belongs to: the server nests the COM port settings under `serial`. */
export function formFieldOf(serverField: string): keyof ConnectionForm | null {
  const field = serverField.startsWith('serial.') ? serverField.slice('serial.'.length) : serverField;
  const known: (keyof ConnectionForm)[] = [
    'transport',
    'enabled',
    'port',
    'allowedIps',
    'encoding',
    'sampleIdFields',
    'orderMessage',
    'agentDeviceId',
    'portName',
    'baudRate',
    'dataBits',
    'parity',
    'stopBits',
    'handshake',
    'dtrEnable',
    'rtsEnable',
  ];
  return known.find((name) => name === field.replace(/\[\d+\]$/, '')) ?? null;
}

/** "Network · port 5100" or "Serial · COM3 on Haematology PC". */
export function connectionSummary(connection: LabConnectionDto): string {
  if (connection.transport === 'Serial') {
    const port = connection.serial?.portName ?? 'COM port not set';
    return `Serial · ${port}${connection.agentName ? ` on ${connection.agentName}` : ''}`;
  }
  return `Network · port ${connection.port ?? '—'}`;
}

export type ConnectionTone = 'success' | 'warning' | 'danger' | 'informative' | 'subtle';

export interface ConnectionStatus {
  label: string;
  tone: ConnectionTone;
  /** What it means and what to do, for the drawer. */
  detail: string;
}

/** The analyser's connection state in plain words. */
export function connectionStatus(
  connection: LabConnectionDto | undefined,
  list: Pick<LabConnectionsDto, 'gatewayEnabled' | 'agents'>,
): ConnectionStatus {
  if (!connection) {
    return {
      label: 'Not set up',
      tone: 'subtle',
      detail: 'Results from this analyser are entered by hand until its connection is set up.',
    };
  }
  if (connection.problem) {
    return {
      label: 'Needs attention',
      tone: 'danger',
      detail: `The connection is not valid: ${connection.problem}. Save it again.`,
    };
  }
  if (!connection.enabled) {
    return { label: 'Turned off', tone: 'subtle', detail: 'The connection is set up but turned off.' };
  }
  if (!list.gatewayEnabled) {
    return {
      label: 'Gateway off',
      tone: 'warning',
      detail:
        'The laboratory gateway is off, so no analyser can connect. Turn it on in Administration › Integrations › Laboratory equipment.',
    };
  }
  const status = connection.status;
  if (connection.transport === 'Tcp') {
    switch (status?.state) {
      case 'listening':
        return status.activeConnections > 0
          ? { label: 'Connected', tone: 'success', detail: `The analyser is connected on port ${connection.port}.` }
          : {
              label: 'Listening',
              tone: 'success',
              detail: `HC360 is listening on port ${connection.port}. The analyser connects when it has something to send.`,
            };
      case 'failed':
        return {
          label: 'Port failed',
          tone: 'danger',
          detail: `${status.lastError ?? 'The port could not be opened.'} Choose another port, or free this one on the server.`,
        };
      default:
        return { label: 'Starting', tone: 'warning', detail: 'HC360 opens the port within a minute.' };
    }
  }

  const agent = list.agents.find((a) => a.id === connection.agentDeviceId);
  switch (status?.state) {
    case 'connected':
      return { label: 'Connected', tone: 'success', detail: `Relayed by ${connection.agentName ?? 'its lab agent'}.` };
    case 'port_error':
      return {
        label: 'COM port problem',
        tone: 'danger',
        detail: `${status.lastError ?? 'The lab agent could not open the COM port.'} Check the cable, the port number, and that no other program uses the port.`,
      };
    default:
      if (!agent) {
        return {
          label: 'No agent',
          tone: 'danger',
          detail: 'The lab agent was revoked or removed. Choose another agent, or enrol one on the lab PC.',
        };
      }
      return agent.online
        ? { label: 'Connecting', tone: 'warning', detail: `${agent.name} is online and is connecting the analyser.` }
        : {
            label: 'Agent offline',
            tone: 'danger',
            detail: `${agent.name} is not in touch. Check that the lab PC is on and on the network, and that the HC360LabAgent service is running.`,
          };
  }
}

/** "Haematology PC · online" for the agent picker. */
export function agentOptionLabel(agent: LabAgentDto): string {
  return `${agent.name}${agent.machineName ? ` (${agent.machineName})` : ''} · ${agent.online ? 'online' : 'offline'}`;
}
