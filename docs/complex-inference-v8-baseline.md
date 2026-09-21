# Complex inference v8 compatibility baseline

Date recorded: 2026-09-21

This is the compatibility floor for the bounded complex-inference work. IR v9
may add a durable model/tool protocol, but it must not reinterpret these v8
contracts or historical bytes.

| Current v8 contract | Repository evidence |
| --- | --- |
| Registered templates expose only the explicitly admitted tool set; missing bindings fail before provider work | `BaizeInferenceExecutorTests.Registered_template_exposes_only_explicit_tools_and_evidence_matches_provider_request`; `Registered_template_missing_declared_tool_fails_before_provider_call_with_evidence` |
| Workflow-owned prompts preserve rendered prompt identity, exact tools, context delivery, and evidence | `FuwenZhinuPromptExecutionTests.Prompt_inference_executes_with_rendered_messages_and_prompt_evidence`; `Declared_tools_flow_into_the_request_and_evidence`; context cases in `FuwenZhinuContextDeliveryTests` |
| Registered prompt aliases require exact preflight and execute through their exact Baize binding | `FuwenZhinuPromptExecutionTests.Registered_prompt_alias_executes_end_to_end_through_exact_Baize_binding`; changed/unavailable binding rejection tests in the same class |
| Inference remains one provider execution from Fuwen's perspective; declared tools are model-visible but are not executed by a general model/tool loop | `BaizeInferenceExecutorTests` request-capture cases and the capability limitation in `docs/capability-matrix.md` |
| Structured output and representation repair remain bounded and typed | malformed, schema mismatch, truncation, raw/repaired byte ceiling, and repair contract tests in `BaizeInferenceExecutorTests` |
| Usage and price evidence preserve unknown values; unknown priced usage prevents further fallback | cost and missing/partial usage tests in `BaizeInferenceExecutorTests` |
| Generation deadlines cover submission, polling, and publication; unsupported limits fail before provider work | generation deadline, caller cancellation, token rejection, handle identity, and partial-publication tests in `BaizeInferenceExecutorTests` |
| Repeat inference is durable and supports focused restart without losing the exact tool set | `FuwenZhinuRepeatTests.Repeat_with_context_and_inference_executes_durably_per_iteration`; `R31ToolSurfaceRecoveryTests.V8_tool_subset_survives_repeat_replay_and_recovery` |
| Fan-out inference is bounded, preserves item identity/order, and supports focused item restart | `FuwenZhinuFanOutTests`; `R31ToolSurfaceRecoveryTests.V8_tool_subset_survives_fan_out_replay_and_item_recovery` |
| Sequential provider work uses stable operation keys, typed failures, cancellation, replay, and transitive focused restart | `FuwenZhinuRecoveryTests` |
| Same-plan and changed-plan forks accept copied evidence only under explicit fingerprint authorization and exact request/path checks | `FuwenZhinuMutationTests` |

## Required preservation tests

Every IR v9 implementation batch must continue to run the complete .NET 8 and
.NET 10 suites. Changes to canonical serialization must additionally prove:

- v3–v8 checked-in canonical JSON remains byte-identical;
- v3–v8 execution fingerprints remain unchanged;
- current `IInferenceExecutor` implementations retain one-call semantics;
- an adapter without the new protocol capability cannot accidentally accept an
  IR v9 complex-inference requirement;
- no new default grants tools, context, credentials, retries, or budget.

The baseline describes current executable behavior. It does not claim that v8
executes model-proposed tools or can resume inside a model/tool interaction.

