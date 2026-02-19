# Agent Workflow (Solo + AI Assistance)

Goal: use AI agents to move faster without turning the repo into an unreviewable mess.

## Rules of Engagement

- One task per change: keep protocol, movement, persistence, and UI changes separate unless tightly coupled.
- Keep diffs small: target <= 300 lines changed unless there's a strong reason.
- Always include acceptance criteria: if you can't state how to verify it, the task is underspecified.
- Keep the server authoritative: no client-side world-state writes that the server doesn't validate.
- Don't overdesign: prefer the simplest thing that proves the loop (connect -> move -> see -> chat -> persist).

## Definition of Done (Per Task)

- Builds: `dotnet build` passes for affected projects; Godot client runs and connects (if client touched).
- Tests: add/adjust a minimal test when changing protocol serialization or persistence.
- Manual smoke: start server, connect 2 clients, move, chat, disconnect/reconnect (if relevant).
- Docs: update `docs/03-network-protocol.md` when messages change; update `docs/01-scope.md` only if scope changes.

## Review Checklist (Before Merging/Moving On)

- Protocol: no field number reuse; protocol version bumped only for breaking changes.
- Networking: reliable vs unreliable choice matches semantics; snapshot sizes stay under MTU target (roughly; log sizes if unsure).
- Server: validates input rates/sizes; no trust of client positions; no unbounded allocations per packet.
- Persistence: parameterized SQL; handles missing records gracefully.

## How to Work with Agents

- Give the agent a narrow goal and a hard stop: "Add message X and handlers; do not change Y."
- Ask for incremental checkpoints: "First update `.proto` and tests; then client handler; then server handler."
- Require a quick test plan in every response: the agent should state how to validate locally.

## Prompt Templates

Early milestone prompt pack:
- See `docs/06-agent-prompts.md` for copy/paste prompts for M0-M8.

Base system prompt:
- See `docs/07-system-prompt.md` for a copy/paste "senior game dev/engineer" system prompt to pair with the task prompts.

Add a network message:
- "Add a new protobuf message `C_Foo` / `S_Bar` for [purpose]. Keep fields minimal. Update `.proto`, shared codegen, server handler, client handler, and `docs/03-network-protocol.md`. Add a small serialization test. Do not change tick rates or unrelated messages."

Implement a feature slice (vertical):
- "Implement [feature] end-to-end with server authority. Constraints: no new systems beyond v0 scope, keep changes small, include acceptance criteria and a smoke test. Touch only these areas: [list]."

Refactor safely:
- "Refactor [module] to improve clarity without behavior changes. Add/keep tests. Provide a risk list (what could break) and a verification checklist."

Bug investigation:
- "Reproduce and isolate: [symptom]. Add logging or a minimal test to capture it. Propose the smallest fix with a verification plan."
