## What and why

<!-- What does this change, and what problem does it solve? -->

## How I checked it

- [ ] `dotnet build CarsControl.sln` and `dotnet test tests\CarsControl.Tests` pass
- [ ] I added or updated a test
- [ ] UI changes: I ran `tools\ui-audit\Capture-All.ps1 -Quick` and `Audit-Keyboard.ps1` and looked at the screens (screenshots below)
- [ ] I updated `CHANGELOG.md` and any docs this makes wrong

## Admin helper and safety

- [ ] This doesn't change what the admin helper accepts, **or** it adds an operation that carries identifiers only, validates them against the helper's own list, has a rejection test and is listed in `docs/security.md`
- [ ] Anything destructive says what it will do first and is undoable where possible
- [ ] No new outbound connection, **or** it's opt-in/necessary and added to the list in `docs/security.md`

<!-- Screenshots, if the UI changed -->
