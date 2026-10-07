---
name: delegate-task
description: Delegate a mission to a named Codex instance with Account Switcher when the user says "delegate to NAME", "délègue à NOM", or an explicit configured project rule covers the work. Also inspect available agents or follow a relay job. Local work without a matching rule does not require delegation.
---

Use the user's configured roles and rules. There is no built-in preferred account,
provider, project or publishing workflow.

Before a bootstrap command, check this chat's effective sandbox and approval
context. Full access shown in another chat is not inherited. If the user expects
full access but this chat is sandboxed, explain how to select it in this chat and
stop before attempting the command. Do not request escalations repeatedly, approve
prompts, edit permission settings, or change executor to bypass a pending approval.

1. Call `get_setup`. Run `../../scripts/bind-chat.ps1` from this skill
   directory in the current chat, without parameters and with the current project
   as the command working directory. It automatically connects this real chat.
   Keep its returned session token private to this chat. Never copy another chat's
   environment or session. Do not ask the user to configure channels.
2. For a user naming an instance, call `list_instances`. Match its exact name or
   stable ID, then `delegate_to_instance` with a fresh 32-character hexadecimal ID,
   a bounded objective, expected evidence, and `access=read`, `write` or `external`.
   Set `explicitDelegation=true` only for the actual human request. Result delivery
   to this chat is enabled by default; finish the turn when no independent work is
   left so the returned result can resume it. An unavailable instance must be opened
   by the user. Do not start or stop their apps without authorization.
3. If the named tool reports an existing advanced project policy, call `list_agents`
   and use `submit_job` respecting its project, resource and destination rules. This
   is an advanced compatibility path, not a prerequisite for ordinary named missions.
   Automatic delegation without a named human request still requires such a rule.
4. Keep and reuse the same ID after an uncertain submission. Never silently select
   another account or repeat an external action. Use `get_job` / bounded `wait_job`
   to inspect progress. Submitted does not mean completed. Verify the result and
   evidence. A blocked or failed result is not success.

Use `ReplyTo` to continue a completed exchange. Use `Parent` only for a relay job
assigned to this chat, within its configured depth and task limits. For parallel
writers, use distinct configured workspaces. Instances on the same PC can share
files when configured to the same folder. Remote PCs do not share or synchronize
files automatically, even when folder names match. Verify the prepared revision
on the destination. Do not create copies or install dependencies just to increase
parallelism. Cursor IDE is not supported by this version.

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
