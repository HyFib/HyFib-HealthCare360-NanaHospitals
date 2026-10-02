import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Router from '@tanstack/react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { useForm } from 'react-hook-form';
import { beforeAll, describe, expect, it, vi } from 'vitest';
import type * as Lookups from '@/lib/lookups';
import type { FacilityDto, PractitionerDto, SpecialtyDto } from './api';
import { DepartmentFields } from './DepartmentEditor';
import { FacilityFields } from './FacilityEditor';
import {
  toDepartmentForm,
  toFacilityForm,
  toPractitionerForm,
  toServiceForm,
  type DepartmentForm,
  type FacilityForm,
  type PractitionerForm,
  type ServiceForm,
} from './forms';
import { ImagePicker } from './ImageField';
import type * as Images from './images';
import { prepareImage, uploadImage } from './images';
import { PractitionerFields } from './PractitionerEditor';
import { ServiceFields } from './ServiceEditor';

// The editors' contents are tested without their drawers, which flake in jsdom. Images are not really shrunk or
// uploaded (jsdom has no canvas), the doctor look-up finds nobody, and the user may set tariff prices.
vi.mock('./images', async (importOriginal) => ({
  ...(await importOriginal<typeof Images>()),
  prepareImage: vi.fn(async (file: File) => file),
  uploadImage: vi.fn(async () => 'file-1'),
  imageDataUrl: vi.fn(async () => 'data:image/png;base64,iVBORw0KGgo='),
}));
vi.mock('@tanstack/react-router', async (importOriginal) => ({
  ...(await importOriginal<typeof Router>()),
  useNavigate: () => vi.fn(),
}));
vi.mock('@/lib/permissions', () => ({ usePermission: () => true }));
vi.mock('@/lib/lookups', async (importOriginal) => ({
  ...(await importOriginal<typeof Lookups>()),
  searchPractitioners: vi.fn(async () => []),
}));

// Fluent's MessageBar measures itself; jsdom has no ResizeObserver.
beforeAll(() => {
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  );
});

function wrap(children: ReactNode) {
  return render(
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        {children}
      </QueryClientProvider>
    </FluentProvider>,
  );
}

const facility = (id: string, name: string, isActive = true): FacilityDto => ({
  id,
  code: id.toUpperCase(),
  name,
  legalName: null,
  address: { line1: null, line2: null, city: null, district: null, state: null, pinCode: null, country: 'India' },
  phone: null,
  email: null,
  website: null,
  gstin: null,
  pan: null,
  registrationNumber: null,
  nabhNumber: null,
  hfrId: null,
  timeZone: 'Asia/Kolkata',
  currency: 'INR',
  logoFileId: null,
  letterheadNote: null,
  isActive,
  version: 1,
});

const doctor: PractitionerDto = {
  id: 'p1',
  code: 'DR001',
  title: 'Dr.',
  firstName: 'Asha',
  lastName: 'Menon',
  displayName: 'Dr. Asha Menon',
  gender: 'Female',
  qualification: null,
  registrationCouncil: null,
  registrationNumber: 'TNMC 12345',
  specialtyId: null,
  specialtyName: null,
  departmentId: null,
  departmentName: null,
  kind: 'Consultant',
  phone: null,
  email: null,
  consultationFeeNew: 500,
  consultationFeeFollowUp: 300,
  followUpValidityDays: 7,
  languages: [],
  bio: null,
  photoFileId: null,
  signatureFileId: null,
  availableForOnlineBooking: false,
  availableForTelehealth: false,
  facilityIds: [],
  isActive: true,
  version: 1,
};

function FacilityHarness({ editing, onPreview }: { editing: boolean; onPreview: () => void }) {
  const form = useForm<FacilityForm>({
    defaultValues: toFacilityForm(editing ? facility('nh', 'Nana Hospitals') : null),
  });
  return <FacilityFields control={form.control} editing={editing} onPreview={onPreview} />;
}

function PractitionerHarness({ facilities }: { facilities: FacilityDto[] }) {
  const form = useForm<PractitionerForm>({ defaultValues: toPractitionerForm(doctor) });
  return (
    <PractitionerFields
      control={form.control}
      editing
      specialties={[]}
      currentSpecialtyId={null}
      departmentOptions={[]}
      facilities={facilities}
    />
  );
}

describe('the facility editor', () => {
  it('keeps the code of a saved facility, and previews the letterhead on request', async () => {
    const onPreview = vi.fn();
    wrap(<FacilityHarness editing onPreview={onPreview} />);

    expect(screen.getByRole('textbox', { name: /Code/ })).toBeDisabled();
    expect(screen.getByText(/can't be changed once it is saved/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Preview letterhead' }));
    expect(onPreview).toHaveBeenCalled();
  });

  it('asks for the code of a new facility', () => {
    wrap(<FacilityHarness editing={false} onPreview={vi.fn()} />);
    expect(screen.getByRole('textbox', { name: /Code/ })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Upload logo' })).toBeInTheDocument();
  });
});

describe('the practitioner editor', () => {
  it('shows the name as it will read, and needs a registration number for doctors only', async () => {
    wrap(<PractitionerHarness facilities={[facility('nh', 'Nana Hospitals')]} />);

    expect(screen.getByText('Shown as Dr. Asha Menon')).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: /Registration number/ })).toBeRequired();
    await userEvent.selectOptions(screen.getByRole('combobox', { name: /Role/ }), 'Nurse');
    expect(screen.getByRole('textbox', { name: /Registration number/ })).not.toBeRequired();
  });

  it('asks where the doctor consults only when there is more than one facility', async () => {
    const { unmount } = wrap(<PractitionerHarness facilities={[facility('nh', 'Nana Hospitals')]} />);
    expect(screen.queryByRole('switch', { name: 'Consults at every facility' })).not.toBeInTheDocument();
    unmount();

    wrap(
      <PractitionerHarness
        facilities={[
          facility('nh', 'Nana Hospitals'),
          facility('na', 'Nana Annexe'),
          facility('old', 'Old wing', false),
        ]}
      />,
    );
    await userEvent.click(screen.getByRole('switch', { name: 'Consults at every facility' }));
    const group = screen.getByRole('group', { name: /Consults at/ });
    expect(within(group).getAllByRole('checkbox')).toHaveLength(2);
    expect(within(group).queryByText(/Old wing/)).not.toBeInTheDocument();
  });
});

describe('the service editor', () => {
  function ServiceHarness({ currentTaxRate }: { currentTaxRate: number | null }) {
    const form = useForm<ServiceForm>({ defaultValues: toServiceForm(null) });
    return (
      <ServiceFields control={form.control} editing={false} departmentOptions={[]} currentTaxRate={currentTaxRate} />
    );
  }

  it('offers the GST rates for hospital services, keeping a service’s older rate', () => {
    wrap(<ServiceHarness currentTaxRate={3} />);
    const gst = screen.getByRole('combobox', { name: /GST/ });
    expect(
      within(gst)
        .getAllByRole('option')
        .map((o) => o.textContent),
    ).toEqual(['0% (exempt)', '3%', '5%', '12% (older slab)', '18%', '28% (older slab)']);
    expect(screen.getByRole('button', { name: 'Set plan prices in Tariffs.' })).toBeInTheDocument();
  });
});

describe('the department editor', () => {
  it('keeps a speciality since taken out of use, and offers the others in use', () => {
    const specialties: SpecialtyDto[] = [
      { id: 's1', code: 'CARDIO', name: 'Cardiology', description: null, isActive: true, version: 1 },
      { id: 's2', code: 'OLD', name: 'Old speciality', description: null, isActive: false, version: 1 },
      { id: 's3', code: 'GONE', name: 'Gone speciality', description: null, isActive: false, version: 1 },
    ];
    function DepartmentHarness() {
      const form = useForm<DepartmentForm>({ defaultValues: { ...toDepartmentForm(null), specialtyId: 's2' } });
      return (
        <DepartmentFields
          control={form.control}
          editing={false}
          specialties={specialties}
          currentSpecialtyId="s2"
          currentHead={null}
        />
      );
    }
    wrap(<DepartmentHarness />);

    const options = within(screen.getByRole('combobox', { name: /Speciality/ })).getAllByRole('option');
    expect(options.map((o) => o.textContent)).toEqual(['None', 'Cardiology', 'Old speciality (not in use)']);
  });
});

describe('the image picker', () => {
  it('uploads a chosen logo and shows it', async () => {
    const onChange = vi.fn();
    const { rerender } = wrap(<ImagePicker label="Logo" kind="logo" value={null} onChange={onChange} />);

    await userEvent.upload(
      screen.getByLabelText('Choose the logo file'),
      new File(['png'], 'logo.png', { type: 'image/png' }),
    );

    await waitFor(() => expect(onChange).toHaveBeenCalledWith('file-1'));
    expect(prepareImage).toHaveBeenCalledWith(expect.any(File), 'logo');
    expect(uploadImage).toHaveBeenCalled();
    rerender(
      <FluentProvider theme={webLightTheme}>
        <QueryClientProvider client={new QueryClient()}>
          <ImagePicker label="Logo" kind="logo" value="file-1" onChange={onChange} />
        </QueryClientProvider>
      </FluentProvider>,
    );
    expect(await screen.findByRole('img', { name: 'The logo as uploaded' })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Remove' }));
    expect(onChange).toHaveBeenLastCalledWith(null);
  });

  it('explains why a file can’t be used, without uploading it', async () => {
    const onChange = vi.fn();
    vi.mocked(uploadImage).mockClear();
    wrap(<ImagePicker label="Signature" kind="signature" value={null} onChange={onChange} />);

    await userEvent
      .setup({ applyAccept: false })
      .upload(
        screen.getByLabelText('Choose the signature file'),
        new File(['%PDF'], 'signature.pdf', { type: 'application/pdf' }),
      );

    expect(await screen.findByRole('alert')).toHaveTextContent('signature.pdf is not an image HC360 can use');
    expect(uploadImage).not.toHaveBeenCalled();
    expect(onChange).not.toHaveBeenCalled();
  });
});
