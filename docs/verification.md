# Milestone 1 verification

| Category | Status | Evidence / next action |
| --- | --- | --- |
| Static review | Completed | Protocol-source/peer review; 8 XML files parsed, project references resolved, workflow parsed, whitespace checks passed. This is not compilation. |
| Windows compilation | Result unverified | Windows CI is configured; GitHub API access is blocked, so no run or outcome is claimed. This also applies to the new Alienware reader/control transport. |
| Focused C# automated tests | Authored; result unverified | Separate Windows CI job for pure Core/protocol/provider tests, including Alienware captured-byte and receiver/link-state scenarios. No C# test counts or passing outcome have been observed. |
| Offline capture parser tests | 5 passed | `python3 -m unittest discover -s tools/alienware -p 'test_*.py' -v`; synthetic USBPcap submissions/completions, IRP reuse, zero bytes/percentage fields, filtering and truncation. This does not validate actual capture files or hardware. |
| Full testing | Not run | Reserved for Milestone 4; requires permission. |
| Application runtime | Not run | Requires explicit permission on Windows. |
| Physical hardware | Not run | Requires explicit permission; no compatibility or accuracy certification claimed. |

The app does not start during CI. Hardware tests are not part of automated tests.

The existing Git proxy successfully pushed the initial source; no extra GitHub token was requested. The cloud network allowlist blocks `api.github.com`, preventing CI-run/log retrieval. An additive domain change is saved in the environment configuration draft; it must be reviewed/saved and the environment published before it is active. After access changes, retrieve the workflow result for the current source commit, investigate failures, and update this report. The earlier documentation-only status commit uses `[skip ci]`; the functional Alienware change does not skip CI.

## Outstanding limitations

- Alienware Pro now has an implementation based on the exact-model MIT project `romain-ncls/awpro`, revision `d92f6df5`, whose documented frames and fixtures come from AWCC captures. This establishes implementation evidence, not local Windows or hardware validation. The original pcaps/descriptors are not public in that repository. See [research](research/alienware-awcc.md).
- Generic Razer HyperPolling and shared receiver association are unsupported. Razer battery replies do not independently prove a live radio connection or exclude firmware-cached data.
- Logitech support is constrained to documented receiver IDs and real percentage capabilities. Level-only models cannot provide the requested exact battery percentage.
- Windows discovery and native I/O, protocol compatibility, reconnect behavior, and firmware caching need application/hardware verification after permission. Read-only query packets still access hardware when the user launches the app.
- No Milestone 2 work should begin until the user approves it. These verification limitations prevent claiming all Milestone 1 hardware requirements are complete.
