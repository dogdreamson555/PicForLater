# Performance measurements

Library search can be measured without adding a benchmark to the regular test suite:

```powershell
dotnet run --project .\tools\performance\PicForLater.LibrarySearchBenchmark\PicForLater.LibrarySearchBenchmark.csproj -c Release
```

The tool creates isolated 1k and 10k SQLite fixtures under the system temporary directory,
warms each query three times, reports p50/p95 over 30 real `LibraryService.QueryAsync` calls,
prints `EXPLAIN QUERY PLAN`, and removes only its own temporary directories.

## Baseline: 2026-08-27

Release build, .NET 10.0.11, Windows 10.0.26200. Each image had a roughly 512-character
OCR stage result, a 96-character summary, and one of 20 categories; every twentieth image
had a reminder. The table records the more conservative value from two consecutive runs
on the development machine.

| Items | Workload | p50 (ms) | p95 (ms) |
|---:|---|---:|---:|
| 1,000 | sparse search match | 6.08 | 6.70 |
| 1,000 | search with no match | 5.98 | 8.19 |
| 1,000 | final CreatedAt page | 4.04 | 5.61 |
| 1,000 | category filter | 3.19 | 3.83 |
| 1,000 | category sort | 4.48 | 5.24 |
| 10,000 | sparse search match | 36.82 | 38.40 |
| 10,000 | search with no match | 41.03 | 46.32 |
| 10,000 | final CreatedAt page | 15.09 | 15.75 |
| 10,000 | category filter | 3.74 | 4.13 |
| 10,000 | category sort | 19.40 | 20.93 |

The plan uses `IX_ImageItems_Active_CreatedAtUtc` for the active list and existing indexes
for OCR, category, reminder, and asset lookups. Category sorting uses a temporary B-tree.
At 10k items the slowest p95 remained below 47 ms, so this baseline does not justify FTS5,
cursor pagination, or a new product index. This is a decision benchmark, not a hardware-
dependent CI performance assertion.
