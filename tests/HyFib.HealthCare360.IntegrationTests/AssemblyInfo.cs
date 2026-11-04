using HyFib.HealthCare360.IntegrationTests;

// One server and database for the whole run; the journeys share it, so they run one at a time.
[assembly: AssemblyFixture(typeof(HospitalServer))]
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

// No test starts near midnight in India, where the hospital's day turns.
[assembly: AwayFromMidnightInIndia]
[assembly: LongRunningTestWatch]
