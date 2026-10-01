## What changed

Describe the user-visible behavior and why the change is needed.

## Validation

List the exact automated tests and any bounded hardware checks that were run.
Do not claim physical-display success from source inspection or synthetic tests.

## Security and privacy

- [ ] No real device serial, USB/PnP instance ID, pairing token, private key,
      private diagnostic capture or contributor-specific absolute path is added.
- [ ] Driver, update, transport and input changes preserve ownership and
      fail-closed checks.
- [ ] New third-party material includes its license and provenance.

## Compatibility catalog (when applicable)

- [ ] The report was manually transcribed from reviewed public, non-unique
      facts; no Issue body, support-bundle JSON, attachment name or raw
      diagnostic output was copied into the catalog.
- [ ] Unverified capabilities remain unverified, and requested Hz is not
      presented as measured FPS.
- [ ] The catalog `--check` command passes and generated schema/Markdown files
      are synchronized.
