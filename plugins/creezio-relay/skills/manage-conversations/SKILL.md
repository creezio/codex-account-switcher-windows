---
name: manage-conversations
description: Copy or transfer a user-selected local Codex conversation between named Switcher instances, or inspect and resume its recorded operation. Use for conversation history migration, not resource sharing or delegation.
---

Bind this actual chat with `../../scripts/bind-chat.ps1` (or `bind_headless_chat`
for its existing owned headless mission). Keep the returned session private.
Use `list_instances` and `list_instance_conversations` to resolve the user's
source instance, destination instance and exact conversation ID. The listing
is paginated: pass its cursor with the same instance and query.

`copy_conversation` preserves the original. `transfer_conversation` copies and
verifies the history, then archives the original after checking both histories
again. It creates a new destination ID; it does not move the existing ID.
Use the operation the user requested; ask only if copy versus transfer is
materially ambiguous. Set `explicitRequest=true` only for the user's mandate.

Supply a fresh 32-character lowercase hex `id`, exact `source`, `destination`,
`threadId` and a destination `title` of at most 160 characters. Persist that
request ID and selection before submitting. The response's `id` is a separate,
durable operation ID: use it for `get_conversation_transfer` and
`resume_conversation_transfer`.

Submission is asynchronous. `queued` or `running` is not success. Check status
with bounded waits and report completion only when `state=completed` and a
`targetThreadId` is present. A transfer additionally needs `sourceArchived=true`.
After a lost submission response, reuse the SAME request ID and selection.
After resolving a `blocked` or `interrupted` operation's reported problem,
resume its durable operation ID; never create a replacement to retry uncertainty.

The source must be idle, and the existing native instances must be available.
These tools do not stop an active turn, start a model turn, send a prompt,
open windows or restart instances. The current active chat cannot be copied
while it is still working. Account changes or revoked access suspend the
operation. Native destination permissions apply and inherited autonomous goals
are cleared by the importer. Files, cloud conversations, provider access and
plugin connections are not migrated with the history. To show a completed
copy, offer its `codex://threads/<targetThreadId>` link; do not open it unless
the user asked.
