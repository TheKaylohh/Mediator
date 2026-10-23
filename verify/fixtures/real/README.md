# Real CLI captures

These files are unedited outputs from real CLI runs, captured on 23 Oct 2026 on Ubuntu 24.04 (x86-64).
Each run used `env -i` with an empty `HOME`, so no logins or keys from the capturing machine could reach it.
The prompt was `Say hi.`, sent on stdin. Every other fixture in the tests is reconstructed from documentation and is labelled that way where it is defined.

| File | CLI | Command | Exit | What it shows |
|---|---|---|---|---|
| `claude-2.1.309-invalid-api-key.stream.jsonl` | Claude Code 2.1.309 | `ANTHROPIC_API_KEY=<invalid> claude -p --output-format stream-json --verbose` | 1 | `system/init` with `apiKeySource: "ANTHROPIC_API_KEY"`; a synthetic assistant message; a `result` with `subtype: "success"` **and** `is_error: true`, `api_error_status: 401`, `terminal_reason: "api_error"`, zero usage |
| `gemini-0.65.0-untrusted-folder.stderr.txt` | Gemini CLI 0.65.0 | `gemini --output-format stream-json` in an untrusted folder | 55 | Headless runs are refused in untrusted folders. Nothing is written to stdout. |
| `gemini-0.65.0-no-auth.stderr.txt` | Gemini CLI 0.65.0 | same, with `GEMINI_CLI_TRUST_WORKSPACE=true` and no auth configured | 41 | Auth failure on stderr; nothing on stdout |
| `gemini-0.65.0-network-blocked.stream.jsonl` and `.stderr.txt` | Gemini CLI 0.65.0 | same, with an invalid `GEMINI_API_KEY` and Google endpoints unreachable | none (killed after 120 s) | `init` and `message` events, then retries with backoff. The stream has no result event (EOF without a result). |

## Not captured

These outputs are **not** captured, and the tests do not claim them:
- Successful runs, tool calls, rate limits and quota exhaustion. These need a signed-in Claude Pro or Google AI Pro account, and the capturing machine must not use one.
- Gemini's API-key error. The Google endpoint was unreachable from the capture machine.

One capture was taken and then **deleted** and never committed. In a Claude Code run with no key, the capture machine's network proxy supplied credentials and the call succeeded. That output belongs to the machine's account, so it is not a fixture.

To add captures of real successful runs, use `scripts/capture-cli.ps1` on the target Windows machine (see docs/v6/INTEGRATION_MATRIX.md).
