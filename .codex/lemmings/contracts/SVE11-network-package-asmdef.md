# SVE11 Network Package Asmdef Contract

## Boundary

Repair the accepted network foundation so its Protocol and Runtime sources compile as the two separate Unity asmdef assemblies defined by the package.

## Required change

- Add `unigame.staticecs.network/Protocol/AssemblyInfo.cs` and its Unity `.meta` file.
- Grant internal access only to the Runtime assembly with exactly `[assembly: InternalsVisibleTo("unigame.staticecs.network")]`.
- Keep `Hashing` and the payload preflight helpers internal. Do not change the public API, asmdefs, wire contract, package manifest, README, runtime code, tests, or unrelated packages.

## Acceptance

- The only declared friend in the new file is `unigame.staticecs.network`.
- The candidate diff is limited to the two owned files and passes `git diff --check`.
- Unity imports and compiles the package without network-package C# errors across the Protocol/Runtime asmdef boundary.
- All focused `unigame.staticecs.network.tests` EditMode tests pass in the real Unity project; report the executed count.

