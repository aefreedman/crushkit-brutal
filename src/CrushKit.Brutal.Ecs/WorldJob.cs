using Arch.Core;

namespace CrushKit.Brutal.Ecs;

/// <summary>A world stage whose dependencies are earlier stage indexes in the submitted graph.
/// Independent stages may run concurrently: the caller must guarantee disjoint data access.</summary>
public sealed class WorldJob
{
    public string Name { get; }
    public Action<World> Execute { get; }
    public IReadOnlyList<int> Dependencies { get; }
    internal int[] DependencyIndexes { get; }

    public WorldJob(string name, Action<World> execute, params int[] dependencies)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ValidateCallback(execute);
        ArgumentNullException.ThrowIfNull(dependencies);
        Name = name;
        Execute = execute;
        DependencyIndexes = (int[])dependencies.Clone();
        Dependencies = Array.AsReadOnly(DependencyIndexes);
    }

    internal static void ValidateCallback(Action<World> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        if (execute.GetInvocationList().Any(callback => callback.Method.IsDefined(
            typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute), inherit: false)))
            throw new ArgumentException("World callbacks must complete synchronously; async-void would escape the graph lifetime.", nameof(execute));
    }
}
