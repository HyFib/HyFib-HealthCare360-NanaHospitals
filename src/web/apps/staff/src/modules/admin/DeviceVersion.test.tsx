import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { DataTable } from '@hc360/ui';
import { render, screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { DeviceDto } from './api';
import { DeviceVersion } from './DeviceVersion';

const devices: Pick<DeviceDto, 'id' | 'name' | 'kind' | 'appVersion' | 'isRevoked'>[] = [
  { id: '1', name: 'OPD Reception 2', kind: 'Workstation', appVersion: '1.1.0', isRevoked: false },
  { id: '2', name: 'Ward 3 nurses', kind: 'Workstation', appVersion: '1.2.0', isRevoked: false },
  { id: '3', name: 'Queue TV', kind: 'DisplayBoard', appVersion: null, isRevoked: false },
  { id: '4', name: 'Biochemistry analyser PC', kind: 'LabAgent', appVersion: '1.0.0', isRevoked: false },
];

// The Devices screen's version column, as Administration › Devices renders it.
function renderColumn(offeredVersion: string | null) {
  render(
    <FluentProvider theme={webLightTheme}>
      <DataTable
        ariaLabel="Devices"
        rows={devices}
        getRowId={(d) => d.id}
        columns={[
          { key: 'name', header: 'Device', render: (d) => d.name },
          {
            key: 'version',
            header: 'App version',
            render: (d) => <DeviceVersion device={d} offeredVersion={offeredVersion} />,
          },
        ]}
      />
    </FluentProvider>,
  );
}

function row(name: string) {
  return within(screen.getByRole('row', { name: new RegExp(name) }));
}

describe('the Devices version column', () => {
  it('marks a workstation behind the version the server offers, and the one it would get', () => {
    renderColumn('1.2.0');

    const behind = row('OPD Reception 2');
    expect(behind.getByText('1.1.0')).toBeInTheDocument();
    expect(behind.getByText('Out of date')).toBeInTheDocument();
    expect(behind.getByText('1.2.0 offered')).toBeInTheDocument();

    const current = row('Ward 3 nurses');
    expect(current.getByText('Up to date')).toBeInTheDocument();
    expect(current.queryByText('Out of date')).not.toBeInTheDocument();

    expect(row('Queue TV').getByText('Version not reported')).toBeInTheDocument();

    // A lab agent is another program: its version is shown, and not compared with HC360 Desktop's.
    const agent = row('Biochemistry analyser PC');
    expect(agent.getByText('1.0.0')).toBeInTheDocument();
    expect(agent.queryByText('Out of date')).not.toBeInTheDocument();
    expect(agent.queryByText('Up to date')).not.toBeInTheDocument();
  });

  it('only shows the versions when the server offers none', () => {
    renderColumn(null);

    expect(row('OPD Reception 2').getByText('1.1.0')).toBeInTheDocument();
    expect(screen.queryByText('Out of date')).not.toBeInTheDocument();
    expect(screen.queryByText('Up to date')).not.toBeInTheDocument();
  });
});
