# Diagnostic reports

Open **Settings → Open logs** to inspect local JSONL files, or **Export diagnostic report** to save a ZIP. Diagnostics run independently of battery results: a storage failure cannot convert a measurement or stop monitoring. Exports are user-selected; nothing is uploaded automatically.

Logs record application version and UTC event times; selected HID collection VID/PID, usage, interface and report capacities; protocol command/capability metadata; validation and I/O failures; battery state, native category/percentage, charging/link states and original sample timestamp. Manual polling outcomes remain separate events. No additional device queries are sent for logging.

The default budget is four 256 KiB log files, an 8 KiB event limit and seven-day retention. Identifying serials, full HID/device paths, canonical receiver/device IDs, names and exception paths are redacted or replaced with per-session keyed tokens. Reports exclude raw packets, settings files, aliases, credentials and stack traces. Tokens correlate activity within a session without publishing the original identity. Session changes intentionally change tokens.

Export includes the bounded redacted logs and a manifest stating version, time and redaction policy. Review a report before sharing it: protocol/model identifiers and battery states are useful diagnostic information, and no general redactor can infer every piece of personal text a device or OS might invent. Logging/export does not establish hardware compatibility, radio connectivity or measurement accuracy beyond the reported evidence.
