; Analyzer release tracking. Roslyn's RS2007/RS2008 rules require every diagnostic
; to be listed here before it can be raised, which makes adding, changing or removing
; a rule a reviewable diff rather than something that appears in someone's build one
; morning. Constraint C7 says the public surface is a forever commitment; a diagnostic
; id is part of that surface, because teams write suppressions against it.
;
; On release, these move to AnalyzerReleases.Shipped.md under a version heading.

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
FLOWX1001 | FlowX | Error | Flow must be partial. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1001.md)
FLOWX1002 | FlowX | Error | Step type is not a capability. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1002.md)
FLOWX1003 | FlowX | Error | Capability references a transport. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1003.md)
FLOWX1004 | FlowX | Error | Capability invokes another capability. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1004.md)
FLOWX1005 | FlowX | Error | Flow inherits from another flow. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1005.md)
FLOWX1006 | FlowX | Error | State-bag contract is outside every generated JSON context; reported on Durable flows only, where the trigger is itself the proof that the code is on a replay path. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1006.md)
FLOWX1007 | FlowX | Warning | Time is read from the ambient clock rather than the context; raised as an error where the compilation shows the code on a durable flow's replay path. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1007.md)
FLOWX1008 | FlowX | Warning | Identity or randomness is taken outside the context; raised as an error where the compilation shows the code on a durable flow's replay path. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1008.md)
FLOWX1009 | FlowX | Warning | Capability or flow holds mutable state; raised as an error where the compilation shows the type on a durable flow's replay path. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1009.md)
FLOWX1010 | FlowX | Error | Capability does not declare an authorisation stance. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1010.md)
FLOWX1011 | FlowX | Warning | Condition, selector or projection reads something outside the flow's state; raised as an error in Durable flows. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1011.md)
FLOWX1012 | FlowX | Warning | Compensation is declared on a flow that is not durable; never raised as an error, because the rule reports only on flows that are not durable and its remedy needs a journal registered on the host. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1012.md)
FLOWX1013 | FlowX | Error | Parallel branches must write disjoint context slots. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1013.md)
FLOWX1014 | FlowX | Error | Retry requires an idempotent capability. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1014.md)
FLOWX1015 | FlowX | Error | Capability implements more than one contract. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1015.md)
FLOWX1016 | FlowX | Warning | Expected failures are values, not exceptions. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1016.md)
FLOWX1017 | FlowX | Error | AwaitSignal requires the Durable profile. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1017.md)
FLOWX1018 | FlowX | Error | Cache requires a capability with no side effects. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1018.md)
FLOWX1019 | FlowX | Warning | Flow deadline is shorter than the step timeouts it must contain. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1019.md)
FLOWX1020 | FlowX | Error | Step consumes a contract no earlier step produces. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1020.md)
FLOWX1021 | FlowX | Error | Sub-flow composition forms a cycle. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1021.md)
FLOWX1026 | FlowX | Error | Sub-flow cannot be composed. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1026.md)
FLOWX1023 | FlowX | Error | Flow declares no steps. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1023.md)
FLOWX1024 | FlowX | Warning | Emit step stages no event to publish; raised only where the chain cannot start, on an Ephemeral flow or on a contract no source-generated JsonSerializerContext declares. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1024.md)
FLOWX1025 | FlowX | Warning | Trigger attribute declares no [TriggerKind]; raised as an error when that attribute is declared in the compilation being built. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1025.md)
FLOWX1027 | FlowX | Warning | Step is unreachable after Fail. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1027.md)
FLOWX1029 | FlowX | Error | Step input mapping produces the wrong contract. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1029.md)
FLOWX1028 | FlowX | Warning | Execution profile is declared but not honoured by the runtime; narrowed to Streaming when WP-52 made the runtime journal a Durable flow, and deleted when P7 lands the stream engine. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1028.md)
FLOWX1030 | FlowX | Error | Authorisation stance names no permission or policy. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1030.md)
FLOWX1033 | FlowX | Error | CompensationRetry is declared on a step with no compensation, so the emitter drops the one policy an undo can carry and the manifest publishes it anyway. Not deleted when the remaining stages land: no release gives a non-compensable step an undo. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1033.md)
FLOWX1032 | FlowX | Warning | Declared policy is not executed by the runtime; narrowed to RateLimit, Idempotency, Cache and Audit when the policy engine landed PolicyStage.Resilience, so Timeout, Retry, CircuitBreaker and Bulkhead left the rule alongside CompensationRetry. Deleted when the last four kinds execute. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1032.md)
FLOWX1034 | FlowX | Error | Step declares more than one policy set; StepModel.WithPolicy assigns rather than accumulates, so the second .WithPolicy(...) replaces the first and the first reaches neither the plan nor the manifest. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1034.md)
FLOWX1035 | FlowX | Warning | CompensationRetry declares a single attempt, so IsRetrying is false, HasCompensationPolicies stays false and the engine dispatches the undo once — while the manifest publishes the kind with no parameters and cannot be told apart from five attempts. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1035.md)
FLOWX1037 | FlowX | Error | Authorisation stance is not enforced by the runtime: Authorization.Policy names an ASP.NET Core authorisation policy, which only IAuthorizationService can evaluate, and FlowX.Runtime may not reference ASP.NET Core. The other four stances are decided in the step loop. Deleted when an evaluator abstraction the engine may call ships. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1037.md)
FLOWX1036 | FlowX | Warning | Policy set cannot be read at compile time — declared in a referenced assembly, returned by a method or assembled at run time — so it reaches no plan node, no manifest entry and none of FLOWX1014, FLOWX1018, FLOWX1019, FLOWX1032 or FLOWX1033. PolicySet's own well-known sets are exempt. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1036.md)
