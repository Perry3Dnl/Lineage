# Cold provenance storage

Lineage treats disk as an overflow tier, not as part of the normal recording hot path.

> Record to RAM. Reclaim first. Spill only under memory pressure. Never synchronously block the application on cold-storage I/O.

## Policy

The default runtime policy is:

- capture current provenance in the bounded in-memory Step/Relation store;
- reclaim unreachable provenance at normal safe points before it consumes long-term storage;
- begin spilling old, still-needed provenance when the hot store crosses its high-water mark;
- hand immutable pages to a bounded background-writer queue;
- cap temporary cold provenance at 100 MiB by default;
- delete the oldest immutable cold segments if the disk budget is exhausted;
- if storage cannot keep up, keep the application running and mark the provenance history incomplete rather than waiting for disk I/O;
- join hot and cold provenance only when a report is requested.

The initial defaults are a 70% spill watermark, a 50% target after spill, 4096 Steps per page, a two-page writer queue, and a 100 MiB cold-storage cap. Very small custom hot buffers that are no larger than one configured spill page remain RAM-only.

Cold session data is temporary and is deleted when its capture scope ends. Saving/exporting a persistent `.lineage` recording is a separate future feature.

## First measured baseline

Measured on a GitHub-hosted Ubuntu 24.04 runner with .NET 10.0.12 on September 15, 2026. These are recorder stress numbers, not yet an end-to-end instrumented-application slowdown measurement.

| Scenario | Events | Recording throughput | Hydration | Result |
| --- | ---: | ---: | ---: | --- |
| Normal active spill | 250,000 | 4.09M events/s | 69.3 ms | Full history retained |
| Writer throttled to 100 ms/page | 20,000 | 1.09M events/s | n/a | History truncated; recorder did not wait |
| Rolling 1 MiB cap | 100,000 | 1.92M events/s | 30.8 ms | Cap respected; oldest history truncated |

For the normal active-spill case, the hot store peaked at 45,674 Steps and finished at 40,288 Steps while 4.40 MiB was persisted across 64 cold segments. The complete 250,000-Step causal chain was successfully rehydrated and no history was truncated.

The deliberately throttled writer is the critical non-blocking test. Even with every cold-page write delayed by 100 ms, the recorder processed 20,000 events in about 18.3 ms. The bounded queue saturated, older provenance was explicitly marked truncated, and the application-side recording loop continued instead of waiting for storage.

The rolling-cap test used a deliberately tiny 1 MiB disk budget. It retained the newest provenance, kept cold storage at about 1,023 KiB, deleted older segments as required, and exposed the recording as incomplete.

## What this proves

The first implementation supports the intended tiering model:

```text
hot RAM
   ↓ pressure
reclaim dead provenance
   ↓ still needed
bounded async cold journal
   ↓ disk/queue budget exhausted
explicit truncation
```

It also confirms that cold storage can be isolated from the application thread. The remaining performance question is the one developers ultimately care about: the slowdown of an actual instrumented application compared with the same workload running with Lineage disabled. That end-to-end benchmark should compare Lineage off, RAM-only capture, active cold spilling, and deliberately slow storage.
