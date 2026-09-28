namespace HyFib.HealthCare360.SharedKernel;

public enum Gender
{
    Unknown = 0,
    Male = 1,
    Female = 2,
    Other = 3,
}

public enum BloodGroup
{
    Unknown = 0,
    APositive,
    ANegative,
    BPositive,
    BNegative,
    ABPositive,
    ABNegative,
    OPositive,
    ONegative,
    BombayPositive,
    BombayNegative,
}

public enum MaritalStatus
{
    Unknown = 0,
    Single,
    Married,
    Divorced,
    Widowed,
    Separated,
}

/// <summary>How the patient (or a visit) is financially covered.</summary>
public enum PayerType
{
    Self = 0,
    Insurance,
    Corporate,
    GovernmentScheme,
    Staff,
    Charity,
}

public enum Weekday
{
    Monday = 1,
    Tuesday,
    Wednesday,
    Thursday,
    Friday,
    Saturday,
    Sunday,
}
