# Contributing to C.A.R.S. Control

Thanks for wanting to help. Bug reports, ideas and pull requests are all welcome.

## Reporting a bug

[Open an issue](https://github.com/CarCar08/cars-control/issues/new/choose) and pick **Bug report**. It asks for:

* the version and what Windows you have: in the app, **Settings › About &amp; updates › Copy diagnostics** gives you a block with no personal data to paste in,
* what you did, what you expected and what happened,
* the last lines of `%LOCALAPPDATA%\CarsControl\logs\errors.log` if the app showed an error,
* for install problems, `%TEMP%\CarsControl-Setup.log`.

Please check the screenshots and logs for anything private (user name in a path, app or file names you'd rather not share) before posting.

## Suggesting something

Use the **Feature request** form. The most useful ones say what you were trying to do and what got in the way, not just the feature you imagine.

## Security problems

Don't open a public issue. See [SECURITY.md](SECURITY.md).

## Working on the code

[docs/building.md](docs/building.md) has everything: prerequisites, building, test copies that run safely next to your real install, the UI audit tools, how to add a cleanup rule, a privacy switch or a helper operation, and the rules that keep the admin helper safe.

The short version:

1. Fork, branch from `main`.
2. `dotnet build CarsControl.sln` and `dotnet test tests\CarsControl.Tests` must pass.
3. Add a test with your change. Logic goes in `CarsControl.Core` so it can be tested without a window.
4. If you changed the UI, run `tools\ui-audit\Capture-All.ps1 -Quick` and `Audit-Keyboard.ps1` (they run a test copy and take no focus) and look at the screens.
5. Update `CHANGELOG.md` and any doc the change makes wrong.
6. Open a pull request. Say what changed and why; screenshots help for UI.

### Rules that matter most

* **Changes to what the admin helper accepts get extra scrutiny.** Operations carry identifiers only, validated against the helper's own list; never a path or command line from the app. See the security notes in `docs/building.md`.
* **Nothing destructive happens without saying what it will do**, and it should be undoable where it can be.
* **No telemetry, no accounts, nothing phoning home** beyond what [docs/security.md](docs/security.md) lists. New outbound connections need to be added to that list and be opt-in or obviously necessary.
* **Privacy and debloat switches never weaken security** (Defender, SmartScreen, firewall, UAC, Windows Update, error reporting).

## License

By contributing you agree your contribution is licensed under the [MIT License](LICENSE).
