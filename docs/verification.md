# Milestone 1 verification

| Category | Status | Evidence / next action |
| --- | --- | --- |
| Static review | In progress | Protocol-source review and source/XML/whitespace checks; not compilation. |
| Windows compilation | Not run | Workflow is authored; a successful Windows GitHub Actions result is required. |
| Focused automated tests | Authored, not run | Pure Core/protocol tests only. CI records count and outcome separately from compilation. |
| Full testing | Not run | Reserved for Milestone 4; requires permission. |
| Application runtime | Not run | Requires explicit permission on Windows. |
| Physical hardware | Not run | Requires explicit permission; no compatibility or accuracy certification claimed. |

The app does not start during CI. Hardware tests are not part of automated tests.

## Outstanding limitations

- Alienware Pro headset battery monitoring cannot be implemented honestly without a trustworthy receiver identity and documented/captured protocol. AW988 and generic Dell IDs are not substitutes.
- Generic Razer HyperPolling and shared receiver association are unsupported. Razer battery replies do not independently prove a live radio connection or exclude firmware-cached data.
- Logitech support is constrained to documented receiver IDs and real percentage capabilities. Level-only models cannot provide the requested exact battery percentage.
- Windows discovery and native I/O, protocol compatibility, reconnect behavior, and firmware caching need application/hardware verification after permission. Read-only query packets still access hardware when the user launches the app.
- No Milestone 2 work should begin until the user approves it. These limitations prevent claiming all Milestone 1 hardware requirements are complete.
