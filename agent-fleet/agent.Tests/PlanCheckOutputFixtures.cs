namespace AgentFleet.Tests;

/// <summary>
/// Check output of a real repair run, as the approved check printed it in each round, in run order. Rounds 1, 3 and 6 are a test
/// file that did not load (esbuild stops at a syntax error and node counts the file as the one failing test); rounds 2, 4, 5 and 7
/// ran the tests and failed some of them. The text is kept as printed, paths relative to the project.
/// </summary>
internal static class PlanCheckOutputFixtures
{
    public const string Round01 = """
Exit code: 1
--- stdout ---
node:internal/modules/run_main:107
    triggerUncaughtException(
    ^

Error: Transform failed with 1 error:
src\server\shopHours.ts:5:0: ERROR: Expected "*/" to terminate multi-line comment
    at failureErrorWithLog (node_modules\esbuild\lib\main.js:1752:15)
    at node_modules\esbuild\lib\main.js:1019:50
    at responseCallbacks.<computed> (node_modules\esbuild\lib\main.js:886:9)
    at handleIncomingPacket (node_modules\esbuild\lib\main.js:941:12)
    at Socket.readFromStdout (node_modules\esbuild\lib\main.js:864:7)
    at Socket.emit (node:events:509:28)
    at addChunk (node:internal/streams/readable:563:12)
    at readableAddChunkPushByteMode (node:internal/streams/readable:514:3)
    at Readable.push (node:internal/streams/readable:394:5)
    at Pipe.onStreamRead (node:internal/stream_base_commons:189:23) {
  name: 'TransformError'
}

Node.js v24.16.0
✖ test\shopHours.test.ts (383.8482ms)
ℹ tests 1
ℹ suites 0
ℹ pass 0
ℹ fail 1
ℹ cancelled 0
ℹ skipped 0
ℹ todo 0
ℹ duration_ms 398.0238

✖ failing tests:

test at test\shopHours.test.ts:1:1
✖ test\shopHours.test.ts (383.8482ms)
  'test failed'
""";

    public const string Round02 = """
Lines naming failures and totals:
✖ shop session shop open (4.9924ms)
✖ shop session post-shop (2.0049ms)
✖ shop session closed (1.8592ms)
✖ shop session good friday (1.5644ms)
✖ shop session independence day (1.6265ms)
✖ shop session thanksgiving (1.9122ms)
✖ shop session day after thanksgiving (2.0467ms)
✖ shop session new year's day (1.7498ms)
✖ isShopHoliday (9.9991ms)
ℹ tests 12
ℹ pass 3
ℹ fail 9
✖ failing tests:

The end of the output:
...: 'pre',
    expected: 'closed',
    operator: 'strictEqual',
    diff: 'simple'
  }

test at test\shopHours.test.ts:1:1242
✖ shop session day after thanksgiving (2.0467ms)
  AssertionError [ERR_ASSERTION]: Expected values to be strictly equal:
  
  'pre' !== 'open'
  
      at TestContext.<anonymous> (test\shopHours.test.ts:52:10)
      at Test.runInAsyncScope (node:async_hooks:227:14)
      at Test.run (node:internal/test_runner/test:1306:25)
      at Test.processPendingSubtests (node:internal/test_runner/test:897:18)
      at Test.postRun (node:internal/test_runner/test:1447:19)
      at Test.run (node:internal/test_runner/test:1372:12)
      at async Test.processPendingSubtests (node:internal/test_runner/test:...
""";

    public const string Round03 = """
Exit code: 1
--- stdout ---
node:internal/modules/run_main:107
    triggerUncaughtException(
    ^

Error: Transform failed with 1 error:
src\server\shopHours.ts:239:2: ERROR: Unexpected "}"
    at failureErrorWithLog (node_modules\esbuild\lib\main.js:1752:15)
    at node_modules\esbuild\lib\main.js:1019:50
    at responseCallbacks.<computed> (node_modules\esbuild\lib\main.js:886:9)
    at handleIncomingPacket (node_modules\esbuild\lib\main.js:941:12)
    at Socket.readFromStdout (node_modules\esbuild\lib\main.js:864:7)
    at Socket.emit (node:events:509:28)
    at addChunk (node:internal/streams/readable:563:12)
    at readableAddChunkPushByteMode (node:internal/streams/readable:514:3)
    at Readable.push (node:internal/streams/readable:394:5)
    at Pipe.onStreamRead (node:internal/stream_base_commons:189:23) {
  name: 'TransformError'
}

Node.js v24.16.0
✖ test\shopHours.test.ts (437.9273ms)
ℹ tests 1
ℹ suites 0
ℹ pass 0
ℹ fail 1
ℹ cancelled 0
ℹ skipped 0
ℹ todo 0
ℹ duration_ms 450.7304

✖ failing tests:

test at test\shopHours.test.ts:1:1
✖ test\shopHours.test.ts (437.9273ms)
  'test failed'
""";

    public const string Round04 = """
Lines naming failures and totals:
✖ shop session shop open (3.6776ms)
✖ shop session post-shop (0.4619ms)
✖ shop session closed (0.421ms)
✖ shop session good friday (0.3297ms)
✖ shop session independence day (0.3881ms)
✖ shop session thanksgiving (0.5122ms)
✖ isShopHoliday (6.6417ms)
ℹ tests 12
ℹ pass 5
ℹ fail 7
✖ failing tests:

The end of the output:
...pen',
    expected: 'closed',
    operator: 'strictEqual',
    diff: 'simple'
  }

test at test\shopHours.test.ts:1:865
✖ shop session independence day (0.3881ms)
  AssertionError [ERR_ASSERTION]: Expected values to be strictly equal:
  
  'open' !== 'closed'
  
      at TestContext.<anonymous> (test\shopHours.test.ts:37:10)
      at Test.runInAsyncScope (node:async_hooks:227:14)
      at Test.run (node:internal/test_runner/test:1306:25)
      at Test.processPendingSubtests (node:internal/test_runner/test:897:18)
      at Test.postRun (node:internal/test_runner/test:1447:19)
      at Test.run (node:internal/test_runner/test:1372:12)
      at async Test.processPendingSubtests (node:internal/test_runner/test:897:7) {
    generatedMessage: true,
    code: 'ERR_ASSERTION',
    actual: 'open',
    expected: 'clo...
""";

    public const string Round05 = """
Lines naming failures and totals:
✖ shop session closed (3.4433ms)
✖ shop session good friday (0.7391ms)
✖ shop session independence day (0.5705ms)
✖ shop session thanksgiving (0.5955ms)
✖ isShopHoliday (5.8067ms)
ℹ tests 12
ℹ pass 7
ℹ fail 5
✖ failing tests:

The end of the output:
...pen',
    expected: 'closed',
    operator: 'strictEqual',
    diff: 'simple'
  }

test at test\shopHours.test.ts:1:865
✖ shop session independence day (0.5705ms)
  AssertionError [ERR_ASSERTION]: Expected values to be strictly equal:
  
  'open' !== 'closed'
  
      at TestContext.<anonymous> (test\shopHours.test.ts:37:10)
      at Test.runInAsyncScope (node:async_hooks:227:14)
      at Test.run (node:internal/test_runner/test:1306:25)
      at Test.processPendingSubtests (node:internal/test_runner/test:897:18)
      at Test.postRun (node:internal/test_runner/test:1447:19)
      at Test.run (node:internal/test_runner/test:1372:12)
      at async Test.processPendingSubtests (node:internal/test_runner/test:897:7) {
    generatedMessage: true,
    code: 'ERR_ASSERTION',
    actual: 'open',
    expected: 'closed',
    operator: 'strictEqual',
    diff: 'simple'
  }

test at test\shopH...
""";

    public const string Round06 = """
Exit code: 1
--- stdout ---
node:internal/modules/run_main:107
    triggerUncaughtException(
    ^

Error: Transform failed with 1 error:
src\server\shopHours.ts:5:37: ERROR: Expected "*/" to terminate multi-line comment
    at failureErrorWithLog (node_modules\esbuild\lib\main.js:1752:15)
    at node_modules\esbuild\lib\main.js:1019:50
    at responseCallbacks.<computed> (node_modules\esbuild\lib\main.js:886:9)
    at handleIncomingPacket (node_modules\esbuild\lib\main.js:941:12)
    at Socket.readFromStdout (node_modules\esbuild\lib\main.js:864:7)
    at Socket.emit (node:events:509:28)
    at addChunk (node:internal/streams/readable:563:12)
    at readableAddChunkPushByteMode (node:internal/streams/readable:514:3)
    at Readable.push (node:internal/streams/readable:394:5)
    at Pipe.onStreamRead (node:internal/stream_base_commons:189:23) {
  name: 'TransformError'
}

Node.js v24.16.0
✖ test\shopHours.test.ts (335.1653ms)
ℹ tests 1
ℹ suites 0
ℹ pass 0
ℹ fail 1
ℹ cancelled 0
ℹ skipped 0
ℹ todo 0
ℹ duration_ms 352.7014

✖ failing tests:

test at test\shopHours.test.ts:1:1
✖ test\shopHours.test.ts (335.1653ms)
  'test failed'
""";

    public const string Round07 = """
Lines naming failures and totals:
✖ shop session closed (4.9679ms)
✖ shop session independence day (1.887ms)
✖ shop session juneteenth (3.329ms)
✖ shop session day after thanksgiving (1.7936ms)
✖ isShopHoliday (11.1142ms)
ℹ tests 12
ℹ pass 7
ℹ fail 5
✖ failing tests:

The end of the output:
...,
    expected: 'closed',
    operator: 'strictEqual',
    diff: 'simple'
  }

test at test\shopHours.test.ts:1:993
✖ shop session juneteenth (3.329ms)
  AssertionError [ERR_ASSERTION]: Expected values to be strictly equal:
  
  'open' !== 'closed'
  
      at TestContext.<anonymous> (test\shopHours.test.ts:42:10)
      at Test.runInAsyncScope (node:async_hooks:227:14)
      at Test.run (node:internal/test_runner/test:1306:25)
      at Test.processPendingSubtests (node:internal/test_runner/test:897:18)
      at Test.postRun (node:internal/test_runner/test:1447:19)
      at Test.run (node:internal/test_runner/test:1372:12)
      at async Test.processPendingSubtests (node:internal/test_runner/test:897:7) {
    generatedMessage: true,
    code: 'ERR_ASSERTION',
    actual: 'open',
    expected: 'closed',
    operator: 'strictEqual',
    diff: 'simple'
  }

test at test\shopHour...
""";
}
