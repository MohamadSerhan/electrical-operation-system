# Phase 1 original-data assessment

This record identifies what was safely adopted from the three original artifacts supplied for comparison. It is not evidence that the PLC, SQL Server or legacy executable was run during this review.

## Sources reviewed

| Source | What it established | What was adopted |
| --- | --- | --- |
| Original Delta PLC project | Target is a Delta AS228T-A; project hardware includes an AS06RTD-A module; configured endpoint and PM5110 tag map. | Traceable T1–T4 meter-point map and hardware provenance. |
| Original SQL database archive | Legacy schema has `User_Registration_Table` and `Logs_Table`; inspected copy had 5 active users and 175 logs. | Password-safe staging/import script for user metadata and historical logs. |
| Original Modbus SCADA executable archive | .NET 10 binary package named `PowerTransformersScadaGrid`, version 1.0.0, with EasyModbusTCP 5.6.0 and System.IO.Ports 10.0.9. | Nothing executable or dependency-related. It contains no source or authoritative point-map document. |

## Confirmed PLC point layout

The Delta tag export defines each measurement as a `DWORD`, giving sixteen values (32 words) per transformer. The V5.1 FC03 decoder already uses that exact order and the fixed blocks below.

| Offset (float) | Tag suffix | V5.1 field |
| ---: | --- | --- |
| 0 | VAN | V A-N |
| 1 | VBN | V B-N |
| 2 | VCN | V C-N |
| 3 | VLNAVG | Average V L-N |
| 4 | VAB | V A-B |
| 5 | VBC | V B-C |
| 6 | VCA | V C-A |
| 7 | VLLAVG | Average V L-L |
| 8 | FREQ | Frequency |
| 9 | IAVG | Average current |
| 10 | PTOTAL | PTOT |
| 11 | IA | Phase A current |
| 12 | IB | Phase B current |
| 13 | IC | Phase C current |
| 14 | IN | Neutral current |
| 15 | DEGC | TDEGC temperature |

| Transformer | PLC address range | V5.1 FC03 start | Register count |
| --- | --- | ---: | ---: |
| T1 | D2000–D2031 | 2000 | 32 |
| T2 | D2050–D2081 | 2050 | 32 |
| T3 | D2100–D2131 | 2100 | 32 |
| T4 | D2150–D2181 | 2150 | 32 |

## Data deliberately not copied

- The five legacy plaintext passwords were not read, exported, converted or embedded.
- Legacy user names, IDs and log-operation contents were not placed in this source archive.
- No compiled EXE, DLL, EasyModbus dependency, serial-port dependency, PLC binary, backup, or live reading was copied into the V5.1 source package.
- No engineering scaling, alarm threshold, physical temperature interpretation, address offset, Modbus float word order or connection state was promoted from inference to verified fact.

## Migration decision

Use `database/002_LegacyPhase1DataMigration.sql` only in a backed-up, approved migration database. It stages non-secret user identity fields for manual account setup and preserves historical log rows. It does not create application users, re-use passwords or preserve legacy role privileges.

## Source integrity references

- Original SQL RAR SHA-256: `CDEA9DE50F58CB57A66C7859F25A48A2EEC2A8EAA1BE58E36234D747B42BF868`.
- Original Modbus SCADA RAR SHA-256: `AF78516247F74B04788FE8FB92CFAF123E39282E156841F70695A2215802E6F8`.
- V5.1 baseline RAR SHA-256: `89D59A5F54FBA00B3EF32E5FCBB6D9D732BAB233D51E33834F5CDD9DC6305C44`.
