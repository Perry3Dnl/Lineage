# Runtime feasibility baseline

This document records the first long-session feasibility baseline for the Lineage provenance core. It is a CI benchmark, not a universal performance guarantee; the purpose is to detect architectural failure modes and provide numbers we can compare as the storage engine evolves.

Baseline environment: GitHub-hosted Ubuntu 24.04 runner, .NET 10.0.12. Captured on 2026-09-15 by the `Runtime Feasibility` workflow.

| Scenario | Logical work | Throughput | Retained managed growth | Allocated | Peak → final raw Steps | Result |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| Raw reclaimable provenance | 10,000,000 Steps | 6,852,494 Steps/s | +16.18 KiB | 59.6 B/Step | 16,386 → 2 | PASS |
| Current value capture | 1,000,000 Steps | 3,046,774 Steps/s | +12.38 KiB | 122.8 B/Step | 16,386 → 2 | PASS |
| Weak-object churn | 10,000 objects / 20,000 Steps | 447,620 Steps/s | +80.88 KiB | 397.2 B/Step | 2,000 → 0 | PASS |
| Continuously growing live chain | 65,537 Steps attempted | 2,078,676 Steps/s | +176 B | 425.1 B/Step | 65,536 → 65,536 | Expected RAM limit |

## What this proves

The core lifetime model is viable for reclaimable provenance. Ten million logical Steps were processed through a fixed 65,536-Step physical store without dropping history. After collection, only the two Steps belonging to the current rolling value remained. Managed memory retained after the run was effectively flat relative to the preallocated session baseline.

Weak object lifetime tracking also behaves as intended. Ten thousand short-lived objects were tracked in batches; their field roots were retired after the application objects became unreachable, and the Step and Relation stores returned to zero after each batch.

The current value-preview path remains bounded in retained memory, but it roughly doubles the measured allocation rate compared with raw Step/Relation capture. This confirms that primitive value encoding should move away from per-Step formatted strings and boxing where possible.

## The hard limit

A live value can legitimately depend on an arbitrarily long causal history. In the growing-chain scenario, every new Step depends on the previous Step. None of that ancestry is garbage, so provenance GC cannot reclaim it. With a 65,536-Step in-memory capacity, the next Step correctly causes the bounded store to report dropped history.

This is not a collector defect. It is the fundamental storage constraint:

> Full retrospective provenance for unbounded live ancestry cannot be guaranteed by finite RAM alone.

To preserve that history, Lineage needs a cold-storage strategy: append-only disk pages, paging, or an explicit checkpoint/truncation policy. The hot in-memory store can stay bounded while older still-live ancestry moves to colder storage.

## Performance signals from the baseline

The raw scenario retained almost no additional managed memory, but it allocated about 59.6 bytes per logical Step over the full run. Much of that cost is temporary data created by provenance collection (`Dictionary`, `List`, and `HashSet` structures used to rebuild reachability). The hot capture record itself is not the only thing that matters; repeated collector scratch allocation is now a measurable optimization target.

The current `Remember` path reached about 3.05 million Steps/s and allocated about 122.8 bytes per Step. That is sufficient for the architecture to remain plausible, but it is not the final representation we want. Compact tagged primitive payloads and metadata/value interning should reduce both allocation and CPU cost substantially.

The object-churn throughput number includes forced full CLR collections used by the stress scenario to prove weak lifetime cleanup, so it should not be interpreted as normal field-write throughput.

## Next storage work

The benchmark changes the next priorities from speculation into measured work:

1. Add an append-only cold journal for live ancestry that no longer fits in the hot bounded store.
2. Replace string-heavy primitive previews with compact value encoding.
3. Reuse collector scratch/index storage so collection does not allocate proportional temporary object graphs on every pass.
4. Add an end-to-end instrumented workload benchmark so IL instrumentation overhead is measured separately from the runtime store.
5. Keep this workflow as a regression gate and compare future results against this baseline.

The current conclusion is therefore: **the bounded provenance-lifetime model is feasible, but complete long-session history requires hot/cold storage rather than provenance GC alone.**
