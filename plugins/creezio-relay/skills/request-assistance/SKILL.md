---
name: request-assistance
description: Ask the responsible person for clarification through Account Switcher when the user requests assistance or the configured assistance rules cover an ambiguous, oversized or blocked task. Read configured rules before deciding; do not escalate every ordinary task.
---

This workflow has no built-in owner, client, project, tool or business rule.

1. Read `get_setup`. Bind this chat with `../../scripts/bind-chat.ps1` using its
   effective permissions. Never copy another chat's token or approve a native
   permission prompt. If binding is unavailable, report that blocker to the user.
2. Call `assistance_rules` with the bound session. Follow the user's actual
   instructions and the applicable configured rule. A rule guides judgment; do
   not claim that all ambiguity or large tasks can be detected automatically.
3. Call `request_assistance` with a stable 32-character lowercase hexadecimal
   `id`, a short `title`, the precise `reason` or question, and minimal `context`.
   Set `explicitRequest` only when the user actually asked for assistance. Reuse
   the ID if submission is uncertain. Never include credentials or unrelated data.
4. Tell the user which clarification is pending and end the turn. Stop only the
   work that depends on this clarification. Do not keep polling or hold files
   open while waiting. The responsible person sees the request in **Assistance**.
5. Use `get_assistance` to inspect this chat's request when needed. A stored
   request is not an answer. A response is delivered into this visible chat after
   it is idle, subject to its native permissions and the configured access level.

A `CREEZIO_ASSISTANCE_RESPONSE` contains the responsible person's reply. Use it
within the original task's scope, verify its meaning, and do not automatically
create another request or acknowledgement loop. It does not grant new native
permissions or authorize unrelated work. If a real new blocker remains, explain
it precisely. Optional prompt hooks must be explicitly configured and trusted in
Codex; this skill does not install, trust or bypass them.
