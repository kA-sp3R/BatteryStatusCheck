# Development rules

- Work in the existing checkout. Cloud tasks are already isolated; do not create a Git worktree unless requested.
- Implement one milestone at a time. The user approved Milestone 4 after reviewing Milestone 3. Final review, optimisation, automated verification and release packaging are in scope; runtime and hardware verification still require separate permission.
- Compile using **Windows GitHub Actions**. Compilation, focused automated tests, application runtime verification, and physical hardware verification are separate outcomes.
- Obtain explicit permission before launching the application, physical hardware investigation/testing, or full testing. Hardware-free focused unit tests may run in their separate CI job. Full testing belongs to Milestone 4.
- Never manufacture battery percentages or treat zero as missing. Preserve Current, Stale, Disconnected, Unsupported, and Failed states and original sample times.
- A plugged-in USB receiver is not evidence of a connected peripheral. Report link state as unknown unless a device-specific exchange establishes it. Do not attribute a reply to a model or receiver slot without evidence.
- Only USB 2.4 GHz receivers are in scope; exclude Bluetooth and wired device IDs. Unverified hardware support must remain explicitly unverified.
- Keep monitoring read-only. Polling-rate changes belong to Milestone 3 and require deliberate user action; never send firmware, pairing, lighting, or configuration writes from a battery provider.
- Independently implement protocol facts, cite researched revisions, and respect upstream licences. Do not transplant copyleft source without an explicit licensing decision.
