namespace HyFib.HealthCare360.Integrations.Tests.Tally;

/// <summary>
/// Tally Prime answers in the layout its XML server uses for a TDL collection export: values carry TYPE attributes,
/// an imported voucher shows its REMOTEID both as the attribute and as its GUID, debit lines are deemed positive with
/// negative amounts, and the hospital's own vouchers sit between HC360's. Written from Tally's published XML formats;
/// a real Tally Prime has not answered these.
/// </summary>
internal static class TallyFixtures
{
    public const string DayExport = """
        <ENVELOPE>
         <HEADER><VERSION>1</VERSION><STATUS>1</STATUS></HEADER>
         <BODY>
          <DESC></DESC>
          <DATA>
           <COLLECTION>
            <VOUCHER REMOTEID="HC360-RCPT-1" VCHKEY="HC360-RCPT-1:00000008" VCHTYPE="Receipt" ACTION="Create" OBJVIEW="Accounting Voucher View">
             <DATE TYPE="Date">20260925</DATE>
             <GUID TYPE="String">HC360-RCPT-1</GUID>
             <VOUCHERTYPENAME TYPE="String">Receipt</VOUCHERTYPENAME>
             <VOUCHERNUMBER TYPE="String">RCPT-1</VOUCHERNUMBER>
             <ISCANCELLED TYPE="Logical">No</ISCANCELLED>
             <ISOPTIONAL TYPE="Logical">No</ISOPTIONAL>
             <ALLLEDGERENTRIES.LIST TYPE="Voucher Ledger Entries">
              <LEDGERNAME TYPE="String">Cash</LEDGERNAME>
              <ISDEEMEDPOSITIVE TYPE="Logical">Yes</ISDEEMEDPOSITIVE>
              <AMOUNT TYPE="Amount">-500.00</AMOUNT>
             </ALLLEDGERENTRIES.LIST>
             <ALLLEDGERENTRIES.LIST TYPE="Voucher Ledger Entries">
              <LEDGERNAME TYPE="String">Patient Advances</LEDGERNAME>
              <ISDEEMEDPOSITIVE TYPE="Logical">No</ISDEEMEDPOSITIVE>
              <AMOUNT TYPE="Amount">500.00</AMOUNT>
             </ALLLEDGERENTRIES.LIST>
            </VOUCHER>
            <VOUCHER REMOTEID="a3f1c2d4-5b6e-4f70-8a9b-0c1d2e3f4a5b-00000012" VCHTYPE="Payment" ACTION="Create">
             <DATE TYPE="Date">20260925</DATE>
             <GUID TYPE="String">a3f1c2d4-5b6e-4f70-8a9b-0c1d2e3f4a5b-00000012</GUID>
             <VOUCHERTYPENAME TYPE="String">Payment</VOUCHERTYPENAME>
             <VOUCHERNUMBER TYPE="String">41</VOUCHERNUMBER>
             <ALLLEDGERENTRIES.LIST>
              <LEDGERNAME>Electricity Charges</LEDGERNAME><ISDEEMEDPOSITIVE>Yes</ISDEEMEDPOSITIVE><AMOUNT>-12000.00</AMOUNT>
             </ALLLEDGERENTRIES.LIST>
             <ALLLEDGERENTRIES.LIST>
              <LEDGERNAME>Bank</LEDGERNAME><ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE><AMOUNT>12000.00</AMOUNT>
             </ALLLEDGERENTRIES.LIST>
            </VOUCHER>
            <VOUCHER REMOTEID="HC360-NHOB260000001" VCHTYPE="Sales" ACTION="Create" OBJVIEW="Invoice Voucher View">
             <DATE TYPE="Date">20260925</DATE>
             <VOUCHERTYPENAME TYPE="String">Sales</VOUCHERTYPENAME>
             <VOUCHERNUMBER TYPE="String">17</VOUCHERNUMBER>
             <LEDGERENTRIES.LIST>
              <LEDGERNAME>Patient Receivables</LEDGERNAME><ISDEEMEDPOSITIVE>Yes</ISDEEMEDPOSITIVE><AMOUNT>-1180.00</AMOUNT>
             </LEDGERENTRIES.LIST>
             <LEDGERENTRIES.LIST>
              <LEDGERNAME>Laboratory Income</LEDGERNAME><ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE><AMOUNT>1000.00</AMOUNT>
             </LEDGERENTRIES.LIST>
             <LEDGERENTRIES.LIST>
              <LEDGERNAME>Output CGST</LEDGERNAME><ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE><AMOUNT>90.00</AMOUNT>
             </LEDGERENTRIES.LIST>
             <LEDGERENTRIES.LIST>
              <LEDGERNAME>Output SGST</LEDGERNAME><ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE><AMOUNT>90.00</AMOUNT>
             </LEDGERENTRIES.LIST>
            </VOUCHER>
            <VOUCHER VCHTYPE="Journal" ACTION="Create">
             <DATE TYPE="Date">20260925</DATE>
             <GUID TYPE="String">HC360-DF-20260925-001</GUID>
             <VOUCHERTYPENAME TYPE="String">Journal</VOUCHERTYPENAME>
             <VOUCHERNUMBER TYPE="String">DF-20260925-001</VOUCHERNUMBER>
             <ISCANCELLED TYPE="Logical">Yes</ISCANCELLED>
             <ALLLEDGERENTRIES.LIST>
              <LEDGERNAME>Doctor Professional Fees</LEDGERNAME><ISDEEMEDPOSITIVE>Yes</ISDEEMEDPOSITIVE><AMOUNT>-1200.00</AMOUNT>
             </ALLLEDGERENTRIES.LIST>
             <ALLLEDGERENTRIES.LIST>
              <LEDGERNAME>Doctor Fees Payable - Dr. Arjun Mehta</LEDGERNAME><ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE><AMOUNT>1200.00</AMOUNT>
             </ALLLEDGERENTRIES.LIST>
            </VOUCHER>
           </COLLECTION>
          </DATA>
         </BODY>
        </ENVELOPE>
        """;
}
