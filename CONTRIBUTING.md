# Contributing

Thanks for your interest in SpatialBlend. Issues and pull requests are welcome.

## Before you start

- Set up the project as in the README (`python3 tools/setup_sdks.py`). Never commit your own credentials (Photon App IDs, a
  TURN secret or Worker URL) or third-party SDK content: the setup script writes them only to git-ignored files.
- Do not commit Asset Store content, Meta sample material (it is installed from your package cache) or narration audio
  whose licence does not allow redistribution under this repository's licence. New third-party files need an entry in
  `NOTICE` / `THIRD_PARTY.md` and, for dependencies, in `SDKS.lock`.

## Code

- C# with Unity conventions; follow the patterns in `Assets/SpatialBlend/Framework/`. 4-space indentation, braces on new lines,
  `PascalCase` for types and public members, `camelCase` for locals, `_camelCase` for private fields.
- Use `SpatialBlendAPI` rather than the native calls (`DllImport`) directly: it converts coordinates (Unity Y-up, core Z-up) and
  marshals callbacks to the main thread.
- Python tools: standard library only where possible, with a test under `tools/test_*.py`.

## Tests

- `python3 -m pytest tools -q` must pass (no Unity, no network).
- Run the EditMode tests in the Unity Test Runner for the areas you change, and say in the pull request what you ran.
- For scene or UI changes, attach a screenshot or a short clip.

## Commits and pull requests

Short, imperative subjects (under about 72 characters) and a body that says why. One logical change per pull request.
