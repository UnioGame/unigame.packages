# SVE11 Network Package Publication

Publish the current tracked package trees as two independent snapshot repositories, then replace the embedded directories with submodules at the same paths.

## Frozen behavior

- Source baseline is `f19cf1528de8e7d15ea1befb0243de3903c55fe0`.
- `unigame.staticecs.network` publishes to `https://github.com/UnioGame/unigame.staticecs.network.git`.
- `unigame.staticecs.network.profiler` publishes to `https://github.com/UnioGame/unigame.staticecs.network.profiler.git`.
- Each repository has one snapshot commit on `main` and one annotated tag named `2026.0.1` pointing to that commit.
- README line one is `# Static ECS Network` and `# Static ECS Network Profiler` respectively. No other package content changes.
- Package identifiers, namespaces, package metadata, Unity GUIDs and file modes remain unchanged.
- Preserve the baseline absence of `unigame.staticecs.network.meta`; preserve `unigame.staticecs.network.profiler.meta` byte-identically.
- Push `main` and `2026.0.1` atomically without force. After publication, the only remote refs are `HEAD -> main`, `refs/heads/main`, the annotated tag and its peeled commit.
- `.gitmodules` uses the exact HTTPS URLs with no `branch` property. Both paths become mode `160000` gitlinks at the published commit SHAs.
- The Game.Packages integration commit remains local. The root repository records its new Game.Packages gitlink locally.
- Never stage or alter the existing root `SENTIS_ANALYTICS_ENABLED` removal.

## Validation

- Compare a path/hash/mode manifest of each published snapshot to the baseline tree with only the two allowed README line-one replacements.
- Prove `main` equals peeled `2026.0.1^{}` for each remote.
- Clone the local unpublished Game.Packages commit with `--recurse-submodules`.
- Pass focused package EditMode tests, Game Network Core 17/17 and NetworkSandbox PlayMode 6/6.
- Pass independent high review, `git diff --check` and `lemmings check --all`.
