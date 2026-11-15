using HyFib.HealthCare360.IntegrationTests;

// One server and database for the whole run; the journeys share it, so they run one at a time.
[assembly: AssemblyFixture(typeof(HospitalServer))]
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

// No test on the real clock starts near midnight in India, where the hospital's day turns: the tests of the turn itself run
// on a clock of their own (MidnightInIndiaTests).
[assembly: AwayFromMidnightInIndia]
[assembly: LongRunningTestWatch]
