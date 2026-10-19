# KvStore: a crash-safe, disk-backed key-value database in C# (.NET 10)

Built by Shayan (GitHub: shayan-qazmi).

## Overview

KvStore stores text keys and values on disk and gives them back. I built it from scratch to
learn how databases work on the inside, and to learn .NET and networking from the basics.
The design follows LSM-tree databases such as LevelDB, RocksDB and Cassandra, in a
simplified form.

It is a learning project, not a production database. It runs on one machine, uses one
global lock, and has no authentication. The "Known limitations" section lists everything I
know it cannot do.

## Contents

- [Quick start](#quick-start)
- [Features](#features)
- [How it works](#how-it-works)
- [Data model and rules](#data-model-and-rules)
- [On-disk format](#on-disk-format)
- [What happens if it crashes](#what-happens-if-it-crashes)
- [Durability modes](#durability-modes)
- [Concurrency](#concurrency)
- [HTTP API](#http-api)
- [Web UI](#web-ui)
- [Project layout](#project-layout)
- [Testing](#testing)
- [Benchmarks](#benchmarks)
- [Design decisions](#design-decisions)
- [Known limitations](#known-limitations)
- [Troubleshooting](#troubleshooting)
- [How it was built](#how-it-was-built)
- [What I learned](#what-i-learned)
- [What I would do next](#what-i-would-do-next)
- [Glossary](#glossary)

## Quick start

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Git.

```
git clone https://github.com/shayan-qazmi/KvStore.Core
cd KvStore.Core
dotnet build
dotnet test
dotnet run --project KvStore.Api --launch-profile http
```

The API now listens on http://localhost:5080. In a second terminal (on Windows PowerShell,
type `curl.exe` instead of `curl`):

```
curl -i -X PUT http://localhost:5080/kv/name --data-binary "shayan"
curl -i http://localhost:5080/kv/name
curl -i http://localhost:5080/kv/missing
curl "http://localhost:5080/kv?start=a&end=z"
curl -i -X DELETE http://localhost:5080/kv/name
```

Expected answers: `204 No Content`, then `200 OK` with `shayan`, then `404 Not Found`, then a
JSON list, then `204 No Content`.

## Features

- Saves text keys and values on disk. Reads one key, or a sorted range of keys.
- Every change goes to a checksummed write-ahead log before anything else.
- In safe mode (the default), a write is forced onto the physical disk before the call returns.
- Recovers after a crash: a half-written or damaged record at the end of the log is detected and cut off.
- Keeps data in sorted, immutable files (SSTables) and merges them (compaction).
- Deletes are stored as "tombstones", because files are never edited.
- A thread-safe wrapper, so the engine can sit behind a web server.
- An ASP.NET Core Minimal API with four key-value endpoints and a status page.
- 45 automated tests, including one that kills the writing process in the middle of a write.
- A benchmark tool that measures the engine and the HTTP API.
- A small React web page (optional demo) that uses the API from a browser.

## How it works

```
 Client (curl, any HTTP client, the benchmark tool)
        |  HTTP
        v
 KvStore.Api  (ASP.NET Core Minimal API)
        |
        v
 ConcurrentLsmStore  (one lock: one request at a time inside the store)
        |
        v
 LsmStore
   |  1. append first                  2. then update
   v                                   v
 wal.log                          Memtable (sorted, in memory)
 (write-ahead log, CRC32          (a delete is stored as a "tombstone")
  checksum on every record)             |  1,000 entries? flush
                                        v
                             SSTable files on disk (sorted, never edited)
                             sst-0000000001.sst   sst-0000000002.sst  ...
                                        |  4 or more files? compaction
                                        v
                             merged into ONE new SSTable
```

### The classes

| Class | File | Job |
|---|---|---|
| `KvStore` | `KvStore.cs` | The first, simple version: a sorted dictionary in memory with an optional log file. Kept because the early phases and the crash-writer tool use it. Also holds `KeyNotFoundInStoreException` |
| `WriteAheadLog` | `WriteAheadLog.cs` | A checksummed, append-only log file. Replays itself on open and cuts off a damaged end |
| `SsTable` | `SsTable.cs` | An immutable sorted file plus an in-memory index of its keys. Also holds the small `Entry` type (a value plus a "deleted" flag) |
| `LsmStore` | `LsmStore.cs` | The engine: memtable, flush, reads, range scans, compaction, recovery |
| `ConcurrentLsmStore` | `ConcurrentLsmStore.cs` | Wraps `LsmStore` with one lock so many threads can use it |

### Writing (Put or Delete)

1. Append a record to `wal.log`: a CRC32 checksum, the body length, then the body.
2. In safe mode, force the bytes onto the physical disk (`Flush(true)`).
3. Update the memtable. A delete stores a tombstone entry.
4. If the memtable now has 1,000 entries, flush it (see below).

The log is written first. If writing the log fails, memory is not changed, so the two never disagree.

### Reading (Get)

1. Look in the memtable. If the key is there, a normal entry is the answer, and a tombstone means "deleted".
2. Otherwise check the SSTables from newest to oldest. The first table that knows the key decides, and a tombstone again means "deleted".
3. If nobody knows the key, it does not exist.

For an SSTable hit, the in-memory index gives the position of the value in the file, and the file is opened to read exactly those bytes.

### Range scan (GetRange)

Collect the entries in the range from every SSTable from **oldest to newest**, then from the
memtable, into one sorted dictionary. A newer entry replaces an older one for the same key. Return
the entries that are not tombstones. Both ends of the range are included. The whole result is
built in memory.

### Flush

1. Write the memtable, already sorted, to a **temporary file** (`sst-N.sst.tmp`), force it onto the disk, then **rename** it to `sst-N.sst`. A half-written SSTable can never exist under its real name.
2. Clear the memtable.
3. Only now empty the WAL.

This order matters. If a crash happens between steps 1 and 3, the WAL still holds records that
are already in the SSTable. On start-up they are replayed and write the same values again, which
changes nothing.

### Compaction

After every flush, if there are 4 or more SSTables, **all** of them are merged into one:

1. Open every table as a sorted stream, oldest first.
2. Repeatedly take the smallest key among the front entries. If several tables have it, the **newest** table's entry wins. Move forward in every table that had that key.
3. Drop the entry if the winner is a tombstone. Otherwise write it to the new table.
4. The new table gets the next, higher number (so it counts as the newest). It is written with the same temp-file-then-rename step.
5. Delete the old files, **oldest first**.

Only one entry from each table is in memory at a time. Dropping tombstones is safe **only because
every table is merged**. No older table is left that a tombstone still has to hide.

### Start-up

1. Delete any leftover `*.tmp` files (an interrupted flush or compaction).
2. Open every `sst-*.sst` file in order of its number (the numbers are zero-padded, so name order is age order) and build its key index.
3. Open `wal.log` and replay it into the memtable. Replay stops at the first record that is cut off or has a wrong checksum, and the damaged end of the file is truncated.

## Data model and rules

- Keys and values are text, saved as UTF-8.
- Keys are sorted by **ordinal** order (by character value, not by culture), in the memtable, in the files and in range scans. This is UTF-16 code unit order, which can differ from UTF-8 byte order for some rare characters.
- `Put` on an existing key replaces the value. An empty value is allowed.
- `Get` of a missing or deleted key fails (`KeyNotFoundInStoreException`, which the API turns into 404).
- `Delete` of a missing key also fails the same way.
- `GetRange(start, end)` returns every key with `start <= key <= end`, sorted.
- An empty key cannot be reached through the API, because the URL path needs at least one character.

## On-disk format

One data folder holds everything:

```
<data folder>/
  wal.log                       the write-ahead log
  sst-0000000001.sst            SSTable files (higher number = newer)
  sst-0000000002.sst
  sst-0000000003.sst.tmp        exists only during a flush or compaction, or after a crash
```

Record formats (all numbers are 4-byte little-endian integers):

```
WAL record : [4 bytes CRC32 of the body][4 bytes body length][body]
body       : [4 key length][key bytes][4 value length][value bytes][1 byte deleted flag]
SSTable    : entries sorted by key, each
             [4 key length][key bytes][1 byte deleted flag][4 value length][value bytes]
```

A real example. `Put("hi", "yo")` becomes this WAL record of 21 bytes:

```
<4 bytes CRC32> | 0D 00 00 00 | 02 00 00 00 | 68 69 | 02 00 00 00 | 79 6F | 00
  checksum        body = 13     key length    "hi"    value length   "yo"   not deleted
```

The same entry inside an SSTable takes 13 bytes:

```
02 00 00 00 | 68 69 | 00 | 02 00 00 00 | 79 6F
```

A unit test checks the record length, the body length and every byte of the body. It does not compare the 4 checksum bytes.

## What happens if it crashes

| Where the program stops | What is on disk | What happens on the next start |
|---|---|---|
| In the middle of writing a WAL record | A cut-off record at the end of the log | It is detected (too short, or wrong checksum) and truncated. That write was never acknowledged |
| After the WAL record, before the memtable update | A complete record | Replay puts it into the memtable |
| In the middle of writing an SSTable | A `.tmp` file | The `.tmp` file is deleted. The WAL still has the data |
| After the SSTable rename, before the WAL is emptied | The SSTable and a full WAL | The WAL is replayed and writes the same values again. Harmless |
| In the middle of writing the compaction output | A `.tmp` file, and all the old tables | The `.tmp` file is deleted. The old tables are intact |
| After the merged table exists, before the old files are deleted | The merged table (newest) and all old tables | Reads check the merged table first. A key that was deleted is missing from the merged table, but an old table still holds its tombstone, so it stays deleted |
| In the middle of deleting the old files (oldest first) | The merged table and some newer old tables | Still correct, for the same reason. A tombstone is never removed before the older value it hides |
| Bytes in the middle of the log are damaged | A record with a wrong checksum | Replay stops there, so everything after it is lost (see limitations) |

## Durability modes

| Failure | Safe mode (default) | Fast mode |
|---|---|---|
| The program crashes or is killed | No acknowledged write is lost | No acknowledged write is lost (Windows keeps the data of a killed process) |
| Windows crashes or the power is cut | No acknowledged write is lost, if the disk really honours flush requests | The last few writes can be lost. The log stays consistent, because a half-written record is cut off |

Safe mode forces every WAL record onto the physical disk (`Flush(true)`). Fast mode only hands
the record to Windows and forces everything onto the disk when the store is closed. SSTables are
always forced onto the disk, once per flush or compaction.

## Concurrency

- `LsmStore` is not thread-safe. `ConcurrentLsmStore` wraps it, and every call takes the same lock, so only one thread at a time is inside the store.
- `GetRange` builds its complete answer inside the lock and returns a finished list. A lock must not stay held while a caller slowly loops over lazy results.
- The API registers one `ConcurrentLsmStore` as a singleton, so all requests share it.
- The cost: reads also wait for each other, and a compaction blocks every request while it runs.
- Two tests use many threads at once. One writes 800 records from 8 threads. The other reads while writing.

## HTTP API

Base address: `http://localhost:5080`

| Method | Path | Body | Success | Errors |
|---|---|---|---|---|
| GET | `/` | none | 200 and a text line saying the API is running | none |
| PUT | `/kv/{key}` | the value, plain text, UTF-8 | 204 No Content | none |
| GET | `/kv/{key}` | none | 200 and the value as `text/plain; charset=utf-8` | 404 if the key does not exist |
| DELETE | `/kv/{key}` | none | 204 No Content | 404 if the key does not exist |
| GET | `/kv?start=X&end=Y` | none | 200 and JSON `[{"key":"a","value":"1"}]`, sorted, both ends included | 400 if `start` or `end` is missing |

- Keys must be URL-encoded (a space becomes `%20`). Keys that contain `/` are not supported.
- The range endpoint is `/kv?start=..&end=..` instead of `/kv/range`, so that a key named "range" still works.
- An empty `start` and a very large `end` match every key.

Settings (give them after `--`, for example `dotnet run --project KvStore.Api -- --Storage:SyncOnEveryWrite=false`):

| Setting | Default | Meaning |
|---|---|---|
| `Storage:Directory` | `KvStoreApi/data` inside your user's local application data folder (on Windows `%LOCALAPPDATA%\KvStoreApi\data`) | where the database files live |
| `Storage:SyncOnEveryWrite` | `true` | `true` is safe mode, `false` is fast mode |

Launch profiles: `http` (safe mode) and `http-fast` (fast mode), both on port 5080.


## Web UI

`KvStore.Ui` is a small React page (built with Vite) that uses the API from a browser. It is
an optional demo client and is not part of the .NET solution.

![KvStore UI](docs/ui.png)

**What it does**
- Saves, reads and deletes one key (PUT, GET, DELETE).
- Searches a range of keys, or shows all keys, in a table. Clicking a row copies it into the form.
- Shows an "API online / offline" badge that is updated after every request..
- Shows a log of every HTTP request it sends, with the method, path, status code and time, so
  the HTTP traffic is visible.

**How the browser reaches the API.** The page is served at `http://localhost:5173` and the API
runs at `http://localhost:5080`. A browser blocks a page from calling a different address unless
that server allows it (this rule is called CORS). To avoid it, the Vite dev server acts as a
proxy: the page calls `/api/...`, and Vite forwards the call to the API and removes the `/api`
part. The API code did not need any change.

**Run it** (needs Node.js, current LTS version):

```
# terminal 1, from the repository root: start the API
dotnet run --project KvStore.Api --launch-profile http

# terminal 2: start the UI
cd KvStore.Ui
npm install
npm run dev
```

Then open http://localhost:5173.

**Files**

| File | What it is |
|---|---|
| `package.json` | the libraries (React, Vite) and the `npm run dev` command |
| `vite.config.js` | the dev server and the `/api` proxy to port 5080 |
| `index.html` | the page shell, with an empty box that React fills |
| `src/main.jsx` | starts React |
| `src/App.jsx` | the whole page: state, the four HTTP calls, and the layout |
| `src/App.css` | the styling, with automatic light and dark mode |

In development, React's strict mode runs start-up code twice, so the log shows two `GET /`
lines when the page opens.

**Limits of the UI:** it only works with the dev server (`npm run dev`), because the proxy lives
there. There is no production build setup, no login, no paging for big ranges, and no automated
UI tests. "Show all keys" uses a very large end key, so a key that starts with the character
U+FFFF would not appear.


## Project layout

```
KvStore.Core/                (repository root, holds KvStore.slnx)
  KvStore.Core/              the engine (class library)
  KvStore.Core.Tests/        35 tests
  KvStore.Api/               ASP.NET Core Minimal API
  KvStore.Api.Tests/         10 tests
  KvStore.CrashWriter/       small program the crash test starts and kills
  KvStore.Benchmarks/        benchmark tool
  KvStore.Ui/                React + Vite demo page (not part of the .slnx)
```

| Project | Type | Notes |
|---|---|---|
| `KvStore.Core` | class library | One NuGet package: `System.IO.Hashing` (for CRC32) |
| `KvStore.Core.Tests` | xUnit tests | References `KvStore.Core` and `KvStore.CrashWriter` |
| `KvStore.Api` | ASP.NET Core Empty | References `KvStore.Core` |
| `KvStore.Api.Tests` | xUnit tests | Uses `Microsoft.AspNetCore.Mvc.Testing` to run the API in memory, with no real network |
| `KvStore.CrashWriter` | console app | Writes records forever and prints each number after the save finished |
| `KvStore.Benchmarks` | console app | Measures writes and reads per second |
| `KvStore.Ui` | React + Vite page | Uses the API from a browser. Needs Node.js. Not part of the `.slnx` |

## Testing

Run everything with `dotnet test`, or use Test Explorer in Visual Studio. Every test uses its own
temporary file or folder with a unique name and deletes it afterwards, so tests never share data.

### The 45 tests

**KvStoreTests: the simple in-memory store (5)**

| Test | What it proves |
|---|---|
| `Put_Then_Get_Returns_Value` | a saved value comes back |
| `Get_Missing_Key_Throws` | a missing key gives a clear error |
| `Delete_Then_Get_Throws` | a deleted key is gone |
| `Overwriting_Key_Updates_Value` | the latest value wins |
| `GetRange_Returns_Correct_Subset` | a range returns the right keys, sorted |

**PersistenceTests: the log file (10)**

| Test | What it proves |
|---|---|
| `Data_Survives_Restart` | data is still there after closing and reopening |
| `Deleted_Key_Stays_Deleted_After_Restart` | tombstones survive a restart |
| `Latest_Value_Wins_After_Restart` | replay order gives the newest value |
| `Non_Ascii_Text_Survives_Restart` | non-English text is saved correctly |
| `Missing_File_Starts_Empty` | a new store starts empty |
| `the file layout matches this README (the 4 checksum bytes are not compared)` | the file layout matches this README, byte for byte |
| `Half_Written_Last_Record_Is_Ignored` | a cut-off last record does not stop the store from opening |
| `Store_Keeps_Working_After_Recovering_From_Half_Written_Record` | new writes after recovery are readable (the broken end was cut off) |
| `Damaged_Record_Is_Detected_By_Checksum` | a changed byte is caught |
| `Tiny_Garbage_At_End_Is_Ignored` | a few stray bytes at the end are ignored |

**CrashRecoveryTests: a real crash (1)**

| Test | What it proves |
|---|---|
| `Saved_Records_Survive_Being_Killed_Ten_Times` | see the next section |

**LsmStoreTests: the engine (11)**

| Test | What it proves |
|---|---|
| `Basic_Put_Get_Delete_Work` | the basics work through the engine |
| `Flush_Creates_SsTable_And_Empties_The_Wal` | a full memtable becomes a file and the log is emptied |
| `Reads_Find_Data_In_SsTables_And_Memtable` | reads look in all places |
| `Newer_Value_Hides_Older_Value` | the newest SSTable wins |
| `Delete_Hides_Key_That_Lives_In_An_SsTable` | a tombstone hides a value stored in a file |
| `Everything_Survives_Restart` | files and log together recover everything |
| `Deleted_Key_Stays_Deleted_After_Restart` | a tombstone that is only in the log is replayed |
| `GetRange_Merges_Memtable_And_SsTables` | ranges combine all sources and respect tombstones |
| `Replaying_Wal_After_Flush_Is_Harmless` | the crash between flush steps changes nothing |
| `Leftover_Temp_File_Is_Removed` | a half-written SSTable's temp file is cleaned up |
| `Keys_Are_Sorted_By_Character_Value` | capital letters sort before lowercase, everywhere |

Rows 1, 3, 4 and 6 have tests of their own. Row 8 is tested only for damage in the last record. Rows 2, 5 and 7 rest on reasoning: they reuse code that other tests cover, and row 7 (only some old files deleted) is not tested on its own.

**CompactionTests (6)**

| Test | What it proves |
|---|---|
| `Compact_Merges_All_SsTables_Into_One` | many files become one, and old files are deleted |
| `Compact_Removes_Overwritten_Values` | the old value is physically gone from the file |
| `Compact_Removes_Deleted_Keys_For_Good` | the key and its tombstone are physically gone |
| `Compaction_Runs_Automatically_When_Threshold_Is_Reached` | it starts by itself at the limit |
| `Data_Survives_Restart_With_Compaction_Running` | recovery works with compaction in the mix |
| `Crash_After_Merged_File_Written_But_Before_Old_Files_Deleted_Is_Still_Correct` | the dangerous crash window keeps a deleted key deleted |

**ConcurrentLsmStoreTests (2)**

| Test | What it proves |
|---|---|
| `Many_Threads_Writing_At_Once_Lose_Nothing` | 8 threads write 800 records with no loss, while flushes and compactions run |
| `Reading_While_Writing_Does_Not_Crash` | scans and reads run safely during writes |

**ApiTests: real HTTP requests into the API (9)**

| Test | What it proves |
|---|---|
| `Put_Then_Get_Returns_Value` | PUT gives 204, GET gives 200 and the value |
| `Get_Missing_Key_Returns_404` | missing key gives 404 |
| `Delete_Then_Get_Returns_404` | DELETE gives 204, then GET gives 404 |
| `Delete_Missing_Key_Returns_404` | deleting a missing key gives 404 |
| `Overwriting_A_Key_Updates_The_Value` | PUT twice keeps the latest |
| `Range_Returns_Sorted_Json` | the range endpoint returns sorted JSON |
| `Range_Without_Parameters_Returns_400` | a bad request is refused |
| `Keys_With_Spaces_And_Unicode_Values_Work` | URL-encoding and UTF-8 work |
| `Many_Requests_At_Once_Are_All_Saved` | 100 parallel requests, none lost |

**RestartTests (1)**

| Test | What it proves |
|---|---|
| `Data_Survives_Stopping_And_Restarting_The_Server` | stop the whole API, start it again on the same folder, the data is still there |

### The process-kill crash test

`KvStore.CrashWriter` runs as its own process. It saves records with 20,000-character values
(big enough that one record is written in several steps, so a kill can land in the middle of
one). It prints a record's number only **after** the save has finished, so every number the
test reads means "this record is saved".

The test then:
1. starts the writer and waits for a random 20 to 79 saved records,
2. kills it with `Process.Kill` (no clean-up, no chance to finish a write),
3. opens the store again on the same file and checks that it opens without an error, that records `0..N` exist with **no gaps**, that every value is complete and correct, and that every record the writer reported as saved is present.

It repeats this 10 times on the same file. Result: it passed every time I ran it. One run (10 kills) takes about 2 to 3 seconds.

### What the tests do not prove

- Killing a process is not a power cut. Windows keeps the data of a killed process, so this test proves recovery from a **program crash**, not that the disk sync works. That part relies on the operating system and the disk, and I did not test it by pulling the power.
- A kill rarely lands exactly in the middle of a record. The simulated tests cut a record in half on purpose, so the two kinds of test cover each other.
- Fast and safe mode look the same to a kill-based test.
- There is no test for damage in the middle of the log, or for files being removed while the store runs.

## Benchmarks

**Machine:** Intel Core i7-1270P (12th gen), 32 GB RAM, Intel SSD (model SSDPEKNW512G8H, 512 GB), Windows, .NET 10, Release build.

**Method:**
- One client sends one request at a time (single thread, no overlap).
- Each value is about 110 bytes. Keys look like `s1000-key0000042`.
- 200 warm-up writes and reads come first and are not timed.
- Writes run first, then the same keys are read in random order (always the same order). Every value read is checked, so wrong answers are never timed.
- The store keeps **growing** between rounds. "Already stored" is the number of keys from earlier rounds.
- "Engine" runs the database inside the benchmark program with no network. "HTTP" sends real requests to the API on localhost.

**Engine, fast mode (no disk sync on every write)**

| Keys written | Already stored | Writes/sec | Reads/sec |
|---|---|---|---|
| 1,000 | 0 | 31,169 | 46,877 |
| 10,000 | 1,000 | 12,604 | 37,737 |
| 100,000 | 11,000 | 11,600 | 30,699 |

**Engine, safe mode (disk sync on every write)**

| Keys written | Already stored | Writes/sec | Reads/sec |
|---|---|---|---|
| 1,000 | 0 | 982 | 43,176 |
| 10,000 | 1,000 | 899 | 35,321 |
| 100,000 | 11,000 | 952 | 28,255 |

**HTTP API (safe mode)**

| Keys written | Already stored | Writes/sec | Reads/sec |
|---|---|---|---|
| 1,000 | 0 | 554 | 2,847 |
| 10,000 | 1,000 | 629 | 6,426 |
| 100,000 | 11,000 | 677 | 6,220 |

An earlier engine run gave similar numbers (safe: 1,044 and 990 writes/sec; fast: 25,579 and
12,095), so results vary by up to about 20% between runs. Read them as orders of magnitude.

**What the numbers show**

- **Durability costs about 1 ms per write.** Safe mode stays at roughly 900 to 1,000 writes per second at every size, so the time goes to the disk sync, not to the amount of data.
- **Fast mode is about 12 to 32 times faster for writes.** It trades safety on a power cut for speed.
- **Fast-mode writes drop once flushes and compactions start** (31,169, then 12,604), then stay roughly flat up to 111,000 keys (11,600). Flushing and compaction are the likely cause. I have not profiled it, and I did not test beyond 111,000 keys, where compaction cost could start to grow.
- **Reads slow by about a third** from 1,000 to 111,000 keys (46,877 to 30,699 in fast mode). I did not find out why. Bigger indexes, more tables to check and opening files are candidates.
- **The network is the biggest cost for reads.** An in-process read takes 0.02 to 0.035 ms. An HTTP read takes 0.16 to 0.35 ms in the latest run, which is roughly 5 to 15 times slower.
- **HTTP adds about 0.4 to 0.8 ms to a write** on top of the disk sync.
- **HTTP read speed varies a lot between runs.** The latest run gave 2,847, 6,426 and 6,220 reads per second. Earlier runs gave as low as 1,537 and as high as 4,786. In the latest run the first round was the slowest, which may be warm-up (200 warm-up requests may not be enough). I would need more runs to say more.
- This is one client on one machine. The numbers show the cost of a single request, not how many users the server can handle.

**Reproduce**

```
# engine, safe mode
dotnet run -c Release --project KvStore.Benchmarks -- engine --sizes 1000,10000,100000

# engine, fast mode
dotnet run -c Release --project KvStore.Benchmarks -- engine --fast --sizes 1000,10000,100000

# HTTP: first start the API in terminal 1 ...
dotnet run -c Release --project KvStore.Api --launch-profile http

# ... then run the benchmark in terminal 2
dotnet run -c Release --project KvStore.Benchmarks -- http --sizes 1000,10000,100000
```

Always use `-c Release`. A Debug build gives misleading numbers. The safe-mode 100,000 round takes
about two minutes.

## Design decisions

| Decision | Why | What it costs |
|---|---|---|
| Log first, memory second | If the log write fails, memory is not changed | Every write touches the disk |
| Tombstones for deletes | Files are never edited, so a delete is a record that hides older values | Deleted keys take space until compaction |
| CRC32 checksum on every log record | A damaged record is detected instead of being trusted | A few bytes and a little time per record |
| SSTable written to a temp file, then renamed | An SSTable is either complete or absent | One extra rename |
| SSTable written before the log is emptied | A crash in between only repeats harmless writes | Duplicate replay after that crash |
| Compaction merges all files, new file first, old files deleted oldest first | Tombstones can be dropped safely, and every crash point stays correct | Rewrites all the data each time |
| Ordinal key order everywhere | The memtable, the files and the range scans always agree | Not language-aware sorting |
| The lock is in a wrapper class | `LsmStore` stays simple and single-threaded | One global lock |
| Safe or fast mode is a setting | The price of safety can be measured instead of guessed | Fast mode can lose writes on a power cut |
| Range scan builds the result in memory | Simple and correct | A huge range uses a lot of memory |

## Known limitations

- Each SSTable keeps all its keys in memory as an index. There is no sparse index and no Bloom filter.
- A read that reaches an SSTable opens the file again each time.
- Compaction merges everything at once, runs inside the call that triggered it, and briefly needs double the disk space.
- The memtable limit counts entries, not bytes, so very large values can use a lot of memory.
- The log stops at the first bad record, so damage in the middle of the file loses everything after it.
- Durability of the SSTable rename across a power cut is not tested.
- One global lock: reads wait for each other, and a compaction blocks every request.
- Plain HTTP with no authentication, meant for localhost.
- Keys and values are text. A whole value is held in memory when it is read.
- Keys containing `/` are not supported through the API.
- Single node. No replication, no transactions, no batching, no expiry.

## Troubleshooting

| Problem | Fix |
|---|---|
| Build fails with "file is being used by another process" | The API is still running. Stop it with Ctrl+C |
| `curl` in PowerShell prints something strange | Type `curl.exe`. Plain `curl` is a different PowerShell command |
| The benchmark says "No connection could be made" | Start the API first, in another terminal |
| Port 5080 is already in use | Stop the other program, or change `applicationUrl` in `KvStore.Api/Properties/launchSettings.json` |
| The crash test says the writer was not found | Build the whole solution first (`dotnet build`) |
| You want an empty database | Stop the API and delete `%LOCALAPPDATA%\KvStoreApi` |
| The UI badge says "API offline" | Start the API first (`dotnet run --project KvStore.Api --launch-profile http`) and click "Check again" |
| PowerShell says "running scripts is disabled" when you type `npm` | Type `npm.cmd install` and `npm.cmd run dev` instead |
| `npm install` says "Invalid package.json" | You are in the wrong folder, or the file is empty. Run it inside `KvStore.Ui` |

## How it was built

I built it one phase at a time and committed after each phase, so the git history shows the order:

1. In-memory store with tests
2. Append-only file persistence
3. Checksummed log, torn-write recovery, and the process-kill crash test
4. LSM-tree: write-ahead log, memtable, SSTables, flush, then compaction
5. Thread-safe wrapper, HTTP API with tests, and the benchmark tool
6. A React web page (Vite) that uses the API 
7. This README

I built it step by step with an AI assistant's guidance (Claude). I ran and tested every phase
myself and can explain how each part works.

## What I learned

- How a .NET solution is organised: class libraries, test projects, console apps and a web project that reference each other.
- Binary file formats, checksums, and why `Flush(true)` is different from `Flush()`.
- Testing crashes on purpose, and being honest about what a test does not prove.
- How HTTP works (verbs, status codes, ports), ASP.NET Core Minimal APIs, dependency injection, `async` and `await`, and why shared state needs a lock.
- Measuring before concluding, and correcting my own guesses when the numbers disagreed.
- How a browser page talks to an API: `fetch`, URL-encoding, the CORS rule and a dev-server proxy.
- The basics of React (components, state, effects) and of the Node tools around it (npm, Vite).

## What I would do next

- Keep SSTable files open between reads, and add a sparse index and Bloom filters.
- Compact in the background, in levels, instead of merging everything at once.
- Use a reader-writer lock so reads can run at the same time.
- Group commit: let several writes share one disk sync.
- Run the benchmarks with several clients at once, and repeat them more often.
- Count the memtable in bytes instead of entries.
- Profile fast-mode writes and the read slowdown to find the real cause.

## Glossary

- **Key-value store:** a database that saves a value under a key and gives it back by that key.
- **Write-ahead log (WAL):** a file where each change is written before anything else, so it can be replayed after a crash.
- **Memtable:** the sorted, in-memory table that holds the newest writes.
- **SSTable (Sorted String Table):** an immutable file of entries sorted by key.
- **LSM-tree:** a design that turns many small random writes into sorted files that are merged later.
- **Tombstone:** a record that says "this key was deleted", used because files are never edited.
- **Flush:** saving the memtable as a new SSTable.
- **Compaction:** merging SSTables into one and throwing away old and deleted data.
- **CRC32:** a number calculated from some bytes. If any byte changes, the number no longer matches.
- **`Flush(true)` (fsync):** forces data onto the physical disk instead of leaving it in the operating system's memory.
- **Torn write:** a write that was cut off in the middle, leaving half a record.
- **Ordinal order:** sorting by character value, with no language rules.
- **Singleton:** one shared instance of an object for the whole application.
- **Minimal API:** ASP.NET Core's short way of mapping a verb and a path to a function.
- **CORS:** a browser rule that blocks a page from calling a server at a different address unless that server allows it.
- **Proxy:** a middleman that forwards a request to another server. Here, the Vite dev server forwards `/api/...` to the C# API.
- **React:** a JavaScript library for building web pages from small functions called components.
- **Vite:** a tool that serves and builds a React page.
