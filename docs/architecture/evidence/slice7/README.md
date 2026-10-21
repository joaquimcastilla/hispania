# Slice 7 Owner Smoke Evidence Intake

Save the owner's original, already-sanitized complete JSON result as:

`published-core-fixture-smoke-20261018.json`

The JSON is not to be reconstructed from a summary or reformatted during intake. Keep the original bytes. Before recording it as accepted evidence, validate that it parses as one JSON object, `overall` is `PASS`, `checks` contains exactly 14 entries and every `result` is `PASS`, `evidence` contains the accepted Core content ID and three fixture SHA-256 values, `coreExitCode` is 0, the three failure fields are null, and `secretsPrinted` is false. Confirm the artifact path/identity is the accepted Windows x64 publish and that no secret material is present. Then calculate SHA-256 over the saved file and record that digest and owner-reported provenance in the Slice 7 report and Evidence Ledger.

Do not edit or normalize the JSON to make it pass validation. If any check fails, preserve the supplied original and report the validation issue without recording it as passing evidence.
