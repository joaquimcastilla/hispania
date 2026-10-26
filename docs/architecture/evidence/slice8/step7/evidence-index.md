# Evidence index — Step 7.4 owner-accepted status record

Paths are shown relative to `%TEMP%` to avoid embedding a Windows profile path in review documents. The run directory token is `hispania-pi35-run-8c9d1335d362400da76ac6ede36bfb17`.

| Evidence | Relative path | SHA-256 | Verification / provenance |
|---|---|---|---|
| PI-35 run summary JSON | `hispania-pi35-run-8c9d1335d362400da76ac6ede36bfb17/evidence/pi35-run-summary.json` | `9554D074C170CEBF8A253BEE531BFAC9D04073799A4A6EC623346E8E2F6ACA4E` | File hash rechecked; owner-reported/executed manually, JSON says overall PASS, 1/0/0, elevated=true. |
| PI-35 TRX | `hispania-pi35-run-8c9d1335d362400da76ac6ede36bfb17/evidence/trx/pi35-direct-symlink.trx` | `5DFF3E72E2243DBA9B2C9B1010D061DC1F7906F80BB07F9482D751CA2031DFDF` | File hash rechecked; one named test Passed, counters 1/0/0. |
| Restore stdout | `.../evidence/restore.stdout.txt` | `A92F5BA646A108DB77EF47163BEC6A232E0AE5F0A07DEB5DB3BBA7130963C69E` | Hash as recorded by run summary. |
| Build stdout | `.../evidence/build.stdout.txt` | `EAA82D85615EFED40A6F3242049167E17C19DE1967903F39B4DA844EE83D872F` | Hash as recorded by run summary. |
| Test stdout | `.../evidence/test.stdout.txt` | `D0D8F95131D1DF3A9009DEE4B477E362E7CA6182829E965EDFA491830E6C23F8` | Hash as recorded by run summary. |
| PI-35 offline transfer ZIP v2 | `%TEMP%/hispania-pi35-transfer-v2-de790c9-20261024-74d732481ee942f3b57b53da46f3f422.zip` | `54EF84BDAFB3B9115C58A028150BB5F29CFC8DF9650ECE90D0243EBA153B934E` | Hash rechecked. |
| Transfer manifest | `%TEMP%/hispania-pi35-transfer-v2-de790c9-20261024-74d732481ee942f3b57b53da46f3f422/payload/TRANSFER-MANIFEST.json` | `9CBC0FA10A6AE0AB3D455E5B7BFF6623F18E3D69B7A5C7502B619826B1BF435B` | Hash rechecked. |
| PI-35 runner v2 | `%TEMP%/hispania-pi35-prepare-run-v2-de790c9-20261024-74d732481ee942f3b57b53da46f3f422.ps1` | `BD5EEFFEEE46F63727E8EA9011FAAC40E6D35BE3F191F68F249F80C10766E3AC` | Hash rechecked; summary does not include runner hash field. |
| Slice 8 candidate manifest | `hispania-slice8-pi33-de790c9-20261021-publish/core-artifact-manifest.json` | `1CB4D94FBC60B9BE2CE55A38317116B7B38B3D5C6C4682BBD8A693EB50C1A21B` | Hash rechecked; validator PASS, content ID 700E… |
| Slice 7 baseline manifest | `hispania-slice7-accepted-20261018-publish/core-artifact-manifest.json` | `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87` | All 19 files including manifest match pinned baseline JSON. |

PI-35 test: `Hispania.Core.Tests.CorePersistenceTests.StoragePathGuardDetectsLinksWithoutTraversingOrDeletingTheirTargets`. Current execution result: restore/build/test exit 0; one pass, zero failed/skipped; direct symbolic links; probe and cleanup PASS. No secrets are reproduced here.

## Artifact locations and candidate identity

- Current candidate root: `%TEMP%\hispania-slice8-pi33-de790c9-20261021-publish`.
- Historical Slice 7 root: `%TEMP%\hispania-slice7-accepted-20261018-publish`.
- Candidate inventory proposal: `candidate-inventory-700e403e74f566b5.json` in this consolidation package.
- Candidate source commit/tree: `de790c970ba9773f667613d752a699e526fad296` / `9e666734baf74d2904c1ad80d471597c65e20c34`.