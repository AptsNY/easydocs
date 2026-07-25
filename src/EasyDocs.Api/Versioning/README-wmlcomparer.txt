WmlComparer package: Clippit 3.8.0 (maintained OpenXmlPowerTools fork).
Diff/merge (Tasks 8/9) use: Clippit.Word.WmlComparer.
Open-Xml-PowerTools 4.4.0 was rejected: it drags in System.Drawing.Common 4.5.0,
which trips NU1904 (critical vuln) and fails under TreatWarningsAsErrors.
