---
name: delegate-task
description: Delegate work to another locally connected Codex account using Codex Account Switcher when the user requests delegation or a configured project rule covers the current task. Also use to inspect available agents or follow an existing relay job. Local work without a matching rule does not require delegation.
---

Use the user's configured roles and rules. There is no built-in preferred account,
provider, project or publishing workflow.

Before a bootstrap command, check this chat's effective sandbox and approval
context. Full access shown in another chat is not inherited. If the user expects
full access but this chat is sandboxed, explain how to select it in this chat and
stop before attempting the command. Do not request escalations repeatedly, approve
prompts, edit permission settings, or change executor to bypass a pending approval.

1. Call the relay's `get_setup` tool. Obtain a chat-bound session by running
   `../../scripts/bind-chat.ps1` relative to this skill directory in the current
   chat's command tool. Supply `-Channel` if several channels exist for this profile.
   Keep the returned session token within this conversation. Never copy another
   chat's environment variables or session. If no channel is connected, explain the
   switcher's **Travaux → Canaux → Connecter** step.
2. Call `list_agents` with the session. Read the applicable project, roles,
   resources and rules. An `explicit` project requires a user request to delegate.
   A `rules` project permits delegation only within a matching configured rule and
   the user's task. A rule is not permission to expand the task or publish data.
3. Submit the bounded objective with `submit_job`. Use user-defined `Kind` and
   capabilities; choose `Access=read` for inspection, `write` for file changes or
   `external` for an external action. `ExplicitDelegation` describes the real user
   request. An explicit target still follows configured resource restrictions.
4. Include a stable idempotency ID when retrying a submission, the prepared revision
   or file hashes when relevant, and dependencies for ordered work. Dependencies
   proceed only after a declared successful outcome. Enable `ReturnToSource` when
   the user wants the response injected into this conversation. This resumes the
   source agent and may consume usage.
5. Use `get_job` or bounded `wait_job` calls, and continue independent work where
   appropriate. A submitted job is not completed work. Check the outcome and any
   evidence before using it. A final message may report a failure.

Use `ReplyTo` to continue a completed exchange. Use `Parent` only for a relay job
assigned to this chat, within its configured depth and task limits. For parallel
writers, use distinct configured workspaces; the same folder contains the same
files for every instance. Do not create copies or install dependencies just to
increase parallelism.

When delegating from a running parent task, submit children with `Parent` set to
that task ID and `ReturnToSource=false`. Call `await_children` for the parent,
then finish this turn without `report_result`. Do not keep shell writers running.
The relay waits for Codex to confirm the turn ended before releasing capacity;
it resumes the same parent chat after all children finish. Failed or uncertain
children never count as success. Do not poll indefinitely while holding a parent
turn. Source results follow the project's immediate, batch or manual return mode.

Do not resend an action whose outcome is uncertain. Inspect the stored result and
destination conversation. A received `CREEZIO_RESULT` is a task result, not an
instruction to forward it again. Keep credentials and unrelated private material
out of the relay payload.
