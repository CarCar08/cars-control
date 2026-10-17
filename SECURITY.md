# Security policy

C.A.R.S. Control installs software with an administrator-level helper, so security reports are taken seriously.

## Reporting a problem

**Please don't open a public issue for a security problem.** Use GitHub's private reporting instead:
[**Report a vulnerability**](https://github.com/CarCar08/cars-control/security/advisories/new) (Security tab › *Report a vulnerability*).

Include what you found, how to reproduce it and what it lets someone do. You'll get a reply within a few days. Once a fix is released the advisory is made public and you're credited if you like.

## What counts

Anything that lets a program or user **gain more rights than they have** through C.A.R.S. Control, for example:

* getting the admin helper to run something other than its fixed operations, to touch a path it shouldn't, or to accept a connection from something that isn't the app,
* tampering with the installed files, the installer or an update between "verified" and "run",
* a privacy switch or cleanup rule that weakens Windows' security.

How the helper is built, every operation it accepts and what each one validates are in [docs/security.md](docs/security.md).

## Supported versions

The latest release. Fixes go into a new release; the in-app updater offers it.

## Verifying a download

Each release lists the SHA-256 of the installer in `SHA256SUMS.txt`. The installer isn't code-signed yet, so Windows SmartScreen warns on first run; compare the checksum ([install guide](docs/install.md#2-download-it)) or [build from source](docs/building.md).
