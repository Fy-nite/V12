using Xunit;

// Runtime tests create GameRoot instances, and GameRoot.Instance is a process
// singleton. Running them in parallel makes MarkDirty target the wrong root.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
