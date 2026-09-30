using HyFib.HealthCare360.IntegrationTests;

// One server and database for the whole run; the journeys share it, so they run one at a time.
[assembly: AssemblyFixture(typeof(HospitalServer))]
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]
