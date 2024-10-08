using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;

namespace Draco.Fuzzing;

/// <summary>
/// A fuzzer loop, which generates test cases and runs them against a target.
/// The method is inspired by the famous AFL fuzzer, which is basically doing 3 steps in a loop:
///  1. Load a test case from the queue
///  2. Minimize the test case
///  3. Mutate the test case
/// </summary>
/// <typeparam name="TInput">The type of the input data.</typeparam>
/// <typeparam name="TCoverage">The type of the compressed coverage data.</typeparam>
/// <param name="seed">The seed to use for the random number generator.</param>
/// <param name="multithreaded">True if the fuzzer should run in multithreaded mode. Only recommended for out-of-process execution.</param>
public sealed class Fuzzer<TInput, TCoverage>
    where TCoverage : notnull
{
    // Minimal result info of an execution
    private readonly record struct ExecutionResult(TCoverage Coverage, FaultResult FaultResult);

    // Initial inputs have no coverage data, so the entry needs to handle the case where coverage is not yet present
    // and we fill it out later
    private sealed class QueueEntry(TInput input, ExecutionResult? executionResult = null)
    {
        public TInput Input { get; } = input;
        public ExecutionResult? ExecutionResult { get; set; } = executionResult;
    }

    /// <summary>
    /// The seed to use for the random number generator.
    /// </summary>
    public int Seed { get; }

    /// <summary>
    /// The maximum number of parallelism.
    /// </summary>
    public int? MaxDegreeOfParallelism { get; }

    /// <summary>
    /// A shared random number generator.
    /// </summary>
    public Random Random { get; }

    /// <summary>
    /// The input minimizer to use.
    /// </summary>
    public required IInputMinimizer<TInput> InputMinimizer { get; init; }

    /// <summary>
    /// The input mutator to use.
    /// </summary>
    public required IInputMutator<TInput> InputMutator { get; init; }

    /// <summary>
    /// The reader to read coverage data with.
    /// </summary>
    public required ICoverageReader CoverageReader { get; init; }

    /// <summary>
    /// The coverage compressor to use.
    /// </summary>
    public required ICoverageCompressor<TCoverage> CoverageCompressor { get; init; }

    /// <summary>
    /// The target executor to use.
    /// </summary>
    public required ITargetExecutor<TInput> TargetExecutor { get; init; }

    /// <summary>
    /// The fault detector to use.
    /// </summary>
    public required IFaultDetector FaultDetector { get; init; }

    /// <summary>
    /// The tracer to use.
    /// </summary>
    public required ITracer<TInput> Tracer { get; init; }

    private readonly Channel<QueueEntry> inputQueue;
    private readonly ConcurrentHashSet<TCoverage> seenCoverages = [];
    private readonly object tracerSync = new();

    public Fuzzer(int? seed = null, int? maxDegreeOfParallelism = null)
    {
        this.Seed = seed ?? Random.Shared.Next();
        this.MaxDegreeOfParallelism = maxDegreeOfParallelism;
        this.Random = new Random(this.Seed);
        this.inputQueue = Channel.CreateUnbounded<QueueEntry>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = maxDegreeOfParallelism == 1,
        });
    }

    /// <summary>
    /// Enqueues the given input into the fuzzer.
    /// </summary>
    /// <param name="input">The input to enqueue.</param>
    public void Enqueue(TInput input)
    {
        this.inputQueue.Writer.TryWrite(new QueueEntry(input));
        lock (this.tracerSync) this.Tracer.InputsEnqueued([input]);
    }

    /// <summary>
    /// Enqueues a range of inputs into the fuzzer.
    /// </summary>
    /// <param name="inputs">The inputs to enqueue.</param>
    public void EnqueueRange(IEnumerable<TInput> inputs)
    {
        foreach (var input in inputs) this.inputQueue.Writer.TryWrite(new QueueEntry(input));
        lock (this.tracerSync) this.Tracer.InputsEnqueued(inputs);
    }

    /// <summary>
    /// Runs the fuzzing loop.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token to stop the loop.</param>
    public void Run(CancellationToken cancellationToken)
    {
        using var semaphore = this.MaxDegreeOfParallelism is null
            ? null
            : new SemaphoreSlim(this.MaxDegreeOfParallelism.Value);
        // First off, make sure the executor is set up
        // For example, in-process execution will need to run all type constructors here
        // The reason is to not poison the coverage data with all the setup code
        this.TargetExecutor.GlobalInitializer();
        while (true)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var entry = null as QueueEntry;
            while (!this.inputQueue.Reader.TryRead(out entry))
            {
                if (cancellationToken.IsCancellationRequested) goto end;
                Thread.Sleep(1);
            }
            lock (this.tracerSync) this.Tracer.InputDequeued(entry.Input);

            if (this.MaxDegreeOfParallelism == 1)
            {
                HandleEntry();
            }
            else
            {
                ThreadPool.QueueUserWorkItem(_ => HandleEntry());
            }

            void HandleEntry()
            {
                semaphore?.Wait(cancellationToken);
                try
                {
                    // We want to minimize the input first
                    entry = this.Minimize(entry);
                    if (entry.ExecutionResult?.FaultResult.IsFaulted == true)
                    {
                        // NOTE: For now we don't mutate faulted results
                        return;
                    }

                    // And we want to mutate the minimized input
                    this.Mutate(entry);
                }
                finally
                {
                    semaphore?.Release();
                }
            }
        }
    end:
        lock (this.tracerSync) this.Tracer.FuzzerFinished();
    }

    private QueueEntry Minimize(QueueEntry entry)
    {
        var referenceResult = this.GetExecutionResult(entry);
        // While we find a minimization step, we continue to minimize
        while (true)
        {
            foreach (var minimizedInput in this.InputMinimizer.Minimize(this.Random, entry.Input))
            {
                var (minimizedResult, _) = this.Execute(minimizedInput);
                if (AreEqualExecutions(referenceResult, minimizedResult))
                {
                    // We found an equivalent execution, replace entry
                    lock (this.tracerSync) this.Tracer.MinimizationFound(entry.Input, minimizedInput);
                    entry = new QueueEntry(minimizedInput, minimizedResult);
                    goto found;
                }
            }
            // No minimization found
            break;
        found:;
        }
        return entry;
    }

    private void Mutate(QueueEntry entry)
    {
        foreach (var mutatedInput in this.InputMutator.Mutate(this.Random, entry.Input))
        {
            var (_, isInteresting) = this.Execute(mutatedInput);
            if (isInteresting)
            {
                lock (this.tracerSync) this.Tracer.MutationFound(entry.Input, mutatedInput);
            }
        }
    }

    private ExecutionResult GetExecutionResult(QueueEntry entry)
    {
        entry.ExecutionResult ??= this.Execute(entry.Input, dontRequeue: true).Result;
        return entry.ExecutionResult.Value;
    }

    private (ExecutionResult Result, bool IsInteresting) Execute(TInput input, bool dontRequeue = false)
    {
        var targetInfo = this.TargetExecutor.Initialize(input);
        this.CoverageReader.Clear(targetInfo);
        var faultResult = this.FaultDetector.Detect(this.TargetExecutor, targetInfo);
        if (faultResult.IsFaulted)
        {
            lock (this.tracerSync) this.Tracer.InputFaulted(input, faultResult);
        }
        var coverage = this.CoverageReader.Read(targetInfo);
        lock (this.tracerSync) this.Tracer.InputFuzzed(input, coverage);
        var compressedCoverage = this.CoverageCompressor.Compress(coverage);
        var isInteresting = this.IsInteresting(compressedCoverage);
        var executionResult = new ExecutionResult(compressedCoverage, faultResult);
        if (!dontRequeue && isInteresting)
        {
            this.inputQueue.Writer.TryWrite(new QueueEntry(input, executionResult));
            lock (this.tracerSync) this.Tracer.InputsEnqueued([input]);
        }
        return (executionResult, isInteresting);
    }

    // We deem an input interesting, if it has not been seen before in terms of coverage
    private bool IsInteresting(TCoverage coverage) => this.seenCoverages.Add(coverage);

    // We deem them equal if they cover the same code and have the same fault result
    private static bool AreEqualExecutions(ExecutionResult a, ExecutionResult b) =>
           EqualityComparer<TCoverage>.Default.Equals(a.Coverage, b.Coverage)
        && FaultEqualityComparer.Instance.Equals(a.FaultResult, b.FaultResult);
}
