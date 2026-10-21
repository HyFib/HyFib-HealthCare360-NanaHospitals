# A page printed three times by Chromium

`AbdmDocumentVersionTests` uses these files to show that the printed PDF in an ABDM document is the same bytes whenever
the same page is printed (`AbdmDocumentBuilder.PdfAsOf`).

`page.html` (a prescription-like page with a table, made up for the test) was printed by **Chromium 141.0.7390.37**
(`headless_shell`, the build Playwright 1.56 installs) through the DevTools protocol, the way HC360's PDF renderer prints
(`EdgePdfRenderer`): `Page.setDocumentContent`, then `Page.printToPDF` with `generateTaggedPDF: true`,
`preferCSSPageSize: true` and `printBackground: true`.

| File | Printed |
|---|---|
| `render-1.pdf` | in a new tab |
| `render-2-same-tab.pdf` | again, a second later, in the same tab (HC360 reuses a tab for up to 50 PDFs) |
| `render-3-new-tab.pdf` | again, in another new tab of the same browser |

The three differ in their creation and modification dates (the time they were printed) and, the first two, in the
ids of the table's header cells (`(node00000017)` in one, `(node00000070)` in the other), which Chromium takes from a
counter the tab keeps from one page to the next. Nothing else differs.
