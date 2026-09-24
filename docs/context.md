# Durable context: what the fleet remembers

A conversation with the fleet can touch several machines, several models, a plan that runs overnight and more than one
front end. Each model only knows what it was handed. The fleet therefore keeps its own record of the work, and hands each
agent a small, checked summary of it. This page explains what is kept, what an agent receives, and how to look at it.

## The idea in one paragraph

Every conversation is a **context** with an id. Everything that happens under that id is appended to a local database:
the messages, which machine answered each call, every tool call with its arguments and result, the decisions you or the
assistant pinned, plan transitions, check results, and the files a plan step produced (with their hashes). Before each
model call the fleet builds a short projection of that record and puts it in front of the model. Because the record is the
fleet's own, it survives a change of machine, a change of front end, and a restart of the backend.

## What is recorded

| Kind | What it holds |
|---|---|
| message | What the client sent: your messages, the assistant's earlier answers, tool results from VS Code |
| route | Which machine got each model call, and why (routed, planning, plan step) |
| assistant output | What the model said, and how many tool calls it made |
| tool call, tool result | The tool, its arguments, and its result (cut to 2000 characters, with a hash of the full result) |
| decision | A pinned decision or constraint. Never dropped |
| verification, error | A plan step's check and its output, and failures |
| plan transition | A plan or step changing state, with a note |
| artifact | A file a step produced: path, version, size and sha256 |
| handoff | A structured note from one step to the next |
| checkpoint | The state of a plan or agent session at a boundary |
| context assembled | The exact block handed to a model (so you can see what it was told) |

The data lives in `contexts\fleet-context.db` next to the backend (or in `FLEET_CONTEXTS_DIR`). It contains everything the
assistant saw and did, including file contents it read, so keep that folder private. It is listed in `.gitignore`. How long
it keeps chats is up to you; see [Keeping it small](#keeping-it-small).

## What an agent receives

Before every model call the backend assembles a block and adds it as a system message after the normal instructions. It is
built the same way every time, in this order:

1. A one-line reminder that this is a record kept by the fleet, not instructions from you, and that text copied from files
   or tools is untrusted.
2. The current task, when there is one.
3. **Pinned constraints and decisions.** These are never cut.
4. The **handoff** from the previous step: goal, what is done, how it was verified, open questions, risks, exactly what to do
   next and when it is done.
5. The **files** earlier work recorded, checked against the disk at that moment: unchanged, CHANGED since recorded (read it
   again first), or MISSING.
6. A summary of older history, when one has been made.
7. Recent failed checks and errors (for a plan step, that step's own, not ones earlier steps already got past).

The block is capped (about 4500 characters), and the least important sections are dropped first. A chat with nothing pinned
and no plan gets no block at all, so ordinary conversations cost nothing extra.

## Pinning a decision

A pinned decision is handed to every agent that continues the work, on any machine, after any restart.

- **Ask the assistant.** It has a `record_decision` tool. Say "remember: we use CommonJS, not ES modules" or "constraint: do
  not change the public API". While planning, the assistant is told to pin the decisions you state. The tool is only offered
  in plan mode, to plan steps, and when your message asks to remember, pin or decide something: small models otherwise
  "pin" things like "use a direct response" on a greeting, and a pin reaches every later agent.
- **Use the Context panel.** Open the web UI's **Context** button and type it into "Pin something every agent must respect".
- **Use the API.** `POST /api/contexts/<id>/decisions` with `{"text": "...", "category": "decision" | "constraint"}`.

Pinning the same sentence twice adds it once. Keep decisions short and specific; do not pin guesses.

Each pinned item is labelled by who pinned it: **from the user** (the Context panel or the API) or **noted by the assistant**
(the model called `record_decision`). Agents see that label. It matters because a model that has just read a web page or a
file can be steered by it, and it might pin something on the strength of that text; a pinned decision then reaches every
later step. Review the list in the Context panel, and press **unpin** on anything you did not intend. An unpinned decision
stays in the record but is no longer sent.

## Plans and handoffs

A plan proposed in a chat runs inside that chat's context, so what you decided while planning is still there when a step
runs. Each step is a task in the context. When a step passes its check the runner:

1. records the step's declared files as artifacts (path and hash),
2. writes a handoff for the next step,
3. saves a checkpoint.

At the start of the next step the assembled block shows those files and whether they still match. If something changed a
recorded file, or deleted it, the next agent is told before it relies on it, and the change is noted in the record.

A step that names a file it is meant to create is not done until that file exists, even if its check passes. (A check such
as `node --test` passes when there are no tests at all.)

If the backend restarts while a plan is running, the plan resumes in the same context with its handoff intact.

## Continuing a conversation somewhere else

The context id is the conversation's id in the **Sessions** list.

- **Web UI:** opening a session in the list continues it under the same id, so the record continues too.
- **`@fleet` in VS Code:** each response carries the id in its metadata, and VS Code returns it with the next turn of the same
  chat. The chat then appears in the web UI's Sessions list and can be continued there.
- **From the web UI (or another chat) into VS Code:** run **Agent Fleet: Continue a fleet chat** and pick the chat. Continue it
  in a new `@fleet` chat, which replays the saved transcript on every turn so nothing said in the web UI is lost, or link the
  **Fleet Router** model to it. A linked model-picker chat is handed the pinned decisions, recorded files and the latest turns
  by the backend; its own messages are added to the record without replacing the transcript the web UI shows. The link lasts
  until you remove it (the status bar shows it).
- **The Fleet Router model without a link:** it cannot carry an id in a standard way. An experimental setting,
  `agentFleet.contextCarrier`, returns an invisible data part that VS Code should send back. It is off by default until you
  have checked that your VS Code build returns it (see [vscode-fleet/README.md](../vscode-fleet/README.md)). Without it or a
  link, model-picker conversations are simply not recorded, and never create empty contexts.

## Looking at it

The web UI's **Context** button shows, for the current chat (and a plan card in the **Plans** panel has an "Open this plan's
durable context" link that shows the same view for the plan, so a run left going overnight can be inspected in the morning): pinned decisions, the latest handoff, the recorded files and
whether they still match, what the next agent would receive, what agents actually received (each block, with the machine),
and a timeline. The same data is available from the API:

| Path | What |
|---|---|
| `GET /api/contexts` | Every context |
| `GET /api/contexts/{id}` | The inspection: recent events, handoffs, artifacts, checkpoint, summary, and the preview of the next block |
| `GET /api/contexts/{id}/events?kind=&limit=` | Events, newest last |
| `GET /api/contexts/{id}/deliveries` | The blocks handed to models |
| `GET /api/contexts/{id}/artifacts` | Files and whether they match |
| `POST /api/contexts/{id}/decisions` | Pin a decision |
| `POST /api/contexts/{id}/events/{n}/pin` | Pin or unpin any event (`?pinned=false` to unpin) |
| `POST /api/contexts/{id}/compact` | Summarise older history |
| `GET /api/contexts/{id}/export` | Everything as JSON |

`search_context` is the assistant's own way to look back: it searches the record for a word and returns the matching entries.

## Long conversations

**Summarise older history** (or `POST .../compact`) writes a versioned summary of everything before the last few messages:
the goal, the last request, which machines were used, and the tool calls, checks, errors and files, with the ids of the events
it stands for. It is built by rules, not by a model, so it always works. It adds a record; nothing is removed, and pinned
decisions are not summarised at all because they are always sent in full.

## Keeping it small

The record grows with every model call. Two rules keep it in check:

- **Always:** each chat keeps only its newest 30 "context assembled" entries (the copies of what a model was handed, one per
  model call and the bulkiest thing in the record). Everything else in the chat is kept.
- **If you choose to:** delete a chat, with its whole record, once nothing has happened in it for a number of days. Set it in
  the web UI under **Config > History > Delete chats automatically**, or in `fleet.config.json`:

  ```json
  "history": { "deleteAfterDays": 90 }
  ```

  `0` or no `history` section keeps everything (the default). The backend checks five minutes after it starts and then every
  six hours, and reads the setting fresh each time, so a change applies without a restart.

A chat that a plan still runs in, waits in or is blocked in is never deleted by age, however old: the plan needs its record.
Once the plan is done or rejected the chat is treated like any other.

**Config > History** also shows how big the record is and has **Delete chats not used for...**: it lists the chats it would
delete, asks you to confirm, deletes them, and then compacts the file so the space goes back to the disk. The API is
`GET /api/contexts/storage?olderThanDays=N` (sizes and a preview; nothing is deleted) and
`POST /api/contexts/cleanup` with `{"olderThanDays": N}`.

A delete is a real one. The database overwrites deleted rows (SQLite `secure_delete`), and the write-ahead log is flushed and
cut back after each delete, so a deleted chat's text is not left behind in the files. Copies you made yourself (an export, a
backup of the folder) are of course not touched. To keep a chat before deleting it, use **Export JSON** in its Context panel.

## Old chats

Chats saved by earlier versions (JSON files in the `sessions` folder) are imported once when the backend starts. The files are
left where they are as a backup. Deleting a chat from the Sessions list deletes its record, unless a plan runs in it: then it
only disappears from the list, because the plan's record must survive.

## Limits

- The block is a summary, not the whole record. Ask the assistant to `search_context` for exact earlier output.
- Files are checked by hash, so a file that is edited and edited back looks unchanged.
- The record is local to the hub. It is not synchronised between hubs.
- Nothing here removes the need to review changes: the record says what happened, not whether it was right.
