---
name: delegate-task
description: Delegate a mission to a named Codex instance with Account Switcher when the user says "delegate to NAME", "délègue à NOM", or an explicit configured project rule covers the work. Also inspect available agents or follow a relay job. Local work without a matching rule does not require delegation.
---

Use the user's configured roles and rules. There is no built-in preferred account,
provider, project or publishing workflow.

For an explicitly requested **Codex Cloud** mission, keep that execution mode:
call `list_instances`, then `list_cloud_environments` for the named account.
Select the requested ready environment, or ask which one if the choice is
ambiguous. Use `delegate_to_cloud` with a fresh 32-character hex ID and the
human's actual mandate. Cloud tasks use their own repository checkout; local
uncommitted files are not available there. Put prepared work on an explicitly
authorized GitHub branch first when required. Supply `feature`, the actual
`lot` number, `specification` (document and section), and `repository`
(owner/name) so the mission can be traced to the project plan. The Switcher
assigns `codex/<account>/<feature>/lot-NNN`; its internal ID remains a separate
idempotency key. `list_cloud_jobs` reads this source chat's durable registry,
including branch, state and reported PR/SHA. It allocates a lot number when
omitted, never reuses an occupied lot in the same repositories, and keeps older
jobs' original branches. Reported PR/SHA are unverified until independently
checked. Keep the project's own mission register current with these exact
returned values; never invent branch names or a lot/specification reference.
When the user specifies execution settings, pass the exact native `model`,
`reasoningEffort` and `permissionProfile`; otherwise the user's saved Cloud
defaults apply. A mission freezes these settings at admission, including retries
after defaults change. The registry distinguishes requested settings from native
observations. Unsupported or unknown required settings block the business prompt;
never silently choose another model, effort, profile or execution mode. Selecting
an authorized native permission profile does not approve a pending native request.

For a human-authorized **project team and backlog**, prepare complete work ahead
of time instead of keeping an orchestrator turn alive to check for free slots.
Bind this exact orchestrator chat, inspect `list_instances` and the selected
accounts' environments, then use these five queue tools:

- `configure_cloud_team`: give a stable team ID, exact repository, actual bounded
  human `mandate`, `explicitDelegation=true`, and eligible targets containing
  their stable instance IDs, permanent account keys and configuration IDs. This
  is project-scoped permission, never blanket authority for unrelated work.
- `prepare_cloud_mission`: give a stable fresh ID and a complete immutable
  mission: objective/context, exclusions, acceptance/tests, feature/lot,
  specification, repository, reserved relative files, priority, eligible subset
  of the team, and dependencies. User execution defaults freeze when prepared;
  explicit model/effort/profile override them only within actual authorization.
  Eligible account/configuration bindings freeze too; the ready environment
  version is verified and pinned when the real Cloud job is admitted, not when
  the backlog is prepared. Local uncommitted files do not become Cloud input.
- `list_cloud_queue`: use its compact snapshot for states, dependencies, selected
  account, actual job/chat/turn IDs and idle alerts. Prompts and private session
  tokens are not repeated. An empty ready queue is a planning gap, not permission
  for the Switcher to invent another mission.
- `review_cloud_dependency`: after independently checking the exact delivery
  and performing the integration required by the project, accept its returned
  job/chat/turn triple. Dependencies default to `accepted`; a completed turn,
  PR, notification or self-reported test result alone does not satisfy review.
  Choose `mode=completed` only explicitly for work that may start without that
  acceptance. Acceptance records the orchestrator's decision; it does not merge
  anything or prove GitHub integration on its own.
- `control_cloud_queue`: pause/resume a team or prepared mission, cancel only
  work not dispatched, or acknowledge a specific idle-alert episode. After
  reviewing an exact terminal failed/cancelled delivery, `action=reject` with
  its `id`, `jobId`, `threadId` and `turnId` releases its file holds without
  accepting it or satisfying dependencies. Send explicit null for an observed
  chat/turn that was never created; never invent IDs. Failed work otherwise
  retains its files for review. Active, uncertain or attention states cannot
  be released this way. These actions never interrupt a native Cloud turn or
  approve its requests.

Fill enough genuinely independent ready missions upfront and reserve conflicting
files/dependencies before leaving the turn. The durable queue dispatches eligible
missions within team/global/account limits without a fresh AI planning prompt;
it never picks a new strategy, expands a mandate, merges or publishes. Reuse IDs
after uncertainty. Changes to a frozen mandate require cancellation before
dispatch and a separately authorized new ID, not silent mutation. On delivery,
review and integrate, accept the precise predecessor, then replenish the ready
queue. Do not poll or sleep while waiting. A paused team stays paused until an
authorized source resumes it; all queue reads and controls belong to the exact
bound orchestrator chat, never an impersonated session or another conversation.

Never silently replace Cloud with
local execution or another account. The Switcher checks the admitted Cloud turn
every 60 seconds and sends its result to this exact source chat when
`returnToSource` is enabled. The Cloud agent does not need a callback tool or
access to the source account. Do independent work or finish this turn instead of
holding it open with polling or shell sleeps. Use `get_cloud_job` or
`list_cloud_jobs` for a bounded status check; `Monitoring` distinguishes checks,
notification attempts and acceptance by Codex. An unavailable source is retried;
an uncertain delivery is inspected without blindly resending. Notification
acceptance does not prove that the orchestrator has processed it. The dashboard
is available in **Account Switcher → Délégations**.
For an authorized review/correction loop, use `follow_up_cloud_job` with the
latest terminal job's `parentId`, a fresh `id`, a focused title and correction
prompt, and `explicitDelegation=true` only within that actual mandate. Include
the GitHub issue and existing PR, review findings, allowed files and expected
tests. This preserves the developer account, Cloud chat, environment, branch
and frozen execution settings; a linked child record tracks its new turn and
return separately. Never use a new `delegate_to_cloud` call to pretend to resume
an existing PR. Reuse the follow-up ID on uncertainty; do not submit a sibling
correction or bypass a native approval. GitHub issues describe missions; PRs
contain the proposed delivery. The orchestrator selects independent ready work,
checks the actual GitHub evidence and sends corrections to the same developer.
The user's **Settings → Cloud team** preference limits simultaneous Cloud
missions, with one per account; queued work waits without opening Codex windows.
A returned PR and SHA need independent
verification before integration. Do not merge unless the user or a configured
project policy has authorized it. A pending native approval stays pending.
After an uncertain send, reuse the same ID; never create a second mission as a
retry. `cancel_cloud_job` only cancels work that has not started. A received
`CREEZIO_CLOUD_RESULT` or `CREEZIO_CLOUD_ATTENTION` is a result or blocker for the
existing mandate, not authorization to delegate again, merge or approve a native
request. Follow the user's actual project and review rules.

Before a bootstrap command, check this chat's effective sandbox and approval
context. Full access shown in another chat is not inherited. If the user expects
full access but this chat is sandboxed, explain how to select it in this chat and
stop before attempting the command. Do not request escalations repeatedly, approve
prompts, edit permission settings, or change executor to bypass a pending approval.

1. Call `get_setup`. In an owned headless mission identified by
   `CREEZIO_HEADLESS_CHAT`, call `bind_headless_chat` with that exact chat ID;
   the relay verifies the actual native engine, account and chat. Do not run a
   bootstrap shell command or request elevation for this headless path. Otherwise
   run `../../scripts/bind-chat.ps1` from this skill
   directory in the current chat, without parameters and with the current project
   as the command working directory. It automatically connects this real chat.
   Keep its returned session token private to this chat. Never copy another chat's
   environment or session. Do not ask the user to configure channels.
2. For a user naming an instance, call `list_instances`. Match its exact name or
   stable ID, then `delegate_to_instance` with a fresh 32-character hexadecimal ID,
   a bounded objective, expected evidence, and `access=read`, `write` or `external`.
   Set `explicitDelegation=true` only for the actual human request. Result delivery
   to this chat is enabled by default; finish the turn when no independent work is
   left so the returned result can resume it. A closed window may receive a local
   mission when its user enabled agents without a window. If unavailable, report
   the actual connection diagnostic. Do not start or stop their apps yourself.
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
