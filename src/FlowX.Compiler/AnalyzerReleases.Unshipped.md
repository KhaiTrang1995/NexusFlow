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
FLOWX1010 | FlowX | Error | Capability does not declare an authorisation stance. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1010.md)
FLOWX1011 | FlowX | Warning | Condition, selector or projection reads something outside the flow's state; raised as an error in Durable flows. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1011.md)
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
FLOWX1024 | FlowX | Warning | Emit step is recorded but not published. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1024.md)
FLOWX1025 | FlowX | Warning | Trigger attribute cannot be read by the compiler. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1025.md)
FLOWX1027 | FlowX | Warning | Step is unreachable after Fail. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1027.md)
FLOWX1028 | FlowX | Error | Step input mapping produces the wrong contract. [Documentation](https://github.com/votrongdao/FlowX/blob/master/docs/diagnostics/FLOWX1028.md)
