# Shared fixtures

Files both test suites read, so the Windows app (`tests/CarsControl.Tests`) and the Mac app (`macos/Tests`) keep agreeing on
formats they exchange through the sync folder.

- `fleet/status-windows.json`: a status file as Windows writes it (seven fraction digits, no `platform` key, as releases
  before the Mac version wrote it).
- `fleet/status-mac.json`: a status file as the Mac writes it.

`scripts/mac.sh` copies this folder to the Mac next to the package.
