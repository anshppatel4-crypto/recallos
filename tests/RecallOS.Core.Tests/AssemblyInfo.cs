using Xunit;

// These are integration tests, not unit tests: they capture the real screen, open real
// SQLite connections and write real files. Several of them measure process-wide resources
// (GDI object counts, store size on disk), which is only meaningful if nothing else in the
// process is allocating at the same time. Running them in parallel produced exactly that
// false failure, so the suite runs sequentially. It completes in about a second either way.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
