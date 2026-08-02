# ADR-0060: The server asks the caller for what it does not have

**Status:** Accepted
**Date:** 2026-08-02
**Deciders:** Repository owner · Platform architecture
**Relates to:** [ADR-0004](ADR-0004-universal-trigger-model.md) ·
[ADR-0027](ADR-0027-authorisation-runs-in-the-step-loop.md) ·
[ADR-0029](ADR-0029-a-refusal-is-a-result-failure.md) ·
[ADR-0047](ADR-0047-internal-is-a-composition-stance.md)

> Two things an agent-facing FlowX deployment does not have: **a human**, and **a model**.
> [13 §6](../13-AI-Native.md#6-capabilities-as-agent-tools) resolved the first by declaring the
> server would never prompt; [13 §5](../13-AI-Native.md#5-ai-assisted-engineering) resolved the
> second by sketching an `IAiProvider` the deployment would hold a key for. Both are on the wrong
> side of the wire. The caller has both, and MCP defines a request direction for asking it.

---

## 1. Context

### 1.1 What the annotation could not do

`plugins/FlowX.Mcp` publishes `confirmationRequired` on every tool descriptor, computed from the
flow's declared `ConfirmationMode` and the union of its capabilities' declared `SideEffects`. That
much is a projection of the manifest and is not in question here.

[13 §6](../13-AI-Native.md#6-capabilities-as-agent-tools) then states the limit in its own words:

> **The server does not prompt the human.** MCP puts human-in-the-loop on the client, which is the
> side a human is attached to; a server-driven prompt would need a session and a second round trip
> that the protocol does not define for this.

The first clause is right and the last clause is wrong. MCP revision `2025-06-18` — the revision
this surface already answers `initialize` with — defines `elicitation/create`: a request the
*server* sends to the *client*, carrying a message and a schema, answered `accept`, `decline` or
`cancel`. Streamable HTTP carries it down the response stream of the POST being served, and the
client's answer arrives as a separate POST. The round trip is defined, and no session is needed to
correlate it.

That matters because of what the annotation leaves standing. `samples/ai-agent`'s own attack table
claims:

> "Do it without asking the user" → confirmation is enforced by the **runtime**, from declared side
> effects — not by the model's cooperation

With an annotation alone, that row is false. The annotation is advice on a descriptor; whether a
human is asked is entirely the client's decision, and a client under the control of the thing being
constrained is not a control. The claim in the sample is exactly the one an annotation cannot make.

### 1.2 What a provider abstraction would cost

[13 §5](../13-AI-Native.md#5-ai-assisted-engineering) describes `flowx ai` as "an optional,
pluggable layer (`IAiProvider` — Anthropic, OpenAI, Azure OpenAI, local)". Building it means, per
provider: a package reference, a row in [`DEPENDENCIES.md`](../DEPENDENCIES.md), an API key in the
deployment's secret store, an egress rule, and a trim/AOT surface to keep clean under constraint
C2. It also means the deployment is the party that pays for and signs every completion, which makes
its last non-negotiable boundary — *"AI runs on the manifest, not on data"* — a promise about what
callers put in prompts.

And the caller on the other end of an MCP connection is, by construction, an agent: something with
a model. MCP defines `sampling/createMessage` for exactly this — the server asks the client's model.

### 1.3 The third thing, which turned out not to need a decision

Building the review layer showed that most of what `flowx ai review` is described as producing does
not need a model at all. Every finding in `samples/ai-agent`'s example output is a set operation
over the manifest. One of them cannot be produced from the manifest by any means — see §5.

---

## 2. Decision

**Where the server lacks something the caller has, it asks the caller for it over the connection,
rather than acquiring one of its own.**

Two instances, and they are one decision because they are the same shape, the same transport
direction and the same failure mode.

### 2.1 Confirmation is elicited from the client, per deployment

`McpOptions.Confirmation` takes `ConfirmationPolicy.Annotate` (default) or
`ConfirmationPolicy.Elicit`.

Under `Elicit`, a `tools/call` whose descriptor says `confirmationRequired` is answered with an
event stream carrying `elicitation/create`, and **the flow is not entered until an approval comes
back**. Declined, cancelled, unreadable, timed out, and a client that did not accept
`text/event-stream` are all one refusal — `mcp.confirmation_refused`, `ErrorCategory.Forbidden`,
distinguished by `detail.outcome`.

The prompt is built from the manifest: the flow's description, its declared side effects, its
required permissions, its idempotency, and the agent's own arguments with every `[Sensitive]` member
withheld and listed as withheld.

### 2.2 Completions are sampled from the client, never from a provider

`IAgentSampler` is in `FlowX.Abstractions` and is implemented by `FlowX.Mcp.McpCallScope`, closed
over the same event stream. A caller with no model — HTTP, a broker, a schedule, or an MCP client
that does not serve the method — yields `AgentSample.Unavailable()`, which is a value and not an
error.

**`FlowX.Ai` ships no provider, holds no key and references no assembly at all.** It reads a
manifest string and returns findings.

---

## 3. Consequences

### 3.1 Positive

- **The sample's central claim about confirmation becomes true**, and is asserted rather than
  described: `AgentSurfaceTests.ADeclinedConfirmationMeansTheFlowIsNeverEntered` checks both halves
  — that the question was put, and that the payment gateway's counter is zero afterwards.
- **`confirmationRequired` becomes load-bearing**, which is what makes `FLOWX1046` worth raising:
  under `Elicit`, `ConfirmationMode.Never` is the one declaration that switches the gate off.
- **No API key, no vendor SDK, no egress and no `DEPENDENCIES.md` row** for the AI layer. The
  completion is paid for and signed by the caller, on the caller's side of the connection.
- **"AI runs on the manifest, not on data" becomes structural.** `AgentSamplingRequest` carries two
  strings, and what `samples/ai-agent` puts in them is `ManifestReview.ToPrompt()` — a pure function
  of the build's manifest. Widening that requires changing a method signature.
- **`FlowX.Ai` links nothing**, so [13 §5](../13-AI-Native.md#5-ai-assisted-engineering)'s
  "uninstall `FlowX.Ai` and everything else works identically" is a fact rather than a claim about
  uninstall order — and a third-party reviewer can be written the same way.

### 3.2 Negative

- **A held request and an open stream per confirmed call.** A `tools/call` under `Elicit` occupies
  a connection and a request scope for as long as a human takes. `McpOptions.ClientRequestTimeout`
  bounds it at 30 seconds by default; a deployment expecting slow humans raises it and pays for it
  in concurrency.
- **`Elicit` breaks clients that cannot read an event stream**, deliberately — they are refused for
  tools with declared consequences. That is why it is **not** the default: making it one would have
  broken every existing MCP client on a version bump.
- **A second correlation table.** `McpPendingClientRequests` joins a server request to the POST
  answering it. It is process-local, so a deployment behind a load balancer must pin the answering
  POST to the node holding the stream — the same affinity any SSE transport needs, and a real
  operational constraint that did not exist before.
- **Confirmation necessarily precedes authorisation.** The server cannot know whether a caller
  holds `payment.refund` without entering the flow, so a human can be asked about a call that is
  then refused at the step. The alternative — running the flow to find out — is the thing the gate
  exists to prevent. `McpElicitation` says so in the prompt: *"The caller must already hold …
  Approving does not grant it."*
- **Borrowed models are not comparable between callers.** Two agents sampling the same review get
  two narrations from two models at two quality levels, and the deployment cannot pin either. That
  is the price of not holding a key, and it is why the deterministic rendering is what the flow
  returns when no model answers.

### 3.3 What this explicitly does not change

**Nothing about authorisation.** Every capability's stance is decided in the step loop against the
caller's own claims ([ADR-0027](ADR-0027-authorisation-runs-in-the-step-loop.md)) on both policy
values. An approval grants nothing and is not a claim.
`AgentSurfaceTests.ApprovingAConfirmationGrantsNoPermission` is that sentence as a test: an agent
holding no `payment.refund` approves its own prompt and is refused at the step, with
`authorization.permission_denied` and a gateway counter of zero.

The two codes are deliberately different so a client cannot confuse "a human said no" with "you may
not do this", which are different facts with different remedies.

---

## 4. Alternatives considered

| Alternative | Why not |
|---|---|
| **Keep the annotation and document the limit** | The limit is the whole claim. `samples/ai-agent` exists to demonstrate that an agent's action surface is constrained by the runtime rather than by the model's cooperation, and an advisory field constrains nothing |
| **Make `Elicit` the default** | Every MCP client that answers with a plain body would lose access to every tool with a declared consequence, on a version bump, with no source change |
| **A session (`Mcp-Session-Id`) to remember client capabilities from `initialize`** | Real state with a lifetime, an eviction policy and a cross-node story, to learn something the elicitation attempt itself reports in one round trip. A client that cannot elicit answers `-32601`, which is the same information |
| **Prompt via a tool result and a second `tools/call`** | Puts the resumption in the model's hands, which is the party the gate constrains. It also needs the first call to leave state behind, and a suspended flow is [ADR-0022](ADR-0022-http-shape-of-a-suspending-flow.md)'s machinery for something that has not started |
| **`IAiProvider` with a deployment-held key** | §1.2. A dependency, a key, an egress rule and a bill per provider, to reach a model the caller already has |
| **Both: a provider, falling back to sampling** | Two paths to a model with different data-handling properties, and the interesting boundary — what may be sent — would then hold on one of them |

---

## 5. The finding that cannot be produced, recorded here because it looks like an omission

[`samples/ai-agent`](../../samples/ai-agent)'s README shows `flowx ai review` producing:

> `order.place step 2 (payment.capture) has a retry policy and a 2s timeout, but the flow deadline
> is 30s. Worst case: 2s + 0.2s + 2s + 0.6s + 2s = 6.8s.`

`ManifestReview` does not produce it and cannot. `ManifestWriter.WritePolicies` emits a policy's
`kind` and its `stage` and **none of its parameters** — there is no `attempts`, no `PT2S` and no
backoff anywhere in the document. ([13 §3](../13-AI-Native.md#3-the-manifest-schema)'s schema
example shows them; nothing writes them.) Recovering the numbers means reading the source, which is
the one input [13 §5](../13-AI-Native.md#5-ai-assisted-engineering) forbids this layer.

It is also already answered where the numbers are: `DeadlineCoherenceAnalyzer` computes that budget
at compile time and raises `FLOWX1019` on every build, rather than when somebody runs a review.

This is recorded rather than fixed because the fix is a manifest change and the manifest is
approaching a freeze ([ADR-0017](ADR-0017-manifest-v1-freeze-criteria.md)). Adding policy
parameters is a field addition with a diff story and a compatibility story, and it belongs to that
decision rather than to this one.

---

## 6. Revisit when

- **A deployment needs `Elicit` across more than one node.** The pending-request table is
  process-local today. The trigger is a load balancer without session affinity, and the answer is
  probably `Mcp-Session-Id` plus a shared store — which is the session this record declined to
  build for correlation alone.
- **A confirmation needs to state a magnitude.** The prompt names `payment-gateway` and cannot name
  €19.98, because the manifest carries side-effect labels and not amounts. If a deployment needs
  the amount, the honest route is a capability that computes it before the effectful step and a
  flow that suspends for the decision — not a prompt that guesses.
- **The manifest gains policy parameters.** §5's finding becomes computable, and `ManifestReview`
  should raise it.
- **A deployment genuinely cannot borrow a model** — a batch reviewer with no interactive caller,
  or a compliance rule requiring a named model. That is the case `IAiProvider` was for, and it
  should be reopened with that case named rather than in general.
